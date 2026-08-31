namespace Callboard.Cards;

/// <summary>
/// What a caller declares when dispositioning a nit <see cref="NitDisposition.Defer"/> or
/// <see cref="NitDisposition.Decline"/> (review-certification: "Nits carry a disposition" — "defer:
/// Promoted to an <c>obligation</c> card", "decline: Promoted to a <c>decision</c> card"), carrying
/// everything <see cref="CardStore.DispositionNit"/> needs to write that second card itself. Same
/// shape as <see cref="FindingBlindSpotRaiseRequest"/> for the same reason: the caller never
/// supplies the raised card's own id — the tool allocates it — only what the raised card should
/// say.
///
/// <para>
/// <b><see cref="Kind"/> is restricted to <see cref="CardKind.Obligation"/> or
/// <see cref="CardKind.Decision"/> by this type's own constructor</b> — which of the two follows
/// mechanically from the disposition (<c>defer</c> → obligation, <c>decline</c> → decision), never
/// a caller's free choice, the same "verify an invariant rather than merely rely on the one call
/// site that currently upholds it" discipline <see cref="FindingBlindSpotRaiseRequest"/>'s own
/// constructor already applies.
/// </para>
/// </summary>
internal sealed record NitDispositionRaiseRequest
{
    internal CardKind Kind { get; }

    internal string Title { get; }

    /// <summary>The raised card's own content — for <c>defer</c>, what discharges the obligation;
    /// for <c>decline</c>, the reason the code is right as it stands (review-certification: "load-
    /// bearing for <c>decline</c>, which becomes a decision card whose whole content is the reason
    /// the code is right as it stands"). The same text also becomes the disposition comment's own
    /// body — see <see cref="CardStore.DispositionNit"/>.</summary>
    internal string Body { get; }

    /// <summary>
    /// 14.5-remediation (§14 supervisor finding, second round): this type no longer carries a
    /// <c>FilePath</c> — <see cref="CardStore.DispositionNit"/> names the raised card itself, the
    /// same "container, then allocate, then <see cref="CardLayout.FileNameFor"/>" ordering every
    /// other card-minting door in this codebase now follows.
    /// </summary>
    internal NitDispositionRaiseRequest(CardKind kind, string title, string body)
    {
        if (!ReferenceEquals(kind, CardKind.Obligation) && !ReferenceEquals(kind, CardKind.Decision))
        {
            throw new ArgumentException(
                $"a dispositioned nit can only be raised as '{CardKind.Obligation.ToWireString()}' or " +
                $"'{CardKind.Decision.ToWireString()}', not '{kind.ToWireString()}'.",
                nameof(kind));
        }

        Kind = kind;
        Title = title;
        Body = body;
    }
}
