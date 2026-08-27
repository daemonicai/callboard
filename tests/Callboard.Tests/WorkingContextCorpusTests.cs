using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §10 block B, 10.6/10.7 — the budget mechanism measured at scale, not merely asserted.
/// "A corpus comparable to the measured change" is design.md D4's own figure: the incumbent
/// DEVLOG reached 2.07 MB for one change. These two tests build a fixture at that scale (never
/// merely "big") and are the evidence base for Product Owner ruling 1 at §10's opening (the read
/// path scans card files, not the index) — if this corpus threatens the ceiling, or the read
/// path is too slow to live with, that is exactly what the ruling deferred, and worth stating
/// plainly rather than silently working around.
/// </summary>
public sealed class WorkingContextCorpusTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    // design.md D4: "the incumbent reached 2.07 MB for one change".
    private const long TargetCorpusBytes = 2_070_000;
    private const int SectionCount = 12;

    // working-context: "Working context fits a stated budget" — a corpus at the incumbent's own
    // measured scale must still produce a response that fits the character ceiling, because the
    // response is governed by the live working set, not by the corpus behind it.
    [Fact]
    public void FullScaleCorpus_ResponseFitsTheCharacterCeiling()
    {
        using var repo = new TempGitRepo();
        var settledBytes = WriteSettledHistory(repo, sectionsToInclude: SectionCount);
        WriteLiveWorkingSet(repo);

        Assert.True(
            settledBytes >= TargetCorpusBytes,
            $"fixture is only {settledBytes} bytes on disk; expected at least {TargetCorpusBytes} " +
            "to be comparable to the measured change (design.md D4's own 2.07 MB figure) — a " +
            "fixture that is merely \"big\" does not test this.");

        var stopwatch = Stopwatch.StartNew();
        var result = Context(repo, "worker");
        stopwatch.Stop();

        var budget = result.GetProperty("budget");
        Assert.False(budget.GetProperty("exceededCeiling").GetBoolean());
        Assert.True(budget.GetProperty("characterCount").GetInt32() <= budget.GetProperty("characterCeiling").GetInt32());

        // Not a hard spec requirement, but the evidence Product Owner ruling 1 (§10 opening) is
        // to be revisited against: reading every live card file at this scale must stay usable
        // for a per-brief command. A generous bound — this is a regression guard, not a
        // benchmark.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 10_000,
            $"reading a {settledBytes}-byte corpus took {stopwatch.ElapsedMilliseconds}ms — too " +
            "slow for a per-brief command; report this to the Architect rather than silently " +
            "optimising around it.");
    }

    // working-context: "Working-context cost does not grow with the change" / "Cost is flat
    // across a long change" — the twelfth section of a change carries eleven sections' worth of
    // settled history the first section does not, yet the same live working set sits on top of
    // both. The response must be governed by that live working set, not by what has accumulated
    // behind it.
    [Fact]
    public void ResponseSize_AtTwelfthSection_IsWithin20PercentOfFirstSection()
    {
        using var firstSectionRepo = new TempGitRepo();
        var firstSectionBytes = WriteSettledHistory(firstSectionRepo, sectionsToInclude: 1);
        WriteLiveWorkingSet(firstSectionRepo);

        using var twelfthSectionRepo = new TempGitRepo();
        var twelfthSectionBytes = WriteSettledHistory(twelfthSectionRepo, sectionsToInclude: SectionCount);
        WriteLiveWorkingSet(twelfthSectionRepo);

        // The two corpora must actually differ in settled history behind an identical live
        // working set — otherwise this test would prove nothing about flat cost.
        Assert.True(twelfthSectionBytes > firstSectionBytes * 5,
            $"the twelfth-section fixture ({twelfthSectionBytes} bytes) is not meaningfully larger " +
            $"than the first-section one ({firstSectionBytes} bytes) — the two states were not built " +
            "honestly distinct.");

        var firstLength = Context(firstSectionRepo, "worker").GetProperty("budget").GetProperty("characterCount").GetInt32();
        var twelfthLength = Context(twelfthSectionRepo, "worker").GetProperty("budget").GetProperty("characterCount").GetInt32();

        var larger = Math.Max(firstLength, twelfthLength);
        var smaller = Math.Min(firstLength, twelfthLength);
        Assert.True(
            larger <= smaller * 1.2,
            $"response size grew with settled history: first-section={firstLength} characters, " +
            $"twelfth-section={twelfthLength} characters — more than 20% apart despite an identical " +
            "live working set.");
    }

    /// <summary>
    /// Writes <paramref name="sectionsToInclude"/> sections' worth of closed, settled block
    /// cards — narrative that must never reach a working-context response, only occupy the
    /// corpus a scan walks past. Returns the total bytes written, so callers can both confirm the
    /// fixture reaches D4's own scale and compare two states honestly.
    /// </summary>
    private static long WriteSettledHistory(TempGitRepo repo, int sectionsToInclude)
    {
        const long targetBytesPerSection = TargetCorpusBytes / SectionCount;
        long totalBytes = 0;
        var cardIndex = 0;

        for (var section = 1; section <= sectionsToInclude; section++)
        {
            long sectionBytes = 0;
            while (sectionBytes < targetBytesPerSection)
            {
                cardIndex++;
                var id = $"B-S{section:D2}-{cardIndex:D5}";
                var path = Path.Combine(repo.ChangesDirectory, $"closed-s{section:D2}-{cardIndex:D5}.md");
                var frontmatter = new CardFrontmatter(
                    id, CardKind.Block, $"Section {section} settled block {cardIndex}", "closed",
                    CardOwner.Worker, CardScope.Change, $"S-{section:D4}", FixedNow, FixedNow);
                var comments = Enumerable.Range(1, 4)
                    .Select(i => new CardComment(
                        $"c-{i}", CardOwner.Architect, FixedNow.AddMinutes(i), FillerText(300, cardIndex * 10 + i), null, null, null, []))
                    .ToArray();
                var card = new CardFile(frontmatter, FillerText(1200, cardIndex), comments, []);
                var text = CardFileWriter.Serialize(card);
                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var bytes = Encoding.UTF8.GetByteCount(text);
                sectionBytes += bytes;
                totalBytes += bytes;
            }
        }

        return totalBytes;
    }

    /// <summary>
    /// The live working set — a repository-scoped rule and hazard, and one live top item with
    /// unresolved threads addressed to the caller — identical in both fixtures, so the only
    /// difference between them is the settled history <see cref="WriteSettledHistory"/> wrote.
    /// </summary>
    private static void WriteLiveWorkingSet(TempGitRepo repo)
    {
        var rulePath = Path.Combine(repo.RegisterDirectory, "r-corpus.md");
        var ruleFrontmatter = new CardFrontmatter(
            "R-9001", CardKind.Rule, "A live rule", "open", CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        WriteCard(rulePath, new CardFile(ruleFrontmatter, FillerText(400, 1), [], [], RegisterFields: RegisterCardFields.Empty));

        var hazardPath = Path.Combine(repo.RegisterDirectory, "h-corpus.md");
        var hazardFrontmatter = new CardFrontmatter(
            "H-9001", CardKind.Hazard, "A live hazard", "open", CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        WriteCard(hazardPath, new CardFile(hazardFrontmatter, FillerText(400, 2), [], [], RegisterFields: RegisterCardFields.Empty));

        var topItemPath = Path.Combine(repo.ChangesDirectory, "live-top-item.md");
        var comments = Enumerable.Range(1, 5)
            .Select(i => new CardComment(
                $"live-c-{i}", CardOwner.Architect, FixedNow.AddMinutes(i), FillerText(300, 900 + i), null, CardOwner.Worker, null, []))
            .ToArray();
        var blockFields = new BlockCardFields("abc123", null, ["10.4", "10.5", "10.6", "10.7"], 1, [], []);
        var frontmatter = new CardFrontmatter(
            "B-9001", CardKind.Block, "The live top item", "in-review", CardOwner.Worker, CardScope.Change, "S-0012", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, FillerText(800, 999), comments, [], BlockFields: blockFields);
        WriteCard(topItemPath, card);
    }

    private static string FillerText(int length, int seed)
    {
        var sentence = $"Settled narrative from card {seed}, recording what happened and why, for the audit trail. ";
        var builder = new StringBuilder(length + sentence.Length);
        while (builder.Length < length)
        {
            builder.Append(sentence);
        }

        return builder.ToString(0, length);
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static JsonElement Context(TempGitRepo repo, string role)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["context", "--role", role], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-context-corpus-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            ChangesDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory("establish-callboard").Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(ChangesDirectory);
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
