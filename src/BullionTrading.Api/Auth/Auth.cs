using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BullionTrading.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BullionTrading.Api.Auth;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "bullion-trading-api";
    public string Audience { get; set; } = "bullion-trading-clients";

    /// <summary>HMAC signing key, at least 32 bytes. Supply via configuration / environment in production.</summary>
    public string SigningKey { get; set; } = "";

    public int LifetimeMinutes { get; set; } = 60;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));
}

/// <summary>Standard short JWT claim names, used both when issuing and validating tokens.</summary>
public static class Claims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string Name = "name";
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Role = "role";
}

public class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(o.LifetimeMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [Claims.UserId] = user.Id.ToString(),
                [Claims.Email] = user.Email,
                [Claims.Name] = user.DisplayName,
                [Claims.Role] = user.Role.ToString(),
            },
            SigningCredentials = new SigningCredentials(o.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}

/// <summary>PBKDF2-SHA256 password hashing: "iterations.salt.hash" (base64).</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(Claims.UserId)
                  ?? throw new InvalidOperationException("Missing user id claim"));
}
