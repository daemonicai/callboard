namespace Callboard.Cards;

/// <summary>
/// The on-disk delimiters ADR-0003 calls for — YAML-style frontmatter fences and comments as
/// delimited blocks appended at the end of the file — plus the escaping that keeps them
/// unambiguous when a card's own body or comment text happens to contain a line that looks like
/// one. Shared between <see cref="CardFileParser"/> and <see cref="CardFileWriter"/> so the two
/// can never drift apart on what counts as a delimiter.
/// </summary>
internal static class CardFileFormat
{
    internal const string FrontmatterFence = "---";
    internal const string CommentHeaderPrefix = "<!-- callboard:comment ";
    internal const string CommentHeaderSuffix = " -->";
    internal const string CommentFooter = "<!-- /callboard:comment -->";

    /// <summary>
    /// True for a line that, written unescaped, would be misread as a structural delimiter on
    /// the next parse — the header prefix, the footer, or an already-escaped instance of either
    /// (any number of leading backslashes stripped still matches). Escaping is checked against
    /// this, not just the bare patterns, so escaping the same content twice stays invertible.
    /// </summary>
    internal static bool LooksLikeDelimiterOrEscapedDelimiter(string line)
    {
        var unescaped = line.TrimStart('\\');
        return unescaped.StartsWith(CommentHeaderPrefix, StringComparison.Ordinal)
            || string.Equals(unescaped, CommentFooter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Escapes one line of body or comment content for writing: a line that would otherwise be
    /// misread as a delimiter gets exactly one more leading backslash than it already has.
    /// Content that doesn't look like a delimiter is written verbatim.
    /// </summary>
    internal static string EscapeContentLine(string line) =>
        LooksLikeDelimiterOrEscapedDelimiter(line) ? "\\" + line : line;

    /// <summary>
    /// Reverses <see cref="EscapeContentLine"/>: a line escaped for having looked like a
    /// delimiter has exactly one leading backslash stripped back off. A raw structural delimiter
    /// is never passed here — the parser only calls this on lines already known to be content.
    /// </summary>
    internal static string UnescapeContentLine(string line)
    {
        if (line.Length == 0 || line[0] != '\\')
        {
            return line;
        }

        var withoutOneBackslash = line[1..];
        return LooksLikeDelimiterOrEscapedDelimiter(withoutOneBackslash) ? withoutOneBackslash : line;
    }

    /// <summary>An unescaped line marking the start of an appended comment's header.</summary>
    internal static bool IsCommentHeader(string line) =>
        line.StartsWith(CommentHeaderPrefix, StringComparison.Ordinal);

    /// <summary>An unescaped line marking the end of an appended comment's body.</summary>
    internal static bool IsCommentFooter(string line) =>
        string.Equals(line, CommentFooter, StringComparison.Ordinal);

    /// <summary>
    /// Escapes a free-text frontmatter field value (<c>id</c>/<c>title</c>/<c>status</c>/
    /// <c>section</c>) so it always occupies exactly one physical line. Frontmatter is
    /// line-based (<c>key: value</c>), unlike the body/comment format above which is delimiter-
    /// based — a literal newline in a value would otherwise split it across lines and the next
    /// read would hit "malformed frontmatter line" on the fragment. A backslash is escaped first
    /// so the scheme stays invertible regardless of what the value already contains.
    /// </summary>
    internal static string EscapeFrontmatterValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

    /// <summary>Reverses <see cref="EscapeFrontmatterValue"/>.</summary>
    internal static string UnescapeFrontmatterValue(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == 'n')
                {
                    builder.Append('\n');
                    i++;
                    continue;
                }

                if (next == 'r')
                {
                    builder.Append('\r');
                    i++;
                    continue;
                }

                if (next == '\\')
                {
                    builder.Append('\\');
                    i++;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
