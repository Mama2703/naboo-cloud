using System.Globalization;
using System.Text.RegularExpressions;

namespace Naboo.Api.Domain;

/// <summary>Dates et heures, toujours exprimées à l'heure de Paris pour l'affichage.</summary>
public static partial class Dates
{
    public static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private static readonly string[] Jours = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };
    private static readonly string[] Mois = { "janvier", "février", "mars", "avril", "mai", "juin", "juillet",
        "août", "septembre", "octobre", "novembre", "décembre" };

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex RegexHeure();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex RegexDate();

    public static bool EstHeure(string? s) => s is not null && RegexHeure().IsMatch(s);

    public static bool TryDate(string? s, out DateOnly d)
    {
        d = default;
        return s is not null && RegexDate().IsMatch(s)
            && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    public static bool TryHeure(string? s, out TimeOnly h)
    {
        h = default;
        return EstHeure(s) && TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out h);
    }

    /// <summary>Date + heure de Paris → instant UTC (gère l'heure d'été).</summary>
    public static DateTimeOffset VersUtc(DateOnly date, TimeOnly heure)
    {
        var local = date.ToDateTime(heure, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Paris.GetUtcOffset(local)).ToUniversalTime();
    }

    public static DateTime EnParis(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Paris).DateTime;

    public static DateOnly AujourdhuiParis(DateTimeOffset maintenant) => DateOnly.FromDateTime(EnParis(maintenant));

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string HeureParis(DateTimeOffset t) => EnParis(t).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Lundi = 1 … dimanche = 7.</summary>
    public static int JourIso(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    /// <summary>Ex. « samedi 7 novembre à 10h00 ».</summary>
    public static string Lisible(DateTimeOffset t)
    {
        var d = EnParis(t);
        return $"{Jours[(int)d.DayOfWeek]} {d.Day} {Mois[d.Month - 1]} à {d.Hour:00}h{d.Minute:00}";
    }

    /// <summary>Ex. « 07/11 » (pour le message Wero).</summary>
    public static string JourMois(DateTimeOffset t) => EnParis(t).ToString("dd/MM", CultureInfo.InvariantCulture);
}
