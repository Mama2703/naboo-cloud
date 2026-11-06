using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Cosmos;
using Naboo.Api.Domain;

namespace Naboo.Api.Stockage;

/// <summary>Accès aux données. Deux implémentations : Cosmos DB (production) et mémoire (tests, développement).</summary>
public interface IStockage
{
    Task<Reglages?> LireReglagesAsync(CancellationToken ct = default);
    Task EcrireReglagesAsync(Reglages r, CancellationToken ct = default);
    Task<string?> LireHashAdminAsync(CancellationToken ct = default);
    Task EcrireHashAdminAsync(string hash, CancellationToken ct = default);

    Task<List<RendezVous>> ListerRendezVousAsync(CancellationToken ct = default);
    Task<RendezVous?> LireRendezVousAsync(string id, CancellationToken ct = default);
    Task EcrireRendezVousAsync(RendezVous rdv, CancellationToken ct = default);
    Task SupprimerRendezVousAsync(string id, CancellationToken ct = default);

    /// <param name="champ">"v" ou "r".</param>
    Task IncrementerAsync(string id, string champ, CancellationToken ct = default);
    Task<List<CompteurStat>> LireStatsAsync(CancellationToken ct = default);
}

/// <summary>Document « securite » du conteneur parametres.</summary>
public sealed class DocSecurite
{
    public string Id { get; set; } = "securite";
    public string HashAdmin { get; set; } = "";
}

public sealed class StockageMemoire : IStockage
{
    private Reglages? _reglages;
    private string? _hash;
    private readonly ConcurrentDictionary<string, RendezVous> _rdv = new();
    private readonly ConcurrentDictionary<string, CompteurStat> _stats = new();
    private readonly object _verrou = new();

    public Task<Reglages?> LireReglagesAsync(CancellationToken ct = default) => Task.FromResult(_reglages);
    public Task EcrireReglagesAsync(Reglages r, CancellationToken ct = default) { _reglages = r; return Task.CompletedTask; }
    public Task<string?> LireHashAdminAsync(CancellationToken ct = default) => Task.FromResult(_hash);
    public Task EcrireHashAdminAsync(string hash, CancellationToken ct = default) { _hash = hash; return Task.CompletedTask; }

    public Task<List<RendezVous>> ListerRendezVousAsync(CancellationToken ct = default) => Task.FromResult(_rdv.Values.ToList());
    public Task<RendezVous?> LireRendezVousAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_rdv.TryGetValue(id, out var r) ? r : null);
    public Task EcrireRendezVousAsync(RendezVous rdv, CancellationToken ct = default) { _rdv[rdv.Id] = rdv; return Task.CompletedTask; }
    public Task SupprimerRendezVousAsync(string id, CancellationToken ct = default) { _rdv.TryRemove(id, out _); return Task.CompletedTask; }

    public Task IncrementerAsync(string id, string champ, CancellationToken ct = default)
    {
        lock (_verrou)
        {
            var c = _stats.GetOrAdd(id, k => new CompteurStat { Id = k });
            if (champ == "v") c.V++; else c.R++;
        }
        return Task.CompletedTask;
    }

    public Task<List<CompteurStat>> LireStatsAsync(CancellationToken ct = default) => Task.FromResult(_stats.Values.ToList());
}

/// <summary>
/// Cosmos DB for NoSQL (offre gratuite). Base « naboo », conteneurs « parametres », « rendezvous » et « stats »,
/// tous partitionnés par /id. Les conteneurs sont créés par Terraform, pas par l'application.
/// </summary>
public sealed class StockageCosmos : IStockage
{
    private readonly Container _parametres;
    private readonly Container _rendezVous;
    private readonly Container _stats;

    public StockageCosmos(CosmosClient client, string baseDeDonnees)
    {
        var db = client.GetDatabase(baseDeDonnees);
        _parametres = db.GetContainer("parametres");
        _rendezVous = db.GetContainer("rendezvous");
        _stats = db.GetContainer("stats");
    }

    public static CosmosClientOptions Options() => new()
    {
        SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
        ApplicationName = "naboo-api",
    };

    public Task<Reglages?> LireReglagesAsync(CancellationToken ct = default) => Lire<Reglages>(_parametres, "reglages", ct);

    public Task EcrireReglagesAsync(Reglages r, CancellationToken ct = default) =>
        _parametres.UpsertItemAsync(r, new PartitionKey(r.Id), cancellationToken: ct);

    public async Task<string?> LireHashAdminAsync(CancellationToken ct = default) =>
        (await Lire<DocSecurite>(_parametres, "securite", ct))?.HashAdmin;

    public Task EcrireHashAdminAsync(string hash, CancellationToken ct = default) =>
        _parametres.UpsertItemAsync(new DocSecurite { HashAdmin = hash }, new PartitionKey("securite"), cancellationToken: ct);

    public async Task<List<RendezVous>> ListerRendezVousAsync(CancellationToken ct = default) =>
        await Tous<RendezVous>(_rendezVous, ct);

    public Task<RendezVous?> LireRendezVousAsync(string id, CancellationToken ct = default) => Lire<RendezVous>(_rendezVous, id, ct);

    public Task EcrireRendezVousAsync(RendezVous rdv, CancellationToken ct = default) =>
        _rendezVous.UpsertItemAsync(rdv, new PartitionKey(rdv.Id), cancellationToken: ct);

    public async Task SupprimerRendezVousAsync(string id, CancellationToken ct = default)
    {
        try { await _rendezVous.DeleteItemAsync<RendezVous>(id, new PartitionKey(id), cancellationToken: ct); }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
    }

    /// <summary>Incrément atomique côté serveur (opération « patch »), avec création du compteur au premier passage.</summary>
    public async Task IncrementerAsync(string id, string champ, CancellationToken ct = default)
    {
        var operations = new[] { PatchOperation.Increment("/" + champ, 1) };
        for (var essai = 0; essai < 3; essai++)
        {
            try
            {
                await _stats.PatchItemAsync<CompteurStat>(id, new PartitionKey(id), operations, cancellationToken: ct);
                return;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                try
                {
                    var nouveau = new CompteurStat { Id = id, V = champ == "v" ? 1 : 0, R = champ == "r" ? 1 : 0 };
                    await _stats.CreateItemAsync(nouveau, new PartitionKey(id), cancellationToken: ct);
                    return;
                }
                catch (CosmosException c) when (c.StatusCode == HttpStatusCode.Conflict)
                {
                    // créé au même moment par une autre requête : on recommence le patch
                }
            }
        }
    }

    public async Task<List<CompteurStat>> LireStatsAsync(CancellationToken ct = default) => await Tous<CompteurStat>(_stats, ct);

    private static async Task<T?> Lire<T>(Container c, string id, CancellationToken ct) where T : class
    {
        try { return (await c.ReadItemAsync<T>(id, new PartitionKey(id), cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private static async Task<List<T>> Tous<T>(Container c, CancellationToken ct)
    {
        var resultat = new List<T>();
        using var iterateur = c.GetItemQueryIterator<T>(new QueryDefinition("SELECT * FROM c"));
        while (iterateur.HasMoreResults)
            resultat.AddRange(await iterateur.ReadNextAsync(ct));
        return resultat;
    }
}
