using System.Net;
using System.Security.Cryptography;
using System.Text;
using Naboo.Api.Domain;
using Naboo.Api.Integrations;
using Naboo.Api.Stockage;

namespace Naboo.Api.Services;

public sealed record DemandeRdv(
    string? Prestation, string? Date, string? Heure, string? Nom, string? Email, string? Telephone,
    string? Message, string? Siteweb, bool Conditions, string? Lieu, string? Adresse);

public sealed record OptionsSite(string EmailProprietaire, string UrlApi);

/// <summary>Cœur métier : réglages, demandes, validation, annulation, statistiques.</summary>
public sealed class ServiceRendezVous
{
    // Une seule réplique tourne (max_replicas = 1 dans Terraform) : ce verrou suffit à éviter deux réservations simultanées du même créneau.
    private static readonly SemaphoreSlim Verrou = new(1, 1);

    private readonly IStockage _db;
    private readonly ICalendrier _agenda;
    private readonly IEnvoiEmail _emails;
    private readonly TimeProvider _temps;
    private readonly OptionsSite _site;
    private readonly ILogger<ServiceRendezVous> _log;

    public ServiceRendezVous(IStockage db, ICalendrier agenda, IEnvoiEmail emails, TimeProvider temps, OptionsSite site,
        ILogger<ServiceRendezVous> log)
    {
        _db = db; _agenda = agenda; _emails = emails; _temps = temps; _site = site; _log = log;
    }

    private DateTimeOffset Maintenant => _temps.GetUtcNow();

    // ---------- Réglages ----------

    public async Task<Reglages> ReglagesAsync(CancellationToken ct = default) =>
        await _db.LireReglagesAsync(ct) ?? Reglages.ParDefaut();

    public async Task<Reglages> EnregistrerReglagesAsync(Reglages recus, CancellationToken ct = default)
    {
        var propres = ValidateurReglages.Valider(recus, await ReglagesAsync(ct), Dates.AujourdhuiParis(Maintenant));
        await _db.EcrireReglagesAsync(propres, ct);
        return propres;
    }

    public static object ConfigPublique(Reglages r)
    {
        var ouverts = r.Horaires.Where(kv => kv.Value.Count > 0).Select(kv => int.Parse(kv.Key)).OrderBy(x => x).ToList();
        var toutes = r.Horaires.Values.Concat(r.OuverturesException.Values).SelectMany(x => x).Where(p => p.Count == 2);
        return new
        {
            nom = r.NomActivite,
            lieu = r.Lieu,
            prestations = r.Prestations.Where(p => p.Actif).Select(p => new { p.Id, p.Nom, p.Duree, p.Prix }),
            joursOuverts = ouverts,
            horaires = r.Horaires,
            joursFermes = r.JoursFermes,
            joursExtra = r.OuverturesException.Keys,
            joursMax = r.JoursMaxALAvance,
            acompte = r.Acompte,
            delaiHeures = r.DelaiMinHeures,
            supplementDomicile = r.SupplementDomicile,
            wero = r.WeroNumero,
            boutiques = r.Boutiques,
            dernierDebut = toutes.Select(p => p[1]).DefaultIfEmpty("00:00").Max(StringComparer.Ordinal),
        };
    }

    public async Task<List<string>> CreneauxAsync(string? date, string? prestation, CancellationToken ct = default)
    {
        var r = await ReglagesAsync(ct);
        var occupes = Creneaux.Occupations(await _db.ListerRendezVousAsync(ct), r, Maintenant);
        return Creneaux.Libres(r, date, prestation, occupes, Maintenant);
    }

    // ---------- Demande de rendez-vous ----------

    public async Task<string?> DemanderAsync(DemandeRdv d, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(d.Siteweb)) return null; // champ piège rempli : robot, on fait semblant d'accepter

        var r = await ReglagesAsync(ct);
        var presta = Creneaux.PrestationActive(r, d.Prestation) ?? throw new ErreurMetier("Prestation inconnue.");
        if (!Dates.TryDate(d.Date, out var date) || !Dates.TryHeure(d.Heure, out var heure)) throw new ErreurMetier("Date ou heure invalide.");
        var nom = Nettoyer(d.Nom, 80);
        var email = Nettoyer(d.Email, 120);
        var tel = Nettoyer(d.Telephone, 30);
        var message = Nettoyer(d.Message, 1000);
        if (nom.Length == 0) throw new ErreurMetier("Le nom est obligatoire.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) throw new ErreurMetier("Email invalide.");
        if (tel.Count(char.IsAsciiDigit) < 9) throw new ErreurMetier("Merci d'indiquer votre numéro de téléphone.");
        if (!d.Conditions) throw new ErreurMetier("Merci d'accepter les conditions de réservation.");
        var domicile = d.Lieu == "domicile";
        var adresse = domicile ? Nettoyer(d.Adresse, 200) : "";
        if (domicile && adresse.Length < 8) throw new ErreurMetier("Merci d'indiquer votre adresse complète à Poitiers.");

        RendezVous rdv;
        await Verrou.WaitAsync(ct);
        try
        {
            var tous = await _db.ListerRendezVousAsync(ct);
            var limite = Maintenant.AddHours(-r.ExpirationAttenteHeures);
            var enAttente = tous.Count(x => x.Statut == Statuts.Attente && x.CreeLe >= limite
                && string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            if (enAttente >= r.MaxDemandesParEmail)
                throw new ErreurMetier($"Vous avez déjà {enAttente} demande(s) en attente. Merci d'envoyer l'acompte ou de nous contacter.", 409);

            var libres = Creneaux.Libres(r, d.Date, presta.Id, Creneaux.Occupations(tous, r, Maintenant), Maintenant);
            if (!libres.Contains(d.Heure!)) throw new ErreurMetier("Ce créneau vient d'être pris. Merci d'en choisir un autre.", 409);

            var debut = Dates.VersUtc(date, heure);
            rdv = new RendezVous
            {
                Id = "R" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)),
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                CreeLe = Maintenant,
                Statut = Statuts.Attente,
                PrestationId = presta.Id,
                Prestation = presta.Nom,
                Debut = debut,
                Fin = debut.AddMinutes(presta.Duree),
                Nom = nom, Email = email, Telephone = tel, Message = message,
                Lieu = domicile ? "domicile" : "salon",
                Adresse = adresse,
            };
            await _db.EcrireRendezVousAsync(rdv, ct);
        }
        finally
        {
            Verrou.Release();
        }

        var quand = Dates.Lisible(rdv.Debut);
        var lieu = domicile ? $"À domicile (+{r.SupplementDomicile}) : {adresse}" : $"Chez {r.NomActivite}";
        var msgWero = MessageWero(rdv);
        var lien = $"{_site.UrlApi.TrimEnd('/')}/rdv/examiner?id={rdv.Id}&token={rdv.Token}";

        await EnvoyerSansEchec(new Email(_site.EmailProprietaire, $"Nouvelle demande de RDV — {nom} — {quand}",
            $"<p><b>{H(nom)}</b> demande un rendez-vous :</p><ul><li>{H(presta.Nom)} ({presta.Duree} min)</li>" +
            $"<li>{H(quand)}</li><li>{H(lieu)}</li><li>{H(email)} — {H(tel)}</li></ul>" +
            (message.Length > 0 ? $"<p>Message : {H(message)}</p>" : "") +
            $"<p>Vérifie que l'acompte de {H(r.Acompte)} est bien arrivé sur Wero (message attendu : « {H(msgWero)} ») avant de valider.</p>" +
            $"<p><a href=\"{H(lien)}\" style=\"background:#8a6440;color:#fff;padding:10px 16px;border-radius:8px;text-decoration:none\">Examiner la demande</a></p>"), ct);

        await EnvoyerSansEchec(new Email(email, $"Demande de rendez-vous reçue — {r.NomActivite}",
            $"<p>Bonjour {H(nom)},</p><p>Votre demande pour <b>{H(presta.Nom)}</b> le <b>{H(quand)}</b> a bien été reçue.<br>Lieu : {H(lieu)}</p>" +
            "<p>Pensez à acheter vos mèches avant le rendez-vous : elles ne sont pas fournies.</p>" +
            $"<p><b>Pour confirmer votre rendez-vous</b>, envoyez un <b>acompte de {H(r.Acompte)}</b> par <b>Wero</b> " +
            $"(depuis l'appli de votre banque) au <b>{H(r.WeroNumero)}</b>, avec en message : « {H(msgWero)} ».</p>" +
            $"<p>L'acompte est déduit du prix et n'est pas remboursable en cas d'annulation moins de 24 h avant. " +
            $"Sans acompte sous {r.ExpirationAttenteHeures} h, le créneau sera libéré.</p><p>{H(r.NomActivite)}</p>",
            _site.EmailProprietaire), ct);

        await CompterAsync("jour:" + Dates.Iso(Dates.AujourdhuiParis(Maintenant)), "r", ct);
        await CompterAsync("presta:" + presta.Id, "r", ct);
        return rdv.Id;
    }

    // ---------- Actions sur un rendez-vous ----------

    public async Task<string> ActionAsync(string id, string action, CancellationToken ct = default)
    {
        var r = await ReglagesAsync(ct);
        await Verrou.WaitAsync(ct);
        try
        {
            var rdv = await _db.LireRendezVousAsync(id, ct) ?? throw new ErreurMetier("Rendez-vous introuvable.", 404);
            var quand = Dates.Lisible(rdv.Debut);
            switch (action)
            {
                case "valider":
                {
                    if (rdv.Statut != Statuts.Attente) return $"Cette demande a déjà été traitée ({Statuts.Libelle(rdv.Statut)}).";
                    var tous = await _db.ListerRendezVousAsync(ct);
                    if (Creneaux.Occupations(tous, r, Maintenant).Any(o => o.Id != rdv.Id && rdv.Debut < o.Fin && rdv.Fin > o.Debut))
                        throw new ErreurMetier("Ce créneau est désormais occupé par un autre rendez-vous. Refuse la demande ou propose un autre horaire.", 409);
                    var uid = $"{rdv.Id}-{Maintenant.ToUnixTimeMilliseconds()}@naboo";
                    await _agenda.AjouterAsync(uid, Ics.Construire(uid, rdv, r, true, Maintenant), ct); // agenda mis à jour en temps réel
                    rdv.Statut = Statuts.Valide;
                    rdv.UidAgenda = uid;
                    await _db.EcrireRendezVousAsync(rdv, ct);
                    var lieu = rdv.Lieu == "domicile"
                        ? $"à domicile, {rdv.Adresse} (supplément {r.SupplementDomicile})"
                        : (r.AdresseSalon.Length > 0 ? r.AdresseSalon : r.Lieu + " — l'adresse exacte vous sera envoyée par message");
                    await EnvoyerSansEchec(new Email(rdv.Email, $"Rendez-vous confirmé — {quand}",
                        $"<p>Bonjour {H(rdv.Nom)},</p><p>Votre rendez-vous <b>{H(rdv.Prestation)}</b> est confirmé pour le <b>{H(quand)}</b>.<br>Lieu : {H(lieu)}</p>" +
                        "<p>N'oubliez pas vos mèches (non fournies). Merci de venir avec les cheveux propres, démêlés et sans produit. " +
                        "Rappel : l'acompte n'est pas remboursable en cas d'annulation moins de 24 h avant le rendez-vous.</p>" +
                        $"<p>Ajoutez-le à votre agenda avec le fichier joint. Pour annuler ou déplacer, répondez à cet email.</p><p>{H(r.NomActivite)}</p>",
                        _site.EmailProprietaire, Encoding.UTF8.GetBytes(Ics.Construire(uid, rdv, r, false, Maintenant))), ct);
                    return "Rendez-vous validé ✅ Il est dans ton agenda iCloud et la cliente est prévenue.";
                }
                case "refuser":
                    if (rdv.Statut != Statuts.Attente) return $"Cette demande a déjà été traitée ({Statuts.Libelle(rdv.Statut)}).";
                    rdv.Statut = Statuts.Refuse;
                    await _db.EcrireRendezVousAsync(rdv, ct);
                    await EnvoyerSansEchec(new Email(rdv.Email, $"Votre demande de rendez-vous — {r.NomActivite}",
                        $"<p>Bonjour {H(rdv.Nom)},</p><p>Malheureusement, le créneau du <b>{H(quand)}</b> n'est pas disponible. " +
                        $"N'hésitez pas à faire une nouvelle demande sur le site.</p><p>{H(r.NomActivite)}</p>", _site.EmailProprietaire), ct);
                    return "Demande refusée. La cliente a été prévenue par email.";
                case "annuler":
                    if (rdv.Statut is not (Statuts.Valide or Statuts.Attente))
                        return $"Ce rendez-vous est déjà {Statuts.Libelle(rdv.Statut).ToLowerInvariant()}.";
                    if (rdv.UidAgenda.Length > 0) await _agenda.SupprimerAsync(rdv.UidAgenda, ct);
                    rdv.Statut = Statuts.Annule;
                    await _db.EcrireRendezVousAsync(rdv, ct);
                    await EnvoyerSansEchec(new Email(rdv.Email, $"Rendez-vous annulé — {quand}",
                        $"<p>Bonjour {H(rdv.Nom)},</p><p>Votre rendez-vous du <b>{H(quand)}</b> a été annulé. " +
                        $"Répondez à cet email pour en convenir d'un autre.</p><p>{H(r.NomActivite)}</p>", _site.EmailProprietaire), ct);
                    return "Rendez-vous annulé : retiré de ton agenda, cliente prévenue.";
                case "supprimer":
                    if (rdv.Statut == Statuts.Valide && rdv.UidAgenda.Length > 0) await _agenda.SupprimerAsync(rdv.UidAgenda, ct);
                    await _db.SupprimerRendezVousAsync(rdv.Id, ct);
                    return "Rendez-vous supprimé définitivement (aucun email envoyé).";
                default:
                    throw new ErreurMetier("Action inconnue.");
            }
        }
        finally
        {
            Verrou.Release();
        }
    }

    /// <summary>Rendez-vous des 60 derniers jours et à venir, sans le jeton secret.</summary>
    public async Task<List<object>> ListeAdminAsync(CancellationToken ct = default)
    {
        var r = await ReglagesAsync(ct);
        var limite = Maintenant.AddDays(-60);
        var expiration = Maintenant.AddHours(-r.ExpirationAttenteHeures);
        return (await _db.ListerRendezVousAsync(ct))
            .Where(x => x.Debut >= limite)
            .OrderBy(x => x.Debut)
            .Select(x => (object)new
            {
                x.Id, x.Statut, x.Prestation, x.Debut, x.Fin, quand = Dates.Lisible(x.Debut), x.Nom, x.Email, x.Telephone,
                x.Message, x.Lieu, x.Adresse, x.CreeLe,
                expire = x.Statut == Statuts.Attente && x.CreeLe < expiration,
                messageWero = MessageWero(x),
            })
            .ToList();
    }

    public async Task<RendezVous?> LirePourExamenAsync(string? id, string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) return null;
        var rdv = await _db.LireRendezVousAsync(id, ct);
        return rdv is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(rdv.Token), Encoding.UTF8.GetBytes(token))
            ? rdv : null;
    }

    // ---------- Statistiques (anonymes, sans cookie) ----------

    public Task CompterVisiteAsync(CancellationToken ct = default) =>
        CompterAsync("jour:" + Dates.Iso(Dates.AujourdhuiParis(Maintenant)), "v", ct);

    public async Task CompterVueAsync(string? prestation, CancellationToken ct = default)
    {
        if (Creneaux.PrestationActive(await ReglagesAsync(ct), prestation) is not null)
            await CompterAsync("presta:" + prestation, "v", ct);
    }

    public async Task<object> StatsAsync(CancellationToken ct = default)
    {
        var r = await ReglagesAsync(ct);
        var noms = r.Prestations.ToDictionary(p => p.Id, p => p.Nom);
        var tous = await _db.LireStatsAsync(ct);
        var depuis = Dates.Iso(Dates.AujourdhuiParis(Maintenant).AddDays(-180));
        return new
        {
            jours = tous.Where(c => c.Id.StartsWith("jour:") && string.CompareOrdinal(c.Id[5..], depuis) >= 0)
                .ToDictionary(c => c.Id[5..], c => new { v = c.V, r = c.R }),
            prestas = tous.Where(c => c.Id.StartsWith("presta:"))
                .Select(c => new { id = c.Id[7..], nom = noms.TryGetValue(c.Id[7..], out var n) ? n : c.Id[7..], vues = c.V, demandes = c.R })
                .OrderByDescending(p => p.vues),
            aujourdhui = Dates.Iso(Dates.AujourdhuiParis(Maintenant)),
        };
    }

    private async Task CompterAsync(string id, string champ, CancellationToken ct)
    {
        // Une statistique ne doit jamais faire échouer une visite ou une réservation
        try { await _db.IncrementerAsync(id, champ, ct); }
        catch (Exception e) { _log.LogWarning(e, "Statistique non enregistrée ({Id})", id); }
    }

    // ---------- Outils ----------

    private async Task EnvoyerSansEchec(Email email, CancellationToken ct)
    {
        try { await _emails.EnvoyerAsync(email, ct); }
        catch (Exception e) { _log.LogError(e, "Email non envoyé à {A} : {Sujet}", email.A, email.Sujet); }
    }

    public static string MessageWero(RendezVous r) => $"Acompte {r.Nom} {Dates.JourMois(r.Debut)}";
    private static string Nettoyer(string? s, int max) { var t = (s ?? "").Trim(); return t.Length > max ? t[..max] : t; }
    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
}
