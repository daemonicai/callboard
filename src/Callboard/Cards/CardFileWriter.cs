using System.Globalization;
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
            onDecision: static () => false);

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

            if (blockFields.Round is { } round)
            {
                builder.Append("round: ").Append(round.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            if (blockFields.BlockedBy.Length > 0)
            {
                builder.Append("blocked_by: ").Append(CardFileFormat.JoinFrontmatterList(blockFields.BlockedBy)).Append('\n');
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

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
