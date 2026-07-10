using System.Security.Cryptography;

/// <summary>
/// PBKDF2-SHA256 password hashing (BCL only — no extra package). Per-user random salt,
/// fixed-time comparison on verify so a mistyped password can't be distinguished from a
/// wrong one by timing. Iteration count is stored alongside the hash (not hard-coded at
/// verify time) so it can be raised later without invalidating existing accounts.
/// Known trade-off: 100k iterations keeps local demo logins snappy; OWASP's current
/// guidance for PBKDF2-HMAC-SHA256 is considerably higher (600k+) for a system actually
/// defending real accounts.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
