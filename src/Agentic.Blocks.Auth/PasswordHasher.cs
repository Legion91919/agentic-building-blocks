using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Agentic.Blocks.Auth;

/// <summary>Outcome of a password check.</summary>
public enum PasswordCheck
{
    /// <summary>Wrong password or unreadable hash.</summary>
    Failed,
    /// <summary>Correct password, hash parameters are current.</summary>
    Success,
    /// <summary>Correct password, but the hash uses old parameters: store a new hash.</summary>
    SuccessRehashNeeded,
}

/// <summary>Hashes and verifies passwords.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a password with a fresh random salt.</summary>
    string Hash(string password);

    /// <summary>Checks a password against a stored hash in constant time.</summary>
    PasswordCheck Verify(string storedHash, string password);
}

/// <summary>PBKDF2 parameters. Defaults follow current OWASP guidance for PBKDF2-HMAC-SHA256.</summary>
public sealed class PasswordHashingOptions
{
    /// <summary>Iterations. Default 600,000.</summary>
    public int Iterations { get; set; } = 600_000;

    /// <summary>Salt size in bytes. Default 16.</summary>
    public int SaltSize { get; set; } = 16;

    /// <summary>Derived key size in bytes. Default 32.</summary>
    public int KeySize { get; set; } = 32;
}

/// <summary>
/// PBKDF2-HMAC-SHA256 via <see cref="Rfc2898DeriveBytes.Pbkdf2(byte[], byte[], int, HashAlgorithmName, int)"/>.
/// Format: <c>v1$pbkdf2-sha256$&lt;iterations&gt;$&lt;salt base64&gt;$&lt;key base64&gt;</c>. The version and
/// parameters live in the hash, so they can be raised later; old hashes still verify and are upgraded on login.
/// </summary>
public sealed class Pbkdf2PasswordHasher(PasswordHashingOptions options) : IPasswordHasher
{
    /// <summary>Current format version.</summary>
    public const string Version = "v1";

    private const string Algorithm = "pbkdf2-sha256";

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(options.SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, options.Iterations, HashAlgorithmName.SHA256, options.KeySize);
        return string.Join('$', Version, Algorithm, options.Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    /// <inheritdoc />
    public PasswordCheck Verify(string storedHash, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrEmpty(storedHash)) return PasswordCheck.Failed;
        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != Version || parts[1] != Algorithm) return PasswordCheck.Failed;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1 || iterations > 10_000_000)
        {
            return PasswordCheck.Failed;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed;
        }

        if (salt.Length == 0 || expected.Length == 0) return PasswordCheck.Failed;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return PasswordCheck.Failed;

        var current = iterations == options.Iterations && salt.Length == options.SaltSize && expected.Length == options.KeySize;
        return current ? PasswordCheck.Success : PasswordCheck.SuccessRehashNeeded;
    }
}
