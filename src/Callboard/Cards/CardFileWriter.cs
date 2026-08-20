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
        builder.Append("id: ").Append(frontmatter.Id).Append('\n');
        builder.Append("kind: ").Append(frontmatter.Kind.ToWireString()).Append('\n');
        builder.Append("title: ").Append(frontmatter.Title).Append('\n');
        builder.Append("status: ").Append(frontmatter.Status).Append('\n');
        builder.Append("owner: ").Append(frontmatter.Owner.ToWireString()).Append('\n');
        builder.Append("scope: ").Append(frontmatter.Scope.ToWireString()).Append('\n');
        builder.Append("section: ").Append(frontmatter.Section).Append('\n');
        builder.Append("created: ").Append(FormatTimestamp(frontmatter.Created)).Append('\n');
        builder.Append("updated: ").Append(FormatTimestamp(frontmatter.Updated)).Append('\n');
        builder.Append(CardFileFormat.FrontmatterFence).Append('\n');

        AppendContent(builder, card.Body);

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
        fields.Append("id=").Append(comment.Id);
        fields.Append(" author=").Append(comment.Author.ToWireString());

        if (comment.ReplyTo is { } replyTo)
        {
            fields.Append(" reply-to=").Append(replyTo);
        }

        if (comment.To is { } to)
        {
            fields.Append(" to=").Append(to.ToWireString());
        }

        fields.Append(" resolved=").Append(comment.Resolved ? "true" : "false");
        fields.Append(" timestamp=").Append(FormatTimestamp(comment.Timestamp));

        return fields.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
