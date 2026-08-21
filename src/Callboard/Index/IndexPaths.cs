namespace Callboard.Index;

/// <summary>
/// Where the derived index lives, relative to a cards root (design.md D4 / ADR-0004: derived,
/// disposable, rebuildable from the primary record, never authoritative, never committed). One
/// named constant, referenced everywhere the path is needed, so the value that must match
/// <c>.gitignore</c>'s <c>callboard/.index/</c> rule exists exactly once rather than being
/// re-typed at each call site.
/// </summary>
internal static class IndexPaths
{
    internal const string RelativeDatabasePath = "callboard/.index/callboard.db";

    /// <summary>Resolves the database path under <paramref name="root"/> — the same root a card's
    /// scope-shaped directories (<see cref="Cards.CardLayout"/>) resolve under.</summary>
    internal static string DatabasePath(string root) =>
        Path.Combine(root, RelativeDatabasePath.Replace('/', Path.DirectorySeparatorChar));
}
