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
}
