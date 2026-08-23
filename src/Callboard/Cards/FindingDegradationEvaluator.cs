namespace Callboard.Cards;

/// <summary>
/// Computes findings' "A `finding` SHALL be section-scoped and SHALL degrade at section close" (§6
/// block D) for one already-read <c>finding</c> card and the path it was read from — read-only, no
/// lock, the same shape <see cref="FindingStalenessEvaluator"/> already established.
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
/// <b>Finding the section card.</b> A <c>finding</c>'s <see cref="CardFrontmatter.Section"/> is a
/// free-text label (e.g. <c>"6"</c>), not a path or an id — identity addressing is §7/§8's open
/// decision, so this stays path-addressed like every other §6 verb. <see
/// cref="CardLayout.DirectoryFor"/> resolves <see cref="CardScope.Section"/> and <see
/// cref="CardScope.Change"/> to the <em>same</em> directory (one flat <c>changes/&lt;name&gt;/</c>
/// per change, not one per section), so this method reads every card in the finding's own
/// containing directory (<see cref="Cards.CardStore.ReadAllCards"/>) and matches every <c>section</c>
/// card whose own <see cref="CardFrontmatter.Section"/> equals the finding's.
/// </para>
///
/// <para>
/// <b>More than one match is refused, not picked (reviewer blocker, §6 block D remediation).</b>
/// Nothing in this codebase's write model guards "at most one <c>section</c> card per label" — there
/// is no section-creation verb at all yet — so a duplicate or hand-edited label collision is
/// reachable, and picking whichever file <see cref="Cards.CardStore.ReadAllCards"/>'s ordinal
/// enumeration happened to return first would make the answer depend on filenames rather than on the
/// record. This method returns <see cref="FindingDegradationEvaluation.Ambiguous"/> instead — the
/// caller (<see cref="Callboard.Cli.CommandDispatcher.RunFindingStatus"/>) turns that into a
/// refusal.
/// </para>
///
/// <para>
/// <b>An unresolvable section reads as <see cref="FindingDegradationStatus.Live"/> only when the
/// directory holds no candidate at all — no card of any kind that could be this finding's section
/// card.</b> If the directory contains zero <c>section</c> cards and every card in it parsed
/// cleanly, there genuinely is no section card yet, and reporting
/// <see cref="FindingDegradationStatus.Degraded"/> would be a false claim this method cannot
/// support. That is the only case this method reads as <see cref="FindingDegradationStatus.Live"/>
/// for a finding with no exact label match.
/// </para>
///
/// <para>
/// <b>§6 remediation (B3) — zero matches among candidates is not the same as zero candidates.</b>
/// <c>--section</c> is unvalidated free text, and there is no section-creation verb: a typo
/// (<c>"6 "</c> against a card labelled <c>"6"</c>) is structurally indistinguishable from a
/// genuinely different section (label <c>"5"</c>). So when the directory holds at least one
/// <c>section</c> card that parsed cleanly but did not match this finding's label — <em>or</em> at
/// least one card that failed to parse at all — this method cannot rule out that one of them is this
/// finding's own section card, mislabelled or corrupt, and reporting
/// <see cref="FindingDegradationStatus.Live"/> would silently equate "no section card exists" with
/// "one exists and cannot be confirmed to match", exactly the "absent is a different answer from
/// failed" convention §3 established and this method's own block D remediation already applied to
/// the corrupt-card case (reviewer blocker). Both are now the same case, reusing
/// <see cref="FindingDegradationStatus.Unreadable"/> rather than minting a fourth answer for the
/// same idea — the reason names whichever kind of non-matching candidate was found, or both.
/// </para>
/// </summary>
internal static class FindingDegradationEvaluator
{
    internal static FindingDegradationEvaluation Evaluate(CardFile finding, string findingFilePath)
    {
        var directory = Path.GetDirectoryName(findingFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Live);
        }

        var matches = new List<(string FilePath, CardFile Card)>();
        var otherSectionPaths = new List<string>();
        var unreadablePaths = new List<string>();

        foreach (var (path, result) in CardStore.ReadAllCards(directory))
        {
            var card = result.Match<CardFile?>(
                onSuccess: static success => success.Card,
                onFailure: static _ => null);

            if (card is null)
            {
                unreadablePaths.Add(path);
                continue;
            }

            if (!CardStore.IsSectionCard(card))
            {
                continue;
            }

            if (string.Equals(card.Frontmatter.Section, finding.Frontmatter.Section, StringComparison.Ordinal))
            {
                matches.Add((path, card));
            }
            else
            {
                // §6 remediation (B3) — a readable section card that does not match is still a
                // candidate that cannot be ruled out: the label is free text, so this could be the
                // finding's own section card under a typo, not proof a different section exists.
                otherSectionPaths.Add(path);
            }
        }

        if (matches.Count > 1)
        {
            var conflictingPaths = matches.Select(static match => match.FilePath).OrderBy(static path => path, StringComparer.Ordinal).ToList();
            return FindingDegradationEvaluation.Ambiguous(finding.Frontmatter.Section, conflictingPaths);
        }

        if (matches.Count == 1)
        {
            var status = matches[0].Card.SectionFields.ClosedAt is not null ? FindingDegradationStatus.Degraded : FindingDegradationStatus.Live;
            return FindingDegradationEvaluation.Resolved(status);
        }

        if (unreadablePaths.Count > 0 || otherSectionPaths.Count > 0)
        {
            unreadablePaths.Sort(StringComparer.Ordinal);
            otherSectionPaths.Sort(StringComparer.Ordinal);

            var reasonParts = new List<string>();
            if (otherSectionPaths.Count > 0)
            {
                reasonParts.Add(
                    $"{otherSectionPaths.Count} readable 'section' card(s) in that directory carry a different label and cannot be ruled out as a mislabelled match: {string.Join(", ", otherSectionPaths)}");
            }

            if (unreadablePaths.Count > 0)
            {
                reasonParts.Add(
                    $"{unreadablePaths.Count} card(s) in that directory could not be parsed and cannot be ruled out as it: {string.Join(", ", unreadablePaths)}");
            }

            return FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Unreadable(
                $"no readable 'section' card in '{directory}' carries the label '{finding.Frontmatter.Section}', but {string.Join("; and ", reasonParts)}."));
        }

        return FindingDegradationEvaluation.Resolved(FindingDegradationStatus.Live);
    }
}
