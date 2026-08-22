using System.Globalization;

namespace Callboard.Cards;

/// <summary>
/// Parses the ADR-0003 card file format: YAML-style frontmatter fences, a Markdown body, and
/// zero or more appended comment blocks — hand-rolled against the deliberately narrow schema
/// card-model defines, not a general YAML parser. See the 2.2 DEVLOG post for the AOT
/// verdict that led here: a reflection-based YAML library (YamlDotNet's default
/// <c>SerializerBuilder</c>/<c>DeserializerBuilder</c>) produced real IL3050/IL2104/IL3053
/// trim/AOT warnings under a real <c>dotnet publish -r osx-arm64</c>, which
/// <c>TreatWarningsAsErrors</c> would turn into build failures — so this format is read and
/// written by hand instead (ADR-0003 Consequences anticipates exactly this outcome).
///
/// Frontmatter keys are matched with explicit <see cref="StringComparer.Ordinal"/> — see
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class CardFileParser
{
    private static readonly string[] LineSplitSeparators = ["\n"];

    // The nine frontmatter keys 2.1's schema defines. Anything else is an unknown field — carried
    // verbatim on CardFile.UnknownFrontmatterFields rather than dropped; see that member's doc
    // comment for the extensibility rule this implements.
    private static readonly HashSet<string> KnownFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "id", "kind", "title", "status", "owner", "scope", "section", "created", "updated",
    };

    // §5's five fields — known only when the card's own kind is block (Architect ruling, §5 block
    // A brief). Checked separately from KnownFrontmatterKeys because that decision needs the
    // card's kind, which is itself one of the frontmatter lines being classified — see the two-pass
    // handling in Parse below.
    private static readonly HashSet<string> BlockOnlyFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "base", "reviewed_state", "tasks", "gate_results", "round", "blocked_by",
    };

    // The six comment-header fields this build recognises. Same rule, same reason, applied to the
    // per-comment header instead of the frontmatter block.
    private static readonly HashSet<string> KnownCommentHeaderKeys = new(StringComparer.Ordinal)
    {
        "id", "author", "reply-to", "to", "resolves", "timestamp",
    };

    // The three handover-line fields this build recognises (card-model 4.5). Same rule again.
    private static readonly HashSet<string> KnownHandoverKeys = new(StringComparer.Ordinal)
    {
        "by", "to", "timestamp",
    };

    // The five block-transition-line fields this build recognises (§5 block C). Same rule again.
    private static readonly HashSet<string> KnownTransitionKeys = new(StringComparer.Ordinal)
    {
        "by", "name", "from", "to", "timestamp",
    };

    internal static CardFileParseResult Parse(string rawText)
    {
        // CardFileWriter.Serialize always terminates its output with exactly one trailing '\n'
        // (from the closing frontmatter fence, the last body line, or the last comment footer —
        // whichever comes last). Stripping that one guaranteed trailing newline before splitting
        // keeps every remaining element a genuine line, rather than leaving a sentinel empty
        // element that a naive split would otherwise misread as one more (empty) line of content.
        var normalized = rawText.EndsWith('\n') ? rawText[..^1] : rawText;
        var lines = normalized.Split(LineSplitSeparators, StringSplitOptions.None);
        var cursor = 0;

        if (cursor >= lines.Length || !string.Equals(lines[cursor], CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
        {
            return Failure("missing opening frontmatter delimiter");
        }

        cursor++;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedFields = new List<(string Key, string RawValue)>();

        while (true)
        {
            if (cursor >= lines.Length)
            {
                return Failure("missing closing frontmatter delimiter");
            }

            var line = lines[cursor];

            if (string.Equals(line, CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
            {
                cursor++;
                break;
            }

            var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return Failure($"malformed frontmatter line: '{line}'");
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 2)..];
            fields[key] = value;
            orderedFields.Add((key, value));

            cursor++;
        }

        var frontmatterResult = BuildFrontmatter(fields);
        if (frontmatterResult.Failure is { } frontmatterFailure)
        {
            return Failure(frontmatterFailure);
        }

        // frontmatterResult.Failure is null here, so Frontmatter is guaranteed non-null by
        // BuildFrontmatter's own contract — this is the only point at which the card's kind is
        // known, so classifying the §5 keys as known-or-unknown has to wait until here.
        var isBlockCard = frontmatterResult.Frontmatter!.Kind.Match(
            onBlock: static () => true,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false);

        var unknownFrontmatterFields = new List<(string Key, string RawValue)>();
        foreach (var (key, value) in orderedFields)
        {
            if (KnownFrontmatterKeys.Contains(key))
            {
                continue;
            }

            if (isBlockCard && BlockOnlyFrontmatterKeys.Contains(key))
            {
                continue;
            }

            unknownFrontmatterFields.Add((key, value));
        }

        var blockFields = BlockCardFields.Empty;
        if (isBlockCard)
        {
            var blockFieldsResult = BuildBlockFields(fields);
            if (blockFieldsResult.Failure is { } blockFieldsFailure)
            {
                return Failure(blockFieldsFailure);
            }

            // blockFieldsResult.Failure is null here, so BlockFields is guaranteed non-null by
            // BuildBlockFields's own contract.
            blockFields = blockFieldsResult.BlockFields!;
        }

        var bodyLines = new List<string>();
        while (cursor < lines.Length && !CardFileFormat.IsCommentHeader(lines[cursor]) && !CardFileFormat.IsHandoverLine(lines[cursor]) && !CardFileFormat.IsTransitionLine(lines[cursor]))
        {
            bodyLines.Add(CardFileFormat.UnescapeContentLine(lines[cursor]));
            cursor++;
        }

        // The body never carries a trailing blank line introduced purely by the join/split
        // round trip — an appended comment, an appended handover, or EOF always follows the last
        // real body line directly.
        var body = string.Join('\n', bodyLines);

        // Comments and handovers are two independent append-only sequences (card-model 4.5) that
        // share one appended region of the file — this loop recognises which kind of block starts
        // at the cursor on each pass and routes to the matching list, rather than assuming a fixed
        // relative order between the two. CardFileWriter always emits every handover before every
        // comment, but nothing here depends on that: a hand-edited or future-written file with the
        // two interleaved parses identically.
        var comments = new List<CardComment>();
        var handovers = new List<CardHandover>();
        var transitions = new List<CardBlockTransitionEntry>();
        while (cursor < lines.Length)
        {
            var headerLine = lines[cursor];

            if (CardFileFormat.IsHandoverLine(headerLine))
            {
                var handoverFieldsText = headerLine[CardFileFormat.HandoverLinePrefix.Length..^CardFileFormat.HandoverLineSuffix.Length];
                var handoverFieldsResult = ParseHandoverFields(handoverFieldsText);
                if (handoverFieldsResult.Failure is { } handoverFieldsFailure)
                {
                    return Failure(handoverFieldsFailure);
                }

                cursor++;

                // handoverFieldsResult.Failure is null here, so Fields/UnknownFields are
                // guaranteed non-null by ParseHandoverFields's own contract.
                var handoverResult = BuildHandover(handoverFieldsResult.Fields!, handoverFieldsResult.UnknownFields!);
                if (handoverResult.Failure is { } handoverFailure)
                {
                    return Failure(handoverFailure);
                }

                // handoverResult.Failure is null here, so Handover is guaranteed non-null by
                // BuildHandover's own contract.
                handovers.Add(handoverResult.Handover!);
                continue;
            }

            if (CardFileFormat.IsTransitionLine(headerLine))
            {
                var transitionFieldsText = headerLine[CardFileFormat.TransitionLinePrefix.Length..^CardFileFormat.TransitionLineSuffix.Length];
                var transitionFieldsResult = ParseTransitionFields(transitionFieldsText);
                if (transitionFieldsResult.Failure is { } transitionFieldsFailure)
                {
                    return Failure(transitionFieldsFailure);
                }

                cursor++;

                // transitionFieldsResult.Failure is null here, so Fields/UnknownFields are
                // guaranteed non-null by ParseTransitionFields's own contract.
                var transitionResult = BuildBlockTransitionEntry(transitionFieldsResult.Fields!, transitionFieldsResult.UnknownFields!);
                if (transitionResult.Failure is { } transitionFailure)
                {
                    return Failure(transitionFailure);
                }

                // transitionResult.Failure is null here, so Entry is guaranteed non-null by
                // BuildBlockTransitionEntry's own contract.
                transitions.Add(transitionResult.Entry!);
                continue;
            }

            if (!CardFileFormat.IsCommentHeader(headerLine))
            {
                return Failure($"expected a comment header, a handover line, a transition line, or end of file, found: '{headerLine}'");
            }

            if (!headerLine.EndsWith(CardFileFormat.CommentHeaderSuffix, StringComparison.Ordinal))
            {
                return Failure($"malformed comment header: '{headerLine}'");
            }

            var headerFieldsText = headerLine[CardFileFormat.CommentHeaderPrefix.Length..^CardFileFormat.CommentHeaderSuffix.Length];
            var headerFieldsResult = ParseCommentHeaderFields(headerFieldsText);
            if (headerFieldsResult.Failure is { } headerFailure)
            {
                return Failure(headerFailure);
            }

            cursor++;

            var commentBodyLines = new List<string>();
            while (cursor < lines.Length && !CardFileFormat.IsCommentFooter(lines[cursor]))
            {
                if (CardFileFormat.IsCommentHeader(lines[cursor]) || CardFileFormat.IsHandoverLine(lines[cursor]) || CardFileFormat.IsTransitionLine(lines[cursor]))
                {
                    return Failure($"missing comment footer before next block: '{lines[cursor]}'");
                }

                commentBodyLines.Add(CardFileFormat.UnescapeContentLine(lines[cursor]));
                cursor++;
            }

            if (cursor >= lines.Length)
            {
                return Failure("missing comment footer at end of file");
            }

            cursor++; // consume the footer line

            // headerFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
            // non-null by ParseCommentHeaderFields's own contract.
            var commentResult = BuildComment(
                headerFieldsResult.Fields!, headerFieldsResult.UnknownFields!, string.Join('\n', commentBodyLines));
            if (commentResult.Failure is { } commentFailure)
            {
                return Failure(commentFailure);
            }

            // commentResult.Failure is null here, so Comment is guaranteed non-null by BuildComment's own contract.
            comments.Add(commentResult.Comment!);
        }

        // frontmatterResult.Failure is null here, so Frontmatter is guaranteed non-null by BuildFrontmatter's own contract.
        return new CardFileParseResult.Success(
            new CardFile(frontmatterResult.Frontmatter!, body, comments, unknownFrontmatterFields, handovers, blockFields, transitions));
    }

    private static (CardFrontmatter? Frontmatter, string? Failure) BuildFrontmatter(
        IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("id", out var rawId))
        {
            return (null, "missing required frontmatter field: id");
        }

        var id = CardFileFormat.UnescapeFrontmatterValue(rawId);

        if (!fields.TryGetValue("kind", out var kindText))
        {
            return (null, "missing required frontmatter field: kind");
        }

        if (!CardKindWireFormat.TryParse(kindText, out var kind))
        {
            return (null, $"unrecognised kind: '{kindText}'. Recognised kinds: {CardKindWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("title", out var rawTitle))
        {
            return (null, "missing required frontmatter field: title");
        }

        var title = CardFileFormat.UnescapeFrontmatterValue(rawTitle);

        if (!fields.TryGetValue("status", out var rawStatus))
        {
            return (null, "missing required frontmatter field: status");
        }

        var status = CardFileFormat.UnescapeFrontmatterValue(rawStatus);

        if (!fields.TryGetValue("owner", out var ownerText))
        {
            return (null, "missing required frontmatter field: owner");
        }

        if (!CardOwnerWireFormat.TryParse(ownerText, out var owner))
        {
            return (null, $"unrecognised owner: '{ownerText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("scope", out var scopeText))
        {
            return (null, "missing required frontmatter field: scope");
        }

        if (!CardScopeWireFormat.TryParse(scopeText, out var scope))
        {
            return (null, $"unrecognised scope: '{scopeText}'. Recognised scopes: {CardScopeWireFormat.RecognisedValues}.");
        }

        var section = fields.TryGetValue("section", out var rawSection)
            ? CardFileFormat.UnescapeFrontmatterValue(rawSection)
            : string.Empty;

        if (!fields.TryGetValue("created", out var createdText))
        {
            return (null, "missing required frontmatter field: created");
        }

        if (!TryParseTimestamp(createdText, out var created))
        {
            return (null, $"invalid created timestamp: '{createdText}'");
        }

        if (!fields.TryGetValue("updated", out var updatedText))
        {
            return (null, "missing required frontmatter field: updated");
        }

        if (!TryParseTimestamp(updatedText, out var updated))
        {
            return (null, $"invalid updated timestamp: '{updatedText}'");
        }

        return (new CardFrontmatter(id, kind, title, status, owner, scope, section, created, updated), null);
    }

    /// <summary>
    /// Extracts §5's five known-on-a-block-card fields from a frontmatter <paramref name="fields"/>
    /// dictionary already confirmed to belong to a <c>block</c> card — see the <c>isBlockCard</c>
    /// gate in <see cref="Parse"/>, the only caller. An absent field, or one present with an empty
    /// raw value, parses to <see langword="null"/> (scalars) or an empty list (<c>tasks</c>/
    /// <c>blocked_by</c>) — "not yet recorded", the same convention <see cref="CardFrontmatter.Section"/>
    /// uses. Two things can fail: <c>round</c>, on any non-empty value that is not a valid integer,
    /// and an empty or whitespace-only item inside <c>tasks</c> or <c>blocked_by</c> — a hand-authored
    /// file that reaches this parser with, say, <c>tasks: ,</c> (a raw value that splits into two
    /// empty items) fails here rather than reaching <see cref="BlockCardFields"/>'s own constructor
    /// guard as an unhandled exception (reviewer finding 1, §5 block A review — see
    /// <see cref="BlockCardFields"/>'s doc comment for why that item is unrepresentable at all).
    /// </summary>
    private static (BlockCardFields? BlockFields, string? Failure) BuildBlockFields(
        IReadOnlyDictionary<string, string> fields)
    {
        var baseCommit = ParseOptionalFrontmatterValue(fields, "base");
        var reviewedState = ParseOptionalFrontmatterValue(fields, "reviewed_state");

        var tasks = fields.TryGetValue("tasks", out var tasksText)
            ? CardFileFormat.SplitFrontmatterList(tasksText)
            : (IReadOnlyList<string>)[];

        if (RequireNoEmptyListItem(tasks, "tasks") is { } tasksFailure)
        {
            return (null, tasksFailure);
        }

        var blockedBy = fields.TryGetValue("blocked_by", out var blockedByText)
            ? CardFileFormat.SplitFrontmatterList(blockedByText)
            : (IReadOnlyList<string>)[];

        if (RequireNoEmptyListItem(blockedBy, "blocked_by") is { } blockedByFailure)
        {
            return (null, blockedByFailure);
        }

        int? round = null;
        if (fields.TryGetValue("round", out var roundText) && roundText.Length > 0)
        {
            if (!int.TryParse(roundText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRound))
            {
                return (null, $"block card has invalid round: '{roundText}'");
            }

            round = parsedRound;
        }

        var (gateResults, gateResultsFailure) = ParseGateResults(fields);
        if (gateResultsFailure is not null)
        {
            return (null, gateResultsFailure);
        }

        return (new BlockCardFields(baseCommit, reviewedState, tasks, round, blockedBy, gateResults!), null);
    }

    /// <summary>
    /// Parses <c>gate_results</c>: a comma-joined list (the same <see cref="CardFileFormat.
    /// SplitFrontmatterList"/> <c>tasks</c>/<c>blocked_by</c> use) of <c>label=exitcode</c> items.
    /// Each item is split on its <em>first</em> <c>=</c> — <see cref="GateResult.IsValidLabel"/>
    /// already refuses a label containing one, so the first (and only) <c>=</c> in a well-formed
    /// item is always the label/exit-code boundary. Three things can fail: a missing <c>=</c>, an
    /// empty or invalid label, and an exit code that is not a valid integer — each folded into a
    /// parse failure here rather than reaching <see cref="BlockCardFields"/>'s own constructor
    /// guard as an unhandled exception, same discipline as <see cref="RequireNoEmptyListItem"/>.
    /// </summary>
    private static (IReadOnlyList<GateResult>? GateResults, string? Failure) ParseGateResults(
        IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("gate_results", out var raw))
        {
            return (Array.Empty<GateResult>(), null);
        }

        var items = CardFileFormat.SplitFrontmatterList(raw);
        var results = new List<GateResult>(items.Count);
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var separatorIndex = item.IndexOf('=');
            if (separatorIndex < 0)
            {
                return (null, $"block card has a malformed gate_results item (expected 'label=exitcode'): '{item}'");
            }

            var label = item[..separatorIndex];
            var exitCodeText = item[(separatorIndex + 1)..];

            if (!GateResult.IsValidLabel(label))
            {
                return (null, $"block card has an invalid gate_results label: '{label}'");
            }

            if (!int.TryParse(exitCodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
            {
                return (null, $"block card has an invalid gate_results exit code for '{label}': '{exitCodeText}'");
            }

            if (!seenLabels.Add(label))
            {
                return (null, $"block card has more than one gate_results entry for label '{label}'");
            }

            results.Add(new GateResult(label, exitCode));
        }

        return (results, null);
    }

    /// <summary>
    /// The parse-time half of the same rule <see cref="BlockCardFields"/>'s constructor enforces
    /// at construction time — see <see cref="BlockCardFields.IsValidListItem"/>, which both this
    /// and that guard react to. Applied before construction so untrusted input that violates it
    /// (an empty raw list item straight off the wire) becomes a parse <see cref="string"/> failure
    /// here, never an exception escaping from the constructor.
    /// </summary>
    private static string? RequireNoEmptyListItem(IReadOnlyList<string> items, string fieldName)
    {
        foreach (var item in items)
        {
            if (!BlockCardFields.IsValidListItem(item))
            {
                return $"block card has an empty or whitespace-only item in '{fieldName}'";
            }
        }

        return null;
    }

    private static string? ParseOptionalFrontmatterValue(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var rawValue) || rawValue.Length == 0)
        {
            return null;
        }

        return CardFileFormat.UnescapeFrontmatterValue(rawValue);
    }

    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseCommentHeaderFields(string headerFieldsText) =>
        ParseKeyValueTokens(headerFieldsText, KnownCommentHeaderKeys, "comment header");

    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseHandoverFields(string handoverFieldsText) =>
        ParseKeyValueTokens(handoverFieldsText, KnownHandoverKeys, "handover line");

    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseTransitionFields(string transitionFieldsText) =>
        ParseKeyValueTokens(transitionFieldsText, KnownTransitionKeys, "transition line");

    /// <summary>
    /// The <c>key=value</c> token parsing comment headers and handover lines both use — one
    /// implementation so the two block kinds can never drift apart on how their header text is
    /// split.
    /// </summary>
    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseKeyValueTokens(string fieldsText, IReadOnlySet<string> knownKeys, string blockLabel)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownFields = new List<(string Key, string RawValue)>();

        if (fieldsText.Length > 0)
        {
            foreach (var token in fieldsText.Split(' '))
            {
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex < 0)
                {
                    return (null, null, $"malformed {blockLabel} field: '{token}'");
                }

                var key = token[..equalsIndex];
                var rawValue = token[(equalsIndex + 1)..];
                fields[key] = rawValue;

                if (!knownKeys.Contains(key))
                {
                    unknownFields.Add((key, rawValue));
                }
            }
        }

        return (fields, unknownFields, null);
    }

    private static (CardComment? Comment, string? Failure) BuildComment(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields,
        string body)
    {
        if (!fields.TryGetValue("id", out var rawId))
        {
            return (null, "comment missing required field: id");
        }

        var id = CardFileFormat.UnescapeCommentHeaderValue(rawId);

        if (!fields.TryGetValue("author", out var authorText))
        {
            return (null, $"comment '{id}' missing required field: author");
        }

        if (!CardOwnerWireFormat.TryParse(authorText, out var author))
        {
            return (null, $"comment '{id}' has unrecognised author: '{authorText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, $"comment '{id}' missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"comment '{id}' has invalid timestamp: '{timestampText}'");
        }

        string? replyTo = fields.TryGetValue("reply-to", out var replyToText)
            ? CardFileFormat.UnescapeCommentHeaderValue(replyToText)
            : null;

        CardOwner? to = null;
        if (fields.TryGetValue("to", out var toText))
        {
            if (!CardOwnerWireFormat.TryParse(toText, out var toOwner))
            {
                return (null, $"comment '{id}' has unrecognised 'to': '{toText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
            }

            to = toOwner;
        }

        string? resolves = fields.TryGetValue("resolves", out var resolvesText)
            ? CardFileFormat.UnescapeCommentHeaderValue(resolvesText)
            : null;

        return (new CardComment(id, author, timestamp, body, replyTo, to, resolves, unknownFields), null);
    }

    private static (CardHandover? Handover, string? Failure) BuildHandover(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue("by", out var byText))
        {
            return (null, "handover missing required field: by");
        }

        if (!CardOwnerWireFormat.TryParse(byText, out var by))
        {
            return (null, $"handover has unrecognised 'by': '{byText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("to", out var toText))
        {
            return (null, "handover missing required field: to");
        }

        if (!CardOwnerWireFormat.TryParse(toText, out var to))
        {
            return (null, $"handover has unrecognised 'to': '{toText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, "handover missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"handover has invalid timestamp: '{timestampText}'");
        }

        return (new CardHandover(by, to, timestamp, unknownFields), null);
    }

    private static (CardBlockTransitionEntry? Entry, string? Failure) BuildBlockTransitionEntry(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue("by", out var byText))
        {
            return (null, "transition missing required field: by");
        }

        if (!CardOwnerWireFormat.TryParse(byText, out var by))
        {
            return (null, $"transition has unrecognised 'by': '{byText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("name", out var name) || name.Length == 0)
        {
            return (null, "transition missing required field: name");
        }

        if (!fields.TryGetValue("from", out var fromText))
        {
            return (null, "transition missing required field: from");
        }

        if (!BlockFlowStateWireFormat.TryParse(fromText, out var from))
        {
            return (null, $"transition has unrecognised 'from': '{fromText}'. Recognised states: {BlockFlowStateWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("to", out var toText))
        {
            return (null, "transition missing required field: to");
        }

        if (!BlockFlowStateWireFormat.TryParse(toText, out var to))
        {
            return (null, $"transition has unrecognised 'to': '{toText}'. Recognised states: {BlockFlowStateWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, "transition missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"transition has invalid timestamp: '{timestampText}'");
        }

        return (new CardBlockTransitionEntry(by, name, from, to, timestamp, unknownFields), null);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static CardFileParseResult.Failure Failure(string reason) => new(reason);
}
