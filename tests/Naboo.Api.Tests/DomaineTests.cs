using System.Text;
using Naboo.Api.Domain;
using Naboo.Api.Securite;

namespace Naboo.Api.Tests;

/// <summary>Horloge figée pour des tests reproductibles.</summary>
public sealed class HorlogeFixe : TimeProvider
{
    public DateTimeOffset Maintenant { get; set; }
    public HorlogeFixe(DateTimeOffset maintenant) => Maintenant = maintenant;
    public override DateTimeOffset GetUtcNow() => Maintenant;
}

public class CreneauxTests
{
    // Vendredi 30 octobre 2026, 21 h à Paris
    private static readonly DateTimeOffset Maintenant = DateTimeOffset.Parse("2026-10-30T20:00:00Z");

    private static RendezVous Rdv(string statut, string debutParis, double dureeH, double creeIlYaH = 1)
    {
        var parts = debutParis.Split(' ');
        var debut = Dates.VersUtc(DateOnly.Parse(parts[0]), TimeOnly.Parse(parts[1]));
        return new RendezVous { Id = Guid.NewGuid().ToString(), Statut = statut, Debut = debut, Fin = debut.AddHours(dureeH), CreeLe = Maintenant.AddHours(-creeIlYaH) };
    }

    private static List<string> Libres(Reglages r, string date, string presta, params RendezVous[] rdvs) =>
        Creneaux.Libres(r, date, presta, Creneaux.Occupations(rdvs, r, Maintenant), Maintenant);

    [Fact]
    public void Samedi_de_9h_a_16h_toutes_les_heures() =>
        Assert.Equal(new[] { "09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00" },
            Libres(Reglages.ParDefaut(), "2026-11-07", "braids"));

    [Fact]
    public void Aucun_creneau_en_semaine() => Assert.Empty(Libres(Reglages.ParDefaut(), "2026-11-10", "braids"));

    [Fact]
    public void Aucun_creneau_a_moins_de_48h() => Assert.Empty(Libres(Reglages.ParDefaut(), "2026-11-01", "twist"));

    [Fact]
    public void Aucun_creneau_au_dela_de_45_jours() => Assert.Empty(Libres(Reglages.ParDefaut(), "2027-01-16", "twist"));

    [Theory]
    [InlineData("2026-11-07", "inconnue")]
    [InlineData("7/11/2026", "twist")]
    [InlineData(null, "twist")]
    public void Entree_invalide_aucun_creneau(string? date, string presta) =>
        Assert.Empty(Creneaux.Libres(Reglages.ParDefaut(), date, presta, Array.Empty<Occupation>(), Maintenant));

    [Fact]
    public void Un_rdv_valide_bloque_les_creneaux_qui_chevauchent() =>
        Assert.Equal(new[] { "15:00", "16:00" },
            Libres(Reglages.ParDefaut(), "2026-11-07", "nattes-collees", Rdv(Statuts.Valide, "2026-11-07 10:00", 5)));

    [Fact]
    public void Une_demande_recente_bloque_le_creneau() =>
        Assert.DoesNotContain("10:00", Libres(Reglages.ParDefaut(), "2026-11-07", "nattes-collees", Rdv(Statuts.Attente, "2026-11-07 09:00", 2, 3)));

    [Fact]
    public void Une_demande_sans_acompte_depuis_24h_libere_le_creneau() =>
        Assert.Contains("09:00", Libres(Reglages.ParDefaut(), "2026-11-07", "nattes-collees", Rdv(Statuts.Attente, "2026-11-07 09:00", 2, 30)));

    [Fact]
    public void Refuse_ou_annule_ne_bloque_rien() =>
        Assert.Equal(8, Libres(Reglages.ParDefaut(), "2026-11-07", "braids",
            Rdv(Statuts.Refuse, "2026-11-07 09:00", 8), Rdv(Statuts.Annule, "2026-11-07 09:00", 8)).Count);

    [Fact]
    public void Jour_ferme_aucun_creneau()
    {
        var r = Reglages.ParDefaut();
        r.JoursFermes.Add("2026-11-14");
        Assert.Empty(Libres(r, "2026-11-14", "twist"));
    }

    [Fact]
    public void Creneaux_bloques_a_la_main()
    {
        var r = Reglages.ParDefaut();
        r.CreneauxBloques["2026-11-07"] = new() { "10:00", "14:00" };
        var c = Libres(r, "2026-11-07", "twist");
        Assert.DoesNotContain("10:00", c);
        Assert.DoesNotContain("14:00", c);
        Assert.Contains("11:00", c);
    }

    [Fact]
    public void Ouverture_exceptionnelle_un_mercredi()
    {
        var r = Reglages.ParDefaut();
        r.OuverturesException["2026-11-11"] = new() { new() { "13:00", "15:00" } };
        Assert.Equal(new[] { "13:00", "14:00", "15:00" }, Libres(r, "2026-11-11", "twist"));
    }

    [Fact]
    public void Prestation_desactivee_non_reservable()
    {
        var r = Reglages.ParDefaut();
        r.Prestations[0].Actif = false;
        Assert.Empty(Libres(r, "2026-11-07", r.Prestations[0].Id));
    }

    [Fact]
    public void Fin_de_plage_stricte_si_debut_seulement_faux()
    {
        var r = Reglages.ParDefaut();
        r.DebutSeulement = false; // braids = 5 h : doit être terminée à 16 h
        Assert.Equal(new[] { "09:00", "10:00", "11:00" }, Libres(r, "2026-11-07", "braids"));
    }
}

public class DatesTests
{
    [Fact]
    public void Heure_d_hiver() =>
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T09:00:00Z"), Dates.VersUtc(new DateOnly(2026, 11, 7), new TimeOnly(10, 0)));

    [Fact]
    public void Heure_d_ete() =>
        Assert.Equal(DateTimeOffset.Parse("2026-07-04T08:00:00Z"), Dates.VersUtc(new DateOnly(2026, 7, 4), new TimeOnly(10, 0)));

    [Fact]
    public void Date_lisible_en_francais() =>
        Assert.Equal("samedi 7 novembre à 10h00", Dates.Lisible(DateTimeOffset.Parse("2026-11-07T09:00:00Z")));
}

public class IcsTests
{
    [Fact]
    public void Evenement_en_UTC_avec_adresse_a_domicile()
    {
        var debut = DateTimeOffset.Parse("2026-11-07T09:00:00Z");
        var rdv = new RendezVous { Prestation = "Twist", Nom = "Awa", Email = "a@b.fr", Debut = debut, Fin = debut.AddHours(4), Lieu = "domicile", Adresse = "3 rue X, Poitiers" };
        var ics = Ics.Construire("u@naboo", rdv, Reglages.ParDefaut(), true, debut);
        Assert.Contains("DTSTART:20261107T090000Z", ics);
        Assert.Contains("DTEND:20261107T130000Z", ics);
        Assert.Contains("LOCATION:3 rue X\\, Poitiers", ics);
        Assert.Contains("TRIGGER:-PT60M", ics);
    }

    [Fact]
    public void Lignes_pliees_a_75_octets_accents_compris()
    {
        var longue = "DESCRIPTION:" + new string('é', 100);
        var pliee = Ics.Plier(longue);
        var lignes = pliee.Split("\r\n");
        Assert.All(lignes, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.Equal(longue, string.Concat(lignes.Select((l, i) => i == 0 ? l : l[1..])));
    }
}

public class ReglagesTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 10, 31);
    private static Reglages Copie() => System.Text.Json.JsonSerializer.Deserialize<Reglages>(System.Text.Json.JsonSerializer.Serialize(Reglages.ParDefaut()))!;

    [Fact]
    public void Reglages_par_defaut_acceptes()
    {
        var o = ValidateurReglages.Valider(Copie(), Reglages.ParDefaut(), Aujourdhui);
        Assert.Equal(5, o.Prestations.Count);
        Assert.Equal("10 €", o.Acompte);
    }

    [Fact]
    public void Refuse_duree_absurde()
    {
        var r = Copie(); r.Prestations[0].Duree = 5000;
        Assert.Contains("Durée", Assert.Throws<ErreurMetier>(() => ValidateurReglages.Valider(r, Reglages.ParDefaut(), Aujourdhui)).Message);
    }

    [Fact]
    public void Refuse_horaires_inverses()
    {
        var r = Copie(); r.Horaires["6"] = new() { new() { "16:00", "09:00" } };
        Assert.Throws<ErreurMetier>(() => ValidateurReglages.Valider(r, Reglages.ParDefaut(), Aujourdhui));
    }

    [Fact]
    public void Refuse_aucune_prestation_active()
    {
        var r = Copie(); r.Prestations.ForEach(p => p.Actif = false);
        Assert.Contains("active", Assert.Throws<ErreurMetier>(() => ValidateurReglages.Valider(r, Reglages.ParDefaut(), Aujourdhui)).Message);
    }

    [Fact]
    public void Identifiants_propres_et_uniques_pour_les_nouvelles_prestations()
    {
        var r = Copie();
        r.Prestations.Add(new Prestation { Nom = "Twist", Duree = 90, Prix = "20 €" });
        r.Prestations.Add(new Prestation { Nom = "Fulani Braids à l'été", Duree = 240, Prix = "55 €" });
        var ids = ValidateurReglages.Valider(r, Reglages.ParDefaut(), Aujourdhui).Prestations.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("fulani-braids-a-l-ete", ids);
    }

    [Fact]
    public void Dates_passees_et_creneaux_mal_formes_retires()
    {
        var r = Copie();
        r.JoursFermes = new() { "2020-01-01", "2026-12-26", "pas-une-date", "2026-12-26" };
        r.CreneauxBloques = new() { ["2020-01-01"] = new() { "10:00" }, ["2026-11-14"] = new() { "10:00", "25:00" } };
        var o = ValidateurReglages.Valider(r, Reglages.ParDefaut(), Aujourdhui);
        Assert.Equal(new[] { "2026-12-26" }, o.JoursFermes);
        Assert.Equal(new[] { "10:00" }, o.CreneauxBloques["2026-11-14"]);
        Assert.False(o.CreneauxBloques.ContainsKey("2020-01-01"));
    }

    [Fact]
    public void Refuse_texte_trop_long()
    {
        var r = Copie(); r.AdresseSalon = new string('x', 500);
        Assert.Contains("200 caractères", Assert.Throws<ErreurMetier>(() => ValidateurReglages.Valider(r, Reglages.ParDefaut(), Aujourdhui)).Message);
    }
}

public class SecuriteTests
{
    [Fact]
    public void Mot_de_passe_hache_et_verifie()
    {
        var h = MotDePasse.Hacher("un-bon-mot-de-passe");
        Assert.StartsWith("pbkdf2-sha256$", h);
        Assert.True(MotDePasse.Verifier("un-bon-mot-de-passe", h));
        Assert.False(MotDePasse.Verifier("mauvais", h));
        Assert.False(MotDePasse.Verifier("x", "n'importe quoi"));
    }

    [Fact]
    public void Jetons_valides_expires_ou_falsifies()
    {
        var horloge = new HorlogeFixe(DateTimeOffset.Parse("2026-11-01T10:00:00Z"));
        var jetons = new Jetons(new byte[32], horloge);
        var j = jetons.Emettre();
        Assert.True(jetons.EstValide(j));
        Assert.False(jetons.EstValide(j[..^1] + (j[^1] == '0' ? '1' : '0')));
        Assert.False(new Jetons(Enumerable.Repeat((byte)7, 32).ToArray(), horloge).EstValide(j)); // autre clé
        horloge.Maintenant = horloge.Maintenant.AddHours(7);
        Assert.False(jetons.EstValide(j));
    }

    [Fact]
    public void Blocage_apres_8_echecs()
    {
        var horloge = new HorlogeFixe(DateTimeOffset.Parse("2026-11-01T10:00:00Z"));
        var garde = new GardeConnexion(horloge);
        for (var i = 0; i < 8; i++) garde.Echec();
        Assert.True(garde.EstBloque());
        horloge.Maintenant = horloge.Maintenant.AddMinutes(16);
        Assert.False(garde.EstBloque());
    }
}
