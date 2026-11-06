using System.Text.Json.Serialization;

namespace Naboo.Api.Domain;

/// <summary>Une prestation proposée (durée en minutes).</summary>
public sealed class Prestation
{
    public string Id { get; set; } = "";
    public string Nom { get; set; } = "";
    public int Duree { get; set; }
    public string Prix { get; set; } = "";
    public bool Actif { get; set; } = true;
}

public sealed class Boutique
{
    public string Nom { get; set; } = "";
    public string Adresse { get; set; } = "";
    public string Tel { get; set; } = "";
}

/// <summary>
/// Réglages modifiables depuis l'espace de gestion.
/// Côté API (System.Text.Json) les noms sont en MAJUSCULES pour rester compatibles avec admin.html ;
/// côté Cosmos DB (Newtonsoft) ils sont stockés en camelCase.
/// </summary>
public sealed class Reglages
{
    [JsonIgnore] public string Id { get; set; } = "reglages";
    [JsonIgnore] public string NomActivite { get; set; } = "Naboo";
    [JsonIgnore] public string Lieu { get; set; } = "Poitiers (86000)";

    [JsonPropertyName("PRESTATIONS")] public List<Prestation> Prestations { get; set; } = new();

    /// <summary>Clé "1" (lundi) à "7" (dimanche) ; chaque plage = [début, fin] au format HH:mm.</summary>
    [JsonPropertyName("HORAIRES")] public Dictionary<string, List<List<string>>> Horaires { get; set; } = new();

    /// <summary>true : la fin de plage est la dernière heure de début possible.</summary>
    [JsonPropertyName("DEBUT_SEULEMENT")] public bool DebutSeulement { get; set; } = true;

    [JsonPropertyName("ACOMPTE")] public string Acompte { get; set; } = "";
    [JsonPropertyName("SUPPLEMENT_DOMICILE")] public string SupplementDomicile { get; set; } = "";
    [JsonPropertyName("WERO_NUMERO")] public string WeroNumero { get; set; } = "";
    [JsonPropertyName("JOURS_FERMES")] public List<string> JoursFermes { get; set; } = new();
    [JsonPropertyName("CRENEAUX_BLOQUES")] public Dictionary<string, List<string>> CreneauxBloques { get; set; } = new();
    [JsonPropertyName("OUVERTURES_EXCEPTION")] public Dictionary<string, List<List<string>>> OuverturesException { get; set; } = new();
    [JsonPropertyName("PAS_CRENEAUX")] public int PasCreneaux { get; set; } = 60;
    [JsonPropertyName("DELAI_MIN_HEURES")] public int DelaiMinHeures { get; set; } = 48;
    [JsonPropertyName("JOURS_MAX_A_L_AVANCE")] public int JoursMaxALAvance { get; set; } = 45;
    [JsonPropertyName("RAPPEL_MINUTES")] public int RappelMinutes { get; set; } = 60;
    [JsonPropertyName("ADRESSE_SALON")] public string AdresseSalon { get; set; } = "";
    [JsonPropertyName("EXPIRATION_ATTENTE_HEURES")] public int ExpirationAttenteHeures { get; set; } = 24;
    [JsonPropertyName("MAX_DEMANDES_PAR_EMAIL")] public int MaxDemandesParEmail { get; set; } = 2;
    [JsonPropertyName("BOUTIQUES")] public List<Boutique> Boutiques { get; set; } = new();

    public static Reglages ParDefaut() => new()
    {
        Prestations = new()
        {
            new() { Id = "braids", Nom = "Braids", Duree = 300, Prix = "40 €" },
            new() { Id = "crochet-locks", Nom = "Crochet Locks", Duree = 180, Prix = "45 €" },
            new() { Id = "twist", Nom = "Twist", Duree = 240, Prix = "45 €" },
            new() { Id = "nattes-collees", Nom = "Nattes Collées", Duree = 120, Prix = "30 €" },
            new() { Id = "tissage-ouvert", Nom = "Tissage Ouvert", Duree = 180, Prix = "50 €" },
        },
        Horaires = new()
        {
            ["1"] = new(), ["2"] = new(), ["3"] = new(), ["4"] = new(), ["5"] = new(),
            ["6"] = new() { new() { "09:00", "16:00" } },
            ["7"] = new() { new() { "09:00", "16:00" } },
        },
        Acompte = "10 €",
        SupplementDomicile = "15 €",
        WeroNumero = "07 52 34 16 52",
        Boutiques = new()
        {
            new() { Nom = "Bel'Ivoire", Adresse = "11 place Jean de Berry, 86000 Poitiers", Tel = "05 49 61 26 37" },
            new() { Nom = "Ekitrade", Adresse = "5 boulevard Jeanne d'Arc, 86000 Poitiers", Tel = "05 49 46 64 25" },
        },
    };
}

public static class Statuts
{
    public const string Attente = "en_attente";
    public const string Valide = "valide";
    public const string Refuse = "refuse";
    public const string Annule = "annule";

    public static string Libelle(string s) => s switch
    {
        Attente => "En attente",
        Valide => "Validé",
        Refuse => "Refusé",
        Annule => "Annulé",
        _ => s,
    };
}

/// <summary>Un rendez-vous (stocké dans le conteneur Cosmos « rendezvous »).</summary>
public sealed class RendezVous
{
    public string Id { get; set; } = "";
    /// <summary>Secret du lien « Examiner la demande » envoyé par email (jamais renvoyé par l'API d'administration).</summary>
    public string Token { get; set; } = "";
    public DateTimeOffset CreeLe { get; set; }
    public string Statut { get; set; } = Statuts.Attente;
    public string PrestationId { get; set; } = "";
    public string Prestation { get; set; } = "";
    public DateTimeOffset Debut { get; set; }
    public DateTimeOffset Fin { get; set; }
    public string Nom { get; set; } = "";
    public string Email { get; set; } = "";
    public string Telephone { get; set; } = "";
    public string Message { get; set; } = "";
    public string UidAgenda { get; set; } = "";
    /// <summary>"salon" ou "domicile".</summary>
    public string Lieu { get; set; } = "salon";
    public string Adresse { get; set; } = "";
}

/// <summary>Compteur de statistiques anonymes (conteneur « stats »). Id = "jour:yyyy-MM-dd" ou "presta:{id}".</summary>
public sealed class CompteurStat
{
    public string Id { get; set; } = "";
    /// <summary>Visites (jour) ou fois où la prestation a été choisie (presta).</summary>
    public int V { get; set; }
    /// <summary>Demandes de rendez-vous.</summary>
    public int R { get; set; }
}

/// <summary>Occupation d'un créneau par un rendez-vous existant.</summary>
public sealed record Occupation(string Id, DateTimeOffset Debut, DateTimeOffset Fin);

public sealed class ErreurMetier : Exception
{
    public int Statut { get; }
    public ErreurMetier(string message, int statut = 400) : base(message) => Statut = statut;
}
