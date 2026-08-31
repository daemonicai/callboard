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
    // §14.1/14.2/14.4: the transition/verdict/handover/comment fixtures below were updated in place
    // to the new delimited-block syntax rather than kept as a permanent historical form the way O-4
    // normally requires — the Architect's own §14 brief states no data migration is owed for this
    // specific change ("no callboard/ board is committed, so fixtures and the corpus move and
    // nothing on disk does"), which is a deliberate, brief-authorised exception to O-4 for this wire
    // form only, not a precedent for widening it generally. §14.4 extends the same authorisation to
    // the comment header, the eighth family this exception covers, on the same terms.
    //
    // §14 remediation (reviewer finding), extended by §14.4: that authorisation covers *dropping*
    // the old forms from this corpus — it does not by itself establish that the parser fails safely
    // on what it no longer emits, which is the property O-4 actually protects against silently
    // breaking. Before the reviewer's remediation it did not hold: an old single-line marker for
    // any of the seven §14.1 families read back successfully with the entry silently absorbed into
    // the card's body and no trace it had ever been recorded — worse than the "fails to parse" case
    // O-4 exists for. §14.4 brought the comment header onto the same shared BlockOpenLinePrefixes
    // declaration those seven already read from, so the same property holds for it too, by the same
    // mechanism, for free — OldSingleLineMarker_ForEveryFamily_FailsLoudly_RatherThanSilentlyMisparsing
    // below now covers all eight and is the test that makes the property true and keeps it true: the
    // old forms now have a permanent home in this file as things that refuse, which is the honest
    // version of what this waiver was gesturing at.
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
            "<!-- callboard:transition\n" +
            "by: architect\n" +
            "name: brief\n" +
            "from: drafting\n" +
            "to: briefed\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "<!-- callboard:transition\n" +
            "by: reviewer\n" +
            "name: request-changes\n" +
            "from: in-review\n" +
            "to: briefed\n" +
            "timestamp: 2026-08-20T09:00:00+00:00\n" +
            "-->\n",

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
            "<!-- callboard:verdict\n" +
            "by: supervisor\n" +
            "verdict: approve\n" +
            "range-from: e055e5b\n" +
            "range-to: 9671619\n" +
            "timestamp: 2026-08-23T09:00:00+00:00\n" +
            "-->\n",

        ["§6 block B (shipped, pre-fingerprint/pre-disposition) — finding card with an explicit extent, no extent_fingerprint or disposition keys"] =
            "---\n" +
            "id: F-0001\n" +
            "kind: finding\n" +
            "title: Reviewed the lock-acquisition path\n" +
            "status: open\n" +
            "owner: reviewer\n" +
            "scope: section\n" +
            "section: 6\n" +
            "created: 2026-08-23T09:00:00+00:00\n" +
            "updated: 2026-08-23T09:00:00+00:00\n" +
            "instrument: manual review\n" +
            "extent: explicit\n" +
            "extent_value: src/Callboard/Cards/CardStore.cs\n" +
            "verified_at: 7ea24e4\n" +
            "blind_spot: none\n" +
            "---\n" +
            "No blind spot found.\n",

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
            "<!-- callboard:handover\n" +
            "by: architect\n" +
            "to: reviewer\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: reviewer\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
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
    public void PreFingerprintFindingFixture_HasNoExtentFingerprint_AndDefaultsToMeasuredDisposition()
    {
        var parsed = Parse("§6 block B (shipped, pre-fingerprint/pre-disposition) — finding card with an explicit extent, no extent_fingerprint or disposition keys");

        Assert.Null(parsed.FindingFields.ExtentFingerprint);
        Assert.Equal(FindingDisposition.Measured, parsed.FindingFields.Disposition);
        Assert.Equal(FindingExtent.Explicit(["src/Callboard/Cards/CardStore.cs"]), parsed.FindingFields.Extent);

        // §6 block C's own staleness lesson, proven against this exact block-B-era shape: no
        // fingerprint was ever recorded, so this must read back NotMeasurable, never Current.
        var status = FindingStalenessEvaluator.Evaluate(parsed.FindingFields, Path.GetTempPath());
        Assert.Equal("not-measurable", status.Match(
            onCurrent: static () => "current",
            onStale: static _ => "stale",
            onNotMeasurable: static _ => "not-measurable",
            onNotApplicable: static _ => "not-applicable"));
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

    // §14 remediation (reviewer finding), extended by §14.4: the property this waiver depends on,
    // made true and pinned — an old pre-14.1/pre-14.4 single-line marker for each of the eight
    // §14.1 block families now fails to parse, loudly, rather than being silently absorbed into the
    // card's body as prose (the reviewer's own repro, generalised to every family: a transition
    // marker that used to read back with Transitions.Count == 0 and no trace it had ever existed).
    // §14.4's comment case covers the eighth family the same repro generalises to: the pre-§14.4
    // header shape (CommentOpenLine + a space + key=value tokens + " -->") is exactly the same
    // "prefix, trailing content, then -->" shape the other seven's old form had.
    [Theory]
    [InlineData(CardFileFormat.HandoverOpenLine, "handover")]
    [InlineData(CardFileFormat.TransitionOpenLine, "transition")]
    [InlineData(CardFileFormat.VerdictOpenLine, "verdict")]
    [InlineData(CardFileFormat.AuthorisationOpenLine, "authorisation")]
    [InlineData(CardFileFormat.ClaimOpenLine, "claim")]
    [InlineData(CardFileFormat.LimitOpenLine, "limit")]
    [InlineData(CardFileFormat.RefusalOpenLine, "refusal")]
    [InlineData(CardFileFormat.CommentOpenLine, "comment")]
    public void OldSingleLineMarker_ForEveryFamily_FailsLoudly_RatherThanSilentlyMisparsing(string openLine, string family)
    {
        const string header =
            "---\nid: X-0700\nkind: block\ntitle: t\nstatus: drafting\nowner: worker\nscope: change\nsection: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n---\n" +
            "Body text.\n";
        // The pre-14.1 shape: prefix, a space, key=value tokens, a trailing " -->" on one physical
        // line — exactly what every family's marker looked like before this section landed.
        var oldSingleLineForm = openLine + " by=architect to=worker timestamp=2026-08-19T09:00:00+00:00 -->\n";

        var result = CardFileParser.Parse(header + oldSingleLineForm);

        result.Match<object?>(
            onSuccess: success => throw new Xunit.Sdk.XunitException(
                $"expected the old {family} single-line marker to fail loudly, but it parsed successfully with 0 {family} entries — exactly the silent-misparse regression this test guards"),
            onFailure: failure =>
            {
                Assert.Contains("malformed", failure.Reason, StringComparison.Ordinal);
                Assert.Contains(family, failure.Reason, StringComparison.Ordinal);
                return null;
            });
    }

    private static CardFile Parse(string label) => Parse(label, Corpus[label]);

    private static CardFile Parse(string label, string raw) =>
        CardFileParser.Parse(raw).Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"fixture '{label}' failed to parse: {failure.Reason}"));
}
