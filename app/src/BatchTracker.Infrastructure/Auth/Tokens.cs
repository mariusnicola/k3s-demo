using System.Security.Claims;
using System.Security.Cryptography;
using BatchTracker.Application;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BatchTracker.Infrastructure;

/// <summary>Only the API holds the private key, so a service that checks tokens cannot mint one.</summary>
public sealed class TokenIssuer(string privateKey, TimeProvider time)
{
    public const string Issuer = "batch-tracker";
    public const string PermissionClaim = "perm";
    public const string FarmClaim = "farm";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly ECDsaSecurityKey signingKey = TokenKeys.Private(privateKey);

    public string Issue(Caller caller)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, caller.User) };
        claims.AddRange(caller.Permissions.Select(p => new Claim(PermissionClaim, p)));
        claims.AddRange(caller.Farms.Select(f => new Claim(FarmClaim, f)));

        var now = time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = now + Lifetime,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256)
        });
    }
}

/// <summary>Keys travel as one line of base64, PKCS#8 private and SubjectPublicKeyInfo public, so they fit a .env file.</summary>
public static class TokenKeys
{
    private const int CurveBits = 256;

    public static ECDsaSecurityKey Private(string base64) =>
        Load(base64, "JWT_PRIVATE_KEY", (key, bytes) => key.ImportPkcs8PrivateKey(bytes, out _));

    public static ECDsaSecurityKey Public(string base64) =>
        Load(base64, "JWT_PUBLIC_KEY", (key, bytes) => key.ImportSubjectPublicKeyInfo(bytes, out _));

    private static ECDsaSecurityKey Load(string base64, string name, Action<ECDsa, byte[]> import)
    {
        var key = ECDsa.Create();
        try
        {
            import(key, Convert.FromBase64String(base64));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidOperationException($"{name} is not a usable P-256 key.");
        }
        if (key.KeySize == CurveBits) return new ECDsaSecurityKey(key);
        key.Dispose();
        throw new InvalidOperationException($"{name} must be a P-256 key.");
    }
}

public static class CallerClaims
{
    public static Caller ToCaller(this ClaimsPrincipal user) => new(
        user.FindFirstValue(JwtRegisteredClaimNames.Sub)!,
        user.FindAll(TokenIssuer.PermissionClaim).Select(c => c.Value).ToList(),
        user.FindAll(TokenIssuer.FarmClaim).Select(c => c.Value).ToList());
}
