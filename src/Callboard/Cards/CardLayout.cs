using System.Linq;

namespace Callboard.Cards;

/// <summary>
/// The scope-shaped directory layout from design.md D3 / ADR-0003: where a card's file lives is
/// a function of its <see cref="CardScope"/>, not its <see cref="CardKind"/> — a rule promoted
/// from <see cref="CardScope.Change"/> to <see cref="CardScope.Repository"/> moves from
/// <c>changes/&lt;name&gt;/</c> into <see cref="RegisterDirectory"/> alongside every other
/// repository-scoped card, which is what makes that promotion a directory move rather than a
/// rewrite. This is the layout only — identity/filename allocation is 4.2, and archive itself
/// (a directory operation on <c>changes/&lt;name&gt;/</c> and nothing else) is later work; this
/// type exists so both can resolve the same directory the same way.
/// </summary>
internal static class CardLayout
{
    internal const string RegisterDirectory = "callboard/register/";
    internal const string DecisionsDirectory = "callboard/decisions/";
    internal const string ChangesRootDirectory = "callboard/changes/";

    /// <summary>
    /// Where 4.2's per-kind identity counters live — a root-level <c>callboard/&lt;x&gt;/</c>
    /// directory like the three above, but not scope-shaped, because a counter file is not a card
    /// (block A's brief). Registered here, rather than left as a constant only
    /// <see cref="CardIdentityAllocator"/> knows about, so this type is the single statement of
    /// every root-level path the record uses — block A's review found two independent statements
    /// of this exact path once <see cref="CardIdentityAllocator"/> shipped its own copy.
    /// <see cref="IdentityCounterPath"/> is the only way <see cref="CardIdentityAllocator"/>
    /// constructs a counter's relative path.
    /// </summary>
    internal const string IdentitiesDirectory = "callboard/identities/";

    /// <summary>
    /// The reserved live-change name: card-model's archive path is
    /// <c>callboard/changes/archive/&lt;name&gt;/</c> (a directory move within the same tree,
    /// mirroring this repository's own <c>openspec/changes/archive/</c>), so a live change named
    /// <c>archive</c> would collide with every archived change's own container the moment it
    /// existed. Refused here, at the one place a live change's directory is resolved, rather than
    /// left to whichever later section builds the archive move to discover by accident.
    /// </summary>
    internal const string ReservedArchiveChangeName = "archive";

    /// <summary>
    /// The directory holding every archived change's own card directory —
    /// <c>callboard/changes/archive/</c> — the single statement of that path anywhere in this
    /// codebase (§4 remediation R1: previously spelled only as a hand-built string inside a test).
    /// A card that survives archive is not at this directory itself; it is one level further down,
    /// at <see cref="ArchivedChangeDirectory"/>, exactly mirroring <see cref="ChangesDirectory"/>
    /// one level up.
    /// </summary>
    internal const string ArchiveDirectory = ChangesRootDirectory + ReservedArchiveChangeName + "/";

    /// <summary>
    /// Where <paramref name="changeName"/>'s cards live once that change has been archived — a
    /// directory move of <see cref="ChangesDirectory"/>'s result to under <see cref="ArchiveDirectory"/>
    /// and nothing else (the Product Owner's binding decision; archive-as-a-verb is later work).
    /// Every derived path that reads the record (<c>index rebuild</c>'s population,
    /// <see cref="CardIdentityAllocator"/>'s counter-violation check) must resolve this the same
    /// way, which is what this method — rather than a second hand-built string — exists to
    /// guarantee.
    /// </summary>
    internal static string ArchivedChangeDirectory(string changeName) =>
        $"{ArchiveDirectory}{RequireSafePathSegment(changeName, nameof(changeName))}/";

    internal static string ChangesDirectory(string changeName)
    {
        var segment = RequireSafePathSegment(changeName, nameof(changeName));
        if (string.Equals(segment, ReservedArchiveChangeName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{ReservedArchiveChangeName}' is a reserved change name — it is where archived changes live.",
                nameof(changeName));
        }

        return $"{ChangesRootDirectory}{segment}/";
    }

    /// <summary>
    /// Resolves the directory a card of the given <paramref name="scope"/> lives in.
    /// <paramref name="changeName"/> is required for <see cref="CardScope.Change"/> and
    /// <see cref="CardScope.Section"/> — a section lives inside the change that raised it, so
    /// both resolve into that change's directory — and is ignored otherwise. A missing change
    /// name for a scope that needs one is a caller error, not a runtime card-model refusal (that
    /// refusal table is 4.4's), so it throws rather than returning a result.
    /// </summary>
    internal static string DirectoryFor(CardScope scope, string? changeName) => scope.Match(
        onSection: () => ChangesDirectory(RequireChangeName(changeName)),
        onChange: () => ChangesDirectory(RequireChangeName(changeName)),
        onCapability: () => DecisionsDirectory,
        onRepository: () => RegisterDirectory);

    /// <summary>The relative path of <paramref name="kind"/>'s identity counter file under
    /// <see cref="IdentitiesDirectory"/> — the one place that path is built.</summary>
    internal static string IdentityCounterPath(CardKind kind) => $"{IdentitiesDirectory}{kind.ToWireString()}.count";

    /// <summary>
    /// Every directory that can hold <c>*.md</c> card files under <paramref name="cardsRoot"/>:
    /// <see cref="RegisterDirectory"/>, <see cref="DecisionsDirectory"/>, one per live change, and
    /// one per <em>archived</em> change (§7 block B). This is the single statement of "where does
    /// the record live" — both <see cref="Index.IndexPopulator"/> (rebuilding the derived index)
    /// and <see cref="CardIdentityResolver"/> (answering "which card carries this id?") walk this
    /// exact list, rather than each carrying its own copy that could silently drift from the
    /// other's. <see cref="ReservedArchiveChangeName"/>'s own container is skipped as a live change
    /// and descended into separately via <see cref="ArchiveDirectory"/> — an id resolution that
    /// only checked live changes would make card-model's "identity SHALL remain valid and
    /// resolvable after the change that raised it is archived" false the moment a change archived.
    ///
    /// <para>
    /// <b>Resolvable is not the same question as live (§7 remediation, blocker 2).</b> This method
    /// answers "where can an id still be found" and deliberately reaches into the archive — that is
    /// what lets a citation in an archived change still count and what lets a promoted rule's prior
    /// identity keep resolving. Whether a card found there counts as part of the *live* register is
    /// a different question, answered by <see cref="ResolveLiveRecordDirectories"/>, not this one.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> ResolveRecordDirectories(string cardsRoot)
    {
        var directories = new List<string>(ResolveLiveRecordDirectories(cardsRoot));

        var archiveRoot = ArchiveRootPath(cardsRoot);
        if (Directory.Exists(archiveRoot))
        {
            directories.AddRange(
                Directory.EnumerateDirectories(archiveRoot).OrderBy(static path => path, StringComparer.Ordinal));
        }

        return directories;
    }

    /// <summary>
    /// The subset of <see cref="ResolveRecordDirectories"/> that has not been archived:
    /// <see cref="RegisterDirectory"/>, <see cref="DecisionsDirectory"/> and one per live change —
    /// never a directory under <see cref="ArchiveDirectory"/> (§7 remediation, blocker 2). This is
    /// what "live" means for a card: register and decisions are never archived at all, and a
    /// change-scoped card stops being live the moment <c>change archive</c> moves its change's
    /// directory under <see cref="ArchiveDirectory"/>, exactly the directory move that makes archive
    /// "a directory-level filter with nothing in transit". <see cref="Cards.RuleCitations.
    /// UncitedOpenRules"/> is the first caller — a never-promoted change-scoped rule left <c>open</c>
    /// in an archived change is resolvable (walking <see cref="ResolveRecordDirectories"/> still
    /// finds it, and a citation of its id from anywhere, archived or not, still counts) but is not
    /// live, so it does not belong in a human review queue that only exists to look at the standing
    /// register.
    /// </summary>
    internal static IReadOnlyList<string> ResolveLiveRecordDirectories(string cardsRoot)
    {
        var directories = new List<string>
        {
            CombineWithLayout(cardsRoot, RegisterDirectory),
            CombineWithLayout(cardsRoot, DecisionsDirectory),
        };

        var changesRoot = CombineWithLayout(cardsRoot, ChangesRootDirectory);
        var archiveRoot = ArchiveRootPath(cardsRoot);

        if (Directory.Exists(changesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(changesRoot).OrderBy(static path => path, StringComparer.Ordinal))
            {
                if (string.Equals(directory, archiveRoot, StringComparison.Ordinal))
                {
                    continue;
                }

                directories.Add(directory);
            }
        }

        return directories;
    }

    // Trimmed to match Directory.EnumerateDirectories' own results, which never carry a trailing
    // separator, while CombineWithLayout's input (a CardLayout constant) always does.
    private static string ArchiveRootPath(string cardsRoot) =>
        Path.TrimEndingDirectorySeparator(CombineWithLayout(cardsRoot, ArchiveDirectory));

    private static string CombineWithLayout(string cardsRoot, string layoutDirectory) =>
        Path.Combine(cardsRoot, layoutDirectory.Replace('/', Path.DirectorySeparatorChar));

    private static string RequireChangeName(string? changeName) =>
        string.IsNullOrEmpty(changeName)
            ? throw new ArgumentException("a change name is required to resolve a change- or section-scoped card's directory.", nameof(changeName))
            : changeName;

    /// <summary>
    /// Validates that <paramref name="value"/> is safe to interpolate as a single path segment —
    /// no separator, no <c>.</c>/<c>..</c>, not empty. Block B's writer is the first caller that
    /// turns a resolved directory into a real filesystem path (a <paramref name="changeName"/>
    /// here, a card <c>id</c> wherever it becomes a filename), so an unvalidated value stops
    /// being a theoretical concern the moment this method's caller reaches disk. Throws rather
    /// than returning a result for the same reason <see cref="RequireChangeName"/> does — this is
    /// a caller error (a card-model refusal for a bad card <c>id</c> is a later section's
    /// business), not something the record itself needs to represent.
    /// </summary>
    internal static string RequireSafePathSegment(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("must not be empty.", paramName);
        }

        if (string.Equals(value, ".", StringComparison.Ordinal) || string.Equals(value, "..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{value}' is not a valid path segment.", paramName);
        }

        if (value.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException($"'{value}' must not contain a path separator.", paramName);
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{value}' must not contain '..'.", paramName);
        }

        return value;
    }
}
