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

    // §5 block E's three fields — known only when the card's own kind is section, the same
    // two-pass reasoning as BlockOnlyFrontmatterKeys above ("base" is shared wire vocabulary with a
    // block card, disambiguated purely by which of isBlockCard/isSectionCard is true for this
    // card — the two are mutually exclusive since CardKind is a closed union each card has exactly
    // one case of).
    private static readonly HashSet<string> SectionOnlyFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "base", "closed_by", "closed_at",
    };

    // §6 block A's four fields — known only when the card's own kind is finding, the same
    // two-pass reasoning as SectionOnlyFrontmatterKeys above.
    private static readonly HashSet<string> FindingOnlyFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "instrument", "extent", "extent_value", "verified_at", "blind_spot", "blind_spot_card",
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

    // The five section-verdict-line fields this build recognises (§5 block E). Same rule again.
    private static readonly HashSet<string> KnownVerdictKeys = new(StringComparer.Ordinal)
    {
        "by", "verdict", "range-from", "range-to", "timestamp",
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
            onDecision: static () => false,
            onSection: static () => false);

        var isSectionCard = frontmatterResult.Frontmatter!.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => true);

        var isFindingCard = frontmatterResult.Frontmatter!.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => true,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

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

            if (isSectionCard && SectionOnlyFrontmatterKeys.Contains(key))
            {
                continue;
            }

            if (isFindingCard && FindingOnlyFrontmatterKeys.Contains(key))
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

        var sectionFields = SectionCardFields.Empty;
        if (isSectionCard)
        {
            var sectionFieldsResult = BuildSectionFields(fields);
            if (sectionFieldsResult.Failure is { } sectionFieldsFailure)
            {
                return Failure(sectionFieldsFailure);
            }

            // sectionFieldsResult.Failure is null here, so SectionFields is guaranteed non-null by
            // BuildSectionFields's own contract.
            sectionFields = sectionFieldsResult.SectionFields!;
        }

        var findingFields = FindingCardFields.Empty;
        if (isFindingCard)
        {
            var findingFieldsResult = BuildFindingFields(fields);
            if (findingFieldsResult.Failure is { } findingFieldsFailure)
            {
                return Failure(findingFieldsFailure);
            }

            // findingFieldsResult.Failure is null here, so FindingFields is guaranteed non-null by
            // BuildFindingFields's own contract.
            findingFields = findingFieldsResult.FindingFields!;
        }

        var bodyLines = new List<string>();
        while (cursor < lines.Length && !CardFileFormat.IsCommentHeader(lines[cursor]) && !CardFileFormat.IsHandoverLine(lines[cursor]) && !CardFileFormat.IsTransitionLine(lines[cursor]) && !CardFileFormat.IsVerdictLine(lines[cursor]))
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
        var verdicts = new List<SectionVerdictEntry>();
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

            if (CardFileFormat.IsVerdictLine(headerLine))
            {
                var verdictFieldsText = headerLine[CardFileFormat.VerdictLinePrefix.Length..^CardFileFormat.VerdictLineSuffix.Length];
                var verdictFieldsResult = ParseVerdictFields(verdictFieldsText);
                if (verdictFieldsResult.Failure is { } verdictFieldsFailure)
                {
                    return Failure(verdictFieldsFailure);
                }

                cursor++;

                // verdictFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
                // non-null by ParseVerdictFields's own contract.
                var verdictResult = BuildSectionVerdictEntry(verdictFieldsResult.Fields!, verdictFieldsResult.UnknownFields!);
                if (verdictResult.Failure is { } verdictFailure)
                {
                    return Failure(verdictFailure);
                }

                // verdictResult.Failure is null here, so Entry is guaranteed non-null by
                // BuildSectionVerdictEntry's own contract.
                verdicts.Add(verdictResult.Entry!);
                continue;
            }

            if (!CardFileFormat.IsCommentHeader(headerLine))
            {
                return Failure($"expected a comment header, a handover line, a transition line, a verdict line, or end of file, found: '{headerLine}'");
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
                if (CardFileFormat.IsCommentHeader(lines[cursor]) || CardFileFormat.IsHandoverLine(lines[cursor]) || CardFileFormat.IsTransitionLine(lines[cursor]) || CardFileFormat.IsVerdictLine(lines[cursor]))
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

        // BuildSectionFields only ever populates the three scalar fields — Verdicts is always the
        // append-only sequence just parsed above, folded in here once both are known.
        var sectionFieldsWithVerdicts = sectionFields with { Verdicts = [.. verdicts] };

        // frontmatterResult.Failure is null here, so Frontmatter is guaranteed non-null by BuildFrontmatter's own contract.
        return new CardFileParseResult.Success(
            new CardFile(frontmatterResult.Frontmatter!, body, comments, unknownFrontmatterFields, handovers, blockFields, transitions, sectionFieldsWithVerdicts, findingFields));
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
    /// Extracts §5 block E's three known-on-a-section-card scalar fields from a frontmatter
    /// <paramref name="fields"/> dictionary already confirmed to belong to a <c>section</c> card —
    /// see the <c>isSectionCard</c> gate in <see cref="Parse"/>, the only caller. Same "absent or
    /// empty parses to null" convention as <see cref="BuildBlockFields"/>. <c>closed_by</c> and
    /// <c>closed_at</c> are recorded together (<see cref="CardStore.CloseSectionUnderExistingLock"/>
    /// is the only writer of either), but this parser accepts either alone rather than refusing a
    /// hand-edited file that carries one without the other — degraded-mode legibility (ADR-0003)
    /// over strict round-trip enforcement here, the same latitude <see cref="BuildFrontmatter"/>
    /// already gives every other optional field. <see cref="SectionCardFields.Verdicts"/> is not
    /// built here — it comes from the append-only verdict-line sequence <see cref="Parse"/> parses
    /// separately, the same reason <see cref="BuildBlockFields"/> does not build
    /// <see cref="CardFile.Transitions"/>.
    /// </summary>
    private static (SectionCardFields? SectionFields, string? Failure) BuildSectionFields(
        IReadOnlyDictionary<string, string> fields)
    {
        var baseCommit = ParseOptionalFrontmatterValue(fields, "base");

        CardOwner? closedBy = null;
        if (fields.TryGetValue("closed_by", out var closedByText) && closedByText.Length > 0)
        {
            if (!CardOwnerWireFormat.TryParse(closedByText, out var parsedClosedBy))
            {
                return (null, $"section card has unrecognised 'closed_by': '{closedByText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
            }

            closedBy = parsedClosedBy;
        }

        DateTimeOffset? closedAt = null;
        if (fields.TryGetValue("closed_at", out var closedAtText) && closedAtText.Length > 0)
        {
            if (!TryParseTimestamp(closedAtText, out var parsedClosedAt))
            {
                return (null, $"section card has invalid 'closed_at': '{closedAtText}'");
            }

            closedAt = parsedClosedAt;
        }

        return (new SectionCardFields(baseCommit, closedBy, closedAt, []), null);
    }

    /// <summary>
    /// Extracts §6 block A's four known-on-a-finding-card fields from a frontmatter
    /// <paramref name="fields"/> dictionary already confirmed to belong to a <c>finding</c> card —
    /// see the <c>isFindingCard</c> gate in <see cref="Parse"/>, the only caller.
    /// <c>instrument</c>/<c>verified_at</c> follow the same "absent or empty parses to null"
    /// convention <see cref="BuildBlockFields"/> and <see cref="BuildSectionFields"/> already use.
    ///
    /// <para>
    /// <c>extent</c> is absent-or-<c>"block-scope"</c> → <see cref="FindingExtent.BlockScope"/> (the
    /// same default findings' "Extent is declared, widest by default" names for an undeclared
    /// extent), <c>"instrument"</c> → <see cref="FindingExtent.Instrument"/> reading its command from
    /// <c>extent_value</c>, or <c>"explicit"</c> → <see cref="FindingExtent.Explicit"/> reading its
    /// comma-joined item list from <c>extent_value</c> the same way <c>tasks</c>/<c>blocked_by</c>
    /// do. Every failure mode <see cref="FindingExtent"/>'s own constructors would otherwise throw
    /// on (a missing/empty command, an empty or blank-item explicit list) is checked here first, the
    /// same discipline <see cref="RequireNoEmptyListItem"/> already applies for
    /// <see cref="BlockCardFields"/> — untrusted input becomes a parse failure, never an unhandled
    /// exception. For the <c>explicit</c> form specifically, <see cref="ParseExtent"/> also wraps
    /// the call to <see cref="FindingExtent.Explicit"/> in a try/catch, so that guarantee holds
    /// regardless of whether these pre-checks stay correctly ordered ahead of construction — see the
    /// comment at that call site.
    /// </para>
    ///
    /// <para>
    /// <c>blind_spot</c> has no absent-parses-to-null convention: it is <c>"none"</c> →
    /// <see cref="FindingBlindSpotDeclaration.None"/>, <c>"raised-as"</c> →
    /// <see cref="FindingBlindSpotDeclaration.RaisedAs"/> reading the card id from
    /// <c>blind_spot_card</c>, or a parse failure — including when the key is absent altogether.
    /// This is deliberate: <see cref="FindingCardFields.BlindSpot"/> cannot represent "undeclared" at
    /// all (see that type's own doc comment), so a finding card genuinely missing the field is
    /// malformed input, the same way a card missing <c>id</c> or <c>kind</c> is — not a legacy wire
    /// form to widen for, since no build has ever shipped a <c>finding</c> card writer before this
    /// one (O-4 does not apply here).
    /// </para>
    /// </summary>
    private static (FindingCardFields? FindingFields, string? Failure) BuildFindingFields(
        IReadOnlyDictionary<string, string> fields)
    {
        var instrument = ParseOptionalFrontmatterValue(fields, "instrument");
        var verifiedAt = ParseOptionalFrontmatterValue(fields, "verified_at");

        var (extent, extentFailure) = ParseExtent(fields);
        if (extentFailure is not null)
        {
            return (null, extentFailure);
        }

        var (blindSpot, blindSpotFailure) = ParseBlindSpot(fields);
        if (blindSpotFailure is not null)
        {
            return (null, blindSpotFailure);
        }

        return (new FindingCardFields(instrument, extent!, verifiedAt, blindSpot!), null);
    }

    private static (FindingExtent? Extent, string? Failure) ParseExtent(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("extent", out var rawForm) || rawForm.Length == 0)
        {
            return (FindingExtent.BlockScope, null);
        }

        var form = CardFileFormat.UnescapeFrontmatterValue(rawForm);
        switch (form)
        {
            case "block-scope":
                return (FindingExtent.BlockScope, null);

            case "instrument":
                {
                    var command = ParseOptionalFrontmatterValue(fields, "extent_value");
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        return (null, "finding card has extent 'instrument' with no (or an empty) 'extent_value'");
                    }

                    return (FindingExtent.Instrument(command), null);
                }

            case "explicit":
                {
                    var items = fields.TryGetValue("extent_value", out var itemsText)
                        ? CardFileFormat.SplitFrontmatterList(itemsText)
                        : (IReadOnlyList<string>)[];

                    if (items.Count == 0)
                    {
                        return (null, "finding card has extent 'explicit' with no items in 'extent_value'");
                    }

                    if (RequireNoEmptyListItem(items, "extent_value") is { } itemsFailure)
                    {
                        return (null, itemsFailure);
                    }

                    // The two checks above are the primary, message-bearing guard. This try/catch
                    // is the backstop: FindingExtent.Explicit's own validating accessor rejects the
                    // same two conditions by throwing ArgumentException, so if either check above is
                    // ever removed or reordered, construction still degrades to a parse failure here
                    // rather than an unhandled exception reaching untrusted-input callers (reviewer
                    // finding, §6 block A).
                    try
                    {
                        return (FindingExtent.Explicit(items), null);
                    }
                    catch (ArgumentException ex)
                    {
                        return (null, $"finding card has an invalid extent 'explicit' declaration: {ex.Message}");
                    }
                }

            default:
                return (null, $"finding card has unrecognised 'extent': '{form}'. Recognised forms: instrument, explicit, block-scope.");
        }
    }

    private static (FindingBlindSpotDeclaration? BlindSpot, string? Failure) ParseBlindSpot(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("blind_spot", out var rawForm) || rawForm.Length == 0)
        {
            return (null, "missing required frontmatter field for a finding card: blind_spot");
        }

        var form = CardFileFormat.UnescapeFrontmatterValue(rawForm);
        switch (form)
        {
            case "none":
                return (FindingBlindSpotDeclaration.None, null);

            case "raised-as":
                {
                    var cardId = ParseOptionalFrontmatterValue(fields, "blind_spot_card");
                    if (string.IsNullOrWhiteSpace(cardId))
                    {
                        return (null, "finding card has blind_spot 'raised-as' with no (or an empty) 'blind_spot_card'");
                    }

                    return (FindingBlindSpotDeclaration.RaisedAs(cardId), null);
                }

            default:
                return (null, $"finding card has unrecognised 'blind_spot': '{form}'. Recognised declarations: none, raised-as.");
        }
    }

    /// <summary>
    /// Parses <c>gate_results</c>: a comma-joined list (the same <see cref="CardFileFormat.
    /// SplitFrontmatterList"/> <c>tasks</c>/<c>blocked_by</c> use) of <c>label=exitcode=round</c>
    /// items (§5 remediation, DEVLOG §5 finding B2 — <c>round</c> added so an earlier round's
    /// result stays distinguishable from the current one; see <see cref="GateResult.Round"/>'s own
    /// doc comment). Each item is split on its <em>first</em> <c>=</c> — <see cref="GateResult.
    /// IsValidLabel"/> already refuses a label containing one, so the first <c>=</c> in a
    /// well-formed item is always the label/exit-code boundary — and the remainder is then checked
    /// for a second <c>=</c> to find the exit-code/round boundary, since neither a valid integer
    /// exit code nor a valid integer round can itself contain one.
    ///
    /// <para>
    /// <b>The legacy two-part form (<c>label=exitcode</c>, no round) still parses (§5 remediation,
    /// reviewer finding against the shipped block D binary).</b> B2's own remediation, in its first
    /// pass, made every two-part <c>gate_results</c> item ever written by the shipped block D
    /// binary unreadable — a real card, in the exact format the tool itself wrote it, refused as
    /// malformed with no warning and no migration. "Anything the tool has ever written, the tool
    /// can read" is unconditional (the same proposition block E's own remediation closed for a
    /// card the tool writes and cannot immediately read back — this is that same defect displaced
    /// in time: a card the tool wrote in the past and can no longer read now). A missing round
    /// separator is therefore not a failure: <c>remainder</c> is read whole as the exit code and
    /// <paramref name="fields"/>'s absent third part defaults to round <c>1</c> — the same default
    /// <see cref="BlockCardFields.GateStatusOf"/> and <see cref="CardStore.
    /// RecordGateResultUnderExistingLock"/> already apply when <c>round</c> itself is unset. This
    /// is not a data migration (nothing rewrites the file); it is simply preserving what parses
    /// today at the cost of one branch, rather than refusing a well-formed card because a later
    /// format grew a field it did not have yet.
    /// </para>
    ///
    /// Three things can fail: an empty or invalid label, an exit code that is not a valid integer,
    /// and (three-part form only) a round that is not a valid integer — each folded into a parse
    /// failure here rather than reaching <see cref="BlockCardFields"/>'s own constructor guard as
    /// an unhandled exception, same discipline as <see cref="RequireNoEmptyListItem"/>.
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
        var seenLabelsByRound = new HashSet<(string Label, int Round)>();
        foreach (var item in items)
        {
            var labelSeparatorIndex = item.IndexOf('=');
            if (labelSeparatorIndex < 0)
            {
                return (null, $"block card has a malformed gate_results item (expected 'label=exitcode' or 'label=exitcode=round'): '{item}'");
            }

            var label = item[..labelSeparatorIndex];
            var remainder = item[(labelSeparatorIndex + 1)..];

            if (!GateResult.IsValidLabel(label))
            {
                return (null, $"block card has an invalid gate_results label: '{label}'");
            }

            // Legacy two-part form: no second '=', so remainder is the whole exit code and round
            // defaults to 1 — see this method's own doc comment for why this is not a refusal.
            var roundSeparatorIndex = remainder.IndexOf('=');
            var exitCodeText = roundSeparatorIndex < 0 ? remainder : remainder[..roundSeparatorIndex];
            var roundText = roundSeparatorIndex < 0 ? null : remainder[(roundSeparatorIndex + 1)..];

            if (!int.TryParse(exitCodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
            {
                return (null, $"block card has an invalid gate_results exit code for '{label}': '{exitCodeText}'");
            }

            var round = 1;
            if (roundText is not null && (!int.TryParse(roundText, NumberStyles.Integer, CultureInfo.InvariantCulture, out round) || round < 1))
            {
                return (null, $"block card has an invalid gate_results round for '{label}': '{roundText}'");
            }

            if (!seenLabelsByRound.Add((label, round)))
            {
                return (null, $"block card has more than one gate_results entry for label '{label}' in round {round}");
            }

            results.Add(new GateResult(label, exitCode, round));
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

    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseVerdictFields(string verdictFieldsText) =>
        ParseKeyValueTokens(verdictFieldsText, KnownVerdictKeys, "verdict line");

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

    private static (SectionVerdictEntry? Entry, string? Failure) BuildSectionVerdictEntry(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue("by", out var byText))
        {
            return (null, "verdict missing required field: by");
        }

        if (!CardOwnerWireFormat.TryParse(byText, out var by))
        {
            return (null, $"verdict has unrecognised 'by': '{byText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("verdict", out var verdictText))
        {
            return (null, "verdict missing required field: verdict");
        }

        if (!SectionVerdictWireFormat.TryParse(verdictText, out var verdict))
        {
            return (null, $"verdict has unrecognised 'verdict': '{verdictText}'. Recognised verdicts: {SectionVerdictWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("range-from", out var rangeFromRaw))
        {
            return (null, "verdict missing required field: range-from");
        }

        var rangeFrom = CardFileFormat.UnescapeCommentHeaderValue(rangeFromRaw);
        if (!SectionVerdictEntry.IsValidRangeValue(rangeFrom))
        {
            return (null, "verdict has an empty or whitespace-only 'range-from'");
        }

        if (!fields.TryGetValue("range-to", out var rangeToRaw))
        {
            return (null, "verdict missing required field: range-to");
        }

        var rangeTo = CardFileFormat.UnescapeCommentHeaderValue(rangeToRaw);
        if (!SectionVerdictEntry.IsValidRangeValue(rangeTo))
        {
            return (null, "verdict has an empty or whitespace-only 'range-to'");
        }

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, "verdict missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"verdict has invalid timestamp: '{timestampText}'");
        }

        return (new SectionVerdictEntry(by, verdict, rangeFrom, rangeTo, timestamp, unknownFields), null);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static CardFileParseResult.Failure Failure(string reason) => new(reason);
}
