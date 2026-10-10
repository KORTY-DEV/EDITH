namespace Edith.Core.Tests;

public sealed class SourceHygieneTests
{
    /// <summary>A mangled regex escape ("\b" turned into a backspace) compiles but silently never matches.</summary>
    [Fact]
    public void NoControlCharactersInSources()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "EDITH.slnx")))
            root = Path.GetDirectoryName(root)!;
        root ??= @"C:\EDITH";

        var bad = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Any(c => c < ' ' && c is not ('\r' or '\n' or '\t')))
            .ToList();

        Assert.Empty(bad);
    }
}
