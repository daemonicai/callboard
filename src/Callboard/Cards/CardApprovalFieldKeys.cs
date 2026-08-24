namespace Callboard.Cards;

/// <summary>
/// The wire-key vocabulary <see cref="CardApprovalClaim"/> and <see cref="CardApprovalLimit"/> lines
/// use — one shared declaration <see cref="CardFileWriter"/> and <see cref="CardFileParser"/> both
/// read, rather than two independently hand-typed lists (§8 block A, the wire-key drift guard
/// carried from §7's close — "whatever new keys the certification sequence introduces get a single
/// shared declaration from the start"). §7 block C's own remediation named exactly the defect two
/// hand-maintained lists invites: a writer-known, parser-unknown key gets filed as unknown and
/// re-emitted alongside the known line on every parse-then-write cycle, duplicating it without
/// bound.
/// </summary>
internal static class CardApprovalFieldKeys
{
    internal const string Id = "id";
    internal const string Round = "round";
    internal const string Text = "text";

    /// <summary>Every field a <c>callboard:claim</c> line carries, in the order
    /// <see cref="CardFileWriter"/> emits them.</summary>
    internal static readonly IReadOnlyList<string> Claim = [Id, Round, Text];

    /// <summary>Every field a <c>callboard:limit</c> line carries — the same as
    /// <see cref="Claim"/> minus <see cref="Id"/>, since a limit is never individually addressed
    /// (<see cref="CardApprovalLimit"/>'s own doc comment).</summary>
    internal static readonly IReadOnlyList<string> Limit = [Round, Text];
}
