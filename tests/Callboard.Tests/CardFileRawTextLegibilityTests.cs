using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 13.9 — "A reader with no access to the tool SHALL be able to determine a card's status, owner
/// and history from the record alone" (<c>record-retrieval</c>, "The record is legible without the
/// tool"; ADR-0003: "opens one file and sees status, owner, scope, body and full thread"). A
/// round-trip test (<see cref="CardFileRoundTripTests"/>) proves the tool can read what the tool
/// wrote — a different proposition from this one. Every assertion here reads the raw string
/// <see cref="CardFileWriter.Serialize"/> produces with a plain substring/line check that shares
/// none of <see cref="CardFileFormat"/>'s escape tables and never calls
/// <see cref="CardFileParser"/> — the same reading a human opening the file in an editor with no
/// tool installed would do.
/// </summary>
public sealed class CardFileRawTextLegibilityTests
{
    private static readonly DateTimeOffset T1 = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 8, 19, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T4 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T5 = new(2026, 8, 19, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T6 = new(2026, 8, 19, 14, 0, 0, TimeSpan.Zero);

    // ---- Re-derivation record (§14.1-14.3) ------------------------------------------------
    //
    // CardFileFormat declares seven §14.1 block-open-line constants (grep for "OpenLine" in that
    // file): HandoverOpenLine, TransitionOpenLine, VerdictOpenLine, AuthorisationOpenLine,
    // ClaimOpenLine, LimitOpenLine, RefusalOpenLine. CardFileParser's own continuation/lookahead
    // conditions (both the "what ends a header run" scan and the "what interrupts a comment body"
    // scan) test exactly these seven predicates and no others. That is seven. Comments are the
    // eighth append-only sequence and keep their own CommentHeaderPrefix/Suffix shape rather than
    // one of the seven block-open shapes — §14.4 brings that one onto this syntax next, not this
    // block; BuildComment (CardFileParser.cs) recognises id (required), author (required),
    // timestamp (required), reply-to, to, resolves, is-nit, required, sites, disposition — ten
    // header fields, four required, six optional.
    //
    // §14.2/14.3's fix: every one of the seven families now carries its fields as one-per-line
    // `key: value`, reusing the frontmatter line shape and its escaper (CardFileFormat.
    // EscapeCardBlockValue, built from EscapeFrontmatterValue plus one more composed step escaping
    // a literal `-->` to `\->` so a rule/remedy/reason/text containing that run can never end the
    // enclosing HTML comment early in a rendered view). Interior spaces are never escaped — the
    // fix this section exists for. See the assertions below for what that looks like on disk now,
    // in contrast to the CardFileFormat.EscapeCommentHeaderValue-based `\s`-per-space rendering
    // 13.9 found and the Product Owner called horrible.

    [Fact]
    public void EveryMarkerLineFamily_IsAttributableByPlainSubstringSearch()
    {
        var frontmatter = new CardFrontmatter(
            "B-0099",
            CardKind.Block,
            "Wire a retry budget into the export path",
            "building",
            CardOwner.Worker,
            CardScope.Change,
            "13",
            T1,
            T2);

        var handover = new CardHandover(CardOwner.Architect, CardOwner.Worker, T1, []);
        var transition = new CardBlockTransitionEntry(
            CardOwner.Worker, "briefed-to-building", BlockFlowState.Briefed, BlockFlowState.Building, T2, []);
        var claim = new CardApprovalClaim("claim-0001", 1, "the retry budget is honoured on every export path", []);
        var limit = new CardApprovalLimit(1, "does not cover the CLI's own retry of a failed write", []);
        var refusal = new CardRefusalEntry(
            CardOwner.Reviewer, "base-immutable", "run 'callboard block transition --base' instead", T3, []);

        var blockFields = new BlockCardFields(
            Base: "a1b2c3d",
            ReviewedState: "a1b2c3d",
            Tasks: ["13.9"],
            Round: 1,
            BlockedBy: ["B-0050"],
            GateResults: [new GateResult("build", 0, 1)],
            FindingKey: "F-0012");

        var card = new CardFile(
            frontmatter,
            "Implements the retry budget.",
            Comments: [],
            UnknownFrontmatterFields: [],
            Handovers: [handover],
            BlockFields: blockFields,
            Transitions: [transition],
            Claims: [claim],
            Limits: [limit],
            Refusals: [refusal]);

        var raw = CardFileWriter.Serialize(card);

        // Frontmatter: status, owner, scope are plain words, not codes.
        Assert.Contains("status: building\n", raw, StringComparison.Ordinal);
        Assert.Contains("owner: worker\n", raw, StringComparison.Ordinal);
        Assert.Contains("scope: change\n", raw, StringComparison.Ordinal);

        // The seven block-only fields, all present and readable without a legend.
        Assert.Contains("base: a1b2c3d\n", raw, StringComparison.Ordinal);
        Assert.Contains("reviewed_state: a1b2c3d\n", raw, StringComparison.Ordinal);
        Assert.Contains("tasks: 13.9\n", raw, StringComparison.Ordinal);
        Assert.Contains("round: 1\n", raw, StringComparison.Ordinal);
        Assert.Contains("blocked_by: B-0050\n", raw, StringComparison.Ordinal);
        Assert.Contains("gate_results: build=0=1\n", raw, StringComparison.Ordinal);
        Assert.Contains("finding_key: F-0012\n", raw, StringComparison.Ordinal);

        // Handover: who handed off to whom, and when — one field per line, in plain words.
        Assert.Contains(
            "<!-- callboard:handover\n" +
            "by: architect\n" +
            "to: worker\n" +
            $"timestamp: {T1:O}\n" +
            "-->",
            raw, StringComparison.Ordinal);

        // Transition: acting role, transition name, both flow-state endpoints, timestamp.
        Assert.Contains(
            "<!-- callboard:transition\n" +
            "by: worker\n" +
            "name: briefed-to-building\n" +
            "from: briefed\n" +
            "to: building\n" +
            $"timestamp: {T2:O}\n" +
            "-->",
            raw, StringComparison.Ordinal);

        // Claim: which claim, which round — attributable. Its text now reads as an ordinary
        // sentence: §14.2 puts the field on its own line, so an interior space is never ambiguous
        // and is never escaped — the fix 13.9's finding demanded.
        Assert.Contains(
            "<!-- callboard:claim\n" +
            "id: claim-0001\n" +
            "round: 1\n" +
            "text: the retry budget is honoured on every export path\n" +
            "-->",
            raw, StringComparison.Ordinal);

        // Limit: what the certification does NOT establish — reads as prose the same way.
        Assert.Contains(
            "<!-- callboard:limit\n" +
            "round: 1\n" +
            "text: does not cover the CLI's own retry of a failed write\n" +
            "-->",
            raw, StringComparison.Ordinal);

        // Refusal: who was refused, the rule, and the remedy — the one field naming a runnable
        // command — all read as sentences now: "run 'callboard block transition --base' instead"
        // appears on the wire exactly as written, no backslash-s standing in for a space.
        Assert.Contains(
            "<!-- callboard:refusal\n" +
            "by: reviewer\n" +
            "rule: base-immutable\n" +
            "remedy: run 'callboard block transition --base' instead\n" +
            $"timestamp: {T3:O}\n" +
            "-->",
            raw, StringComparison.Ordinal);
    }

    [Fact]
    public void VerdictAndAuthorisation_OnASectionCard_AreAttributableByPlainSubstringSearch()
    {
        var frontmatter = new CardFrontmatter(
            "S-0013",
            CardKind.Section,
            "Section 13 — record legibility",
            "in-review",
            CardOwner.Supervisor,
            CardScope.Change,
            "13",
            T1,
            T4);

        var sectionFields = SectionCardFields.Empty with
        {
            Base = "f100b77",
            ClosedBy = CardOwner.Supervisor,
            ClosedAt = T4,
        };

        var verdict = new SectionVerdictEntry(
            CardOwner.Supervisor, SectionVerdict.RequestChanges, "f100b77", "9a4233b", T3, []);
        var authorisation = new SectionAuthorisationEntry(
            CardOwner.ProductOwner, "a third remediation round is warranted here", T4, []);

        var card = new CardFile(
            frontmatter,
            "Section body.",
            Comments: [],
            UnknownFrontmatterFields: [],
            SectionFields: sectionFields with { Verdicts = [verdict], Authorisations = [authorisation] });

        var raw = CardFileWriter.Serialize(card);

        Assert.Contains("base: f100b77\n", raw, StringComparison.Ordinal);
        Assert.Contains("closed_by: supervisor\n", raw, StringComparison.Ordinal);
        Assert.Contains($"closed_at: {T4:O}\n", raw, StringComparison.Ordinal);

        Assert.Contains(
            "<!-- callboard:verdict\n" +
            "by: supervisor\n" +
            "verdict: request-changes\n" +
            "range-from: f100b77\n" +
            "range-to: 9a4233b\n" +
            $"timestamp: {T3:O}\n" +
            "-->",
            raw, StringComparison.Ordinal);
        // Authorisation reason reads as an ordinary sentence — the same fix as claim/limit/refusal
        // text: "a third remediation round is warranted here" appears verbatim, no backslash-s.
        Assert.Contains(
            "<!-- callboard:authorisation\n" +
            "by: product-owner\n" +
            "reason: a third remediation round is warranted here\n" +
            $"timestamp: {T4:O}\n" +
            "-->",
            raw, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentThread_IncludingReplyToAddresseeResolvesAndNitFields_IsAttributableByPlainSubstringSearch()
    {
        var frontmatter = new CardFrontmatter(
            "F-0012",
            CardKind.Finding,
            "SerializeResult fails open on an unreadable card",
            "open",
            CardOwner.Reviewer,
            CardScope.Section,
            "13",
            T1,
            T5);

        var opening = new CardComment(
            "C-0001",
            CardOwner.Reviewer,
            T1,
            "Four readers still discard the parse failure with onFailure: static _ => null.",
            ReplyTo: null,
            To: CardOwner.Architect,
            Resolves: null,
            UnknownHeaderFields: []);

        var nit = new CardComment(
            "C-0002",
            CardOwner.Reviewer,
            T5,
            "The fingerprint's RelativePath is inherited from extent_value verbatim.",
            ReplyTo: "C-0001",
            To: CardOwner.Worker,
            Resolves: "C-0001",
            UnknownHeaderFields: [],
            IsNit: true,
            Required: true,
            Sites: ["src/Callboard/Cards/FindingExtentFingerprint.cs:42"],
            Disposition: NitDisposition.FixBeforeLand);

        var card = new CardFile(
            frontmatter,
            "Body.",
            Comments: [opening, nit],
            UnknownFrontmatterFields: []);

        var raw = CardFileWriter.Serialize(card);

        Assert.Contains(
            $"<!-- callboard:comment id=C-0001 author=reviewer to=architect timestamp={T1:O} -->",
            raw, StringComparison.Ordinal);
        Assert.Contains(
            "Four readers still discard the parse failure with onFailure: static _ => null.",
            raw, StringComparison.Ordinal);

        Assert.Contains(
            $"<!-- callboard:comment id=C-0002 author=reviewer reply-to=C-0001 to=worker resolves=C-0001 " +
            $"timestamp={T5:O} is-nit=true required=true " +
            "sites=src/Callboard/Cards/FindingExtentFingerprint.cs:42 disposition=fix-before-land -->",
            raw, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingQuestionAndRegisterFields_AreAttributableByPlainSubstringSearch()
    {
        var findingFrontmatter = new CardFrontmatter(
            "F-0013", CardKind.Finding, "Escape table gap", "open",
            CardOwner.Reviewer, CardScope.Change, "13", T1, T1);
        var findingCard = new CardFile(
            findingFrontmatter, "Body.", [], [],
            FindingFields: new FindingCardFields(
                Instrument: "dotnet test",
                Extent: FindingExtent.Explicit(["src/Callboard/Cards/CardFileFormat.cs"]),
                VerifiedAt: "9a4233b",
                BlindSpot: FindingBlindSpotDeclaration.RaisedAs("F-0014"),
                ExtentFingerprint: null,
                Disposition: FindingDisposition.ArguedClean));
        var findingRaw = CardFileWriter.Serialize(findingCard);

        Assert.Contains("instrument: dotnet test\n", findingRaw, StringComparison.Ordinal);
        Assert.Contains("extent: explicit\n", findingRaw, StringComparison.Ordinal);
        Assert.Contains("extent_value: src/Callboard/Cards/CardFileFormat.cs\n", findingRaw, StringComparison.Ordinal);
        Assert.Contains("verified_at: 9a4233b\n", findingRaw, StringComparison.Ordinal);
        Assert.Contains("blind_spot: raised-as\n", findingRaw, StringComparison.Ordinal);
        Assert.Contains("blind_spot_card: F-0014\n", findingRaw, StringComparison.Ordinal);
        Assert.Contains("disposition: argued-clean\n", findingRaw, StringComparison.Ordinal);

        var questionFrontmatter = new CardFrontmatter(
            "Q-0009", CardKind.Question, "Which retry policy applies?", "answered",
            CardOwner.Architect, CardScope.Repository, string.Empty, T1, T2);
        var questionCard = new CardFile(
            questionFrontmatter, "Body.", [], [],
            QuestionFields: new QuestionCardFields
            {
                AnsweredBy = CardOwner.ProductOwner,
                AnsweredAt = T2,
                AnswerInline = "fixed backoff, three attempts",
            });
        var questionRaw = CardFileWriter.Serialize(questionCard);

        Assert.Contains("answered_by: product-owner\n", questionRaw, StringComparison.Ordinal);
        Assert.Contains($"answered_at: {T2:O}\n", questionRaw, StringComparison.Ordinal);
        Assert.Contains("answer_inline: fixed backoff, three attempts\n", questionRaw, StringComparison.Ordinal);

        var ruleFrontmatter = new CardFrontmatter(
            "R-0004", CardKind.Rule, "A refusal must name its remedy as a command that exists", "open",
            CardOwner.Architect, CardScope.Repository, string.Empty, T1, T1);
        var ruleCard = new CardFile(
            ruleFrontmatter, "Body.", [], [],
            RegisterFields: new RegisterCardFields(
                Condition: null,
                Cadence: null,
                DischargedBy: null,
                DischargedAt: null,
                EarnedFrom: ["F-0009"]));
        var ruleRaw = CardFileWriter.Serialize(ruleCard);

        Assert.Contains("earned_from: F-0009\n", ruleRaw, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentBaseAndEmptySection_AreDistinctOnTheWire_ButLookIdenticalToAReaderWhoDoesNotKnowTheConvention()
    {
        var frontmatter = new CardFrontmatter(
            "B-0100", CardKind.Block, "A block with no recorded base", "drafting",
            CardOwner.Architect, CardScope.Change, Section: string.Empty, T1, T1);
        var card = new CardFile(frontmatter, "Body.", [], [], BlockFields: BlockCardFields.Empty);

        var raw = CardFileWriter.Serialize(card);

        // section: is always present, even empty ("field present, nothing recorded"); base: is
        // omitted entirely ("present only when set") — two different conventions producing what
        // reads, to someone who has not been told the rule, as the same "nothing here" signal.
        Assert.Contains("section: \n", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("base:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapedEdgeWhitespace_ReadsAsLiteralBackslashSToAReaderWhoDoesNotKnowTheConvention()
    {
        var frontmatter = new CardFrontmatter(
            "Q-0001",
            CardKind.Question,
            "Which retry policy? ", // trailing space
            "open",
            CardOwner.Architect,
            CardScope.Repository,
            string.Empty,
            T1,
            T1);
        var card = new CardFile(frontmatter, "Body.", [], []);

        var raw = CardFileWriter.Serialize(card);
        var titleLine = raw.Split('\n').Single(line => line.StartsWith("title: ", StringComparison.Ordinal));

        // The on-disk line reads, to plain-text eyes, as the title ending in the two characters
        // backslash-s — not as a trailing space, and not as the plain question mark the value
        // actually ends with. A reader who has not read CardFileFormat's doc comment cannot
        // recover "the real title has a trailing space" from this line alone; \s is not a
        // documented convention on the wire itself, only in the tool's source.
        Assert.Equal("title: Which retry policy?\\s", titleLine);
        Assert.DoesNotContain("Which retry policy? ", raw, StringComparison.Ordinal);
    }
}
