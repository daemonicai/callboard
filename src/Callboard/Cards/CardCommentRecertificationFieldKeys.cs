namespace Callboard.Cards;

/// <summary>
/// The wire-key vocabulary a recertification-record <see cref="CardComment"/> header adds to the
/// six a plain comment already carries — one shared declaration <see cref="CardFileWriter"/> and
/// <see cref="CardFileParser"/> both read, rather than the key being hand-typed twice (§8 block C,
/// the wire-key drift guard carried from §7's close: "whatever new keys the certification sequence
/// introduces get a single shared declaration from the start"). Same reasoning
/// <see cref="CardCommentNitFieldKeys"/>'s own doc comment gives for its own four keys; kept as its
/// own type rather than folded into that one because this key has nothing to do with a nit.
/// </summary>
internal static class CardCommentRecertificationFieldKeys
{
    /// <summary><see langword="true"/> only on the comment <see cref="CardStore.
    /// RecordRecertification"/> appends to record one <c>block recertify</c> call
    /// (<see cref="CardComment.IsRecertification"/>). Omitted (never written) on every other
    /// comment.</summary>
    internal const string IsRecertification = "is-recertification";

    /// <summary>Every recertification-only comment-header key this build recognises. The one list
    /// <see cref="CardFileParser"/>'s known-key set is built from — see this type's own doc
    /// comment.</summary>
    internal static readonly IReadOnlyList<string> All = [IsRecertification];
}
