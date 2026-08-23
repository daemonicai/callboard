using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// O-4 (DEVLOG §5 close, discharged here): "every wire form any shipped writer has emitted must
/// still parse." §6 is the first frontmatter widening since O-4 was named, and §5's own
/// remediation is the concrete failure this exists to stop repeating — its <c>gate_results</c>
/// widening (<c>label=exitcode</c> → <c>label=exitcode=round</c>) made every card the previous
/// binary had written unreadable, and nothing structural caught it before a real card refused.
///
/// <para>
/// This is the corpus: one fixture per historical wire form any build of this tool has ever
/// written, gathered in <see cref="Corpus"/> rather than scattered across unrelated test files, so
/// that <em>every</em> future frontmatter-widening change is forced to run against the whole
/// history in one place, not just the form its own author remembered to check. <b>Reader may widen;
/// writer emits exactly one form</b> — nothing here asserts what the current writer emits, only
/// that every past form still parses.
/// </para>
///
/// <para>
/// <b>What this guarantees, precisely, and what it does not.</b> If a fixture already in
/// <see cref="Corpus"/> stops parsing, <see cref="EveryFixtureInTheCorpus_StillParses"/> fails and
/// names it — that part is mechanical. Nothing here is structural about a <em>new</em> wire form: if
/// a future change widens what <see cref="CardFileWriter.Serialize"/> emits and its author forgets
/// to add a fixture for the old form here, no test catches the omission. "Forced through" in the
/// paragraph above means forced through by the next author reading this doc comment and adding a
/// fixture — a discipline this file states and asks for, not a mechanism that enforces itself. Do
/// not read this corpus as a structural guarantee it does not provide.
/// </para>
///
/// <para>
/// <b>Embedded raw strings rather than a fixtures directory on disk.</b> The brief's fix shape
/// names "a directory of card fixtures"; this implementation keeps the fixtures in-process as named
/// raw strings instead of files under a test-output-copied directory, to avoid depending on
/// <c>CopyToOutputDirectory</c> plumbing and relative-path resolution under xUnit v3's
/// Microsoft.Testing.Platform executable model (no vstest host, see the test project's own csproj
/// comment). The corpus property that matters — one collection every future widening must add a
/// fixture to, iterated exhaustively by one test — is preserved; only the storage medium differs
/// from the brief's literal phrasing. Flagged for the Architect rather than assumed acceptable.
/// </para>
/// </summary>
public sealed class CardFileWireCompatibilityCorpusTests
{
    /// <summary>
    /// Every historical wire form, named by what shipped it and what makes it distinct from the
    /// current writer's output. Add a fixture here — never replace one — the first time a future
    /// section changes what <see cref="CardFileWriter.Serialize"/> emits.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["§2 — the original nine-field card, no kind-specific fields at all"] =
            "---\n" +
            "id: B-0001\n" +
            "kind: block\n" +
            "title: Primary record\n" +
            "status: drafting\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: 2\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "---\n" +
            "Body text.\n",

        ["§5 block D (shipped, pre-remediation) — gate_results as legacy label=exitcode, no round"] =
            "---\n" +
            "id: B-0100\n" +
            "kind: block\n" +
            "title: Gate results, legacy spelling\n" +
            "status: building\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "base: abc1234\n" +
            "tasks: 5.6,5.7\n" +
            "gate_results: build=0,test=1\n" +
            "---\n" +
            "Body text.\n",

        ["§5 remediation (current writer) — gate_results as label=exitcode=round"] =
            "---\n" +
            "id: B-0101\n" +
            "kind: block\n" +
            "title: Gate results, current spelling\n" +
            "status: building\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "base: abc1234\n" +
            "round: 2\n" +
            "tasks: 5.6,5.7\n" +
            "gate_results: build=0=1,build=0=2,test=1=1\n" +
            "blocked_by: Q-0009\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:transition by=architect name=brief from=drafting to=briefed timestamp=2026-08-19T09:00:00+00:00 -->\n" +
            "<!-- callboard:transition by=reviewer name=request-changes from=in-review to=briefed timestamp=2026-08-20T09:00:00+00:00 -->\n",

        ["§5 block E — section card with base/closed_by/closed_at and a verdict"] =
            "---\n" +
            "id: S-0005\n" +
            "kind: section\n" +
            "title: Work lifecycle and sections\n" +
            "status: closed\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-23T09:00:00+00:00\n" +
            "base: e055e5b\n" +
            "closed_by: architect\n" +
            "closed_at: 2026-08-23T09:00:00+00:00\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:verdict by=supervisor verdict=approve range-from=e055e5b range-to=9671619 timestamp=2026-08-23T09:00:00+00:00 -->\n",

        ["§4 — question card with a comment thread and a handover, no kind-specific fields"] =
            "---\n" +
            "id: Q-0007\n" +
            "kind: question\n" +
            "title: Which retry policy applies?\n" +
            "status: open\n" +
            "owner: reviewer\n" +
            "scope: repository\n" +
            "section: \n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:handover by=architect to=reviewer timestamp=2026-08-19T09:00:00+00:00 -->\n" +
            "<!-- callboard:comment id=C-0001 author=reviewer timestamp=2026-08-19T09:00:00+00:00 -->\n" +
            "A comment.\n" +
            "<!-- /callboard:comment -->\n",
    };

    [Fact]
    public void EveryFixtureInTheCorpus_StillParses()
    {
        foreach (var (label, raw) in Corpus)
        {
            var result = CardFileParser.Parse(raw);
            result.Match<object?>(
                onSuccess: static _ => null,
                onFailure: failure => throw new Xunit.Sdk.XunitException(
                    $"fixture '{label}' failed to parse: {failure.Reason}"));
        }
    }

    [Fact]
    public void LegacyGateResultsSpelling_ReadsBackAsExitCodeAtRoundOne()
    {
        var parsed = Parse("§5 block D (shipped, pre-remediation) — gate_results as legacy label=exitcode, no round");

        Assert.Equal(GateStatus.Recorded(0), parsed.BlockFields.GateStatusOf("build"));
        Assert.Equal(GateStatus.Recorded(1), parsed.BlockFields.GateStatusOf("test"));
        Assert.Equal(2, parsed.BlockFields.GateResults.Length);
        Assert.All(parsed.BlockFields.GateResults, static result => Assert.Equal(1, result.Round));
    }

    [Fact]
    public void CurrentGateResultsSpelling_KeepsSupersededAndCurrentRoundsDistinct()
    {
        var parsed = Parse("§5 remediation (current writer) — gate_results as label=exitcode=round");

        Assert.Equal(3, parsed.BlockFields.GateResults.Length);
        // Round 2 is current (BlockFields.Round == 2): the round-1 "build" entry is retained on
        // GateResults but is not what GateStatusOf reports for the current round.
        Assert.Equal(GateStatus.Recorded(0), parsed.BlockFields.GateStatusOf("build"));
        Assert.Equal(GateStatus.Absent, parsed.BlockFields.GateStatusOf("test"));
    }

    [Fact]
    public void OriginalNineFieldCard_HasEveryKindSpecificFieldsTypeAtItsEmptyDefault()
    {
        var parsed = Parse("§2 — the original nine-field card, no kind-specific fields at all");

        Assert.Equal(BlockCardFields.Empty, parsed.BlockFields);
        Assert.Equal(SectionCardFields.Empty, parsed.SectionFields);
        Assert.Equal(FindingCardFields.Empty, parsed.FindingFields);
        Assert.Empty(parsed.UnknownFrontmatterFields);
    }

    [Fact]
    public void SectionCardFixture_ReadsBackItsVerdictAndClosure()
    {
        var parsed = Parse("§5 block E — section card with base/closed_by/closed_at and a verdict");

        Assert.Equal(CardOwner.Architect, parsed.SectionFields.ClosedBy);
        Assert.NotNull(parsed.SectionFields.ClosedAt);
        Assert.Single(parsed.SectionFields.Verdicts);
    }

    [Fact]
    public void EveryFixture_RoundTripsThroughTheCurrentWriterWithoutLosingItsFrontmatter()
    {
        // "The tool must read back what the tool wrote — including what older versions of the tool
        // wrote" (§5 working rule): parse, re-serialise with today's writer, parse again — the
        // second parse must agree with the first on every field this build models, proving a
        // targeted write against a legacy card is lossless rather than merely "does not throw".
        foreach (var (label, raw) in Corpus)
        {
            var first = Parse(label, raw);
            var reparsed = Parse(label, CardFileWriter.Serialize(first));

            Assert.True(
                first.Frontmatter == reparsed.Frontmatter,
                $"fixture '{label}': frontmatter changed across a re-serialise/re-parse cycle");
            Assert.Equal(first.BlockFields, reparsed.BlockFields);
            Assert.Equal(first.SectionFields, reparsed.SectionFields);
            Assert.Equal(first.FindingFields, reparsed.FindingFields);
        }
    }

    private static CardFile Parse(string label) => Parse(label, Corpus[label]);

    private static CardFile Parse(string label, string raw) =>
        CardFileParser.Parse(raw).Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"fixture '{label}' failed to parse: {failure.Reason}"));
}
