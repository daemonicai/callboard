using System.Globalization;
using System.Linq;
using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Serialises a <see cref="CardFile"/> back to the ADR-0003 text format
/// <see cref="CardFileParser"/> reads — hand-rolled for the same reason (see that type's doc
/// comment for the AOT verdict). Frontmatter fields are written in a fixed order so the format
/// is diffable per card: a change to one field is one line's diff, not a shuffle.
/// </summary>
internal static class CardFileWriter
{
    internal static string Serialize(CardFile card)
    {
        var builder = new StringBuilder();
        var frontmatter = card.Frontmatter;

        builder.Append(CardFileFormat.FrontmatterFence).Append('\n');
        builder.Append("id: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Id)).Append('\n');
        builder.Append("kind: ").Append(frontmatter.Kind.ToWireString()).Append('\n');
        builder.Append("title: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Title)).Append('\n');
        builder.Append("status: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Status)).Append('\n');
        builder.Append("owner: ").Append(frontmatter.Owner.ToWireString()).Append('\n');
        builder.Append("scope: ").Append(frontmatter.Scope.ToWireString()).Append('\n');
        builder.Append("section: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Section)).Append('\n');
        builder.Append("created: ").Append(FormatTimestamp(frontmatter.Created)).Append('\n');
        builder.Append("updated: ").Append(FormatTimestamp(frontmatter.Updated)).Append('\n');

        // §5's five block-only fields: emitted, in this fixed order, only for a block card, and
        // only the ones actually recorded — the same "present only when set" convention
        // BuildHeaderFields below already applies to a comment's optional reply-to/to/resolves, not
        // the "always present, empty when unset" convention section above uses. A freshly created
        // block card with none of the five set round-trips to exactly the same nine-field shape as
        // before this field existed, rather than gaining five blank lines. A card of any other kind
        // never reaches here with non-empty BlockFields (CardFileParser only ever populates it for
        // kind block), so this block is silently a no-op for every other kind rather than needing
        // its own guard.
        var isBlockCard = frontmatter.Kind.Match(
            onBlock: static () => true,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        if (isBlockCard)
        {
            var blockFields = card.BlockFields;

            if (blockFields.Base is { } baseCommit)
            {
                builder.Append("base: ").Append(CardFileFormat.EscapeFrontmatterValue(baseCommit)).Append('\n');
            }

            if (blockFields.ReviewedState is { } reviewedState)
            {
                builder.Append("reviewed_state: ").Append(CardFileFormat.EscapeFrontmatterValue(reviewedState)).Append('\n');
            }

            if (blockFields.Tasks.Length > 0)
            {
                builder.Append("tasks: ").Append(CardFileFormat.JoinFrontmatterList(blockFields.Tasks)).Append('\n');
            }

            if (blockFields.GateResults.Length > 0)
            {
                var gateItems = blockFields.GateResults
                    .Select(static result =>
                        $"{result.Label}={result.ExitCode.ToString(CultureInfo.InvariantCulture)}={result.Round.ToString(CultureInfo.InvariantCulture)}")
                    .ToList();
                builder.Append("gate_results: ").Append(CardFileFormat.JoinFrontmatterList(gateItems)).Append('\n');
            }

            if (blockFields.Round is { } round)
            {
                builder.Append("round: ").Append(round.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            if (blockFields.BlockedBy.Length > 0)
            {
                builder.Append("blocked_by: ").Append(CardFileFormat.JoinFrontmatterList(blockFields.BlockedBy)).Append('\n');
            }
        }

        // §5 block E's three section-only scalar fields — same "present only when set" convention
        // as the block fields above, and the same guarantee that a card of any other kind never
        // reaches here with non-empty SectionFields (CardFileParser only ever populates it for kind
        // section).
        var isSectionCard = frontmatter.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => true);

        if (isSectionCard)
        {
            var sectionFields = card.SectionFields;

            if (sectionFields.Base is { } baseCommit)
            {
                builder.Append("base: ").Append(CardFileFormat.EscapeFrontmatterValue(baseCommit)).Append('\n');
            }

            if (sectionFields.ClosedBy is { } closedBy)
            {
                builder.Append("closed_by: ").Append(closedBy.ToWireString()).Append('\n');
            }

            if (sectionFields.ClosedAt is { } closedAt)
            {
                builder.Append("closed_at: ").Append(FormatTimestamp(closedAt)).Append('\n');
            }
        }

        // §6 block A's four finding-only fields — same "present only when set" convention as the
        // block/section fields above, and the same guarantee that a card of any other kind never
        // reaches here with non-default FindingFields (CardFileParser only ever populates it for
        // kind finding). Extent's own default (FindingExtent.BlockScope) writes nothing at all —
        // an undeclared extent and a wire-absent extent are the same state, by design (see
        // FindingCardFields' own doc comment). BlindSpot is always emitted: it can never be the
        // "not yet recorded" state the other optional fields represent by omission, because that
        // state is not representable on FindingCardFields.BlindSpot in the first place.
        var isFindingCard = frontmatter.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => true,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        if (isFindingCard)
        {
            var findingFields = card.FindingFields;

            if (findingFields.Instrument is { } instrument)
            {
                builder.Append("instrument: ").Append(CardFileFormat.EscapeFrontmatterValue(instrument)).Append('\n');
            }

            var (extentForm, extentValue) = findingFields.Extent.Match(
                onInstrument: static command => ("instrument", CardFileFormat.EscapeFrontmatterValue(command)),
                onExplicit: static items => ("explicit", CardFileFormat.JoinFrontmatterList(items)),
                onBlockScope: static () => ((string?)null, (string?)null));

            if (extentForm is { } form)
            {
                builder.Append("extent: ").Append(form).Append('\n');
                builder.Append("extent_value: ").Append(extentValue).Append('\n');
            }

            if (findingFields.VerifiedAt is { } verifiedAt)
            {
                builder.Append("verified_at: ").Append(CardFileFormat.EscapeFrontmatterValue(verifiedAt)).Append('\n');
            }

            var (blindSpotForm, blindSpotCardId) = findingFields.BlindSpot.Match(
                onNone: static () => ("none", (string?)null),
                onRaisedAs: static cardId => ("raised-as", (string?)cardId));

            builder.Append("blind_spot: ").Append(blindSpotForm).Append('\n');
            if (blindSpotCardId is { } cardId)
            {
                builder.Append("blind_spot_card: ").Append(CardFileFormat.EscapeFrontmatterValue(cardId)).Append('\n');
            }
        }

        // Unknown fields (a §5/§6 field this build does not model, or a hand-added line) are
        // re-emitted after the known ones rather than interleaved back into their original
        // position — the parser records only the value at each known key, not a full original
        // line ordering, so exact interleaving cannot be reconstructed. What matters is that
        // nothing is lost: the raw key and the raw (already-escaped) value survive verbatim.
        foreach (var (key, rawValue) in card.UnknownFrontmatterFields)
        {
            builder.Append(key).Append(": ").Append(rawValue).Append('\n');
        }

        builder.Append(CardFileFormat.FrontmatterFence).Append('\n');

        AppendContent(builder, card.Body);

        // Handovers before comments — a fixed, deterministic layout (like the unknown-frontmatter-
        // fields convention above), not a claim about the physical order handovers and comments
        // actually happened in relative to each other. Each sequence's own internal order (oldest
        // first) is what the append-only guarantee is actually about, and that survives exactly:
        // CardStore only ever appends to one list or the other under the card's lock, never
        // reorders either.
        foreach (var handover in card.Handovers)
        {
            builder.Append(CardFileFormat.HandoverLinePrefix)
                .Append(BuildHandoverFields(handover))
                .Append(CardFileFormat.HandoverLineSuffix)
                .Append('\n');
        }

        // Transitions after handovers, before comments — the same fixed, deterministic layout
        // convention as handovers-before-comments above; each sequence's own internal order
        // (oldest first) is what the append-only guarantee is actually about.
        foreach (var transition in card.Transitions)
        {
            builder.Append(CardFileFormat.TransitionLinePrefix)
                .Append(BuildTransitionFields(transition))
                .Append(CardFileFormat.TransitionLineSuffix)
                .Append('\n');
        }

        // Verdicts after transitions, before comments — the same fixed, deterministic layout
        // convention as handovers-before-transitions above; each sequence's own internal order
        // (oldest first) is what the append-only guarantee is actually about. A section may
        // accumulate more than one verdict across supervisor rounds (work-lifecycle §3c: request
        // changes, remediate, re-review), so this is its own append-only sequence for the same
        // reason Transitions is not folded into a scalar.
        foreach (var verdict in card.SectionFields.Verdicts)
        {
            builder.Append(CardFileFormat.VerdictLinePrefix)
                .Append(BuildVerdictFields(verdict))
                .Append(CardFileFormat.VerdictLineSuffix)
                .Append('\n');
        }

        foreach (var comment in card.Comments)
        {
            builder.Append(CardFileFormat.CommentHeaderPrefix)
                .Append(BuildHeaderFields(comment))
                .Append(CardFileFormat.CommentHeaderSuffix)
                .Append('\n');

            AppendContent(builder, comment.Body);

            builder.Append(CardFileFormat.CommentFooter).Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendContent(StringBuilder builder, string content)
    {
        if (content.Length == 0)
        {
            return;
        }

        foreach (var line in content.Split('\n'))
        {
            builder.Append(CardFileFormat.EscapeContentLine(line)).Append('\n');
        }
    }

    private static string BuildHeaderFields(CardComment comment)
    {
        var fields = new StringBuilder();
        fields.Append("id=").Append(CardFileFormat.EscapeCommentHeaderValue(comment.Id));
        fields.Append(" author=").Append(comment.Author.ToWireString());

        if (comment.ReplyTo is { } replyTo)
        {
            fields.Append(" reply-to=").Append(CardFileFormat.EscapeCommentHeaderValue(replyTo));
        }

        if (comment.To is { } to)
        {
            fields.Append(" to=").Append(to.ToWireString());
        }

        if (comment.Resolves is { } resolves)
        {
            fields.Append(" resolves=").Append(CardFileFormat.EscapeCommentHeaderValue(resolves));
        }

        fields.Append(" timestamp=").Append(FormatTimestamp(comment.Timestamp));

        foreach (var (key, rawValue) in comment.UnknownHeaderFields)
        {
            fields.Append(' ').Append(key).Append('=').Append(rawValue);
        }

        return fields.ToString();
    }

    private static string BuildHandoverFields(CardHandover handover)
    {
        var fields = new StringBuilder();
        fields.Append("by=").Append(handover.By.ToWireString());
        fields.Append(" to=").Append(handover.To.ToWireString());
        fields.Append(" timestamp=").Append(FormatTimestamp(handover.Timestamp));

        foreach (var (key, rawValue) in handover.UnknownFields)
        {
            fields.Append(' ').Append(key).Append('=').Append(rawValue);
        }

        return fields.ToString();
    }

    private static string BuildTransitionFields(CardBlockTransitionEntry transition)
    {
        var fields = new StringBuilder();
        fields.Append("by=").Append(transition.By.ToWireString());
        fields.Append(" name=").Append(transition.Name);
        fields.Append(" from=").Append(transition.From.ToWireString());
        fields.Append(" to=").Append(transition.To.ToWireString());
        fields.Append(" timestamp=").Append(FormatTimestamp(transition.Timestamp));

        foreach (var (key, rawValue) in transition.UnknownFields)
        {
            fields.Append(' ').Append(key).Append('=').Append(rawValue);
        }

        return fields.ToString();
    }

    private static string BuildVerdictFields(SectionVerdictEntry verdict)
    {
        var fields = new StringBuilder();
        fields.Append("by=").Append(verdict.By.ToWireString());
        fields.Append(" verdict=").Append(verdict.Verdict.ToWireString());
        fields.Append(" range-from=").Append(CardFileFormat.EscapeCommentHeaderValue(verdict.RangeFrom));
        fields.Append(" range-to=").Append(CardFileFormat.EscapeCommentHeaderValue(verdict.RangeTo));
        fields.Append(" timestamp=").Append(FormatTimestamp(verdict.Timestamp));

        foreach (var (key, rawValue) in verdict.UnknownFields)
        {
            fields.Append(' ').Append(key).Append('=').Append(rawValue);
        }

        return fields.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
