namespace Callboard.Cards;

/// <summary>
/// The four frontmatter fields findings' "Clean findings are cards, distinct from rules" names as
/// known on a <c>finding</c> card only: <c>instrument</c> (the instrument used), <c>extent</c> (the
/// extent covered — see <see cref="FindingExtent"/>), <c>verified_at</c> (the state it was verified
/// against), and <c>blind_spot</c> (the declared blind spot — see
/// <see cref="FindingBlindSpotDeclaration"/>). Not part of <see cref="CardFrontmatter"/> — see that
/// type's doc comment for why kind-specific fields live in their own type instead, the same reason
/// <see cref="BlockCardFields"/> and <see cref="SectionCardFields"/> are their own types.
///
/// <para>
/// <b>Known only on a <c>finding</c> card (Architect ruling, §6 block A brief).</b> The same keys
/// hand-written on a <c>block</c>, <c>question</c>, or any other kind stay preserved-unknown on
/// <see cref="CardFile.UnknownFrontmatterFields"/>, untouched — exactly the convention
/// <see cref="BlockCardFields"/>'s own doc comment states. <see cref="CardFileParser"/> is what
/// decides which of the homes a given card's keys land in, based on the card's own <c>kind</c>. A
/// <c>rule</c> deliberately carries none of these fields — findings: "A <c>rule</c> SHALL carry none
/// of these fields" — satisfied by these fields living here rather than on a type every kind shares.
/// </para>
///
/// <para>
/// <b><see cref="Instrument"/> and <see cref="VerifiedAt"/> are optional here</b> — the same "block A
/// carries the vocabulary, not the enforcement" convention <see cref="BlockCardFields"/>'s own doc
/// comment states; whether they must be set before a finding may be recorded is a later block's
/// refusal, not this type's job. <see cref="VerifiedAt"/> is a state name, recorded as supplied — it
/// is never validated against git (Product Owner ruling, §6 DEVLOG brief: <c>callboard</c> never
/// invokes git).
/// </para>
///
/// <para>
/// <b><see cref="Extent"/> and <see cref="BlindSpot"/> are not optional here.</b> <see cref="Extent"/>
/// always carries one of <see cref="FindingExtent"/>'s three cases — an undeclared extent is not
/// "no extent", it is <see cref="FindingExtent.BlockScope"/> by default (findings: "Where no extent
/// is declared, the system SHALL default to the scope of the block that raised the finding"), so
/// there is no absent state for this property to represent. <see cref="BlindSpot"/> always carries
/// one of <see cref="FindingBlindSpotDeclaration"/>'s two cases for the reason that type's own doc
/// comment states — a third, undeclared state is unrepresentable by construction, not merely
/// discouraged.
/// </para>
/// </summary>
internal sealed record FindingCardFields
{
    /// <summary>The instrument used to produce this finding, or <see langword="null"/> when not yet
    /// recorded.</summary>
    internal string? Instrument { get; init; }

    /// <summary>The extent this finding covers. Defaults to <see cref="FindingExtent.BlockScope"/>
    /// when no extent was declared — see this type's own doc comment.</summary>
    internal FindingExtent Extent { get; init; }

    /// <summary>The state this finding was verified against, recorded as supplied, or
    /// <see langword="null"/> when not yet recorded.</summary>
    internal string? VerifiedAt { get; init; }

    /// <summary>The declared blind spot — never "undeclared"; see this type's own doc comment and
    /// <see cref="FindingBlindSpotDeclaration"/>'s.</summary>
    internal FindingBlindSpotDeclaration BlindSpot { get; init; }

    /// <summary>The four fields at their defaults — every card that is not a <c>finding</c>. Not a
    /// state a real finding is meant to be recorded in: <see cref="BlindSpot"/> here is
    /// <see cref="FindingBlindSpotDeclaration.None"/> only because this type's public surface
    /// requires <em>some</em> concrete declaration, the same way <see cref="Extent"/> here is
    /// <see cref="FindingExtent.BlockScope"/> only because absence itself is not representable —
    /// whether a real finding may be recorded with neither declared is 6.2's refusal, not this
    /// type's concern.</summary>
    internal static readonly FindingCardFields Empty = new(null, FindingExtent.BlockScope, null, FindingBlindSpotDeclaration.None);

    internal FindingCardFields(string? Instrument, FindingExtent Extent, string? VerifiedAt, FindingBlindSpotDeclaration BlindSpot)
    {
        this.Instrument = Instrument;
        this.Extent = Extent;
        this.VerifiedAt = VerifiedAt;
        this.BlindSpot = BlindSpot;
    }
}
