namespace Naboo.Api.Domain;

/// <summary>Calcul des créneaux libres (logique pure, sans accès aux données : facile à tester).</summary>
public static class Creneaux
{
    public static Prestation? PrestationActive(Reglages r, string? id) =>
        r.Prestations.FirstOrDefault(p => p.Id == id && p.Actif);

    /// <summary>Plages d'ouverture d'un jour : ouverture exceptionnelle, sinon horaires de la semaine.</summary>
    public static List<List<string>> Plages(Reglages r, DateOnly date)
    {
        var iso = Dates.Iso(date);
        if (r.JoursFermes.Contains(iso)) return new();
        if (r.OuverturesException.TryGetValue(iso, out var exception)) return exception;
        return r.Horaires.TryGetValue(Dates.JourIso(date).ToString(), out var h) ? h : new();
    }

    /// <summary>Heures de début libres (HH:mm, heure de Paris) pour une date et une prestation.</summary>
    public static List<string> Libres(Reglages r, string? dateStr, string? prestationId,
        IEnumerable<Occupation> occupes, DateTimeOffset maintenant)
    {
        var resultat = new List<string>();
        var presta = PrestationActive(r, prestationId);
        if (presta is null || !Dates.TryDate(dateStr, out var date)) return resultat;

        var bloques = r.CreneauxBloques.TryGetValue(dateStr!, out var b) ? b : new List<string>();
        var minDebut = maintenant.AddHours(r.DelaiMinHeures);
        var maxDebut = maintenant.AddDays(r.JoursMaxALAvance);
        var duree = TimeSpan.FromMinutes(presta.Duree);
        var pas = Math.Max(15, r.PasCreneaux);
        var liste = occupes.ToList();

        foreach (var plage in Plages(r, date))
        {
            if (plage.Count != 2 || !Dates.TryHeure(plage[0], out var ouverture) || !Dates.TryHeure(plage[1], out var fermeture)) continue;
            var finPlage = Dates.VersUtc(date, fermeture);
            var dernierDebut = r.DebutSeulement ? finPlage : finPlage - duree;
            for (var t = Dates.VersUtc(date, ouverture); t <= dernierDebut; t = t.AddMinutes(pas))
            {
                if (t < minDebut || t > maxDebut) continue;
                var hhmm = Dates.HeureParis(t);
                if (bloques.Contains(hhmm)) continue;
                if (liste.Any(o => t < o.Fin && t + duree > o.Debut)) continue;
                resultat.Add(hhmm);
            }
        }
        return resultat;
    }

    /// <summary>Rendez-vous qui bloquent un créneau : validés, ou en attente d'acompte depuis moins de N heures.</summary>
    public static List<Occupation> Occupations(IEnumerable<RendezVous> rdvs, Reglages r, DateTimeOffset maintenant)
    {
        var limite = maintenant.AddHours(-r.ExpirationAttenteHeures);
        return rdvs
            .Where(x => x.Statut == Statuts.Valide || (x.Statut == Statuts.Attente && x.CreeLe >= limite))
            .Select(x => new Occupation(x.Id, x.Debut, x.Fin))
            .ToList();
    }
}
