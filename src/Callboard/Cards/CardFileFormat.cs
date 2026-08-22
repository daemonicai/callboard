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
    /// An ownership-handover entry (card-model 4.5): one self-contained line, no body and no
    /// separate footer, since a handover carries no prose — <c>by</c>/<c>to</c>/<c>timestamp</c>
    /// fields only, same <c>key=value</c> token shape as a comment header.
    /// </summary>
    internal const string HandoverLinePrefix = "<!-- callboard:handover ";
    internal const string HandoverLineSuffix = " -->";

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
            || string.Equals(unescaped, CommentFooter, StringComparison.Ordinal)
            || unescaped.StartsWith(HandoverLinePrefix, StringComparison.Ordinal);
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

    /// <summary>An unescaped, self-contained ownership-handover entry line.</summary>
    internal static bool IsHandoverLine(string line) =>
        line.StartsWith(HandoverLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(HandoverLineSuffix, StringComparison.Ordinal);

    private static readonly IReadOnlyDictionary<char, char> FrontmatterEscapeTable =
        new Dictionary<char, char> { ['n'] = '\n', ['r'] = '\r' };

    private static readonly IReadOnlyDictionary<char, char> CommentHeaderEscapeTable =
        new Dictionary<char, char> { ['s'] = ' ' };

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
    internal static string UnescapeFrontmatterValue(string value) => UnescapeUsing(value, FrontmatterEscapeTable);

    /// <summary>
    /// Escapes a free-text comment-header field value (<c>id</c>/<c>reply-to</c> — the only two
    /// fields in the header that are free text rather than a closed enum or a fixed-format
    /// timestamp) so it can never be misread by the parser's own space-split tokenising of the
    /// header. The header is <c>key=value</c> tokens joined by a single literal
    /// space (see <see cref="CardFileWriter"/>), so the one character that would otherwise split a
    /// value across tokens on the read side is the space itself — a backslash is escaped first, the same
    /// invertibility discipline <see cref="EscapeFrontmatterValue"/> already applies, then every
    /// space becomes <c>\s</c>. A literal <c>=</c> inside a value needs no escaping: the parser
    /// splits each token on its <em>first</em> <c>=</c> only, and the fixed key literal
    /// (<c>id</c>/<c>reply-to</c>) never itself contains one, so that first match is always the
    /// true key/value boundary regardless of how many further <c>=</c> characters the value holds.
    /// Escaping every space this way also closes the header-terminator lookalike the reviewer's
    /// argument named: the terminator is <c>" -->"</c>, and its leading character is a literal
    /// space — once every space in an escaped value has become the two-character <c>\s</c>, no
    /// unescaped space (and so no literal <c>" -->"</c>) can ever occur inside it.
    /// </summary>
    internal static string EscapeCommentHeaderValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(" ", "\\s", StringComparison.Ordinal);

    /// <summary>Reverses <see cref="EscapeCommentHeaderValue"/>.</summary>
    internal static string UnescapeCommentHeaderValue(string value) => UnescapeUsing(value, CommentHeaderEscapeTable);

    private static readonly IReadOnlyDictionary<char, char> FrontmatterListItemEscapeTable =
        new Dictionary<char, char> { ['n'] = '\n', ['r'] = '\r', [','] = ',' };

    /// <summary>
    /// Escapes one item of a comma-joined list-valued frontmatter field (§5's <c>tasks</c> and
    /// <c>blocked_by</c>): the same backslash/newline/carriage-return escaping
    /// <see cref="EscapeFrontmatterValue"/> applies to a scalar value, plus the list separator
    /// itself, so an item containing a literal comma cannot be misread as two items. A backslash
    /// is escaped first, same invertibility discipline as every other escaper here.
    /// </summary>
    internal static string EscapeFrontmatterListItem(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    /// <summary>Reverses <see cref="EscapeFrontmatterListItem"/>.</summary>
    internal static string UnescapeFrontmatterListItem(string value) => UnescapeUsing(value, FrontmatterListItemEscapeTable);

    /// <summary>
    /// Joins already-unescaped list items into the one-line raw frontmatter value
    /// <see cref="SplitFrontmatterList"/> reverses. An empty list serialises to
    /// <see cref="string.Empty"/> and reads back as an empty list — the same convention
    /// <see cref="CardFrontmatter.Section"/> uses for "field present, nothing recorded".
    /// </summary>
    internal static string JoinFrontmatterList(IReadOnlyList<string> items) =>
        string.Join(",", items.Select(EscapeFrontmatterListItem));

    /// <summary>
    /// Splits a raw comma-joined frontmatter list value back into its unescaped items, scanning
    /// for an unescaped comma rather than a naive <c>Split(',')</c> — a comma preceded by a
    /// backslash (escaped by <see cref="EscapeFrontmatterListItem"/>) is content, not a
    /// separator. <see cref="string.Empty"/> yields an empty list.
    /// </summary>
    internal static IReadOnlyList<string> SplitFrontmatterList(string raw)
    {
        if (raw.Length == 0)
        {
            return [];
        }

        var items = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                current.Append(raw[i]).Append(raw[i + 1]);
                i++;
                continue;
            }

            if (raw[i] == ',')
            {
                items.Add(UnescapeFrontmatterListItem(current.ToString()));
                current.Clear();
                continue;
            }

            current.Append(raw[i]);
        }

        items.Add(UnescapeFrontmatterListItem(current.ToString()));
        return items;
    }

    /// <summary>
    /// The one unescape shape both <see cref="UnescapeFrontmatterValue"/> and
    /// <see cref="UnescapeCommentHeaderValue"/> reduce to: scan for a backslash, and if the
    /// character after it is a key in <paramref name="table"/>, substitute the mapped character
    /// and consume both; an escaped backslash (<c>\\</c>) is always reversed regardless of the
    /// table, since both escapers escape a literal backslash the same way first. Anything else
    /// (an unescaped run, or a backslash the table doesn't recognise) passes through verbatim.
    /// Kept as one implementation so the two formats' escaping can never drift apart from each
    /// other by accident — only their substitution tables genuinely differ.
    /// </summary>
    private static string UnescapeUsing(string value, IReadOnlyDictionary<char, char> table)
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
                if (next == '\\')
                {
                    builder.Append('\\');
                    i++;
                    continue;
                }

                if (table.TryGetValue(next, out var mapped))
                {
                    builder.Append(mapped);
                    i++;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
