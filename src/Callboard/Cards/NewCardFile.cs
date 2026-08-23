namespace Callboard.Cards;

/// <summary>
/// The shape <see cref="CardStore.WriteCard"/> accepts to create a brand-new card (card-model §4
/// remediation, R3). Carries only <see cref="Frontmatter"/> and <see cref="Body"/> — no
/// <see cref="CardFile.Comments"/>, no <see cref="CardFile.Handovers"/>. A freshly created card has
/// no comment thread and no ownership-handover history by definition, so the shape that could let
/// <see cref="CardFrontmatter.Owner"/> disagree with a caller-supplied <see cref="CardFile.Handovers"/>
/// tail — or silently drop a caller-supplied <see cref="CardFile.Comments"/> list — is not
/// representable here at all, rather than accepted and then checked. See
/// <see cref="CardStore.WriteCard"/>'s own doc comment for what this closes.
///
/// <para>
/// <b>Confirmed not to compile (§3/§4's standard):</b> the exact mistake this closes —
/// <c>CardStore.WriteCard(root, path, new CardFile(fm with { Owner = CardOwner.Worker }, "Body.",
/// [], [], [new CardHandover(CardOwner.Architect, CardOwner.Reviewer, t, [])]), timeout,
/// changeName)</c>, a <c>CardFile</c> whose <c>Owner</c> disagrees with its own <c>Handovers</c>
/// tail — was added to a scratch test file, built, and confirmed to fail with CS1503 ("cannot
/// convert from 'CardFile' to 'NewCardFile'"), then discarded. Not a refusal check: this method's
/// input has no <c>Handovers</c> parameter for the mistake to occupy.
/// </para>
///
/// <para>
/// <b><see cref="FindingFields"/> (§6 block B addition):</b> the one kind-specific field this type
/// carries at creation time, unlike <see cref="CardFile.BlockFields"/> or
/// <see cref="CardFile.SectionFields"/>, which every existing creation site leaves at their default
/// and populates later through a dedicated write verb (<c>block transition</c>, <c>block gate</c>,
/// …). A <c>finding</c> card has no such follow-up verb — work-lifecycle's "Clean findings are
/// cards" fields (instrument, extent, <c>verified_at</c>, the blind-spot declaration) are known in
/// full at the moment the finding is recorded, never filled in afterwards — so <see cref="CardStore.
/// WriteCard"/> has to be able to write them at creation or nothing in this codebase ever could:
/// block A built <see cref="Cards.FindingCardFields"/> and the wire keys, but never threaded a value
/// through this type, so every finding a build before this one could construct actually reached disk
/// with its four fields silently defaulted to <see cref="Cards.FindingCardFields.Empty"/> regardless
/// of what a caller supplied — this parameter is what closes that gap. <see langword="null"/> (the
/// default) means "not a finding, or no finding fields to carry", normalised the same way
/// <see cref="CardFile"/>'s own <see cref="CardFile.FindingFields"/> property does.
/// </para>
///
/// <para>
/// <b><see cref="RegisterFields"/> (§7 block A addition):</b> the same "known in full at creation"
/// reasoning as <see cref="FindingFields"/> — a hazard's <c>condition</c>/<c>cadence</c> are refused
/// at creation when absent (register: "the system refuses and states the condition it requires"),
/// so they are never filled in by a later verb. <see langword="null"/> means the same thing it does
/// for <see cref="FindingFields"/>: "not a register kind, or no register fields to carry".
/// </para>
/// </summary>
internal sealed record NewCardFile(CardFrontmatter Frontmatter, string Body, FindingCardFields? FindingFields = null, RegisterCardFields? RegisterFields = null);
