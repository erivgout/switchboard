using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Switchboard.Core.Domain;

namespace Switchboard.Web.Hosting;

/// <summary>
/// Argon2id password hashing (SR-AUTHN-1), OWASP parameters: 19 MiB memory, 2 iterations, 1 lane.
/// Hashes produced by ASP.NET Identity's default hasher still verify and are rehashed on next login.
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher<User>
{
    private const int MemoryKb = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "$argon2id$v=19$";

    private readonly PasswordHasher<User> _legacy = new();

    public string HashPassword(User user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism);
        return $"{Prefix}m={MemoryKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
    {
        if (!hashedPassword.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var legacy = _legacy.VerifyHashedPassword(user, hashedPassword, providedPassword);
            return legacy == PasswordVerificationResult.Failed ? legacy : PasswordVerificationResult.SuccessRehashNeeded;
        }

        var parts = hashedPassword[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return PasswordVerificationResult.Failed;
        }
        var parameters = parts[0].Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Compute(providedPassword, salt, parameters["m"], parameters["t"], parameters["p"]);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }
        return parameters["m"] < MemoryKb || parameters["t"] < Iterations
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }
}
