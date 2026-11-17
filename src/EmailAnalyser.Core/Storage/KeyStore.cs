using System.Security.Cryptography;
using System.Text;

namespace EmailAnalyser.Core.Storage;

/// <summary>
/// The database key: 256 random bits, stored on disk protected with DPAPI for the
/// current Windows user. Anyone who can run code as that user can unprotect it;
/// copies of the disk or of the file cannot.
/// </summary>
public static class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EmailAnalyser.DatabaseKey.v1");

    /// <summary>Returns the key as 64 hex characters, creating it on first use.</summary>
    public static string GetOrCreateHexKey(string path)
    {
        if (!File.Exists(path))
        {
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(protectedKey);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created it first; fall through and read that one.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Convert.ToHexString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
