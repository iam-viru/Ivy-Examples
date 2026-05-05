namespace TendrilDeploy.Services;

using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

/// <summary>
/// Builds Ivy BasicAuth env values for deployment: Argon2id hashes with pepper (Ivy <c>BasicAuthProvider</c> verifies with
/// <see cref="Argon2.Verify"/> using UTF-8 password bytes and <see cref="Argon2Config.Secret"/>).
/// </summary>
public static class TendrilBasicAuthBootstrap
{
    public const int MinPasswordLength = 8;

    /// <summary>Returns env-ready values for BasicAuth:Users, BasicAuth:HashSecret, BasicAuth:JwtSecret.</summary>
    public static (string UsersValue, string HashSecretBase64, string JwtSecretBase64) BuildSecrets(string username, string password)
    {
        var u = (username ?? "").Trim();
        var p = password ?? "";
        if (u.Length == 0)
            throw new ArgumentException("Username is required.");
        if (u.IndexOfAny([';', ':']) >= 0)
            throw new ArgumentException("Username cannot contain ':' or ';'.");
        if (p.Length < MinPasswordLength)
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.");

        Span<byte> hashKey = stackalloc byte[32];
        Span<byte> jwtKey = stackalloc byte[32];
        RandomNumberGenerator.Fill(hashKey);
        RandomNumberGenerator.Fill(jwtKey);
        var hashSecretB64 = Convert.ToBase64String(hashKey);
        var jwtSecretB64 = Convert.ToBase64String(jwtKey);

        var hashSecretBytes = Convert.FromBase64String(hashSecretB64);
        var pwdHash = Argon2.Hash(
            Encoding.UTF8.GetBytes(p),
            hashSecretBytes,
            timeCost: 3,
            memoryCost: 65536,
            parallelism: 1,
            type: Argon2Type.HybridAddressing,
            hashLength: 32);

        var usersValue = $"{u}:{pwdHash}";
        return (usersValue, hashSecretB64, jwtSecretB64);
    }
}
