using System.Globalization;
using System.Text;

namespace Naboo.Api.Domain;

/// <summary>Génération d'événements iCalendar (RFC 5545) pour iCloud et pour la cliente.</summary>
public static class Ics
{
    /// <param name="pourProprietaire">true : événement de l'agenda de Naboo (coordonnées de la cliente, rappel).</param>
    public static string Construire(string uid, RendezVous rdv, Reglages r, bool pourProprietaire, DateTimeOffset maintenant)
    {
        var titre = pourProprietaire ? $"{rdv.Prestation} — {rdv.Nom}" : $"{rdv.Prestation} — {r.NomActivite}";
        var description = pourProprietaire
            ? string.Join("\n", new[]
            {
                rdv.Lieu == "domicile" ? $"À DOMICILE (+{r.SupplementDomicile}) : {rdv.Adresse}" : "",
                $"Cliente : {rdv.Nom}",
                $"Email : {rdv.Email}",
                rdv.Telephone.Length > 0 ? $"Tél : {rdv.Telephone}" : "",
                rdv.Message.Length > 0 ? $"Message : {rdv.Message}" : "",
            }.Where(l => l.Length > 0))
            : "Rendez-vous confirmé.";
        var lieu = rdv.Lieu == "domicile" && rdv.Adresse.Length > 0 ? rdv.Adresse : r.Lieu;

        var lignes = new List<string>
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//naboo//Prise de rendez-vous//FR", "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            "UID:" + uid,
            "DTSTAMP:" + Utc(maintenant),
            "DTSTART:" + Utc(rdv.Debut),
            "DTEND:" + Utc(rdv.Fin),
            "SUMMARY:" + Echapper(titre),
            "DESCRIPTION:" + Echapper(description),
        };
        if (lieu.Length > 0) lignes.Add("LOCATION:" + Echapper(lieu));
        if (pourProprietaire && r.RappelMinutes > 0)
        {
            lignes.AddRange(new[]
            {
                "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:" + Echapper(titre),
                $"TRIGGER:-PT{r.RappelMinutes}M", "END:VALARM",
            });
        }
        lignes.Add("END:VEVENT");
        lignes.Add("END:VCALENDAR");
        return string.Join("\r\n", lignes.Select(Plier)) + "\r\n";
    }

    public static string Utc(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Echapper(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    /// <summary>Plie les lignes à 75 octets UTF-8, sans couper un caractère.</summary>
    public static string Plier(string ligne)
    {
        var sb = new StringBuilder();
        var taille = 0;
        var e = StringInfo.GetTextElementEnumerator(ligne);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            var octets = Encoding.UTF8.GetByteCount(element);
            if (taille + octets > 75)
            {
                sb.Append("\r\n ");
                taille = 1;
            }
            sb.Append(element);
            taille += octets;
        }
        return sb.ToString();
    }
}
