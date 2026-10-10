using System.Security.Cryptography;
using System.Text;

namespace Edith.Speech;

/// <summary>
/// Secrets on disk, encrypted with Windows DPAPI for the current user: "key.txt.dpapi" is
/// preferred over a plain "key.txt". scripts/Protect-Secrets.ps1 creates the encrypted copy
/// and removes the plain one after checking the round trip.
/// </summary>
public static class LocalSecrets
{
    public const string ProtectedSuffix = ".dpapi";

    /// <summary>The secret, or null when neither the protected nor the plain file exists.</summary>
    public static string? Read(string plainPath)
    {
        string protectedPath = plainPath + ProtectedSuffix;
        if (File.Exists(protectedPath))
        {
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(protectedPath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear).Trim();
        }

        return File.Exists(plainPath) ? File.ReadAllText(plainPath).Trim() : null;
    }

    public static bool Exists(string plainPath) =>
        File.Exists(plainPath + ProtectedSuffix) || File.Exists(plainPath);

    /// <summary>True while a secret still sits on disk in clear text.</summary>
    public static bool IsPlainText(string plainPath) =>
        File.Exists(plainPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(plainPath));

    public static void WriteProtected(string plainPath, string secret) =>
        File.WriteAllBytes(
            plainPath + ProtectedSuffix,
            ProtectedData.Protect(Encoding.UTF8.GetBytes(secret.Trim()), null, DataProtectionScope.CurrentUser));
}
