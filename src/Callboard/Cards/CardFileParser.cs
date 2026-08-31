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
        // §8a block B's addition: the key of the finding this card is remediating (BlockCardFields.
        // FindingKey), unset for a task-implementing block.
        "finding_key",
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
        // §6 block C's two additions: extent_fingerprint (FindingExtentFingerprint) and
        // disposition (FindingDisposition) — same two-pass classification, same rationale.
        "extent_fingerprint", "disposition",
    };

    // Known only when the card's own kind is one of the four register kinds (rule, hazard,
    // obligation, decision), the same two-pass reasoning as FindingOnlyFrontmatterKeys above.
    // §7 block C remediation: built from RegisterCardFieldKeys.All — the one declaration
    // CardFileWriter's own emission also reads from — rather than a second, independently
    // hand-typed list. That type's own doc comment explains the defect this structurally closes:
    // an earlier version of this HashSet listed four keys while the writer emitted seven, so the
    // three it didn't know (owed_by/supersedes/superseded_by) were filed as unknown and re-emitted
    // alongside the known line on every parse-then-write cycle, duplicating it without bound.
    private static readonly HashSet<string> RegisterOnlyFrontmatterKeys = new(RegisterCardFieldKeys.All, StringComparer.Ordinal);

    // §9 block D's seven fields — known only when the card's own kind is question, the same
    // two-pass reasoning as SectionOnlyFrontmatterKeys above (a question cannot share
    // RegisterOnlyFrontmatterKeys — see QuestionCardFields's own doc comment for why).
    private static readonly HashSet<string> QuestionOnlyFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "answered_by", "answered_at", "answer_decision", "answer_inline",
        "deferred_by", "deferred_at", "deferred_target",
    };

    // The six comment-header fields this build recognises, plus the four §8 block B nit-only ones
    // (CardCommentNitFieldKeys.All — the shared declaration this set and CardFileWriter's own
    // emission both read from, the wire-key drift guard carried from §7's close). Same rule, same
    // reason, applied to the per-comment header instead of the frontmatter block.
    private static readonly HashSet<string> KnownCommentHeaderKeys = new(
        new[] { "id", "author", "reply-to", "to", "resolves", "timestamp" }
            .Concat(CardCommentNitFieldKeys.All),
        StringComparer.Ordinal);

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

    // The three section-authorisation-line fields this build recognises (§8a block C). Same rule again.
    private static readonly HashSet<string> KnownAuthorisationKeys = new(StringComparer.Ordinal)
    {
        "by", "reason", "timestamp",
    };

    // The approval-claim-line and approval-limit-line fields this build recognises (§8 block A) —
    // built from CardApprovalFieldKeys, the one declaration CardFileWriter's own emission also reads
    // from, rather than a second hand-typed list (the wire-key drift guard carried from §7's close;
    // see that type's own doc comment).
    private static readonly HashSet<string> KnownClaimKeys = new(CardApprovalFieldKeys.Claim, StringComparer.Ordinal);
    private static readonly HashSet<string> KnownLimitKeys = new(CardApprovalFieldKeys.Limit, StringComparer.Ordinal);

    // The four refusal-line fields this build recognises (§9 block A). Same rule again.
    private static readonly HashSet<string> KnownRefusalKeys = new(StringComparer.Ordinal)
    {
        "by", "rule", "remedy", "timestamp",
    };

    /// <summary>
    /// Best-effort recovery of a declared <c>id</c> from a file <see cref="Parse"/> has already
    /// refused (§13.6) — evidence about a claim, never a second route to the record (D3: the
    /// record is the file, and a file <see cref="Parse"/> rejected is not the record). Reads only
    /// the leading frontmatter fence — the first line through the next line that is exactly
    /// <see cref="CardFileFormat.FrontmatterFence"/> — the same span <see cref="Parse"/> itself
    /// would trust as frontmatter. A body line that happens to read <c>id: blk-0007</c> is never
    /// consulted, because it is never inside that span: this walks lines in file order and returns
    /// as soon as it meets the closing fence, so a second, later "id:" line anywhere past it — in
    /// the body, or inside a comment block — cannot influence the result. If the fence itself is
    /// not intact (no opening line, or no closing line before end of file) there is nothing safe to
    /// recover, and this returns <see langword="null"/> rather than guessing from unbounded text —
    /// the same class of defect §11 named: attributing meaning to text a caller/file author
    /// controls without first validating the structure that bounds it.
    /// </summary>
    internal static string? TryRecoverDeclaredId(string rawText)
    {
        var normalized = rawText.EndsWith('\n') ? rawText[..^1] : rawText;
        var lines = normalized.Split(LineSplitSeparators, StringSplitOptions.None);

        if (lines.Length == 0 || !string.Equals(lines[0], CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
        {
            return null;
        }

        // §13.8 remediation: checked for the same blank-line exposure Parse had. This loop never
        // fails on a line it doesn't recognise — it only ever looks for the closing fence or an
        // "id: " line — so a blank line here just falls through both checks and is silently
        // skipped by cursor++ below, exactly like every other non-"id:" frontmatter line. No fix
        // needed; recorded so a reader checking the sibling method doesn't have to re-derive it.
        string? recoveredId = null;
        var cursor = 1;
        while (cursor < lines.Length && !string.Equals(lines[cursor], CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
        {
            var line = lines[cursor];
            var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex >= 0 && string.Equals(line[..separatorIndex], "id", StringComparison.Ordinal))
            {
                recoveredId = CardFileFormat.UnescapeFrontmatterValue(line[(separatorIndex + 2)..]);
            }

            cursor++;
        }

        // cursor reached lines.Length without meeting the closing fence: the fence itself is
        // broken, so whatever "id:"-shaped line was seen along the way is not trustworthy either.
        return cursor < lines.Length ? recoveredId : null;
    }

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

            // §13.8 remediation, round two: skip a blank line here too, same reasoning and same
            // remedy as the appended-region loop below — an editor that leaves one behind (a
            // blank line between frontmatter fields) means nothing, so it's dropped rather than
            // failed. The writer is unchanged; this is read-side only.
            if (line.Length == 0)
            {
                cursor++;
                continue;
            }

            if (string.Equals(line, CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
            {
                cursor++;
                break;
            }

            string key;
            string value;
            var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                key = line[..separatorIndex];
                value = line[(separatorIndex + 2)..];
            }
            else if (line[^1] == ':')
            {
                // §13.8 remediation, round two: CardFileWriter always emits "key: value" — for an
                // empty-valued field (e.g. `section` on a repository-scoped card) that's "key: "
                // with a trailing space. An editor that strips trailing whitespace on save turns
                // that into "key:" with nothing after the colon, and a save that changes nothing
                // else would otherwise corrupt the card. Tolerated as that key with an empty
                // value — the same value "key: " already parses to. A line with no colon at all
                // still fails below, and so does "key:something" with no space and a non-empty
                // tail: only a colon as the line's very last character is accepted this way.
                key = line[..^1];
                value = string.Empty;
            }
            else
            {
                return Failure($"malformed frontmatter line: '{line}'");
            }

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

        // §9 block D: known only when the card's own kind is question, same two-pass reasoning.
        var isQuestionCard = frontmatterResult.Frontmatter!.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => true,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        var isFindingCard = frontmatterResult.Frontmatter!.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => true,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        // §7 block A: the four register kinds share one frontmatter field set — a rule/hazard/
        // obligation/decision card is what CardStore.IsRegisterCard also decides over, same
        // exhaustive match shape.
        var isRegisterCard = frontmatterResult.Frontmatter!.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => true,
            onRule: static () => true,
            onHazard: static () => true,
            onDecision: static () => true,
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

            if (isRegisterCard && RegisterOnlyFrontmatterKeys.Contains(key))
            {
                continue;
            }

            if (isQuestionCard && QuestionOnlyFrontmatterKeys.Contains(key))
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

        var registerFields = RegisterCardFields.Empty;
        if (isRegisterCard)
        {
            var registerFieldsResult = BuildRegisterFields(fields);
            if (registerFieldsResult.Failure is { } registerFieldsFailure)
            {
                return Failure(registerFieldsFailure);
            }

            // registerFieldsResult.Failure is null here, so RegisterFields is guaranteed non-null
            // by BuildRegisterFields's own contract.
            registerFields = registerFieldsResult.RegisterFields!;
        }

        var questionFields = QuestionCardFields.Empty;
        if (isQuestionCard)
        {
            var questionFieldsResult = BuildQuestionFields(fields);
            if (questionFieldsResult.Failure is { } questionFieldsFailure)
            {
                return Failure(questionFieldsFailure);
            }

            // questionFieldsResult.Failure is null here, so QuestionFields is guaranteed non-null
            // by BuildQuestionFields's own contract.
            questionFields = questionFieldsResult.QuestionFields!;
        }

        var bodyLines = new List<string>();
        while (cursor < lines.Length && !CardFileFormat.IsCommentLine(lines[cursor]) && !CardFileFormat.IsHandoverLine(lines[cursor]) && !CardFileFormat.IsTransitionLine(lines[cursor]) && !CardFileFormat.IsVerdictLine(lines[cursor]) && !CardFileFormat.IsAuthorisationLine(lines[cursor]) && !CardFileFormat.IsClaimLine(lines[cursor]) && !CardFileFormat.IsLimitLine(lines[cursor]) && !CardFileFormat.IsRefusalLine(lines[cursor]))
        {
            // §14 remediation, extended by §14.4: a line that starts with one of the eight §14.1
            // block-open prefixes but does not exactly match one can never be legitimate body
            // content — the writer
            // always escapes exactly such a line (CardFileFormat.LooksLikeDelimiterOrEscapedDelimiter)
            // before emitting it — so an unescaped match here is a hand-authored or pre-§14.1
            // legacy marker, refused rather than silently absorbed as prose (§13.6).
            if (CardFileFormat.MalformedBlockOpenLineFamily(lines[cursor]) is { } malformedFamily)
            {
                return Failure($"malformed {malformedFamily} block open line: '{lines[cursor]}' — the open line must be exactly its own line with nothing else on it, or escaped with a leading backslash if it is body text");
            }

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
        var authorisations = new List<SectionAuthorisationEntry>();
        var claims = new List<CardApprovalClaim>();
        var limits = new List<CardApprovalLimit>();
        var refusals = new List<CardRefusalEntry>();
        while (cursor < lines.Length)
        {
            var headerLine = lines[cursor];

            // §13.8 remediation: skip a blank line here — between two blocks, or trailing at
            // EOF. An editor that guarantees a final newline turns the file's own trailing '\n'
            // into an empty line once Parse's normalization above strips one of them, and a
            // blank line separating blocks by eye is indistinguishable from one inside a comment
            // body. Neither means anything, so it is dropped rather than failed. This is the
            // only place that happens: a blank line inside a comment body (the loop just below,
            // and CardFileFormat.UnescapeContentLine) is still content and is preserved
            // unchanged, and the pre-append body loop above is untouched. The next tool write
            // re-emits the file without the stray blanks (CardFileWriter is unchanged) — that
            // normalization is deliberate, not drift.
            if (headerLine.Length == 0)
            {
                cursor++;
                continue;
            }

            if (CardFileFormat.IsHandoverLine(headerLine))
            {
                cursor++; // consume the open line
                var handoverFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownHandoverKeys, "handover block");
                if (handoverFieldsResult.Failure is { } handoverFieldsFailure)
                {
                    return Failure(handoverFieldsFailure);
                }

                // handoverFieldsResult.Failure is null here, so Fields/UnknownFields are
                // guaranteed non-null by ParseBlockFieldLines's own contract.
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
                cursor++; // consume the open line
                var transitionFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownTransitionKeys, "transition block");
                if (transitionFieldsResult.Failure is { } transitionFieldsFailure)
                {
                    return Failure(transitionFieldsFailure);
                }

                // transitionFieldsResult.Failure is null here, so Fields/UnknownFields are
                // guaranteed non-null by ParseBlockFieldLines's own contract.
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
                cursor++; // consume the open line
                var verdictFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownVerdictKeys, "verdict block");
                if (verdictFieldsResult.Failure is { } verdictFieldsFailure)
                {
                    return Failure(verdictFieldsFailure);
                }

                // verdictFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
                // non-null by ParseBlockFieldLines's own contract.
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

            if (CardFileFormat.IsAuthorisationLine(headerLine))
            {
                cursor++; // consume the open line
                var authorisationFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownAuthorisationKeys, "authorisation block");
                if (authorisationFieldsResult.Failure is { } authorisationFieldsFailure)
                {
                    return Failure(authorisationFieldsFailure);
                }

                // authorisationFieldsResult.Failure is null here, so Fields/UnknownFields are
                // guaranteed non-null by ParseBlockFieldLines's own contract.
                var authorisationResult = BuildSectionAuthorisationEntry(authorisationFieldsResult.Fields!, authorisationFieldsResult.UnknownFields!);
                if (authorisationResult.Failure is { } authorisationFailure)
                {
                    return Failure(authorisationFailure);
                }

                // authorisationResult.Failure is null here, so Entry is guaranteed non-null by
                // BuildSectionAuthorisationEntry's own contract.
                authorisations.Add(authorisationResult.Entry!);
                continue;
            }

            if (CardFileFormat.IsClaimLine(headerLine))
            {
                cursor++; // consume the open line
                var claimFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownClaimKeys, "claim block");
                if (claimFieldsResult.Failure is { } claimFieldsFailure)
                {
                    return Failure(claimFieldsFailure);
                }

                // claimFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
                // non-null by ParseBlockFieldLines's own contract.
                var claimResult = BuildCardApprovalClaim(claimFieldsResult.Fields!, claimFieldsResult.UnknownFields!);
                if (claimResult.Failure is { } claimFailure)
                {
                    return Failure(claimFailure);
                }

                // claimResult.Failure is null here, so Claim is guaranteed non-null by
                // BuildCardApprovalClaim's own contract.
                claims.Add(claimResult.Claim!);
                continue;
            }

            if (CardFileFormat.IsLimitLine(headerLine))
            {
                cursor++; // consume the open line
                var limitFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownLimitKeys, "limit block");
                if (limitFieldsResult.Failure is { } limitFieldsFailure)
                {
                    return Failure(limitFieldsFailure);
                }

                // limitFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
                // non-null by ParseBlockFieldLines's own contract.
                var limitResult = BuildCardApprovalLimit(limitFieldsResult.Fields!, limitFieldsResult.UnknownFields!);
                if (limitResult.Failure is { } limitFailure)
                {
                    return Failure(limitFailure);
                }

                // limitResult.Failure is null here, so Limit is guaranteed non-null by
                // BuildCardApprovalLimit's own contract.
                limits.Add(limitResult.Limit!);
                continue;
            }

            if (CardFileFormat.IsRefusalLine(headerLine))
            {
                cursor++; // consume the open line
                var refusalFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownRefusalKeys, "refusal block");
                if (refusalFieldsResult.Failure is { } refusalFieldsFailure)
                {
                    return Failure(refusalFieldsFailure);
                }

                // refusalFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
                // non-null by ParseBlockFieldLines's own contract.
                var refusalResult = BuildCardRefusalEntry(refusalFieldsResult.Fields!, refusalFieldsResult.UnknownFields!);
                if (refusalResult.Failure is { } refusalFailure)
                {
                    return Failure(refusalFailure);
                }

                // refusalResult.Failure is null here, so Entry is guaranteed non-null by
                // BuildCardRefusalEntry's own contract.
                refusals.Add(refusalResult.Entry!);
                continue;
            }

            if (!CardFileFormat.IsCommentLine(headerLine))
            {
                return Failure($"expected a comment line, a handover line, a transition line, a verdict line, an authorisation line, a claim line, a limit line, a refusal line, or end of file, found: '{headerLine}'");
            }

            // §14.4: the comment header is now a §14.1 delimited block like its seven siblings — the
            // same ParseBlockFieldLines reader, so an unterminated header (no closing '-->') fails
            // loudly the same way an unterminated handover/transition/etc. block already does,
            // rather than the header line itself needing its own suffix check.
            cursor++; // consume the open line
            var headerFieldsResult = ParseBlockFieldLines(lines, ref cursor, KnownCommentHeaderKeys, "comment header");
            if (headerFieldsResult.Failure is { } headerFailure)
            {
                return Failure(headerFailure);
            }

            // §14.4: the header's own close line is the shared BlockCloseLine ("-->") — the same
            // line the other seven families terminate on. ParseBlockFieldLines already consumed it
            // above, so the comment body below begins on the line right after; a body whose first
            // line happens to be exactly "-->" is ordinary content here, not a second terminator —
            // this loop only ever watches for CardFileFormat.CommentFooter, never BlockCloseLine (see
            // CardFileFormatBlockValueEscapeTests/CardFileRoundTripTests for the pinned case).
            var commentBodyLines = new List<string>();
            while (cursor < lines.Length && !CardFileFormat.IsCommentFooter(lines[cursor]))
            {
                if (CardFileFormat.IsCommentLine(lines[cursor]) || CardFileFormat.IsHandoverLine(lines[cursor]) || CardFileFormat.IsTransitionLine(lines[cursor]) || CardFileFormat.IsVerdictLine(lines[cursor]) || CardFileFormat.IsAuthorisationLine(lines[cursor]) || CardFileFormat.IsClaimLine(lines[cursor]) || CardFileFormat.IsLimitLine(lines[cursor]) || CardFileFormat.IsRefusalLine(lines[cursor]))
                {
                    return Failure($"missing comment footer before next block: '{lines[cursor]}'");
                }

                // §14 remediation: the same malformed-open-line refusal the body loop above gives,
                // applied inside a comment body — see that loop's comment for the full reasoning.
                if (CardFileFormat.MalformedBlockOpenLineFamily(lines[cursor]) is { } malformedFamily)
                {
                    return Failure($"malformed {malformedFamily} block open line inside a comment body: '{lines[cursor]}' — the open line must be exactly its own line with nothing else on it, or escaped with a leading backslash if it is comment text");
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
            // non-null by ParseBlockFieldLines's own contract.
            var commentResult = BuildComment(
                headerFieldsResult.Fields!, headerFieldsResult.UnknownFields!, string.Join('\n', commentBodyLines));
            if (commentResult.Failure is { } commentFailure)
            {
                return Failure(commentFailure);
            }

            // commentResult.Failure is null here, so Comment is guaranteed non-null by BuildComment's own contract.
            comments.Add(commentResult.Comment!);
        }

        // BuildSectionFields only ever populates the three scalar fields — Verdicts and
        // Authorisations are always the append-only sequences just parsed above, folded in here
        // once all are known.
        var sectionFieldsWithVerdicts = sectionFields with { Verdicts = [.. verdicts], Authorisations = [.. authorisations] };

        // frontmatterResult.Failure is null here, so Frontmatter is guaranteed non-null by BuildFrontmatter's own contract.
        return new CardFileParseResult.Success(
            new CardFile(frontmatterResult.Frontmatter!, body, comments, unknownFrontmatterFields, handovers, blockFields, transitions, sectionFieldsWithVerdicts, findingFields, registerFields, claims, limits, refusals, questionFields));
    }

    /// <summary>Register's own recognised wire values for a <c>finding</c> card's <c>status</c> —
    /// <see cref="CardStore.RaiseFinding"/> and <see cref="CardStore.ChangeArchive"/> are the only
    /// writers, and both always write the literal <c>open</c> (findings: a finding is never closed,
    /// see <see cref="CardLifecycle"/>'s doc comment). No closed union backs this — the brief for
    /// this validation (§12 block A) is explicit that a kind carrying a single fixed literal does
    /// not earn one.</summary>
    private const string FindingRecognisedStatuses = "open";

    /// <summary>
    /// Validates a card's own <c>status</c> against its own <paramref name="kind"/>'s wire
    /// vocabulary before the card is ever constructed (§12 block A ruling: "register liveness
    /// closes at the parse door"). The kind/format map mirrors <see
    /// cref="CardLifecycle.IsClosed"/>'s own mapping rather than duplicating its shape by hand: block
    /// and section read their own flow-state union, question its own three-state union, the four
    /// register kinds the shared open/discharged union, and finding the one literal above. A card
    /// whose status does not parse against its own kind's vocabulary is never handed back as a
    /// <see cref="CardFileParseResult.Success"/> — every downstream reader that used to have to
    /// choose a direction to fail in when a status did not parse no longer needs to, because that
    /// card no longer exists as far as any of them are concerned.
    /// </summary>
    private static string? ValidateStatus(CardKind kind, string status)
    {
        var kindName = kind.ToWireString();
        return kind.Match(
            onBlock: () => BlockFlowStateWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.",
            onQuestion: () => QuestionStatusWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {QuestionStatusWireFormat.RecognisedValues}.",
            onFinding: () => string.Equals(status, FindingRecognisedStatuses, StringComparison.Ordinal)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {FindingRecognisedStatuses}.",
            onObligation: () => RegisterLifecycleStateWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {RegisterLifecycleStateWireFormat.RecognisedValues}.",
            onRule: () => RegisterLifecycleStateWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {RegisterLifecycleStateWireFormat.RecognisedValues}.",
            onHazard: () => RegisterLifecycleStateWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {RegisterLifecycleStateWireFormat.RecognisedValues}.",
            onDecision: () => RegisterLifecycleStateWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {RegisterLifecycleStateWireFormat.RecognisedValues}.",
            onSection: () => SectionFlowStateWireFormat.TryParse(status, out _)
                ? null
                : $"unrecognised status: '{status}' for kind '{kindName}'. Recognised statuses: {SectionFlowStateWireFormat.RecognisedValues}.");
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

        if (ValidateStatus(kind, status) is { } statusFailure)
        {
            return (null, statusFailure);
        }

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

        var findingKey = ParseOptionalFrontmatterValue(fields, "finding_key");

        return (new BlockCardFields(baseCommit, reviewedState, tasks, round, blockedBy, gateResults!, findingKey), null);
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
    /// already gives every other optional field. <see cref="SectionCardFields.Verdicts"/> and
    /// <see cref="SectionCardFields.Authorisations"/> are not built here — they come from their own
    /// append-only line sequences <see cref="Parse"/> parses separately, the same reason
    /// <see cref="BuildBlockFields"/> does not build <see cref="CardFile.Transitions"/>.
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

        return (new SectionCardFields(baseCommit, closedBy, closedAt, [], []), null);
    }

    /// <summary>
    /// Extracts §9 block D's seven known-on-a-question-card fields from a frontmatter
    /// <paramref name="fields"/> dictionary already confirmed to belong to a <c>question</c> card —
    /// see the <c>isQuestionCard</c> gate in <see cref="Parse"/>, the only caller. Same "absent or
    /// empty parses to null" convention as <see cref="BuildSectionFields"/>'s own
    /// <c>closed_by</c>/<c>closed_at</c>: <c>answered_by</c>/<c>answered_at</c> are recorded
    /// together (<see cref="CardStore.AnswerQuestionUnderExistingLock"/> is the only writer of
    /// either) and <c>deferred_by</c>/<c>deferred_at</c> likewise (<see cref="CardStore.
    /// DeferQuestionUnderExistingLock"/>), but this parser accepts any of the four alone rather than
    /// refusing a hand-edited file — degraded-mode legibility (ADR-0003) over strict round-trip
    /// enforcement, the same latitude every other kind-specific field builder in this type gives.
    /// </summary>
    private static (QuestionCardFields? QuestionFields, string? Failure) BuildQuestionFields(
        IReadOnlyDictionary<string, string> fields)
    {
        CardOwner? answeredBy = null;
        if (fields.TryGetValue("answered_by", out var answeredByText) && answeredByText.Length > 0)
        {
            if (!CardOwnerWireFormat.TryParse(answeredByText, out var parsedAnsweredBy))
            {
                return (null, $"question card has unrecognised 'answered_by': '{answeredByText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
            }

            answeredBy = parsedAnsweredBy;
        }

        DateTimeOffset? answeredAt = null;
        if (fields.TryGetValue("answered_at", out var answeredAtText) && answeredAtText.Length > 0)
        {
            if (!TryParseTimestamp(answeredAtText, out var parsedAnsweredAt))
            {
                return (null, $"question card has invalid 'answered_at': '{answeredAtText}'");
            }

            answeredAt = parsedAnsweredAt;
        }

        var answerDecisionId = ParseOptionalFrontmatterValue(fields, "answer_decision");
        var answerInline = ParseOptionalFrontmatterValue(fields, "answer_inline");

        CardOwner? deferredBy = null;
        if (fields.TryGetValue("deferred_by", out var deferredByText) && deferredByText.Length > 0)
        {
            if (!CardOwnerWireFormat.TryParse(deferredByText, out var parsedDeferredBy))
            {
                return (null, $"question card has unrecognised 'deferred_by': '{deferredByText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
            }

            deferredBy = parsedDeferredBy;
        }

        DateTimeOffset? deferredAt = null;
        if (fields.TryGetValue("deferred_at", out var deferredAtText) && deferredAtText.Length > 0)
        {
            if (!TryParseTimestamp(deferredAtText, out var parsedDeferredAt))
            {
                return (null, $"question card has invalid 'deferred_at': '{deferredAtText}'");
            }

            deferredAt = parsedDeferredAt;
        }

        var deferredTarget = ParseOptionalFrontmatterValue(fields, "deferred_target");

        return (new QuestionCardFields
        {
            AnsweredBy = answeredBy,
            AnsweredAt = answeredAt,
            AnswerDecisionId = answerDecisionId,
            AnswerInline = answerInline,
            DeferredBy = deferredBy,
            DeferredAt = deferredAt,
            DeferredTarget = deferredTarget,
        }, null);
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

        var (extentFingerprint, extentFingerprintFailure) = ParseExtentFingerprint(fields);
        if (extentFingerprintFailure is not null)
        {
            return (null, extentFingerprintFailure);
        }

        var (disposition, dispositionFailure) = ParseDisposition(fields);
        if (dispositionFailure is not null)
        {
            return (null, dispositionFailure);
        }

        return (new FindingCardFields(instrument, extent!, verifiedAt, blindSpot!, extentFingerprint, disposition!), null);
    }

    /// <summary>
    /// Extracts every known-on-a-register-card field from a frontmatter <paramref name="fields"/>
    /// dictionary already confirmed to belong to a <c>rule</c>/<c>hazard</c>/<c>obligation</c>/
    /// <c>decision</c> card — see the <c>isRegisterCard</c> gate in <see cref="Parse"/>, the only
    /// caller. <c>condition</c>/<c>cadence</c> (§7 block A) and <c>owed_by</c>/<c>supersedes</c>/
    /// <c>superseded_by</c> (§7 block C) all follow the same "absent or empty parses to null"
    /// convention <see cref="BuildSectionFields"/> already uses for <c>closed_by</c>/
    /// <c>closed_at</c> — <c>discharged_by</c>/<c>discharged_at</c> are recorded together
    /// (<see cref="CardStore.DischargeRegisterCardUnderExistingLock"/> is the only writer of
    /// either), but this parser accepts either alone rather than refusing a hand-edited file,
    /// degraded-mode legibility (ADR-0003) over strict round-trip enforcement, the same latitude
    /// <see cref="BuildSectionFields"/> already gives. <c>earned_from</c> (§7 block E) and
    /// <c>absorbs</c> (§7 block F) are list-valued and follow <c>tasks</c>/<c>blocked_by</c>'s own
    /// convention instead — split via <see cref="CardFileFormat.SplitFrontmatterList"/>,
    /// absent-or-empty reads back as an empty list, and every item is checked by the same
    /// <see cref="RequireNoEmptyListItem"/> guard those two fields already use.
    /// </summary>
    private static (RegisterCardFields? RegisterFields, string? Failure) BuildRegisterFields(
        IReadOnlyDictionary<string, string> fields)
    {
        var condition = ParseOptionalFrontmatterValue(fields, RegisterCardFieldKeys.Condition);
        var cadence = ParseOptionalFrontmatterValue(fields, RegisterCardFieldKeys.Cadence);
        var owedBy = ParseOptionalFrontmatterValue(fields, RegisterCardFieldKeys.OwedBy);
        var supersedes = ParseOptionalFrontmatterValue(fields, RegisterCardFieldKeys.Supersedes);
        var supersededBy = ParseOptionalFrontmatterValue(fields, RegisterCardFieldKeys.SupersededBy);
        var declinedReason = ParseOptionalFrontmatterValue(fields, RegisterCardFieldKeys.DeclinedReason);

        var earnedFrom = fields.TryGetValue(RegisterCardFieldKeys.EarnedFrom, out var earnedFromText)
            ? CardFileFormat.SplitFrontmatterList(earnedFromText)
            : (IReadOnlyList<string>)[];
        if (RequireNoEmptyListItem(earnedFrom, RegisterCardFieldKeys.EarnedFrom) is { } earnedFromFailure)
        {
            return (null, earnedFromFailure);
        }

        var absorbs = fields.TryGetValue(RegisterCardFieldKeys.Absorbs, out var absorbsText)
            ? CardFileFormat.SplitFrontmatterList(absorbsText)
            : (IReadOnlyList<string>)[];
        if (RequireNoEmptyListItem(absorbs, RegisterCardFieldKeys.Absorbs) is { } absorbsFailure)
        {
            return (null, absorbsFailure);
        }

        CardOwner? dischargedBy = null;
        if (fields.TryGetValue(RegisterCardFieldKeys.DischargedBy, out var dischargedByText) && dischargedByText.Length > 0)
        {
            if (!CardOwnerWireFormat.TryParse(dischargedByText, out var parsedDischargedBy))
            {
                return (null, $"register card has unrecognised 'discharged_by': '{dischargedByText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
            }

            dischargedBy = parsedDischargedBy;
        }

        DateTimeOffset? dischargedAt = null;
        if (fields.TryGetValue(RegisterCardFieldKeys.DischargedAt, out var dischargedAtText) && dischargedAtText.Length > 0)
        {
            if (!TryParseTimestamp(dischargedAtText, out var parsedDischargedAt))
            {
                return (null, $"register card has invalid 'discharged_at': '{dischargedAtText}'");
            }

            dischargedAt = parsedDischargedAt;
        }

        return (new RegisterCardFields(condition, cadence, dischargedBy, dischargedAt, owedBy, supersedes, supersededBy, earnedFrom, absorbs, declinedReason), null);
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
    /// <c>extent_fingerprint</c> (§6 block C): a comma-joined list of <c>path=hash</c> items, the
    /// same <see cref="CardFileFormat.SplitFrontmatterList"/> shape as <c>tasks</c>/<c>blocked_by</c>
    /// and the same "split each item on its first <c>=</c>" convention <see cref="ParseGateResults"/>
    /// already established — a path is never expected to itself contain <c>=</c>, and a SHA-256 hex
    /// hash or the literal <c>absent</c> sentinel never does either. Absent-key parses to
    /// <see langword="null"/> — see <see cref="FindingCardFields.ExtentFingerprint"/>'s own doc
    /// comment for why <see langword="null"/> is itself a meaningful state (no fingerprint recorded,
    /// distinct from "fingerprint of zero files"), not merely "not yet parsed".
    /// </summary>
    private static (FindingExtentFingerprint? ExtentFingerprint, string? Failure) ParseExtentFingerprint(
        IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("extent_fingerprint", out var raw))
        {
            return (null, null);
        }

        var items = CardFileFormat.SplitFrontmatterList(raw);
        var files = new List<FindingExtentFileFingerprint>(items.Count);
        foreach (var item in items)
        {
            var separatorIndex = item.IndexOf('=');
            if (separatorIndex < 0)
            {
                return (null, $"finding card has a malformed extent_fingerprint item (expected 'path=hash' or 'path=absent'): '{item}'");
            }

            var path = item[..separatorIndex];
            var hashText = item[(separatorIndex + 1)..];
            if (path.Length == 0)
            {
                return (null, $"finding card has an extent_fingerprint item with an empty path: '{item}'");
            }

            files.Add(new FindingExtentFileFingerprint(path, hashText == "absent" ? null : hashText));
        }

        return (new FindingExtentFingerprint(files), null);
    }

    /// <summary>
    /// <c>disposition</c> (§6 block C): absent-or-<c>"measured"</c> → <see cref="FindingDisposition.
    /// Measured"/> (the same "undeclared and default are the same wire state" convention <see cref="
    /// ParseExtent"/> already applies for <c>block-scope</c>), <c>"argued-clean"</c> →
    /// <see cref="FindingDisposition.ArguedClean"/>, or a parse failure for anything else.
    /// </summary>
    private static (FindingDisposition? Disposition, string? Failure) ParseDisposition(
        IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("disposition", out var rawForm) || rawForm.Length == 0)
        {
            return (FindingDisposition.Measured, null);
        }

        var form = CardFileFormat.UnescapeFrontmatterValue(rawForm);
        return form switch
        {
            "measured" => (FindingDisposition.Measured, null),
            "argued-clean" => (FindingDisposition.ArguedClean, null),
            _ => (null, $"finding card has unrecognised 'disposition': '{form}'. Recognised dispositions: measured, argued-clean."),
        };
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

    /// <summary>
    /// §14.1: reads one §14.1 delimited block's field lines — starting just past its already-
    /// consumed open line — until a line exactly equal to <see cref="CardFileFormat.BlockCloseLine"/>
    /// (consumed on the way out) or end of file, whichever comes first. Reaching end of file first
    /// is the unterminated-block case 14.1 requires to fail loudly, rather than silently treating
    /// whatever was read as the whole block. Each field line is <c>key: value</c>, the same shape
    /// and the same trailing-colon-only tolerance (an empty value whose trailing space an editor
    /// stripped on save) <see cref="Parse"/>'s own frontmatter loop already gives — reusing the
    /// frontmatter line shape per §14.2, not a second implementation of it. A blank line is skipped,
    /// the same §13.8 tolerance the appended-region loop already gives between blocks.
    /// </summary>
    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseBlockFieldLines(string[] lines, ref int cursor, IReadOnlySet<string> knownKeys, string blockLabel)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownFields = new List<(string Key, string RawValue)>();

        while (true)
        {
            if (cursor >= lines.Length)
            {
                return (null, null, $"unterminated {blockLabel}: missing closing '{CardFileFormat.BlockCloseLine}'");
            }

            var line = lines[cursor];

            if (line.Length == 0)
            {
                cursor++;
                continue;
            }

            if (CardFileFormat.IsBlockCloseLine(line))
            {
                cursor++;
                break;
            }

            string key;
            string rawValue;
            var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                key = line[..separatorIndex];
                rawValue = line[(separatorIndex + 2)..];
            }
            else if (line[^1] == ':')
            {
                key = line[..^1];
                rawValue = string.Empty;
            }
            else
            {
                return (null, null, $"malformed {blockLabel} field: '{line}'");
            }

            fields[key] = rawValue;
            if (!knownKeys.Contains(key))
            {
                unknownFields.Add((key, rawValue));
            }

            cursor++;
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

        var id = CardFileFormat.UnescapeCardBlockValue(rawId);

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
            ? CardFileFormat.UnescapeCardBlockValue(replyToText)
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
            ? CardFileFormat.UnescapeCardBlockValue(resolvesText)
            : null;

        var isNit = fields.TryGetValue(CardCommentNitFieldKeys.IsNit, out var isNitText) && string.Equals(isNitText, "true", StringComparison.Ordinal);
        var required = fields.TryGetValue(CardCommentNitFieldKeys.Required, out var requiredText) && string.Equals(requiredText, "true", StringComparison.Ordinal);
        var sites = fields.TryGetValue(CardCommentNitFieldKeys.Sites, out var sitesText)
            ? CardFileFormat.SplitSiteList(sitesText)
            : (IReadOnlyList<string>?)null;

        NitDisposition? disposition = null;
        if (fields.TryGetValue(CardCommentNitFieldKeys.Disposition, out var dispositionText))
        {
            if (!NitDispositionWireFormat.TryParse(dispositionText, out var parsedDisposition))
            {
                return (null, $"comment '{id}' has unrecognised disposition: '{dispositionText}'. Recognised dispositions: {NitDispositionWireFormat.RecognisedValues}.");
            }

            disposition = parsedDisposition;
        }

        return (new CardComment(id, author, timestamp, body, replyTo, to, resolves, unknownFields, isNit, required, sites, disposition), null);
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

        var rangeFrom = CardFileFormat.UnescapeCardBlockValue(rangeFromRaw);
        if (!SectionVerdictEntry.IsValidRangeValue(rangeFrom))
        {
            return (null, "verdict has an empty or whitespace-only 'range-from'");
        }

        if (!fields.TryGetValue("range-to", out var rangeToRaw))
        {
            return (null, "verdict missing required field: range-to");
        }

        var rangeTo = CardFileFormat.UnescapeCardBlockValue(rangeToRaw);
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

    private static (SectionAuthorisationEntry? Entry, string? Failure) BuildSectionAuthorisationEntry(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue("by", out var byText))
        {
            return (null, "authorisation missing required field: by");
        }

        if (!CardOwnerWireFormat.TryParse(byText, out var by))
        {
            return (null, $"authorisation has unrecognised 'by': '{byText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("reason", out var reasonRaw))
        {
            return (null, "authorisation missing required field: reason");
        }

        var reason = CardFileFormat.UnescapeCardBlockValue(reasonRaw);
        if (!SectionAuthorisationEntry.IsValidReasonValue(reason))
        {
            return (null, "authorisation has an empty or whitespace-only 'reason'");
        }

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, "authorisation missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"authorisation has invalid timestamp: '{timestampText}'");
        }

        return (new SectionAuthorisationEntry(by, reason, timestamp, unknownFields), null);
    }

    private static (CardRefusalEntry? Entry, string? Failure) BuildCardRefusalEntry(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue("by", out var byText))
        {
            return (null, "refusal missing required field: by");
        }

        if (!CardOwnerWireFormat.TryParse(byText, out var by))
        {
            return (null, $"refusal has unrecognised 'by': '{byText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("rule", out var ruleRaw))
        {
            return (null, "refusal missing required field: rule");
        }

        var rule = CardFileFormat.UnescapeCardBlockValue(ruleRaw);

        if (!fields.TryGetValue("remedy", out var remedyRaw))
        {
            return (null, "refusal missing required field: remedy");
        }

        var remedy = CardFileFormat.UnescapeCardBlockValue(remedyRaw);

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, "refusal missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"refusal has invalid timestamp: '{timestampText}'");
        }

        return (new CardRefusalEntry(by, rule, remedy, timestamp, unknownFields), null);
    }

    private static (CardApprovalClaim? Claim, string? Failure) BuildCardApprovalClaim(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue(CardApprovalFieldKeys.Id, out var rawId) || rawId.Length == 0)
        {
            return (null, "claim missing required field: id");
        }

        var id = CardFileFormat.UnescapeCardBlockValue(rawId);

        if (!fields.TryGetValue(CardApprovalFieldKeys.Round, out var roundText) || !int.TryParse(roundText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var round))
        {
            return (null, $"claim '{id}' has invalid or missing 'round': '{roundText}'");
        }

        if (!fields.TryGetValue(CardApprovalFieldKeys.Text, out var rawText))
        {
            return (null, $"claim '{id}' missing required field: text");
        }

        var text = CardFileFormat.UnescapeCardBlockValue(rawText);
        if (!CardApprovalClaim.IsValidText(text))
        {
            return (null, $"claim '{id}' has an empty or whitespace-only 'text'");
        }

        return (new CardApprovalClaim(id, round, text, unknownFields), null);
    }

    private static (CardApprovalLimit? Limit, string? Failure) BuildCardApprovalLimit(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields)
    {
        if (!fields.TryGetValue(CardApprovalFieldKeys.Round, out var roundText) || !int.TryParse(roundText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var round))
        {
            return (null, $"limit has invalid or missing 'round': '{roundText}'");
        }

        if (!fields.TryGetValue(CardApprovalFieldKeys.Text, out var rawText))
        {
            return (null, "limit missing required field: text");
        }

        var text = CardFileFormat.UnescapeCardBlockValue(rawText);
        if (!CardApprovalLimit.IsValidText(text))
        {
            return (null, "limit has an empty or whitespace-only 'text'");
        }

        return (new CardApprovalLimit(round, text, unknownFields), null);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static CardFileParseResult.Failure Failure(string reason) => new(reason);
}
