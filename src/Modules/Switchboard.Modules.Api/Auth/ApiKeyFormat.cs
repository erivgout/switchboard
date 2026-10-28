using System.Security.Cryptography;
using System.Text;

namespace Switchboard.Modules.Api.Auth;

/// <summary>
/// Personal API keys look like <c>sb_</c> followed by 43 base62 characters (256 bits of randomness).
/// Only the SHA-256 hash and a short display prefix are stored (SR-AUTHN-7).
/// </summary>
public static class ApiKeyFormat
{
    public const string Marker = "sb_";
    public const int RandomLength = 43;
    public const int Length = 46;

    /// <summary>Characters of the key kept for display, e.g. <c>sb_Ab12Cd34</c>.</summary>
    public const int PrefixLength = 11;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Generate() => Marker + RandomNumberGenerator.GetString(Alphabet, RandomLength);

    /// <summary>True when <paramref name="value"/> has the shape of a key. Says nothing about whether it exists.</summary>
    public static bool IsWellFormed(string? value)
    {
        if (value is not { Length: Length } || !value.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var c in value.AsSpan(Marker.Length))
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string PrefixOf(string key) => key[..PrefixLength];
}
