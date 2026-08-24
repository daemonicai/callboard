namespace Callboard.Cards;

/// <summary>
/// The wire-key vocabulary a nit-raising or nit-dispositioning <see cref="CardComment"/> header
/// adds to the six a plain comment already carries — one shared declaration
/// <see cref="CardFileWriter"/> and <see cref="CardFileParser"/> both read, rather than the key
/// being hand-typed twice (§8 block B, the wire-key drift guard carried from §7's close: "whatever
/// new keys the certification sequence introduces get a single shared declaration from the start").
/// §7 block C's own remediation named exactly the defect two hand-typed lists invites: a
/// writer-known, parser-unknown key gets filed as unknown and re-emitted alongside the known line
/// on every parse-then-write cycle, duplicating it without bound.
/// </summary>
internal static class CardCommentNitFieldKeys
{
    /// <summary><see langword="true"/> only on the comment that raised the nit
    /// (<see cref="CardComment.IsNit"/>). Omitted (never written) on every ordinary comment.</summary>
    internal const string IsNit = "is-nit";

    /// <summary>The reviewer's optional marking (<see cref="CardComment.Required"/>) — advisory
    /// only, never consulted when validating a disposition (review-certification: "that marking
    /// SHALL NOT bind the architect's disposition"). Omitted when <see langword="false"/>.</summary>
    internal const string Required = "required";

    /// <summary>The sites the nit names, comma-joined (<see cref="CardComment.Sites"/>) —
    /// guidance to whoever picks up the fix, so they know where to start (review-certification:
    /// "guidance to whoever does the work and SHALL NOT be treated as a bound on what the fix may
    /// touch — where the reviewer noticed the problem, not a claim about where the problem ends").
    /// Omitted when empty.</summary>
    internal const string Sites = "sites";

    /// <summary>The disposition this comment records (<see cref="CardComment.Disposition"/>) —
    /// present only on a disposition comment, never on the nit-raising comment itself. Omitted when
    /// not set.</summary>
    internal const string Disposition = "disposition";

    /// <summary>Every nit-only comment-header key this build recognises, in the order
    /// <see cref="CardFileWriter"/> emits them (after the six a plain comment already carries). The
    /// one list <see cref="CardFileParser"/>'s known-key set is built from — see this type's own
    /// doc comment.</summary>
    internal static readonly IReadOnlyList<string> All = [IsNit, Required, Sites, Disposition];
}
