using Edith.Speech;

namespace Edith.Speech.Tests;

public sealed class LocalSecretsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-secrets-" + Guid.NewGuid().ToString("N"));
    private string Plain => Path.Combine(_dir, "key.txt");

    public LocalSecretsTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void ProtectedCopy_IsPreferred_AndRoundTrips()
    {
        File.WriteAllText(Plain, "old-plain-key");
        LocalSecrets.WriteProtected(Plain, " secret-key-123 ");

        Assert.Equal("secret-key-123", LocalSecrets.Read(Plain));
        Assert.DoesNotContain("secret-key-123", File.ReadAllText(Plain + LocalSecrets.ProtectedSuffix));
        Assert.True(LocalSecrets.IsPlainText(Plain));
    }

    [Fact]
    public void PlainFile_StillWorks_AndMissingIsNull()
    {
        Assert.Null(LocalSecrets.Read(Plain));
        Assert.False(LocalSecrets.Exists(Plain));

        File.WriteAllText(Plain, "plain\n");
        Assert.Equal("plain", LocalSecrets.Read(Plain));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
