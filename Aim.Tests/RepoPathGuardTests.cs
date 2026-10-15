using Aim;

namespace Aim.Tests;

public class RepoPathGuardTests
{
    [Fact]
    public void RelativePathIsNormalized()
    {
        Assert.True(RepoPathGuard.TryNormalize("src/./App.cs", out var relative, out var error), error);
        Assert.Equal("src/App.cs", relative);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/note.txt")]
    [InlineData(".git/config")]
    [InlineData("src/../../etc/passwd")]
    [InlineData("foo:bar")]
    [InlineData("")]
    public void UnsafePathsAreRejected(string path)
    {
        Assert.False(RepoPathGuard.TryNormalize(path, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ResolvedPathMustStayUnderTheRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "aim-guard-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            Assert.True(RepoPathGuard.TryResolve(root, "src/App.cs", out var relative, out var full, out var error), error);
            Assert.Equal("src/App.cs", relative);
            Assert.StartsWith(Path.GetFullPath(root), full);

            Assert.False(RepoPathGuard.TryResolve(root, "../outside.txt", out _, out _, out error));
            Assert.Contains("inside the repository", error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SymlinkThatLeavesTheRepositoryIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "aim-guard-" + Guid.NewGuid().ToString("n"));
        var outside = Path.Combine(Path.GetTempPath(), "aim-guard-out-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "secret");
        var link = Path.Combine(root, "leak.txt");
        File.CreateSymbolicLink(link, secret);
        try
        {
            Assert.False(RepoPathGuard.TryResolve(root, "leak.txt", out _, out _, out var error));
            Assert.Contains("inside the repository", error);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }
}
