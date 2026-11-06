using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Azure.Identity;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Azure.Cosmos;
using Naboo.Api.Domain;
using Naboo.Api.Integrations;
using Naboo.Api.Pages;
using Naboo.Api.Securite;
using Naboo.Api.Services;
using Naboo.Api.Stockage;

var builder = WebApplication.CreateBuilder(args);
var s = builder.Services;

// ---------- Services (la configuration est lue au démarrage, variables d'environnement en production) ----------
s.AddSingleton(TimeProvider.System);
s.AddHttpClient();

s.AddSingleton<IStockage>(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    if (c["STOCKAGE"] != "cosmos") return new StockageMemoire();
    var connexion = c["COSMOS_CONNEXION"];
    var client = !string.IsNullOrEmpty(connexion)
        ? new CosmosClient(connexion, StockageCosmos.Options())
        : new CosmosClient(c["COSMOS_ENDPOINT"], new DefaultAzureCredential(), StockageCosmos.Options()); // identité managée : aucune clé
    return new StockageCosmos(client, c["COSMOS_BASE"] ?? "naboo");
});

s.AddSingleton<ICalendrier>(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    var url = c["ICLOUD_URL_CALENDRIER"];
    if (string.IsNullOrEmpty(url)) return ActivatorUtilities.CreateInstance<CalendrierJournal>(sp);
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("icloud");
    return new CalendrierICloud(http, c["ICLOUD_IDENTIFIANT"] ?? "", c["ICLOUD_MOT_DE_PASSE_APP"] ?? "", url);
});

s.AddSingleton<IEnvoiEmail>(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    if (string.IsNullOrEmpty(c["SMTP_HOTE"])) return ActivatorUtilities.CreateInstance<EnvoiJournal>(sp);
    return new EnvoiSmtp(new ConfigSmtp(c["SMTP_HOTE"]!, int.TryParse(c["SMTP_PORT"], out var p) ? p : 587,
        c["SMTP_UTILISATEUR"] ?? "", c["SMTP_MOT_DE_PASSE"] ?? "", c["EMAIL_EXPEDITEUR"] ?? c["SMTP_UTILISATEUR"] ?? "",
        c["NOM_EXPEDITEUR"] ?? "Naboo"));
});

s.AddSingleton(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    return new OptionsSite(c["EMAIL_PROPRIETAIRE"] ?? "proprietaire@exemple.fr", c["URL_API"] ?? "http://localhost:8080");
});

s.AddSingleton(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    var secret = c["SECRET_SESSION"];
    if (string.IsNullOrEmpty(secret))
    {
        if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
            throw new InvalidOperationException("SECRET_SESSION manquant (au moins 32 caractères).");
        secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); // développement : jetons perdus au redémarrage
    }
    return new Jetons(Encoding.UTF8.GetBytes(secret), sp.GetRequiredService<TimeProvider>());
});
s.AddSingleton<GardeConnexion>();
s.AddSingleton<ServiceRendezVous>();

// CORS : seul le site (Static Web Apps) peut appeler l'API depuis un navigateur
s.AddCors();
s.AddOptions<CorsOptions>().Configure<IConfiguration>((o, c) =>
{
    var origines = (c["CORS_ORIGINES"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    o.AddDefaultPolicy(p => p.WithOrigins(origines).WithMethods("GET", "POST", "PUT").WithHeaders("Content-Type", "Authorization"));
});

// Limitation de débit par adresse IP
s.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("connexion", ctx => RateLimitPartition.GetFixedWindowLimiter(Ip(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(Ip(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("demande", ctx => RateLimitPartition.GetFixedWindowLimiter(Ip(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
});

s.AddProblemDetails();

var app = builder.Build();

// Derrière l'entrée HTTPS d'Azure Container Apps : récupérer la vraie adresse IP du client
var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Erreurs métier → { ok: false, erreur } avec le bon code HTTP ; erreurs inattendues → 500 sans détail technique
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ErreurMetier e)
    {
        ctx.Response.StatusCode = e.Statut;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, erreur = e.Message });
    }
    catch (BadHttpRequestException)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, erreur = "Requête invalide." });
    }
    catch (Exception e) when (!ctx.Response.HasStarted)
    {
        app.Logger.LogError(e, "Erreur inattendue sur {Chemin}", ctx.Request.Path);
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, erreur = "Erreur du serveur. Réessayez dans un instant." });
    }
});

app.UseCors();
app.UseRateLimiter();

// Sécurité des réponses
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

// ============================== API PUBLIQUE ==============================

app.MapGet("/sante", () => Results.Ok(new { ok = true }));

var api = app.MapGroup("/api").RequireRateLimiting("public");

api.MapGet("/config", async (ServiceRendezVous svc, CancellationToken ct) =>
{
    await svc.CompterVisiteAsync(ct);
    return Results.Ok(new { ok = true, config = ServiceRendezVous.ConfigPublique(await svc.ReglagesAsync(ct)) });
});

api.MapGet("/creneaux", async (string? date, string? prestation, ServiceRendezVous svc, CancellationToken ct) =>
    Results.Ok(new { ok = true, creneaux = await svc.CreneauxAsync(date, prestation, ct) }));

api.MapGet("/vue", async (string? presta, ServiceRendezVous svc, CancellationToken ct) =>
{
    await svc.CompterVueAsync(presta, ct);
    return Results.Ok(new { ok = true });
});

api.MapPost("/rendezvous", async (DemandeRdv d, ServiceRendezVous svc, CancellationToken ct) =>
    Results.Ok(new { ok = true, id = await svc.DemanderAsync(d, ct) })).RequireRateLimiting("demande");

// ============================== PAGE D'EXAMEN (lien de l'email) ==============================

app.MapGet("/rdv/examiner", async (string? id, string? token, ServiceRendezVous svc, CancellationToken ct) =>
    Results.Content(PageExamen.Html(await svc.LirePourExamenAsync(id, token, ct), await svc.ReglagesAsync(ct)),
        "text/html; charset=utf-8"));

app.MapPost("/rdv/decision", async (DecisionExamen d, ServiceRendezVous svc, CancellationToken ct) =>
{
    var rdv = await svc.LirePourExamenAsync(d.Id, d.Token, ct) ?? throw new ErreurMetier("Lien invalide.", 403);
    if (d.Decision is not ("valider" or "refuser")) throw new ErreurMetier("Décision inconnue.");
    return Results.Ok(new { ok = true, message = await svc.ActionAsync(rdv.Id, d.Decision, ct) });
}).RequireRateLimiting("connexion");

// ============================== ESPACE DE GESTION ==============================

app.MapPost("/api/admin/connexion", async (Connexion d, IStockage db, IConfiguration conf, Jetons jetons, GardeConnexion garde, CancellationToken ct) =>
{
    if (garde.EstBloque()) throw new ErreurMetier("Trop d'essais. Réessaie dans 15 minutes.", 429);
    var hash = await db.LireHashAdminAsync(ct);
    var initial = conf["ADMIN_MOT_DE_PASSE"] ?? "";
    var ok = !string.IsNullOrEmpty(hash)
        ? MotDePasse.Verifier(d.MotDePasse ?? "", hash)
        : initial.Length >= 10 && MotDePasse.Egal(d.MotDePasse ?? "", initial);
    if (!ok)
    {
        garde.Echec();
        await Task.Delay(500, ct);
        throw new ErreurMetier("Mot de passe incorrect.", 401);
    }
    garde.Reussite();
    return Results.Ok(new { ok = true, jeton = jetons.Emettre() });
}).RequireRateLimiting("connexion");

var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
{
    var entete = ctx.HttpContext.Request.Headers.Authorization.ToString();
    var jeton = entete.StartsWith("Bearer ", StringComparison.Ordinal) ? entete[7..] : null;
    if (!ctx.HttpContext.RequestServices.GetRequiredService<Jetons>().EstValide(jeton))
        return Results.Json(new { ok = false, session = false, erreur = "Session expirée, reconnecte-toi." }, statusCode: 401);
    return await next(ctx);
}).RequireRateLimiting("public");

admin.MapGet("/etat", async (ServiceRendezVous svc, CancellationToken ct) =>
    Results.Ok(new { ok = true, reglages = await svc.ReglagesAsync(ct), rdv = await svc.ListeAdminAsync(ct), urlFeuille = "" }));

admin.MapPut("/reglages", async (EnvoiReglages d, ServiceRendezVous svc, CancellationToken ct) =>
    Results.Ok(new { ok = true, reglages = await svc.EnregistrerReglagesAsync(d.Reglages!, ct), message = "Réglages enregistrés ✅ Le site est déjà à jour." }));

admin.MapPost("/rendezvous/{id}/{quoi}", async (string id, string quoi, ServiceRendezVous svc, CancellationToken ct) =>
{
    if (quoi is not ("valider" or "refuser" or "annuler" or "supprimer")) throw new ErreurMetier("Action inconnue.");
    var message = await svc.ActionAsync(id, quoi, ct);
    return Results.Ok(new { ok = true, message, rdv = await svc.ListeAdminAsync(ct) });
});

admin.MapGet("/stats", async (ServiceRendezVous svc, CancellationToken ct) =>
    Results.Ok(new { ok = true, stats = await svc.StatsAsync(ct) }));

admin.MapPost("/motdepasse", async (ChangementMotDePasse d, IStockage db, IConfiguration conf, CancellationToken ct) =>
{
    var hash = await db.LireHashAdminAsync(ct);
    var actuelOk = !string.IsNullOrEmpty(hash)
        ? MotDePasse.Verifier(d.Actuel ?? "", hash)
        : MotDePasse.Egal(d.Actuel ?? "", conf["ADMIN_MOT_DE_PASSE"] ?? "");
    if (!actuelOk) throw new ErreurMetier("Mot de passe actuel incorrect.", 403);
    if ((d.Nouveau ?? "").Length < 10) throw new ErreurMetier("Le nouveau mot de passe doit faire au moins 10 caractères.");
    await db.EcrireHashAdminAsync(MotDePasse.Hacher(d.Nouveau!), ct);
    return Results.Ok(new { ok = true, message = "Mot de passe modifié ✅" });
});

admin.MapPost("/deconnexion", () => Results.Ok(new { ok = true }));

app.Run();

static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "inconnue";

public sealed record DecisionExamen(string? Id, string? Token, string? Decision);
public sealed record Connexion(string? MotDePasse);
public sealed record EnvoiReglages(Reglages? Reglages);
public sealed record ChangementMotDePasse(string? Actuel, string? Nouveau);

/// <summary>Rend la classe Program accessible aux tests d'intégration.</summary>
public partial class Program { }
