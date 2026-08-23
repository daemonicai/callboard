using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Callboard.Cards;

/// <summary>
/// One file's content state at the moment it was fingerprinted: <see cref="ContentHash"/> is a
/// lowercase-hex SHA-256 of the file's bytes, or <see langword="null"/> when the file did not exist
/// or could not be read at that moment — "absent" is itself a fingerprinted state, not an error
/// (§6 block C ruling: "A file that has been deleted or is unreadable at re-check is stale — the
/// extent moved — not an error and not current"), and treating it as a third, ordinary value here
/// (rather than throwing, or omitting the file from the collection) is what lets
/// <see cref="FindingStalenessEvaluator"/> compare "was present, now absent" and "was absent, now
/// present" the same way it compares two different hashes — a change in either direction is a
/// change.
/// </summary>
internal sealed record FindingExtentFileFingerprint(string RelativePath, string? ContentHash);

/// <summary>
/// The content fingerprint findings' "Findings stale when their extent moves" requires (§6 block C
/// Product Owner ruling: "Staleness is a content fingerprint, not git — <c>callboard</c> does not
/// invoke git. At record time the tool fingerprints the files/ranges the declared extent covers and
/// stores that alongside <c>verified_at</c>; staleness is re-fingerprinting now and comparing.")
/// Recorded on <see cref="FindingCardFields.ExtentFingerprint"/> only when
/// <see cref="FindingCardFields.Extent"/> is <see cref="FindingExtent.Explicit"/> — see
/// <see cref="Compute"/>.
///
/// <para>
/// <b>File granularity, not range/symbol granularity — deliberately the over-reporting
/// direction.</b> An <see cref="FindingExtent.Explicit"/> item may name a line range or a symbol
/// (e.g. <c>src/Foo.cs:10-20</c> or <c>src/Foo.cs#Bar</c>), but no earlier section defined a parsed
/// structure for either — <see cref="FindingExtent"/>'s own items are opaque strings. Rather than
/// inventing an unspecified range/symbol grammar this build would have to guess at, <see cref="
/// FilePathFor"/> resolves every item down to the file it names (everything before the first
/// <c>:</c> or <c>#</c>, whichever comes first; the whole item when neither appears) and this type
/// fingerprints that whole file's content. This is the architect ruling's named "never
/// under-report; over-reporting is the safe direction" applied concretely: a line range or symbol
/// whose file changed elsewhere is reported stale even though the range itself did not move, rather
/// than risking a cleverer per-range fingerprint that could under-report the safer way and stay
/// silently wrong. A path with a literal <c>:</c> or <c>#</c> in its own name is the one case this
/// resolution cannot distinguish from a qualifier — a known, stated limitation, not silently
/// assumed away.
/// </para>
///
/// <para>
/// <b>Files are deduplicated and sorted by <see cref="StringComparer.Ordinal"/></b> before
/// fingerprinting, so the wire form (<see cref="CardFileWriter"/>'s <c>extent_fingerprint</c> list)
/// is deterministic regardless of how many <see cref="FindingExtent.Explicit"/> items resolve to
/// the same file or what order the caller declared them in.
/// </para>
/// </summary>
internal sealed record FindingExtentFingerprint
{
    private readonly ImmutableArray<FindingExtentFileFingerprint> _files;

    internal ImmutableArray<FindingExtentFileFingerprint> Files
    {
        get => _files;
        init => _files = value;
    }

    internal FindingExtentFingerprint(IEnumerable<FindingExtentFileFingerprint> files)
    {
        _files = files.ToImmutableArray();
    }

    // ImmutableArray<T>'s own Equals compares the underlying array by reference, not
    // element-wise — same reason BlockCardFields overrides Equals for Tasks/BlockedBy and
    // FindingExtent's ExplicitCase does for Items.
    public bool Equals(FindingExtentFingerprint? other) => other is not null && Files.SequenceEqual(other.Files);

    public override int GetHashCode() => Files.Length;

    /// <summary>
    /// Fingerprints <paramref name="extent"/> against <paramref name="repoRoot"/> — files are
    /// resolved relative to <paramref name="repoRoot"/>, the same root <see cref="RepoRootResolver"/>
    /// hands every other card-writing verb. Only <see cref="FindingExtent.Explicit"/> has a file set
    /// to fingerprint; the other two forms return <see langword="null"/> — findings' "Staleness is
    /// only measurable for an Explicit extent" (§6 block C ruling): an <see cref="FindingExtent.
    /// Instrument"/> extent has no file set at all (re-verification means re-running the command),
    /// and a <see cref="FindingExtent.BlockScope"/> extent has no enumerable file set either.
    /// </summary>
    internal static FindingExtentFingerprint? Compute(FindingExtent extent, string repoRoot) => extent.Match<FindingExtentFingerprint?>(
        onInstrument: static _ => null,
        onExplicit: items => ComputeForFiles(items, repoRoot),
        onBlockScope: static () => null);

    /// <summary>
    /// The file an <see cref="FindingExtent.Explicit"/> item names — everything before the first
    /// <c>:</c> or <c>#</c>, whichever occurs first, or the whole item when neither does. See this
    /// type's own doc comment for why this resolves at file granularity rather than parsing a
    /// range/symbol grammar this codebase has never defined.
    /// </summary>
    internal static string FilePathFor(string extentItem)
    {
        var colonIndex = extentItem.IndexOf(':');
        var hashIndex = extentItem.IndexOf('#');
        var boundary = colonIndex < 0
            ? hashIndex
            : hashIndex < 0 ? colonIndex : Math.Min(colonIndex, hashIndex);

        return boundary < 0 ? extentItem : extentItem[..boundary];
    }

    private static FindingExtentFingerprint ComputeForFiles(ImmutableArray<string> items, string repoRoot)
    {
        var relativePaths = items
            .Select(FilePathFor)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal);

        var files = relativePaths.Select(relativePath =>
            new FindingExtentFileFingerprint(relativePath, HashFileOrNull(Path.Combine(repoRoot, relativePath))));

        return new FindingExtentFingerprint(files);
    }

    /// <summary>
    /// Lowercase-hex SHA-256 of the file's bytes, or <see langword="null"/> when the file cannot be
    /// read — deleted, never existed, or a permissions failure. Never throws: absence is a
    /// fingerprinted state (this type's own doc comment), not a tool failure, at both record time
    /// and re-check time.
    /// </summary>
    private static string? HashFileOrNull(string fullPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
