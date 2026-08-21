using Callboard;

namespace Callboard.Tests;

public sealed class RepoRootResolverTests
{
    [Fact]
    public void Resolve_WhenGitEntryIsADirectory_ReturnsThatDirectory()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, ".git"));
        var nested = Path.Combine(root.Path, "a", "b");
        Directory.CreateDirectory(nested);

        var resolved = RepoRootResolver.Resolve(nested);

        Assert.Equal(new DirectoryInfo(root.Path).FullName, resolved);
    }

    // The nit the reviewer flagged: a worktree checkout's ".git" entry is a *file* pointing at
    // the real repository's metadata directory, not a directory itself. RepoRootResolver.Resolve
    // is shared by both the cards root and the index path, so this branch needs its own test
    // rather than riding on whichever case the index tests happen to exercise.
    [Fact]
    public void Resolve_WhenGitEntryIsAFile_ReturnsThatDirectory()
    {
        using var root = new TempDirectory();
        File.WriteAllText(Path.Combine(root.Path, ".git"), "gitdir: /somewhere/else/.git/worktrees/example\n");
        var nested = Path.Combine(root.Path, "a", "b");
        Directory.CreateDirectory(nested);

        var resolved = RepoRootResolver.Resolve(nested);

        Assert.Equal(new DirectoryInfo(root.Path).FullName, resolved);
    }

    [Fact]
    public void Resolve_WithNoGitEntryAboveTheStartDirectory_ReturnsNull()
    {
        using var root = new TempDirectory();

        var resolved = RepoRootResolver.Resolve(root.Path);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_AtTheRootItself_ReturnsIt()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, ".git"));

        var resolved = RepoRootResolver.Resolve(root.Path);

        Assert.Equal(new DirectoryInfo(root.Path).FullName, resolved);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-reporoot-test-{Guid.NewGuid():N}");

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
