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
    /// A block flow-transition entry (work-lifecycle: "Every transition SHALL record the acting
    /// role and the time it occurred", §5 block C): the same self-contained, no-body, no-footer
    /// shape as <see cref="HandoverLinePrefix"/>, for the same reason — a transition carries no
    /// prose, only <c>by</c>/<c>name</c>/<c>from</c>/<c>to</c>/<c>timestamp</c> fields.
    /// </summary>
    internal const string TransitionLinePrefix = "<!-- callboard:transition ";
    internal const string TransitionLineSuffix = " -->";

    /// <summary>
    /// A section's supervisor-verdict entry (work-lifecycle: "Sections are entities" — "the
    /// verdict, the range and the acting role are recorded against that section entity", §5 block
    /// E): the same self-contained, no-body, no-footer shape as <see cref="TransitionLinePrefix"/>,
    /// for the same reason — a verdict carries no prose, only
    /// <c>by</c>/<c>verdict</c>/<c>range-from</c>/<c>range-to</c>/<c>timestamp</c> fields.
    /// </summary>
    internal const string VerdictLinePrefix = "<!-- callboard:verdict ";
    internal const string VerdictLineSuffix = " -->";

    /// <summary>
    /// One enumerated claim of an approval (review-certification: "Certification enumerates its
    /// claims", §8 block A). Self-contained, no body and no footer — the same shape as
    /// <see cref="TransitionLinePrefix"/>/<see cref="VerdictLinePrefix"/> — but, unlike those two,
    /// more than one can belong to the same approval, so each carries its own <c>id</c> (Architect
    /// ruling: "each claim carrying its own id" — 8.8, out of this block's scope, re-asserts an
    /// existing approval's claims individually and needs a stable handle to assert or refuse) and a
    /// <c>round</c> tying it to the remediation round it was certified in, the same scoping
    /// <see cref="GateResult.Round"/> already established for "only the current round's evidence is
    /// evidence".
    /// </summary>
    internal const string ClaimLinePrefix = "<!-- callboard:claim ";
    internal const string ClaimLineSuffix = " -->";

    /// <summary>
    /// One stated limit of an approval — what the certification does NOT establish
    /// (review-certification: "An approval SHALL ... state what it does not establish"). Same shape
    /// as <see cref="ClaimLinePrefix"/>, minus an <c>id</c>: a limit is never individually asserted
    /// or refused (8.8 re-asserts claims, never limits — Architect ruling), so it needs no identity
    /// of its own, only the <c>round</c> it was certified in.
    /// </summary>
    internal const string LimitLinePrefix = "<!-- callboard:limit ";
    internal const string LimitLineSuffix = " -->";

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
            || unescaped.StartsWith(HandoverLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(TransitionLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(VerdictLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(ClaimLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(LimitLinePrefix, StringComparison.Ordinal);
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

    /// <summary>An unescaped, self-contained block flow-transition entry line.</summary>
    internal static bool IsTransitionLine(string line) =>
        line.StartsWith(TransitionLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(TransitionLineSuffix, StringComparison.Ordinal);

    /// <summary>An unescaped, self-contained section-verdict entry line.</summary>
    internal static bool IsVerdictLine(string line) =>
        line.StartsWith(VerdictLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(VerdictLineSuffix, StringComparison.Ordinal);

    /// <summary>An unescaped, self-contained approval-claim entry line.</summary>
    internal static bool IsClaimLine(string line) =>
        line.StartsWith(ClaimLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(ClaimLineSuffix, StringComparison.Ordinal);

    /// <summary>An unescaped, self-contained approval-limit entry line.</summary>
    internal static bool IsLimitLine(string line) =>
        line.StartsWith(LimitLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(LimitLineSuffix, StringComparison.Ordinal);

    private static readonly IReadOnlyDictionary<char, char> FrontmatterEscapeTable =
        new Dictionary<char, char> { ['n'] = '\n', ['r'] = '\r' };

    private static readonly IReadOnlyDictionary<char, char> CommentHeaderEscapeTable =
        new Dictionary<char, char> { ['s'] = ' ' };

    /// <summary>
    /// The forward mirror of <see cref="FrontmatterEscapeTable"/>/<see cref="CommentHeaderEscapeTable"/>/
    /// <see cref="FrontmatterListItemEscapeTable"/>: each maps a character worth escaping to its
    /// multi-character replacement, keyed by the raw character rather than the escape letter, and
    /// each always includes a literal backslash first — every escaper here needs a backslash
    /// escaped before anything else stays invertible. Every <c>Escape*Value</c>/<c>Escape*Item</c>
    /// function below reduces to <see cref="EscapeUsing"/> over one of these, the same collapsing
    /// <see cref="UnescapeUsing"/> already did for the reverse direction.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> FrontmatterEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", ['\n'] = "\\n", ['\r'] = "\\r" };

    private static readonly IReadOnlyDictionary<char, string> CommentHeaderEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", [' '] = "\\s" };

    private static readonly IReadOnlyDictionary<char, string> FrontmatterListItemEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", ['\n'] = "\\n", ['\r'] = "\\r", [','] = "\\," };

    /// <summary>
    /// Escapes a free-text frontmatter field value (<c>id</c>/<c>title</c>/<c>status</c>/
    /// <c>section</c>) so it always occupies exactly one physical line. Frontmatter is
    /// line-based (<c>key: value</c>), unlike the body/comment format above which is delimiter-
    /// based — a literal newline in a value would otherwise split it across lines and the next
    /// read would hit "malformed frontmatter line" on the fragment. A backslash is escaped first
    /// so the scheme stays invertible regardless of what the value already contains.
    /// </summary>
    internal static string EscapeFrontmatterValue(string value) => EscapeUsing(value, FrontmatterEscapeForwardTable);

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
    internal static string EscapeCommentHeaderValue(string value) => EscapeUsing(value, CommentHeaderEscapeForwardTable);

    /// <summary>Reverses <see cref="EscapeCommentHeaderValue"/>.</summary>
    internal static string UnescapeCommentHeaderValue(string value) => UnescapeUsing(value, CommentHeaderEscapeTable);

    private static readonly IReadOnlyDictionary<char, char> CertificationTextEscapeTable =
        new Dictionary<char, char> { ['s'] = ' ', ['n'] = '\n', ['r'] = '\r' };

    private static readonly IReadOnlyDictionary<char, string> CertificationTextEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", [' '] = "\\s", ['\n'] = "\\n", ['\r'] = "\\r" };

    /// <summary>
    /// Escapes a claim's or limit's free-text <c>text</c> field (§8 block A) — the same
    /// space-escaping <see cref="EscapeCommentHeaderValue"/> applies (the line is <c>key=value</c>
    /// tokens joined by a single space, so an unescaped space would split a value across tokens),
    /// plus newline/carriage-return escaping <see cref="EscapeCommentHeaderValue"/> does not need
    /// for its own <c>id</c>/<c>reply-to</c> values (always a single generated token, never
    /// free-flowing prose) but certification text does: review-certification's own text is written
    /// as sentences a later reviewer reads, and a literal newline embedded unescaped in a
    /// single-physical-line format would corrupt the next line's own parse.
    /// </summary>
    internal static string EscapeCertificationTextValue(string value) => EscapeUsing(value, CertificationTextEscapeForwardTable);

    /// <summary>Reverses <see cref="EscapeCertificationTextValue"/>.</summary>
    internal static string UnescapeCertificationTextValue(string value) => UnescapeUsing(value, CertificationTextEscapeTable);

    private static readonly IReadOnlyDictionary<char, char> SiteListItemEscapeTable =
        new Dictionary<char, char> { ['s'] = ' ', [','] = ',', ['n'] = '\n', ['r'] = '\r' };

    private static readonly IReadOnlyDictionary<char, string> SiteListItemEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", [' '] = "\\s", [','] = "\\,", ['\n'] = "\\n", ['\r'] = "\\r" };

    /// <summary>
    /// Escapes one item of a nit's comma-joined <c>sites</c> comment-header value (§8 block B) —
    /// the same space-escaping <see cref="EscapeCommentHeaderValue"/> applies (the header is
    /// <c>key=value</c> tokens joined by a single space), plus the list separator itself (a path
    /// containing a literal comma must not be misread as two sites) and newline/carriage-return
    /// escaping, the same combination <see cref="EscapeCertificationTextValue"/> already applies for
    /// its own reasons.
    /// </summary>
    internal static string EscapeSiteListItem(string value) => EscapeUsing(value, SiteListItemEscapeForwardTable);

    /// <summary>Reverses <see cref="EscapeSiteListItem"/>.</summary>
    internal static string UnescapeSiteListItem(string value) => UnescapeUsing(value, SiteListItemEscapeTable);

    /// <summary>
    /// Joins already-unescaped sites into the one comment-header token <see cref="SplitSiteList"/>
    /// reverses — the same shape <see cref="JoinFrontmatterList"/> gives frontmatter's own
    /// comma-joined lists, applied to a header value instead. An empty list joins to
    /// <see cref="string.Empty"/>, and the caller omits the <c>sites</c> key entirely in that case
    /// (the same "field present, nothing recorded" convention would otherwise be ambiguous with
    /// "key absent" on a single-line header — omitting the key sidesteps the question rather than
    /// answering it the way <see cref="CardFrontmatter.Section"/> does for frontmatter).
    /// </summary>
    internal static string JoinSiteList(IReadOnlyList<string> items) =>
        string.Join(",", items.Select(EscapeSiteListItem));

    /// <summary>
    /// Splits a raw comma-joined <c>sites</c> header value back into its unescaped items, scanning
    /// for an unescaped comma — the same algorithm <see cref="SplitFrontmatterList"/> uses for
    /// frontmatter's own lists, adapted to <see cref="SiteListItemEscapeTable"/>.
    /// </summary>
    internal static IReadOnlyList<string> SplitSiteList(string raw)
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
                items.Add(UnescapeSiteListItem(current.ToString()));
                current.Clear();
                continue;
            }

            current.Append(raw[i]);
        }

        items.Add(UnescapeSiteListItem(current.ToString()));
        return items;
    }

    private static readonly IReadOnlyDictionary<char, char> FrontmatterListItemEscapeTable =
        new Dictionary<char, char> { ['n'] = '\n', ['r'] = '\r', [','] = ',' };

    /// <summary>
    /// Escapes one item of a comma-joined list-valued frontmatter field (§5's <c>tasks</c> and
    /// <c>blocked_by</c>): the same backslash/newline/carriage-return escaping
    /// <see cref="EscapeFrontmatterValue"/> applies to a scalar value, plus the list separator
    /// itself, so an item containing a literal comma cannot be misread as two items. A backslash
    /// is escaped first, same invertibility discipline as every other escaper here.
    /// </summary>
    internal static string EscapeFrontmatterListItem(string value) => EscapeUsing(value, FrontmatterListItemEscapeForwardTable);

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
    /// The one escape shape every <c>Escape*</c> function above reduces to: walk the value one
    /// character at a time and substitute <paramref name="table"/>'s replacement for any character
    /// it maps, passing everything else through verbatim. Each table always carries a literal
    /// backslash entry, so — matching the sequential <c>string.Replace</c> chain this replaced —
    /// a backslash in the input always becomes a doubled backslash before any other substitution
    /// is considered; the two never interact, because no replacement string this method ever
    /// writes introduces a character another entry in the same table also maps.
    /// </summary>
    private static string EscapeUsing(string value, IReadOnlyDictionary<char, string> table)
    {
        if (!value.Any(table.ContainsKey))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (table.TryGetValue(ch, out var replacement))
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
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
