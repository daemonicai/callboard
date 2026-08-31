namespace Callboard.Cards;

/// <summary>
/// What a caller declares when recording a clean finding whose blind-spot declaration is not
/// <see cref="FindingBlindSpotDeclaration.None"/> (findings: "A declared blind spot … SHALL be
/// raised as an <c>obligation</c> or a <c>hazard</c>"), carrying everything <see cref="CardStore.
/// RecordFinding"/> needs to write that second card itself — the caller never supplies the id
/// (§6 block B Architect ruling: "the tool raises the blind spot; the caller does not pre-raise
/// it"), only what the raised card should say.
///
/// <para>
/// <b><see cref="Kind"/> is restricted to <see cref="CardKind.Obligation"/> or
/// <see cref="CardKind.Hazard"/> by this type's own constructor</b> — "whether the blind spot is an
/// <c>obligation</c> or a <c>hazard</c> is the caller's declaration, never inferred" (§6 block B
/// brief), but it is still exactly one of those two, never any other <see cref="CardKind"/>. The one
/// caller that ever constructs this (<c>CommandParser.ParseFindingRecord</c>) only ever passes one of
/// the two after checking <c>--blind-spot</c>'s own wire value, so this check is a second,
/// independent statement of the same restriction rather than the only one — the same "verify an
/// invariant rather than merely rely on the one call site that currently upholds it" discipline
/// <see cref="FindingExtent"/>'s own validating accessors already apply.
/// </para>
/// </summary>
internal sealed record FindingBlindSpotRaiseRequest
{
    internal CardKind Kind { get; }

    internal string Title { get; }

    /// <summary>
    /// The blind spot's own content. Never recorded on the finding itself — findings: "A declared
    /// blind spot SHALL NOT be recorded as part of the clean result" — this is where it actually
    /// lives.
    /// </summary>
    internal string Body { get; }

    /// <summary>
    /// 14.5-remediation (§14 supervisor finding): this type no longer carries a <c>FilePath</c>.
    /// <see cref="CardStore.RecordFinding"/> names the raised card's file itself, the same
    /// "container, then allocate, then <see cref="CardLayout.FileNameFor"/>" ordering
    /// <see cref="CardStore.CreateCard"/> already established (14.5) — a caller was never able to
    /// supply one here in the first place, so there is nothing left for this type to carry.
    /// </summary>
    internal FindingBlindSpotRaiseRequest(CardKind kind, string title, string body)
    {
        if (!ReferenceEquals(kind, CardKind.Obligation) && !ReferenceEquals(kind, CardKind.Hazard))
        {
            throw new ArgumentException(
                $"a declared blind spot can only be raised as '{CardKind.Obligation.ToWireString()}' or " +
                $"'{CardKind.Hazard.ToWireString()}', not '{kind.ToWireString()}'.",
                nameof(kind));
        }

        Kind = kind;
        Title = title;
        Body = body;
    }
}
