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

    /// <summary>
    /// §14.4: the single-line delimiter closing an appended comment's body — never an open line
    /// (it names no family and carries no fields of its own), so it is deliberately kept out of
    /// <see cref="BlockOpenLinePrefixes"/> even though it needs the same write-side protection every
    /// entry there gets. Folding it in as a <c>StartsWith</c>-matched prefix would either weaken
    /// that protection (a footer-prefixed line with trailing content would stop being escaped) or
    /// misfire the malformed-open-line refusal on such a line (it is not an open line, so "not an
    /// exact match" is the wrong question to ask of it) — both directions are guarded against by
    /// giving it its own <see cref="IsCommentFooter"/> exact-equality check and its own explicit
    /// entry in <see cref="LooksLikeDelimiterOrEscapedDelimiter"/>, exactly as before §14.4.
    /// </summary>
    internal const string CommentFooter = "<!-- /callboard:comment -->";

    /// <summary>
    /// §14.1: the one delimited-block syntax shared by every one of the eight append-only
    /// block-entry families below (derived from what <see cref="CardFileWriter"/> emits and
    /// <see cref="CardFileParser"/> reads for the appended region — §14.4 brought the comment
    /// header, the eighth and last family, onto this same syntax; its body and
    /// <see cref="CommentFooter"/> stay their own append-only-content shape, not a ninth family).
    /// One open line names the family and carries nothing else; each field is then its own
    /// <c>key: value</c> line, the same shape frontmatter itself uses; the block ends at a line
    /// <em>exactly equal to</em> <see cref="BlockCloseLine"/> — not merely containing it. That
    /// equality requirement is what makes an unterminated block fail loudly (§13.6's "a card which
    /// will not parse beats one that parses wrongly", applied here): a stray <c>--&gt;</c> embedded
    /// inside a field's own text can never be mistaken for the close line, because §14.3 escapes
    /// exactly that run (<see cref="EscapeCardBlockValue"/>) before it ever reaches the file.
    /// </summary>
    internal const string BlockCloseLine = "-->";

    /// <summary>
    /// An ownership-handover entry (card-model 4.5): <c>by</c>/<c>to</c>/<c>timestamp</c> fields,
    /// no prose. §14.1: the delimited block shape above — this is only the open line.
    /// </summary>
    internal const string HandoverOpenLine = "<!-- callboard:handover";

    /// <summary>
    /// A block flow-transition entry (work-lifecycle: "Every transition SHALL record the acting
    /// role and the time it occurred", §5 block C): the same delimited block shape as
    /// <see cref="HandoverOpenLine"/>, for the same reason — a transition carries no prose, only
    /// <c>by</c>/<c>name</c>/<c>from</c>/<c>to</c>/<c>timestamp</c> fields.
    /// </summary>
    internal const string TransitionOpenLine = "<!-- callboard:transition";

    /// <summary>
    /// A section's supervisor-verdict entry (work-lifecycle: "Sections are entities" — "the
    /// verdict, the range and the acting role are recorded against that section entity", §5 block
    /// E): the same delimited block shape as <see cref="TransitionOpenLine"/>, for the same reason —
    /// a verdict carries no prose, only
    /// <c>by</c>/<c>verdict</c>/<c>range-from</c>/<c>range-to</c>/<c>timestamp</c> fields.
    /// </summary>
    internal const string VerdictOpenLine = "<!-- callboard:verdict";

    /// <summary>
    /// A section's Product-Owner-authorisation entry (work-lifecycle: "Remediation beyond the
    /// second round requires recorded authorisation" — "The authorisation SHALL be part of the
    /// record", §8a block C): the same delimited block shape as <see cref="VerdictOpenLine"/>, for
    /// the same reason — an authorisation carries no prose beyond its short <c>reason</c> field,
    /// only <c>by</c>/<c>reason</c>/<c>timestamp</c>.
    /// </summary>
    internal const string AuthorisationOpenLine = "<!-- callboard:authorisation";

    /// <summary>
    /// One enumerated claim of an approval (review-certification: "Certification enumerates its
    /// claims", §8 block A). The same delimited block shape as
    /// <see cref="TransitionOpenLine"/>/<see cref="VerdictOpenLine"/> — but, unlike those two, more
    /// than one can belong to the same approval, so each carries its own <c>id</c> (Architect
    /// ruling: "each claim carrying its own id" — 8.8, out of this block's scope, re-asserts an
    /// existing approval's claims individually and needs a stable handle to assert or refuse) and a
    /// <c>round</c> tying it to the remediation round it was certified in, the same scoping
    /// <see cref="GateResult.Round"/> already established for "only the current round's evidence is
    /// evidence".
    /// </summary>
    internal const string ClaimOpenLine = "<!-- callboard:claim";

    /// <summary>
    /// One stated limit of an approval — what the certification does NOT establish
    /// (review-certification: "An approval SHALL ... state what it does not establish"). Same shape
    /// as <see cref="ClaimOpenLine"/>, minus an <c>id</c>: a limit is never individually asserted
    /// or refused (8.8 re-asserts claims, never limits — Architect ruling), so it needs no identity
    /// of its own, only the <c>round</c> it was certified in.
    /// </summary>
    internal const string LimitOpenLine = "<!-- callboard:limit";

    /// <summary>
    /// A card's append-only refusal entry (process-enforcement: "Refusals are explained and
    /// attributable" — "A refusal SHALL be recorded against the card with the acting role and the
    /// time", §9 block A). Same delimited block shape as <see cref="TransitionOpenLine"/> and its
    /// siblings — a refusal carries no prose beyond its own <c>rule</c>/<c>remedy</c> text, only
    /// <c>by</c>/<c>rule</c>/<c>remedy</c>/<c>timestamp</c> fields.
    /// </summary>
    internal const string RefusalOpenLine = "<!-- callboard:refusal";

    /// <summary>
    /// An appended comment's own header block's open line (§14.4: the comment header moved onto the
    /// §14.1 delimited-block shape — its body and <see cref="CommentFooter"/> are unchanged, only
    /// the header carrying <c>id</c>/<c>author</c>/<c>timestamp</c>/etc. is now a block like the
    /// other seven). Joining <see cref="BlockOpenLinePrefixes"/> is what gives a pre-§14.4
    /// single-line comment header the same loud, named-family refusal the other seven already have,
    /// for free — see <see cref="MalformedBlockOpenLineFamily"/>.
    /// </summary>
    internal const string CommentOpenLine = "<!-- callboard:comment";

    /// <summary>
    /// §14 remediation (reviewer finding on `14.1–14.3`), extended by §14.4: the one shared
    /// declaration of the eight §14.1 block-open-line prefixes, paired with a human label for a
    /// refusal message. <see cref="LooksLikeDelimiterOrEscapedDelimiter"/> (write side — is this
    /// body/comment content that must be escaped so it is never misread as a real block open line)
    /// and <see cref="MalformedBlockOpenLineFamily"/> (read side — is this an unescaped line that
    /// starts with one of these prefixes but is not an exact block open line, i.e. a malformed or
    /// legacy marker that must never be silently absorbed as prose) both read from this one list, so
    /// the two questions about the same eight prefixes can never drift apart. §14.4 added
    /// <see cref="CommentOpenLine"/> here and nothing else — the mechanism this declaration exists
    /// for is exactly that adding an eighth family costs one list entry, not a parallel
    /// implementation on either side. <see cref="CommentFooter"/> is deliberately excluded — see its
    /// own doc comment for why it is not an open line and is protected separately.
    /// </summary>
    private static readonly IReadOnlyList<(string Prefix, string Family)> BlockOpenLinePrefixes =
    [
        (HandoverOpenLine, "handover"),
        (TransitionOpenLine, "transition"),
        (VerdictOpenLine, "verdict"),
        (AuthorisationOpenLine, "authorisation"),
        (ClaimOpenLine, "claim"),
        (LimitOpenLine, "limit"),
        (RefusalOpenLine, "refusal"),
        (CommentOpenLine, "comment"),
    ];

    /// <summary>
    /// True for a line that, written unescaped, would be misread as a structural delimiter on
    /// the next parse — the comment footer, one of the eight §14.1 block open lines
    /// (<see cref="BlockOpenLinePrefixes"/>, comment's own open line included since §14.4), or an
    /// already-escaped instance of any of those (any number of leading backslashes stripped still
    /// matches). Escaping is checked against this, not just the bare patterns, so escaping the same
    /// content twice stays invertible. Deliberately does not include <see cref="BlockCloseLine"/>
    /// itself: a bare <c>--&gt;</c> line only means "end of block" while the parser is already
    /// scanning fields inside one of the eight open lines above, never while scanning body or
    /// comment content — see the field-value escaping this task also adds
    /// (<see cref="EscapeCardBlockValue"/>) for the different hazard a literal <c>--&gt;</c>
    /// <em>inside</em> a field's own text poses.
    /// </summary>
    internal static bool LooksLikeDelimiterOrEscapedDelimiter(string line)
    {
        var unescaped = line.TrimStart('\\');
        if (string.Equals(unescaped, CommentFooter, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var (prefix, _) in BlockOpenLinePrefixes)
        {
            if (unescaped.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §14 remediation: §13.6's rule ("a card which will not parse beats one that parses wrongly")
    /// applied to the malformed-open-line case, symmetric with §14.1's own unterminated-block case.
    /// Returns the family name when <paramref name="line"/> starts with one of the eight §14.1
    /// block-open prefixes (<see cref="BlockOpenLinePrefixes"/>) but is not itself an exact block
    /// open line — a line the writer could never have produced as body or comment content, since
    /// <see cref="LooksLikeDelimiterOrEscapedDelimiter"/> always escapes exactly such a line (one
    /// leading backslash) before writing it. An escaped line therefore starts with <c>\</c>, never
    /// with <c>&lt;</c>, and can never match a bare prefix here — so an unescaped match is always
    /// either a hand-authored line or a pre-§14.1/pre-§14.4 legacy marker, and the caller refuses it
    /// instead of silently absorbing it into prose. Returns <see langword="null"/> when line is not
    /// such a case, including when it is itself exactly one of the eight open lines (the caller
    /// checks that first via the <c>Is*Line</c> predicates) or when it carries a leading backslash
    /// (an escaped content line, reversed by <see cref="UnescapeContentLine"/> instead — never this).
    /// </summary>
    internal static string? MalformedBlockOpenLineFamily(string line)
    {
        foreach (var (prefix, family) in BlockOpenLinePrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(line, prefix, StringComparison.Ordinal))
            {
                return family;
            }
        }

        return null;
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

    /// <summary>§14.4: an unescaped appended-comment block's open line — checked the same way as
    /// its seven siblings below, since it now shares their shape.</summary>
    internal static bool IsCommentLine(string line) =>
        string.Equals(line, CommentOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped line marking the end of an appended comment's body — not an open line;
    /// see <see cref="CommentFooter"/>'s own doc comment for why it stays outside
    /// <see cref="BlockOpenLinePrefixes"/>.</summary>
    internal static bool IsCommentFooter(string line) =>
        string.Equals(line, CommentFooter, StringComparison.Ordinal);

    /// <summary>An unescaped ownership-handover block's open line.</summary>
    internal static bool IsHandoverLine(string line) =>
        string.Equals(line, HandoverOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped block flow-transition block's open line.</summary>
    internal static bool IsTransitionLine(string line) =>
        string.Equals(line, TransitionOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped section-verdict block's open line.</summary>
    internal static bool IsVerdictLine(string line) =>
        string.Equals(line, VerdictOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped section-authorisation block's open line.</summary>
    internal static bool IsAuthorisationLine(string line) =>
        string.Equals(line, AuthorisationOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped approval-claim block's open line.</summary>
    internal static bool IsClaimLine(string line) =>
        string.Equals(line, ClaimOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped approval-limit block's open line.</summary>
    internal static bool IsLimitLine(string line) =>
        string.Equals(line, LimitOpenLine, StringComparison.Ordinal);

    /// <summary>An unescaped refusal block's open line.</summary>
    internal static bool IsRefusalLine(string line) =>
        string.Equals(line, RefusalOpenLine, StringComparison.Ordinal);

    /// <summary>§14.1: an unescaped line marking the end of any of the eight block families above —
    /// checked for exact equality, not merely containment, so that a field value carrying a literal
    /// <c>--&gt;</c> (escaped by <see cref="EscapeCardBlockValue"/> before it ever reaches the file)
    /// can never be misread as this.</summary>
    internal static bool IsBlockCloseLine(string line) =>
        string.Equals(line, BlockCloseLine, StringComparison.Ordinal);

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

    /// <summary>
    /// The forward mirror of <see cref="FrontmatterEscapeTable"/>/<see cref="FrontmatterListItemEscapeTable"/>:
    /// each maps a character worth escaping to its multi-character replacement, keyed by the raw
    /// character rather than the escape letter, and each always includes a literal backslash first
    /// — every escaper here needs a backslash escaped before anything else stays invertible. Every
    /// <c>Escape*Value</c>/<c>Escape*Item</c> function below reduces to <see cref="EscapeUsing"/>
    /// over one of these, the same collapsing <see cref="UnescapeUsing"/> already did for the
    /// reverse direction.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> FrontmatterEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", ['\n'] = "\\n", ['\r'] = "\\r" };

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
    /// still parses afterwards holding a different value. Interior spaces are never escaped:
    /// frontmatter is <c>key: value</c> to end of line, so an interior space is never ambiguous, and escaping it
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
    /// §14.2/14.3: escapes a free-text field value carried by one of the eight §14.1 block families
    /// (a refusal's <c>rule</c>/<c>remedy</c>, an authorisation's <c>reason</c>, a claim's or
    /// limit's <c>text</c>, a verdict's <c>range-from</c>/<c>range-to</c>, and — since §14.4 — a
    /// comment's own <c>id</c>/<c>reply-to</c>/<c>resolves</c>, superseding the header's former
    /// dedicated space-escaping pair) — the field this section's amendment exists for: these are the
    /// values the Product Owner read as <c>rule=work-lifecycle:\sblock\scards\smove...</c> and
    /// called horrible. §14.1 puts one field per physical <c>key: value</c> line inside the block's
    /// enclosing HTML comment, the same shape frontmatter already uses, so
    /// <see cref="EscapeFrontmatterValue"/>'s own escaping applies unchanged and unlocks the same
    /// win it gives frontmatter: an interior space is never ambiguous on a <c>key: value</c> line,
    /// so it is never escaped, and the value reads as prose.
    ///
    /// What a frontmatter value never needs and this one does: every field here lives inside
    /// <c>&lt;!-- ... --&gt;</c>, and a rendered view (a browser, GitHub's own Markdown viewer) ends
    /// an HTML comment at the <em>first</em> literal <c>--&gt;</c> it finds anywhere in it, not only
    /// at the line-exact boundary <see cref="CardFileParser"/> itself enforces — so a rule, remedy,
    /// reason, or claim/limit text that happens to contain that literal three-character run (a real
    /// example: "the record moves --&gt; forward") would leak the rest of the block into the
    /// rendered page. §14.3 escapes exactly that run, mapping it to <c>\-&gt;</c> — not <c>&gt;</c>
    /// alone, which a char-keyed table would also hit on every unrelated <c>=&gt;</c> and silently
    /// mangle ordinary code-shaped prose (the very card that prompted this amendment carries
    /// <c>onFailure: static _ =&gt; null</c>). Order matters: the arrow escape runs after
    /// <see cref="EscapeFrontmatterValue"/>'s own backslash-doubling, so a value genuinely
    /// containing the literal text <c>\-&gt;</c> is already <c>\\-&gt;</c> by this point and can
    /// never be misread by <see cref="UnescapeCardBlockValue"/> as an escaped terminator.
    /// </summary>
    internal static string EscapeCardBlockValue(string value) =>
        EscapeArrowTerminator(EscapeFrontmatterValue(value));

    private static string EscapeArrowTerminator(string value) =>
        value.Contains(BlockCloseLine, StringComparison.Ordinal)
            ? value.Replace(BlockCloseLine, "\\->", StringComparison.Ordinal)
            : value;

    /// <summary>
    /// Reverses <see cref="EscapeCardBlockValue"/> in one left-to-right pass, checking a doubled
    /// backslash before the arrow escape before <see cref="FrontmatterEscapeTable"/>'s own
    /// single-character entries — the same priority <see cref="UnescapeUsing"/> already gives
    /// <c>\\</c> over a table entry, extended with one more case. That priority is what keeps this
    /// invertible: a value that already contained the literal text <c>\-&gt;</c> was serialised as
    /// <c>\\-&gt;</c> (backslash doubled first), and since the doubled-backslash pair is always
    /// consumed as a unit before the arrow check ever runs, the trailing <c>-&gt;</c> left behind is
    /// never mistaken for an escaped terminator — see <see cref="EscapeCardBlockValue"/>'s own doc
    /// comment for the worked trace.
    /// </summary>
    internal static string UnescapeCardBlockValue(string value)
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

                if (next == '-' && i + 2 < value.Length && value[i + 2] == '>')
                {
                    builder.Append(BlockCloseLine);
                    i += 2;
                    continue;
                }

                if (FrontmatterEscapeTable.TryGetValue(next, out var mapped))
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

    private static readonly IReadOnlyDictionary<char, char> SiteListItemEscapeTable =
        new Dictionary<char, char> { ['s'] = ' ', [','] = ',', ['n'] = '\n', ['r'] = '\r' };

    private static readonly IReadOnlyDictionary<char, string> SiteListItemEscapeForwardTable =
        new Dictionary<char, string> { ['\\'] = "\\\\", [' '] = "\\s", [','] = "\\,", ['\n'] = "\\n", ['\r'] = "\\r" };

    /// <summary>
    /// Escapes one item of a nit's comma-joined <c>sites</c> comment-header value (§8 block B) — the
    /// list separator itself (a path containing a literal comma must not be misread as two sites)
    /// plus newline/carriage-return escaping. The space escaping here predates §14.4's move of the
    /// comment header onto a <c>key: value</c> line (where an interior space is no longer
    /// ambiguous); it is harmless and left unchanged rather than touched as part of this task's line-
    /// shape-only scope — see the field-by-field accounting on
    /// <see cref="FrontmatterListItemEscapeForwardTable"/> for the same distinction applied to
    /// frontmatter's own list fields.
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
    /// The one unescape shape every <c>Unescape*Value</c>/<c>Unescape*Item</c> function above
    /// reduces to: scan for a backslash, and if the
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
