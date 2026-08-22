namespace Callboard.Cards;

/// <summary>
/// A card file path proven, at construction time, to live in the exact directory its own
/// <see cref="CardScope"/> requires under a specific repository root (card-model 4.5, O-1: "anchor
/// <c>CardStore</c> to the repo root"). The old guard (<c>CardStore.ValidateAgainstLayout</c>)
/// compared <paramref name="filePath"/>'s trailing segments against
/// <see cref="CardLayout.DirectoryFor"/>'s <b>relative</b> result alone — a path with an entirely
/// different root but a correctly-shaped tail (e.g. <c>/tmp/somewhere-else/callboard/register/x.md</c>
/// when the real cards root is elsewhere) passed, because nothing in the comparison ever looked at
/// the root. <see cref="TryCreate"/> closes that: it combines <paramref name="cardsRoot"/> with the
/// expected relative directory into one full, normalised path and requires
/// <paramref name="filePath"/>'s own directory to equal it <b>exactly</b>, not merely share a
/// suffix — <see cref="Path.GetFullPath(string)"/> resolves away any <c>..</c> segments on both
/// sides first, so a path that only looks anchored before resolution cannot pass by construction.
///
/// <para>
/// <b>Structural, not conventional</b> (§2/§3's working rule, copying
/// <c>StdinBodyReader.ReadBody</c>/<c>RedirectedStdin</c>'s shape): <c>CardStore</c>'s write
/// boundary (<c>AtomicWrite</c>) accepts only this type, never a raw <c>string</c> path — there is
/// no overload that takes an unvalidated path. The only way to obtain an instance is
/// <see cref="TryCreate"/> succeeding, so a write cannot reach disk without having passed the
/// anchored-directory check; there is no separate "remember to validate first" step for a caller to
/// skip. The constructor is private and this is a reference type (not a <see langword="struct"/>,
/// which would still let <c>default(AnchoredCardPath)</c> compile as a null-backed instance).
/// </para>
/// </summary>
internal sealed class AnchoredCardPath
{
    private AnchoredCardPath(string filePath) => FilePath = filePath;

    internal string FilePath { get; }

    /// <summary>
    /// Succeeds only when <paramref name="filePath"/>'s containing directory, once fully resolved,
    /// is exactly <paramref name="cardsRoot"/> combined with the directory a <paramref name="scope"/>-
    /// scoped card belongs in (<see cref="CardLayout.DirectoryFor"/>). <paramref name="changeName"/>
    /// is forwarded to that same call and is required exactly when it is — see that method's own
    /// doc comment. On any mismatch, or an invalid <paramref name="changeName"/> for a scope that
    /// needs one, returns <see langword="null"/> and sets <paramref name="failure"/> to the reason a
    /// <c>CardStore</c> write must report.
    /// </summary>
    internal static AnchoredCardPath? TryCreate(
        string cardsRoot, string filePath, CardScope scope, string? changeName, out CardWriteResult.LayoutMismatch? failure)
    {
        string expectedRelativeDirectory;
        try
        {
            expectedRelativeDirectory = CardLayout.DirectoryFor(scope, changeName);
        }
        catch (ArgumentException ex)
        {
            failure = new CardWriteResult.LayoutMismatch(ex.Message);
            return null;
        }

        // TrimEnd the directory separator on both sides before comparing: Path.GetFullPath
        // preserves a trailing separator on the expected side (built with one, from
        // CardLayout.DirectoryFor's own convention) but Path.GetDirectoryName never produces one
        // on the actual side — an untrimmed comparison would refuse every legitimately anchored
        // write, not just a misrooted one.
        var expectedFullDirectory = Path.GetFullPath(
            Path.Combine(cardsRoot, expectedRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)))
            .TrimEnd(Path.DirectorySeparatorChar);

        var directory = Path.GetDirectoryName(filePath);
        var actualFullDirectory = Path.GetFullPath(string.IsNullOrEmpty(directory) ? "." : directory)
            .TrimEnd(Path.DirectorySeparatorChar);

        if (!string.Equals(actualFullDirectory, expectedFullDirectory, StringComparison.Ordinal))
        {
            failure = new CardWriteResult.LayoutMismatch(
                $"'{filePath}' does not live in the directory a '{scope.ToWireString()}'-scoped card belongs " +
                $"in under repository root '{cardsRoot}' ('{expectedFullDirectory}').");
            return null;
        }

        failure = null;
        return new AnchoredCardPath(filePath);
    }
}
