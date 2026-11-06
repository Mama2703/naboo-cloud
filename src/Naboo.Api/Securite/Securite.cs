using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Naboo.Api.Securite;

/// <summary>Hachage de mot de passe PBKDF2-SHA256 (sel aléatoire, 210 000 itérations, recommandation OWASP).</summary>
public static class MotDePasse
{
    private const int Iterations = 210_000;

    public static string Hacher(string motDePasse)
    {
        var sel = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(motDePasse, sel, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(sel)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verifier(string motDePasse, string? stocke)
    {
        if (string.IsNullOrEmpty(stocke)) return false;
        var parties = stocke.Split('$');
        if (parties.Length != 4 || parties[0] != "pbkdf2-sha256" || !int.TryParse(parties[1], out var iterations)) return false;
        try
        {
            var sel = Convert.FromBase64String(parties[2]);
            var attendu = Convert.FromBase64String(parties[3]);
            var calcule = Rfc2898DeriveBytes.Pbkdf2(motDePasse, sel, iterations, HashAlgorithmName.SHA256, attendu.Length);
            return CryptographicOperations.FixedTimeEquals(calcule, attendu);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Comparaison en temps constant de deux textes (ex. mot de passe initial en variable d'environnement).</summary>
    public static bool Egal(string a, string b) =>
        a.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

/// <summary>
/// Jetons de session sans état, signés HMAC-SHA256 : « expiration.nonce.signature ».
/// Ils survivent aux redémarrages du conteneur (mise à l'échelle à zéro) tant que la clé ne change pas.
/// </summary>
public sealed class Jetons
{
    private readonly byte[] _cle;
    private readonly TimeProvider _temps;
    public static readonly TimeSpan Duree = TimeSpan.FromHours(6);

    public Jetons(byte[] cle, TimeProvider temps)
    {
        if (cle.Length < 32) throw new ArgumentException("La clé de session doit faire au moins 32 octets.");
        _cle = cle;
        _temps = temps;
    }

    public string Emettre()
    {
        var expiration = _temps.GetUtcNow().Add(Duree).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var charge = expiration + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        return charge + "." + Signer(charge);
    }

    public bool EstValide(string? jeton)
    {
        if (string.IsNullOrEmpty(jeton)) return false;
        var parties = jeton.Split('.');
        if (parties.Length != 3 || !long.TryParse(parties[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expiration)) return false;
        var attendu = Encoding.ASCII.GetBytes(Signer(parties[0] + "." + parties[1]));
        var recu = Encoding.ASCII.GetBytes(parties[2]);
        return CryptographicOperations.FixedTimeEquals(attendu, recu)
            && expiration > _temps.GetUtcNow().ToUnixTimeSeconds();
    }

    private string Signer(string charge) => Convert.ToHexString(HMACSHA256.HashData(_cle, Encoding.UTF8.GetBytes(charge)));
}

/// <summary>
/// Protection contre les essais de mot de passe en série : après 8 échecs en 15 minutes,
/// la connexion est bloquée 15 minutes (en plus de la limitation de débit par adresse IP).
/// </summary>
public sealed class GardeConnexion
{
    private readonly TimeProvider _temps;
    private readonly ConcurrentQueue<DateTimeOffset> _echecs = new();
    public const int EssaisMax = 8;
    public static readonly TimeSpan Fenetre = TimeSpan.FromMinutes(15);

    public GardeConnexion(TimeProvider temps) => _temps = temps;

    public bool EstBloque()
    {
        Purger();
        return _echecs.Count >= EssaisMax;
    }

    public void Echec() => _echecs.Enqueue(_temps.GetUtcNow());

    public void Reussite() => _echecs.Clear();

    private void Purger()
    {
        var limite = _temps.GetUtcNow() - Fenetre;
        while (_echecs.TryPeek(out var t) && t < limite) _echecs.TryDequeue(out _);
    }
}
