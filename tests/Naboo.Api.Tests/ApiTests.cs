using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Naboo.Api.Integrations;
using Naboo.Api.Stockage;

namespace Naboo.Api.Tests;

/// <summary>Tests de bout en bout de l'API, avec stockage en mémoire, agenda et emails simulés.</summary>
public class ApiTests
{
    private const string MotDePasseAdmin = "mot-de-passe-de-test";

    private sealed class Banc : IDisposable
    {
        public WebApplicationFactory<Program> Usine { get; }
        public HttpClient Client { get; }
        public CalendrierJournal Agenda { get; } = new(NullLogger<CalendrierJournal>.Instance);
        public EnvoiJournal Emails { get; } = new(NullLogger<EnvoiJournal>.Instance);
        public StockageMemoire Db { get; } = new();

        public Banc()
        {
            Usine = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ADMIN_MOT_DE_PASSE", MotDePasseAdmin);
                b.UseSetting("SECRET_SESSION", new string('s', 40));
                b.UseSetting("EMAIL_PROPRIETAIRE", "naboo@exemple.fr");
                b.UseSetting("URL_API", "https://api.exemple.fr");
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(new HorlogeFixe(DateTimeOffset.Parse("2026-10-30T20:00:00Z")));
                    s.RemoveAll<IStockage>(); s.AddSingleton<IStockage>(Db);
                    s.RemoveAll<ICalendrier>(); s.AddSingleton<ICalendrier>(Agenda);
                    s.RemoveAll<IEnvoiEmail>(); s.AddSingleton<IEnvoiEmail>(Emails);
                });
            });
            Client = Usine.CreateClient();
        }

        public async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

        public async Task<string> Connecter()
        {
            var r = await Client.PostAsJsonAsync("/api/admin/connexion", new { motDePasse = MotDePasseAdmin });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var jeton = (await Json(r))["jeton"]!.GetValue<string>();
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
            return jeton;
        }

        public Task<HttpResponseMessage> Reserver(string heure = "10:00", string email = "fatou@exemple.fr") =>
            Client.PostAsJsonAsync("/api/rendezvous", new
            {
                prestation = "twist", date = "2026-11-07", heure, nom = "Fatou Test", email,
                telephone = "07 00 00 00 00", message = "", siteweb = "", conditions = true, lieu = "salon", adresse = "",
            });

        public void Dispose() { Client.Dispose(); Usine.Dispose(); }
    }

    [Fact]
    public async Task Config_publique_et_comptage_des_visites()
    {
        using var b = new Banc();
        var j = await b.Json(await b.Client.GetAsync("/api/config"));
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.Equal(5, j["config"]!["prestations"]!.AsArray().Count);
        Assert.Equal("10 €", j["config"]!["acompte"]!.GetValue<string>());
        Assert.Equal("16:00", j["config"]!["dernierDebut"]!.GetValue<string>());
        Assert.Equal(1, (await b.Db.LireStatsAsync()).Single(c => c.Id == "jour:2026-10-30").V);
    }

    [Fact]
    public async Task Parcours_complet_reservation_validation_reglages_suppression()
    {
        using var b = new Banc();

        // 1. Réservation : 2 emails (propriétaire + cliente)
        var r = await b.Reserver();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, b.Emails.Envoyes.Count);
        Assert.Contains(b.Emails.Envoyes, e => e.A == "naboo@exemple.fr" && e.Html.Contains("https://api.exemple.fr/rdv/examiner?id="));

        // 2. Le même créneau n'est plus proposé, et une 2e demande dessus est refusée
        var c = await b.Json(await b.Client.GetAsync("/api/creneaux?date=2026-11-07&prestation=twist"));
        Assert.DoesNotContain("10:00", c["creneaux"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Conflict, (await b.Reserver(email: "autre@exemple.fr")).StatusCode);

        // 3. Espace de gestion : refusé sans session, mauvais mot de passe refusé
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Client.GetAsync("/api/admin/etat")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await b.Client.PostAsJsonAsync("/api/admin/connexion", new { motDePasse = "mauvais" })).StatusCode);
        await b.Connecter();
        var etat = await b.Json(await b.Client.GetAsync("/api/admin/etat"));
        var rdv = etat["rdv"]!.AsArray().Single()!;
        Assert.Equal("en_attente", rdv["statut"]!.GetValue<string>());
        Assert.Null(rdv["token"]); // le secret du lien email n'est jamais exposé
        var id = rdv["id"]!.GetValue<string>();

        // 4. Validation : événement iCloud + email de confirmation avec .ics
        var v = await b.Json(await b.Client.PostAsync($"/api/admin/rendezvous/{id}/valider", null));
        Assert.Contains("validé", v["message"]!.GetValue<string>());
        Assert.Single(b.Agenda.Evenements, e => e.StartsWith('+'));
        Assert.Contains(b.Emails.Envoyes, e => e.Sujet.StartsWith("Rendez-vous confirmé") && e.Ics is not null);

        // 5. Réglages : l'acompte modifié est immédiatement visible sur le site public
        var reglages = etat["reglages"]!;
        reglages["ACOMPTE"] = "15 €";
        var put = await b.Client.PutAsync("/api/admin/reglages",
            new StringContent(new JsonObject { ["reglages"] = JsonNode.Parse(reglages.ToJsonString()) }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var config = await b.Json(await b.Client.GetAsync("/api/config"));
        Assert.Equal("15 €", config["config"]!["acompte"]!.GetValue<string>());

        // 6. Réglages invalides refusés avec un message clair
        reglages["DELAI_MIN_HEURES"] = 9999;
        var mauvais = await b.Client.PutAsync("/api/admin/reglages",
            new StringContent(new JsonObject { ["reglages"] = JsonNode.Parse(reglages.ToJsonString()) }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, mauvais.StatusCode);
        Assert.Contains("Délai minimum", (await b.Json(mauvais))["erreur"]!.GetValue<string>());

        // 7. Suppression définitive : retiré de l'agenda, aucune donnée restante
        await b.Client.PostAsync($"/api/admin/rendezvous/{id}/supprimer", null);
        Assert.Contains(b.Agenda.Evenements, e => e.StartsWith('-'));
        Assert.Empty(await b.Db.ListerRendezVousAsync());

        // 8. Statistiques
        var stats = await b.Json(await b.Client.GetAsync("/api/admin/stats"));
        Assert.Equal(1, stats["stats"]!["jours"]!["2026-10-30"]!["r"]!.GetValue<int>());
    }

    [Fact]
    public async Task Lien_email_examiner_et_decider()
    {
        using var b = new Banc();
        await b.Reserver();
        var rdv = (await b.Db.ListerRendezVousAsync()).Single();

        var faux = await b.Client.GetStringAsync($"/rdv/examiner?id={rdv.Id}&token=faux");
        Assert.Contains("Lien invalide", faux);
        var page = await b.Client.GetStringAsync($"/rdv/examiner?id={rdv.Id}&token={rdv.Token}");
        Assert.Contains("Fatou Test", page);

        var refus = await b.Client.PostAsJsonAsync("/rdv/decision", new { id = rdv.Id, token = "faux", decision = "valider" });
        Assert.Equal(HttpStatusCode.Forbidden, refus.StatusCode);
        var ok = await b.Json(await b.Client.PostAsJsonAsync("/rdv/decision", new { id = rdv.Id, token = rdv.Token, decision = "valider" }));
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("valide", (await b.Db.LireRendezVousAsync(rdv.Id))!.Statut);
    }

    [Fact]
    public async Task Validation_des_demandes()
    {
        using var b = new Banc();
        var sansConditions = await b.Client.PostAsJsonAsync("/api/rendezvous", new
        {
            prestation = "twist", date = "2026-11-07", heure = "10:00", nom = "X", email = "x@exemple.fr",
            telephone = "0700000000", conditions = false, lieu = "salon",
        });
        Assert.Equal(HttpStatusCode.BadRequest, sansConditions.StatusCode);
        Assert.Contains("conditions", (await b.Json(sansConditions))["erreur"]!.GetValue<string>());

        // Champ piège rempli (robot) : réponse « ok » mais rien n'est enregistré
        var robot = await b.Client.PostAsJsonAsync("/api/rendezvous", new { siteweb = "http://spam", conditions = true });
        Assert.Equal(HttpStatusCode.OK, robot.StatusCode);
        Assert.Empty(await b.Db.ListerRendezVousAsync());
    }

    [Fact]
    public async Task Changement_de_mot_de_passe()
    {
        using var b = new Banc();
        await b.Connecter();
        var r = await b.Client.PostAsJsonAsync("/api/admin/motdepasse", new { actuel = MotDePasseAdmin, nouveau = "nouveau-mot-de-passe" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("pbkdf2-sha256$", await b.Db.LireHashAdminAsync());
        b.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await b.Client.PostAsJsonAsync("/api/admin/connexion", new { motDePasse = MotDePasseAdmin })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await b.Client.PostAsJsonAsync("/api/admin/connexion", new { motDePasse = "nouveau-mot-de-passe" })).StatusCode);
    }

    [Fact]
    public async Task Sante() => Assert.Equal(HttpStatusCode.OK, (await new Banc().Client.GetAsync("/sante")).StatusCode);
}
