namespace Callboard;

/// <summary>
/// Resolves the real repository root by walking up from a starting directory looking for a
/// <c>.git</c> entry (a directory in a normal checkout, a file in a worktree). Every path this
/// process derives from the record — the cards root, the derived index's path — must anchor to
/// this, not to whatever the process happened to be launched from, or a card-store validation
/// (<c>Cards.CardStore.ValidateAgainstLayout</c>) and a <c>.gitignore</c> rule
/// (<c>Index.IndexPaths</c>) both silently stop meaning what they say the moment the caller's
/// working directory and the repository root diverge (§3 obligation 1, carried from §2 and
/// doubled by block A). One resolver, used everywhere a root is needed, rather than each caller
/// inventing its own notion of "here".
/// </summary>
internal static class RepoRootResolver
{
    private const string GitEntryName = ".git";

    /// <summary>
    /// Returns the repository root above (or equal to) <paramref name="startDirectory"/>, or
    /// <see langword="null"/> if none was found — the caller's job is to turn that into a refusal,
    /// not this resolver's, since a missing repo root means the process cannot proceed and only a
    /// command handler knows how to say so.
    /// </summary>
    internal static string? Resolve(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null)
        {
            var gitEntryPath = Path.Combine(current.FullName, GitEntryName);
            if (Directory.Exists(gitEntryPath) || File.Exists(gitEntryPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
