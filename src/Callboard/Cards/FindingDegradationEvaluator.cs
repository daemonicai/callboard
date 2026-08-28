using System.Linq;

namespace Callboard.Cards;

/// <summary>
/// Computes findings' "A `finding` SHALL be section-scoped and SHALL degrade at section close" (§6
/// block D) for one already-read <c>finding</c> card — read-only, no lock, the same shape
/// <see cref="FindingStalenessEvaluator"/> already established.
///
/// <para>
/// <b>Degradation is derived, not stored (Architect ruling, §6 block D brief).</b> The finding card
/// carries no field naming its own liveness, and <see cref="Cards.CardStore.WriteCard"/> stays
/// create-only — this method never writes anything. A finding is degraded because its
/// <em>section card</em> is closed, exactly the fact <see
/// cref="Cards.CardStore.CloseSectionUnderExistingLock"/> already records on that section card's own
/// <c>closed_at</c>. This is the same "one source of truth, never a second field that can disagree"
/// answer §5 gave for <c>blocked</c>.
/// </para>
///
/// <para>
/// <b>Finding the section card (§7 block B rewire).</b> A <c>finding</c>'s
/// <see cref="CardFrontmatter.Section"/> is now the section card's own <c>id</c> (Product Owner
/// ruling, "identity is the reference, and identity resolves") — not a free-text label matched by
/// scanning the finding's own directory. This method hands that id straight to
/// <see cref="CardIdentityResolver.Resolve"/>, which walks the whole record — every live change,
/// the register, decisions, and every archived change — rather than the one directory the old
/// label-matching version was confined to. That is also what makes a finding raised in a change
/// that later archives keep degrading correctly: the section card moves with the rest of the change
/// (archive is a directory move, not a rewrite), and the resolver simply finds it wherever it now
/// lives.
/// </para>
///
/// <para>
/// <b>More than one match is refused, not picked.</b> <see cref="CardIdentityResolution.Duplicate"/>
/// becomes <see cref="FindingDegradationEvaluation.Ambiguous"/> here — the caller (<see
/// cref="Callboard.Cli.CommandDispatcher.RunFindingStatus"/>) turns that into a refusal, the same
/// fail-closed shape §6 block D's remediation established for the label-matching mechanism this
/// rewires, now backed by a genuine record-wide id collision rather than a same-label coincidence.
/// </para>
///
/// <para>
/// <b><see cref="CardIdentityResolution.NotFound"/> reads as <see cref="FindingDegradationStatus.Live"/>.</b>
/// The resolver has, by construction, searched the entire record and confirmed no card anywhere
/// carries the id — a stronger, record-wide guarantee than the old evaluator's "this one directory
/// held no candidate" default, and the same honest conclusion follows: nothing proves the section
/// closed (nothing proves it exists at all), so this does not claim <see
/// cref="FindingDegradationStatus.Degraded"/>.
/// </para>
///
/// <para>
/// <b><see cref="CardIdentityResolution.Unreadable"/> — the B3 lesson, now the resolver's own
/// job.</b> When the resolver could not rule out that the requested id lives in a file it failed to
/// read, this reads <see cref="FindingDegradationStatus.Unreadable"/>, not <see
/// cref="FindingDegradationStatus.Live"/> — the same "absent is a different answer from failed"
/// convention §3 established, now enforced once, in the resolver, instead of being re-derived by
/// every consumer that walks the record for an id.
/// </para>
/// </summary>
internal static class FindingDegradationEvaluator
{
    internal static FindingDegradationEvaluation Evaluate(CardFile finding, string cardsRoot)
    {
        var resolution = CardIdentityResolver.Resolve(cardsRoot, finding.Frontmatter.Section);

        return resolution.Match<FindingDegradationEvaluation>(
            onFound: (filePath, card) =>
            {
                if (!CardStore.IsSectionCard(card))
                {
                    return FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Unreadable(
                        $"the card carrying id '{finding.Frontmatter.Section}' — this finding's own 'section' field — " +
                        $"is a '{card.Frontmatter.Kind.ToWireString()}' card at '{filePath}', not a 'section' card, " +
                        "so this finding's degradation cannot be confirmed."));
                }

                var status = card.SectionFields.ClosedAt is not null ? FindingDegradationStatus.Degraded : FindingDegradationStatus.Live;
                return FindingDegradationEvaluation.Resolved(status);
            },
            onNotFound: static _ => FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Live),
            onDuplicate: static (id, filePaths) => FindingDegradationEvaluation.Ambiguous(id, filePaths),
            onCorrupt: (id, claimants) => FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Unreadable(
                $"{claimants.Count} file(s) declaring id '{id}' — this finding's own 'section' field — could not be " +
                $"parsed and cannot be trusted, so its closure cannot be confirmed: " +
                $"{string.Join(", ", claimants.Select(static claimant => $"{claimant.FilePath}: {claimant.Reason}"))}.")),
            onUnreadable: (id, files) => FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Unreadable(
                $"{files.Count} card file(s) in the record could not be read while resolving id '{id}' — this " +
                $"finding's own 'section' field — so its absence cannot be confirmed: {string.Join(", ", files.Select(static file => file.FilePath))}.")));
    }
}
