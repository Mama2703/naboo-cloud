using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Naboo.Api.Domain;

/// <summary>Vérifie et nettoie les réglages envoyés par l'espace de gestion (ne jamais faire confiance au navigateur).</summary>
public static class ValidateurReglages
{
    /// <param name="r">Réglages reçus.</param>
    /// <param name="actuels">Réglages en place (pour les champs non modifiables).</param>
    /// <param name="aujourdhui">Date du jour à Paris : les dates passées sont retirées.</param>
    public static Reglages Valider(Reglages? r, Reglages actuels, DateOnly aujourdhui)
    {
        if (r is null) throw new ErreurMetier("Réglages invalides.");
        var auj = Dates.Iso(aujourdhui);
        var o = new Reglages { Id = actuels.Id, NomActivite = actuels.NomActivite, Lieu = actuels.Lieu };

        // Prestations
        if (r.Prestations is null || r.Prestations.Count is 0 or > 30)
            throw new ErreurMetier("Il faut entre 1 et 30 prestations.");
        var ids = new HashSet<string>();
        for (var i = 0; i < r.Prestations.Count; i++)
        {
            var p = r.Prestations[i] ?? throw new ErreurMetier("Prestation invalide.");
            var nom = Texte(p.Nom, 60, "Nom de prestation");
            if (nom.Length == 0) throw new ErreurMetier("Chaque prestation doit avoir un nom.");
            var id = Regex.IsMatch(p.Id ?? "", "^[a-z0-9-]{1,40}$") ? p.Id! : Slug(nom);
            var baseId = id;
            var n = 2;
            while (!ids.Add(id)) id = $"{baseId}-{n++}";
            o.Prestations.Add(new Prestation
            {
                Id = id,
                Nom = nom,
                Duree = Nombre(p.Duree, 15, 720, $"Durée de « {nom} » (minutes)"),
                Prix = Texte(p.Prix, 20, "Prix"),
                Actif = p.Actif,
            });
        }
        if (!o.Prestations.Any(p => p.Actif)) throw new ErreurMetier("Au moins une prestation doit rester active.");

        // Horaires de la semaine
        for (var j = 1; j <= 7; j++)
        {
            var h = r.Horaires is not null && r.Horaires.TryGetValue(j.ToString(), out var x) ? x : new();
            if (h.Count > 3) throw new ErreurMetier("Horaires invalides.");
            o.Horaires[j.ToString()] = h.Select(p => Plage(p, "Horaires")).ToList();
        }
        o.DebutSeulement = r.DebutSeulement;

        // Fermetures, créneaux bloqués, ouvertures exceptionnelles (dates passées retirées)
        o.JoursFermes = (r.JoursFermes ?? new())
            .Where(d => Dates.TryDate(d, out _) && string.CompareOrdinal(d, auj) >= 0)
            .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (o.JoursFermes.Count > 200) throw new ErreurMetier("Trop de jours fermés.");

        foreach (var (date, heures) in (r.CreneauxBloques ?? new()).OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!Dates.TryDate(date, out _) || string.CompareOrdinal(date, auj) < 0) continue;
            var hs = (heures ?? new()).Where(Dates.EstHeure).Distinct().OrderBy(h => h, StringComparer.Ordinal).Take(48).ToList();
            if (hs.Count > 0) o.CreneauxBloques[date] = hs;
        }
        if (o.CreneauxBloques.Count > 200) throw new ErreurMetier("Trop de créneaux bloqués.");

        foreach (var (date, plages) in (r.OuverturesException ?? new()).OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!Dates.TryDate(date, out _) || string.CompareOrdinal(date, auj) < 0) continue;
            var ps = (plages ?? new()).Select(p => Plage(p, $"Ouverture du {date}")).Take(3).ToList();
            if (ps.Count > 0) o.OuverturesException[date] = ps;
        }

        // Règles
        o.PasCreneaux = Nombre(r.PasCreneaux, 15, 240, "Intervalle entre créneaux (minutes)");
        o.DelaiMinHeures = Nombre(r.DelaiMinHeures, 0, 720, "Délai minimum (heures)");
        o.JoursMaxALAvance = Nombre(r.JoursMaxALAvance, 1, 365, "Réservation possible jusqu'à (jours)");
        o.RappelMinutes = Nombre(r.RappelMinutes, 0, 2880, "Rappel dans l'agenda (minutes)");
        o.ExpirationAttenteHeures = Nombre(r.ExpirationAttenteHeures, 1, 336, "Délai pour envoyer l'acompte (heures)");
        o.MaxDemandesParEmail = Nombre(r.MaxDemandesParEmail, 1, 20, "Demandes en attente par email");

        // Paiement, lieu
        o.Acompte = Texte(r.Acompte, 20, "Acompte");
        o.SupplementDomicile = Texte(r.SupplementDomicile, 20, "Supplément à domicile");
        o.WeroNumero = Texte(r.WeroNumero, 30, "Numéro Wero");
        o.AdresseSalon = Texte(r.AdresseSalon, 200, "Adresse");
        if (o.Acompte.Length == 0 || o.WeroNumero.Length == 0)
            throw new ErreurMetier("L'acompte et le numéro Wero sont obligatoires.");

        // Boutiques
        if (r.Boutiques is null || r.Boutiques.Count > 15) throw new ErreurMetier("15 boutiques maximum.");
        o.Boutiques = r.Boutiques
            .Select(b => new Boutique
            {
                Nom = Texte(b?.Nom, 60, "Nom de boutique"),
                Adresse = Texte(b?.Adresse, 150, "Adresse de boutique"),
                Tel = Texte(b?.Tel, 30, "Téléphone de boutique"),
            })
            .Where(b => b.Nom.Length > 0).ToList();

        if (JsonSerializer.Serialize(o).Length > 200_000) throw new ErreurMetier("Trop de réglages enregistrés.");
        return o;
    }

    /// <summary>« Fulani Braids à l'été » → « fulani-braids-a-l-ete ».</summary>
    public static string Slug(string nom)
    {
        var sb = new StringBuilder();
        foreach (var c in nom.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var s = Regex.Replace(sb.ToString().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 30) s = s[..30].Trim('-');
        return s.Length == 0 ? "presta" : s;
    }

    private static string Texte(string? v, int max, string nom)
    {
        var t = (v ?? "").Trim();
        if (t.Length > max) throw new ErreurMetier($"{nom} : {max} caractères maximum.");
        return t;
    }

    private static int Nombre(int v, int min, int max, string nom) =>
        v < min || v > max ? throw new ErreurMetier($"{nom} : entre {min} et {max}.") : v;

    private static List<string> Plage(List<string>? p, string nom)
    {
        if (p is null || p.Count != 2 || !Dates.EstHeure(p[0]) || !Dates.EstHeure(p[1]) || string.CompareOrdinal(p[0], p[1]) > 0)
            throw new ErreurMetier($"{nom} : horaires invalides (début avant fin, format HH:MM).");
        return new List<string> { p[0], p[1] };
    }
}
