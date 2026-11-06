using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Naboo.Api.Integrations;

// ============================== AGENDA ==============================

public interface ICalendrier
{
    Task AjouterAsync(string uid, string ics, CancellationToken ct = default);
    Task SupprimerAsync(string uid, CancellationToken ct = default);
}

/// <summary>Agenda iCloud via CalDAV (identifiant Apple + mot de passe pour app).</summary>
public sealed class CalendrierICloud : ICalendrier
{
    private readonly HttpClient _http;
    private readonly string _urlCalendrier;

    public CalendrierICloud(HttpClient http, string identifiant, string motDePasseApp, string urlCalendrier)
    {
        _http = http;
        _urlCalendrier = urlCalendrier.EndsWith('/') ? urlCalendrier : urlCalendrier + "/";
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(identifiant + ":" + motDePasseApp));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
    }

    public async Task AjouterAsync(string uid, string ics, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, Url(uid))
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
        };
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var rep = await _http.SendAsync(req, ct);
        if (rep.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent))
            throw new InvalidOperationException($"iCloud a refusé la création ({(int)rep.StatusCode}).");
    }

    public async Task SupprimerAsync(string uid, CancellationToken ct = default)
    {
        using var rep = await _http.DeleteAsync(Url(uid), ct);
        if (rep.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound))
            throw new InvalidOperationException($"iCloud a refusé la suppression ({(int)rep.StatusCode}).");
    }

    private string Url(string uid)
    {
        var nom = new string(uid.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return _urlCalendrier + Uri.EscapeDataString(nom) + ".ics";
    }
}

/// <summary>Agenda factice (développement, tests, ou iCloud non configuré) : journalise seulement.</summary>
public sealed class CalendrierJournal : ICalendrier
{
    private readonly ILogger<CalendrierJournal> _log;
    public List<string> Evenements { get; } = new();
    public CalendrierJournal(ILogger<CalendrierJournal> log) => _log = log;

    public Task AjouterAsync(string uid, string ics, CancellationToken ct = default)
    {
        lock (Evenements) Evenements.Add("+" + uid);
        _log.LogInformation("Agenda (simulé) : ajout {Uid}", uid);
        return Task.CompletedTask;
    }

    public Task SupprimerAsync(string uid, CancellationToken ct = default)
    {
        lock (Evenements) Evenements.Add("-" + uid);
        _log.LogInformation("Agenda (simulé) : suppression {Uid}", uid);
        return Task.CompletedTask;
    }
}

// ============================== EMAILS ==============================

public sealed record Email(string A, string Sujet, string Html, string? RepondreA = null, byte[]? Ics = null);

public interface IEnvoiEmail
{
    Task EnvoyerAsync(Email email, CancellationToken ct = default);
}

public sealed record ConfigSmtp(string Hote, int Port, string Utilisateur, string MotDePasse, string Expediteur, string NomExpediteur);

/// <summary>Envoi par SMTP (ex. Gmail ou Outlook avec un mot de passe d'application).</summary>
public sealed class EnvoiSmtp : IEnvoiEmail
{
    private readonly ConfigSmtp _c;
    public EnvoiSmtp(ConfigSmtp c) => _c = c;

    public async Task EnvoyerAsync(Email email, CancellationToken ct = default)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(_c.NomExpediteur, _c.Expediteur));
        m.To.Add(MailboxAddress.Parse(email.A));
        if (!string.IsNullOrEmpty(email.RepondreA)) m.ReplyTo.Add(MailboxAddress.Parse(email.RepondreA));
        m.Subject = email.Sujet;
        var corps = new BodyBuilder { HtmlBody = email.Html };
        if (email.Ics is not null) corps.Attachments.Add("rendez-vous.ics", email.Ics, new ContentType("text", "calendar"));
        m.Body = corps.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_c.Hote, _c.Port, _c.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(_c.Utilisateur, _c.MotDePasse, ct);
        await smtp.SendAsync(m, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

/// <summary>Emails factices (développement, tests, ou SMTP non configuré) : journalise seulement.</summary>
public sealed class EnvoiJournal : IEnvoiEmail
{
    private readonly ILogger<EnvoiJournal> _log;
    public List<Email> Envoyes { get; } = new();
    public EnvoiJournal(ILogger<EnvoiJournal> log) => _log = log;

    public Task EnvoyerAsync(Email email, CancellationToken ct = default)
    {
        lock (Envoyes) Envoyes.Add(email);
        _log.LogInformation("Email (simulé) à {A} : {Sujet}", email.A, email.Sujet);
        return Task.CompletedTask;
    }
}
