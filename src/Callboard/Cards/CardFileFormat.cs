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
    /// A section's Product-Owner-authorisation entry (work-lifecycle: "Remediation beyond the
    /// second round requires recorded authorisation" — "The authorisation SHALL be part of the
    /// record", §8a block C): the same self-contained, no-body, no-footer shape as
    /// <see cref="VerdictLinePrefix"/>, for the same reason — an authorisation carries no prose
    /// beyond its short <c>reason</c> field, only <c>by</c>/<c>reason</c>/<c>timestamp</c>.
    /// </summary>
    internal const string AuthorisationLinePrefix = "<!-- callboard:authorisation ";
    internal const string AuthorisationLineSuffix = " -->";

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
    /// A card's append-only refusal entry (process-enforcement: "Refusals are explained and
    /// attributable" — "A refusal SHALL be recorded against the card with the acting role and the
    /// time", §9 block A). Same self-contained, no-body, no-footer shape as
    /// <see cref="TransitionLinePrefix"/> and its siblings — a refusal carries no prose beyond its
    /// own <c>rule</c>/<c>remedy</c> text, only <c>by</c>/<c>rule</c>/<c>remedy</c>/<c>timestamp</c>
    /// fields.
    /// </summary>
    internal const string RefusalLinePrefix = "<!-- callboard:refusal ";
    internal const string RefusalLineSuffix = " -->";

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
            || unescaped.StartsWith(AuthorisationLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(ClaimLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(LimitLinePrefix, StringComparison.Ordinal)
            || unescaped.StartsWith(RefusalLinePrefix, StringComparison.Ordinal);
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

    /// <summary>An unescaped, self-contained section-authorisation entry line.</summary>
    internal static bool IsAuthorisationLine(string line) =>
        line.StartsWith(AuthorisationLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(AuthorisationLineSuffix, StringComparison.Ordinal);

    /// <summary>An unescaped, self-contained approval-claim entry line.</summary>
    internal static bool IsClaimLine(string line) =>
        line.StartsWith(ClaimLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(ClaimLineSuffix, StringComparison.Ordinal);

    /// <summary>An unescaped, self-contained approval-limit entry line.</summary>
    internal static bool IsLimitLine(string line) =>
        line.StartsWith(LimitLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(LimitLineSuffix, StringComparison.Ordinal);

    /// <summary>An unescaped, self-contained refusal entry line.</summary>
    internal static bool IsRefusalLine(string line) =>
        line.StartsWith(RefusalLinePrefix, StringComparison.Ordinal)
            && line.EndsWith(RefusalLineSuffix, StringComparison.Ordinal);

    /// <summary>
    /// §13 remediation: gained <c>['s'] = ' '</c> so <see cref="UnescapeFrontmatterValue"/> reverses
    /// the leading/trailing-space escaping <see cref="EscapeFrontmatterValue"/> now emits (see
    /// <see cref="EscapeEdgeSpaces"/>). This is a deliberate reading of every card already on disk:
    /// a bare <c>\s</c> a hand-written card happened to contain read as literal text before this
    /// change and reads as a space now. The trade was made on purpose (Architect ruling, §13) — the
    /// failure this closes is passive and near-universal (an editor's trailing-whitespace-on-save
    /// silently truncating a title, indistinguishable from layout on disk and never reported), while
    /// the failure this risks is active and rare (nobody types a bare backslash-s into a card title
    /// by accident). A tool-written card is unaffected either way: <see cref="EscapeFrontmatterValue"/>
    /// escapes a literal backslash to <c>\\</c> before this table ever sees it, so a value that
    /// already contains literal <c>\s</c> text round-trips through <c>\\s</c> on disk regardless of
    /// this entry.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, char> FrontmatterEscapeTable =
        new Dictionary<char, char> { ['n'] = '\n', ['r'] = '\r', ['s'] = ' ' };

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

    /// <summary>
    /// §13 remediation, corrected twice after review: this table's edge characters are exposed the
    /// same way <see cref="FrontmatterEscapeForwardTable"/>'s were, and every one of this block's
    /// enumerations of the affected fields has so far been wrong on at least one member — check the
    /// tracing below against the code, do not extend it from memory.
    ///
    /// <b>Every one of the seven items below is a hand-typed CLI-argument string</b> — there is no
    /// field here whose content the tool invents unprompted, so "generated versus hand-typed" (this
    /// comment's first, incorrect framing) is not the split that actually distinguishes them. What
    /// differs is whether anything about the field suggests legitimate content could carry edge
    /// whitespace:
    /// <list type="bullet">
    /// <item><description><c>tasks</c> (<see cref="BlockCardFields"/>) — one <c>--task &lt;reference&gt;</c>
    /// flag per item (<c>CommandParser.ParseBlockCreate</c>), stored verbatim, no trimming. Conventionally a
    /// task reference (e.g. <c>13.5</c>) — nothing about its use suggests free text.</description></item>
    /// <item><description><c>blocked_by</c> (<see cref="BlockCardFields"/>) — one positional card id per
    /// invocation (<c>CommandParser.ParseBlockedByMutation</c>), stored verbatim. Conventionally a card
    /// id.</description></item>
    /// <item><description><c>gate_results</c> (<see cref="BlockCardFields"/>) — each item is
    /// <c>{label}={exitCode}={round}</c>; <c>label</c> is a positional CLI argument
    /// (<c>CommandParser.ParseBlockGate</c>), rejected only if empty/whitespace-only or containing
    /// <c>=</c>/<c>,</c> — not trimmed. Conventionally a fixed gate name (<c>build</c>, <c>test</c>);
    /// <c>exitCode</c>/<c>round</c> are integers with no whitespace of their own.</description></item>
    /// <item><description><c>earned_from</c>/<c>absorbs</c> (register/rule CLI fields) — a comma list from
    /// one <c>--earned-from</c>/<c>--absorbs</c> flag, split with <see cref="SplitFrontmatterList"/> itself
    /// — the CLI already expects the caller to use this escaping convention on typed input. Conventionally
    /// finding/rule ids.</description></item>
    /// <item><description><b><c>extent_value</c> under <see cref="FindingExtent.Explicit"/> — the one field
    /// documented to hold open-ended content.</b> Built from <c>--extent-explicit</c>
    /// (<see cref="Callboard.Cli.CommandParser"/>), split with a plain, escaping-unaware
    /// <c>string.Split(',')</c> and <b>not trimmed</b>, holding — per <see cref="FindingExtent"/>'s own doc
    /// comment — "paths, line ranges or symbols". <see cref="BlockCardFields.IsValidListItem"/> rejects only
    /// an empty or whitespace-only item, so an item with edge whitespace validates and reaches this table.
    /// The untrimmed split manufactures a leading space on any non-first item on its own —
    /// <c>--extent-explicit "a.cs, b.cs"</c> is an entirely natural thing to type and produces one with no
    /// editor or hand-edit involved at all.</description></item>
    /// <item><description><c>extent_fingerprint</c> (<see cref="FindingCardFields"/>) — each item is
    /// <c>{RelativePath}={ContentHash-or-"absent"}</c> (<c>FindingExtentFingerprint.ComputeForFiles</c>).
    /// <b>Not independently generated</b>: <c>RelativePath</c> is the corresponding <c>extent_value</c> item
    /// carried over verbatim (only an <see cref="FindingExtent.Explicit"/> extent produces any fingerprint
    /// items at all), so it inherits <c>extent_value</c>'s exact exposure through the same string, not a
    /// separate one; only <c>ContentHash</c> is tool-computed, from hashing the file the path
    /// names.</description></item>
    /// </list>
    /// The fix applies uniformly regardless — see <see cref="EscapeFrontmatterListItem"/> — so this split
    /// changes nothing about the code, only what a reader should expect to find hand-edited in a real
    /// card.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> FrontmatterListItemEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", ['\n'] = "\\n", ['\r'] = "\\r", [','] = "\\," };

    /// <summary>
    /// Escapes a free-text frontmatter field value (<c>id</c>/<c>title</c>/<c>status</c>/
    /// <c>section</c>) so it always occupies exactly one physical line. Frontmatter is
    /// line-based (<c>key: value</c>), unlike the body/comment format above which is delimiter-
    /// based — a literal newline in a value would otherwise split it across lines and the next
    /// read would hit "malformed frontmatter line" on the fragment. A backslash is escaped first
    /// so the scheme stays invertible regardless of what the value already contains.
    ///
    /// §13 remediation: a leading and/or trailing space is then escaped as <c>\s</c>
    /// (<see cref="EscapeEdgeSpaces"/>) — on disk that whitespace is indistinguishable from layout,
    /// so an editor that strips trailing whitespace on save silently truncates it, and the card
    /// still parses afterwards holding a different value. Interior spaces are never escaped: unlike
    /// <see cref="CommentHeaderEscapeForwardTable"/>'s space-delimited format, frontmatter is
    /// <c>key: value</c> to end of line, so an interior space is never ambiguous, and escaping it
    /// anyway (<c>title: Which\sretry\spolicy?</c>) would cost the plain-text legibility the record
    /// is required to keep for a reader with no access to the tool. A value with no edge whitespace
    /// still serialises to exactly the bytes it always has.
    /// </summary>
    internal static string EscapeFrontmatterValue(string value) =>
        EscapeEdgeSpaces(EscapeUsing(value, FrontmatterEscapeForwardTable));

    /// <summary>
    /// Escapes a leading and/or trailing space in an already backslash/newline/CR-escaped
    /// frontmatter value as <c>\s</c>. Not table-driven like the escapers above — <see cref="EscapeUsing"/>
    /// substitutes by character alone, with no notion of position, and this rule is positional: the
    /// same space is content in the middle of a value and layout-ambiguous at its edge. Checked
    /// against the already-escaped value's own edges (not the raw input's) so a value whose
    /// original edge character was a backslash — now doubled and no longer at the true edge — is
    /// judged by what actually ends up on disk.
    /// </summary>
    private static string EscapeEdgeSpaces(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        if (value.Length == 1)
        {
            return value[0] == ' ' ? "\\s" : value;
        }

        var leading = value[0] == ' ';
        var trailing = value[^1] == ' ';
        if (!leading && !trailing)
        {
            return value;
        }

        var start = leading ? 1 : 0;
        var length = value.Length - start - (trailing ? 1 : 0);
        var middle = value.Substring(start, length);
        return (leading ? "\\s" : string.Empty) + middle + (trailing ? "\\s" : string.Empty);
    }

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

    /// <summary>
    /// §13 remediation: gained <c>['s'] = ' '</c>, the same deliberate trade
    /// <see cref="FrontmatterEscapeTable"/>'s doc comment records — a bare <c>\s</c> a
    /// hand-written list item happened to contain now reads as a space where it read as literal
    /// text before. The field this matters for is <c>extent_value</c> under
    /// <see cref="FindingExtent.Explicit"/> — open-ended free text ("paths, line ranges or
    /// symbols") — and, through the same string, <c>extent_fingerprint</c>'s inherited
    /// <c>RelativePath</c> component. See <see cref="FrontmatterListItemEscapeForwardTable"/>'s doc
    /// comment for the full field-by-field accounting; every other list-valued field's own
    /// convention gives no reason to expect a hand-written value here in the first place.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, char> FrontmatterListItemEscapeTable =
        new Dictionary<char, char> { ['n'] = '\n', ['r'] = '\r', [','] = ',', ['s'] = ' ' };

    /// <summary>
    /// Escapes one item of a comma-joined list-valued frontmatter field (§5's <c>tasks</c> and
    /// <c>blocked_by</c>, among others enumerated on <see cref="FrontmatterListItemEscapeForwardTable"/>):
    /// the same backslash/newline/carriage-return escaping <see cref="EscapeFrontmatterValue"/>
    /// applies to a scalar value, plus the list separator itself, so an item containing a literal
    /// comma cannot be misread as two items. A backslash is escaped first, same invertibility
    /// discipline as every other escaper here.
    ///
    /// §13 remediation: then the same leading/trailing-space escaping <see cref="EscapeFrontmatterValue"/>
    /// applies (<see cref="EscapeEdgeSpaces"/>, shared with it verbatim) — but applied to <b>every
    /// item</b>, not only the first/last: <see cref="JoinFrontmatterList"/> calls this once per item
    /// with no positional awareness of its own, so an item's own leading/trailing space is escaped
    /// the same way whether that item sits at the joined value's true line edge or in the middle,
    /// next to a separating comma on both sides. That is deliberately broader than the true-line-edge
    /// case alone: a space immediately after a comma is exactly as easy to introduce by accident
    /// (<see cref="FrontmatterListItemEscapeForwardTable"/>'s doc comment — typing a comma-separated
    /// list with a space after the comma) and exactly as ambiguous to a reader as one at the line's
    /// actual end, so there is no narrower rule worth having here. Composition with the list
    /// separator stays invertible: <see cref="SplitFrontmatterList"/>'s boundary scan treats any
    /// backslash followed by another character as one protected pair regardless of which letter
    /// follows, so a <c>\,</c> and a <c>\s</c> sitting next to each other in the same item are each
    /// consumed as their own pair and neither can be misread as, or swallow, the other.
    /// </summary>
    internal static string EscapeFrontmatterListItem(string value) =>
        EscapeEdgeSpaces(EscapeUsing(value, FrontmatterListItemEscapeForwardTable));

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
