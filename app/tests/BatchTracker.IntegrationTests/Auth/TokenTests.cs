using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BatchTracker.IntegrationTests;

public class TokenTests(Stack stack) : StackTest(stack)
{
    private static string Base64Url(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

    private static string Unsigned(string alg, string subject)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = "{\"sub\":\"" + subject + "\",\"perm\":[\"ops.admin\"],\"farm\":[\"*\"]," +
            $"\"iss\":\"{TokenIssuer.Issuer}\",\"aud\":\"{TokenIssuer.Issuer}\"," +
            $"\"nbf\":{now.ToUnixTimeSeconds()},\"exp\":{now.AddHours(1).ToUnixTimeSeconds()}" + "}";
        return $"{Base64Url("{\"alg\":\"" + alg + "\",\"typ\":\"JWT\"}")}.{Base64Url(payload)}.";
    }

    private static string Signed(string issuer, string audience, IEnumerable<Claim> claims, DateTime notBefore)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(Env.PrivateKey), out _);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256)
        });
    }

    private static readonly Claim[] AdaClaims =
        [new(JwtRegisteredClaimNames.Sub, "ada"), new(TokenIssuer.PermissionClaim, "ops.admin"), new(TokenIssuer.FarmClaim, "*")];

    private Task<HttpResponseMessage> MeWith(string token)
    {
        var client = Anonymous().Http;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync("/api/me");
    }

    private static string Valid() => new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("viewer")!);

    [Fact]
    public async Task REQ_BT_047_A_valid_token_signs_in_so_the_refusals_below_mean_something()
    {
        Assert.Equal(HttpStatusCode.OK, (await MeWith(Valid())).StatusCode);
    }

    [Fact]
    public void REQ_BT_047_Tokens_are_signed_es256()
    {
        Assert.Equal("ES256", new JsonWebToken(Valid()).Alg);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    public async Task REQ_BT_047_Unsigned_token_is_401(string alg)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith(Unsigned(alg, "ada"))).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Public_key_used_as_an_hmac_secret_is_401()
    {
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TokenIssuer.Issuer,
            Audience = TokenIssuer.Issuer,
            Subject = new ClaimsIdentity(AdaClaims),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Env.PublicKey)), SecurityAlgorithms.HmacSha256)
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith(forged)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Changed_payload_is_401()
    {
        var parts = Valid().Split('.');
        var admin = Unsigned("ES256", "ada").Split('.')[1];

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith($"{parts[0]}.{admin}.{parts[2]}")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Stripped_signature_is_401()
    {
        var parts = Valid().Split('.');

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith($"{parts[0]}.{parts[1]}.")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Flipped_signature_is_401()
    {
        var parts = Valid().Split('.');
        var signature = Base64UrlEncoder.DecodeBytes(parts[2]);
        signature[0] ^= 0xFF;

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await MeWith($"{parts[0]}.{parts[1]}.{Base64UrlEncoder.Encode(signature)}")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Wrong_issuer_is_401()
    {
        var token = Signed("someone-else", TokenIssuer.Issuer, AdaClaims, DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith(token)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Wrong_audience_is_401()
    {
        var token = Signed(TokenIssuer.Issuer, "someone-else", AdaClaims, DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith(token)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Future_not_before_is_401()
    {
        var token = Signed(TokenIssuer.Issuer, TokenIssuer.Issuer, AdaClaims, DateTime.UtcNow.AddMinutes(10));

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith(token)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Token_with_no_subject_is_401_not_500()
    {
        var token = Signed(TokenIssuer.Issuer, TokenIssuer.Issuer, AdaClaims[1..], DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeWith(token)).StatusCode);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("c2hvcnQ=")]
    public void REQ_BT_047_Unreadable_signing_key_refuses_to_start(string key)
    {
        Assert.Throws<InvalidOperationException>(() => new TokenIssuer(key, TimeProvider.System));
    }

    [Fact]
    public void REQ_BT_047_Key_of_another_curve_refuses_to_start()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<InvalidOperationException>(
            () => new TokenIssuer(Convert.ToBase64String(other.ExportPkcs8PrivateKey()), TimeProvider.System));
    }

    [Fact]
    public void REQ_BT_047_Public_key_cannot_sign()
    {
        Assert.Throws<InvalidOperationException>(() => new TokenIssuer(Env.PublicKey, TimeProvider.System));
    }
}
