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
/// </summary>
internal sealed record NewCardFile(CardFrontmatter Frontmatter, string Body);
