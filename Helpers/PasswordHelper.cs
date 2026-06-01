using System;
using System.Linq;
using System.Security.Cryptography;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Provides PBKDF2/SHA-256 password hashing and verification helpers.
    /// Suitable for storing credentials in dbo.Mng_Users (PasswordHash / PasswordSalt columns).
    /// </summary>
    public static class PasswordHelper
    {
        private const int SaltSize   = 32;       // 256 bits
        private const int HashSize   = 32;       // 256 bits
        private const int Iterations = 100_000;  // OWASP minimum for PBKDF2-SHA256

        // ---------------------------------------------------------------------------
        // Hash
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Hashes a plain-text password and returns the derived hash and a random salt.
        /// Store both in the database; never store the plain-text password.
        /// </summary>
        public static (byte[] Hash, byte[] Salt) HashPassword(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));

            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);

            using var deriveBytes =
                new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            var hash = deriveBytes.GetBytes(HashSize);

            return (hash, salt);
        }

        // ---------------------------------------------------------------------------
        // Verify
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Verifies a plain-text password against a stored hash/salt pair.
        /// Uses constant-time comparison to mitigate timing attacks.
        /// </summary>
        public static bool VerifyPassword(string password, byte[] storedHash, byte[] storedSalt)
        {
            if (password == null)    throw new ArgumentNullException(nameof(password));
            if (storedHash == null)  throw new ArgumentNullException(nameof(storedHash));
            if (storedSalt == null)  throw new ArgumentNullException(nameof(storedSalt));

            using var deriveBytes =
                new Rfc2898DeriveBytes(password, storedSalt, Iterations, HashAlgorithmName.SHA256);
            var computed = deriveBytes.GetBytes(HashSize);

            // SequenceEqual is sufficient here; for higher security use CryptographicOperations.FixedTimeEquals
            return CryptographicOperations.FixedTimeEquals(computed, storedHash);
        }
    }
}
