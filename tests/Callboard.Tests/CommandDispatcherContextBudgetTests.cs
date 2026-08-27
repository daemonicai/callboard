using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §10 block B — the character budget on <c>context --role &lt;role&gt;</c>: the stated budget
/// itself, narrative-only truncation once the ceiling is threatened, and the one case where the
/// register and brief alone exceed it (working-context: "the budget SHALL be a requirement of
/// the response and not a target it may exceed" / "truncation is never silent").
/// </summary>
public sealed class CommandDispatcherContextBudgetTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Success_AlwaysStatesTheBudget_EvenWhenNothingIsTruncated()
    {
        using var repo = new TempGitRepo();
        WriteBlock(repo, "b-small", "B-0001", CardOwner.Worker, "briefed");

        var budget = Context(repo, "worker").GetProperty("budget");

        Assert.Equal(WorkingContextBudget.TokenBudget, budget.GetProperty("tokenBudget").GetInt32());
        Assert.Equal(WorkingContextBudget.CharactersPerToken, budget.GetProperty("charactersPerToken").GetDouble());
        Assert.Equal(WorkingContextBudget.MarginFraction, budget.GetProperty("marginFraction").GetDouble());
        Assert.Equal(WorkingContextBudget.CharacterCeiling, budget.GetProperty("characterCeiling").GetInt32());
        Assert.Equal(8100, budget.GetProperty("characterCeiling").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(budget.GetProperty("statement").GetString()));
        Assert.False(budget.GetProperty("truncated").GetBoolean());
        Assert.False(budget.TryGetProperty("truncationStatement", out _));
        Assert.False(budget.GetProperty("exceededCeiling").GetBoolean());
        Assert.False(budget.TryGetProperty("overageStatement", out _));
        Assert.Empty(budget.GetProperty("omittedNarrativeCommentIds").EnumerateArray());
        Assert.True(budget.GetProperty("characterCount").GetInt32() > 0);
        Assert.True(budget.GetProperty("characterCount").GetInt32() <= WorkingContextBudget.CharacterCeiling);
    }

    // working-context: "Oversized content is truncated in the narrative" — a card's accumulated
    // thread pushes the response past budget; the register and brief are delivered whole and the
    // narrative (comment bodies) is what shortens.
    [Fact]
    public void OversizedAddressedThread_DropsNarrativeOnly_KeepsRegisterAndBriefWhole_StatesOmission()
    {
        using var repo = new TempGitRepo();
        WriteRule(repo, "r-0001", "R-0001", "open");
        var briefBody = "Brief body, unshortened.";
        var (path, id) = WriteBlockWithComments(
            repo, "b-oversized", "B-0002", CardOwner.Worker, "in-review", briefBody,
            commentCount: 40, commentBodyLength: 400); // 40 * 400 = 16,000 chars — well past the ceiling.

        var result = Context(repo, "worker");
        var budget = result.GetProperty("budget");
        var topItem = result.GetProperty("topItem");

        // Register and brief are delivered whole — never shortened.
        Assert.Equal("Body.", result.GetProperty("liveRules").EnumerateArray().First().GetProperty("body").GetString());
        Assert.Equal(briefBody, topItem.GetProperty("body").GetString());
        Assert.Equal(id, topItem.GetProperty("id").GetString());

        Assert.True(budget.GetProperty("truncated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(budget.GetProperty("truncationStatement").GetString()));
        Assert.False(budget.GetProperty("exceededCeiling").GetBoolean());
        var omittedIds = budget.GetProperty("omittedNarrativeCommentIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.NotEmpty(omittedIds);
        Assert.True(budget.GetProperty("characterCount").GetInt32() <= WorkingContextBudget.CharacterCeiling);

        var threads = topItem.GetProperty("unresolvedThreadsAddressedToCaller").EnumerateArray().ToArray();
        Assert.Equal(40, threads.Length);

        var kept = threads.Where(t => !t.GetProperty("truncated").GetBoolean()).ToArray();
        var dropped = threads.Where(t => t.GetProperty("truncated").GetBoolean()).ToArray();
        Assert.NotEmpty(kept);
        Assert.NotEmpty(dropped);

        // A dropped comment still carries its structural facts — only the body is withheld.
        foreach (var thread in dropped)
        {
            Assert.False(string.IsNullOrEmpty(thread.GetProperty("commentId").GetString()));
            Assert.False(string.IsNullOrEmpty(thread.GetProperty("author").GetString()));
            Assert.False(thread.TryGetProperty("body", out _));
        }

        foreach (var thread in kept)
        {
            Assert.True(thread.TryGetProperty("body", out var body));
            Assert.False(string.IsNullOrEmpty(body.GetString()));
        }

        // The kept comments are exactly the priority-order prefix (oldest addressed first) —
        // dropping starts from the point the ceiling is reached and continues to the end, never
        // skipping ahead to a smaller later comment.
        var keptIds = kept.Select(t => t.GetProperty("commentId").GetString()).ToArray();
        var allIdsInOrder = threads.Select(t => t.GetProperty("commentId").GetString()).ToArray();
        Assert.Equal(allIdsInOrder.Take(keptIds.Length), keptIds);
    }

    // working-context: "the system SHALL NOT shorten the register or the brief" even when they
    // alone exceed the ceiling — the one case the response cannot satisfy its own stated budget,
    // and it must say so rather than silently shipping an over-budget response unremarked.
    [Fact]
    public void RegisterAndBriefAloneExceedCeiling_DeliveredWholeAnyway_OverageStated()
    {
        using var repo = new TempGitRepo();
        var hugeBody = new string('x', 10_000); // alone, comfortably past the 8,100 ceiling.
        var (_, id) = WriteBlockWithComments(repo, "b-huge", "B-0003", CardOwner.Worker, "in-review", hugeBody, commentCount: 3, commentBodyLength: 100);

        var result = Context(repo, "worker");
        var budget = result.GetProperty("budget");
        var topItem = result.GetProperty("topItem");

        Assert.Equal(id, topItem.GetProperty("id").GetString());
        // The brief is delivered whole — full 10,000-character body — even though it alone blows
        // the ceiling.
        Assert.Equal(hugeBody, topItem.GetProperty("body").GetString());

        Assert.True(budget.GetProperty("exceededCeiling").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(budget.GetProperty("overageStatement").GetString()));
        Assert.True(budget.GetProperty("characterCount").GetInt32() > WorkingContextBudget.CharacterCeiling);

        // Every narrative comment body was dropped — there is no room left for any of it.
        var threads = topItem.GetProperty("unresolvedThreadsAddressedToCaller").EnumerateArray().ToArray();
        Assert.Equal(3, threads.Length);
        Assert.All(threads, thread =>
        {
            Assert.True(thread.GetProperty("truncated").GetBoolean());
            Assert.False(thread.TryGetProperty("body", out _));
        });
        Assert.True(budget.GetProperty("truncated").GetBoolean());
        var omittedIds = budget.GetProperty("omittedNarrativeCommentIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(3, omittedIds.Length);
    }

    // §10 block B review, blocker 1: a measurement of an intermediate object cannot catch a
    // defect in what actually ships — this asserts against the exact line CommandDispatcher
    // writes to stdout (the CliEnvelope-wrapped JSON), not budget.characterCount in isolation.
    [Fact]
    public void Success_EmittedStdoutLine_FitsTheCharacterCeiling_AndMatchesBudget()
    {
        using var repo = new TempGitRepo();
        WriteRule(repo, "r-0001", "R-0001", "open");
        WriteBlockWithComments(repo, "b-narrative", "B-0004", CardOwner.Worker, "in-review", "Brief body.", commentCount: 10, commentBodyLength: 200);

        var (result, emittedLine) = ContextWithEmittedLine(repo, "worker");
        var budget = result.GetProperty("budget");

        Assert.True(emittedLine.Length <= WorkingContextBudget.CharacterCeiling,
            $"the emitted stdout line is {emittedLine.Length} characters — over the {WorkingContextBudget.CharacterCeiling}-character ceiling.");
        Assert.Equal(emittedLine.Length, budget.GetProperty("characterCount").GetInt32());
    }

    // The same emitted-line assertion in the oversized case — the ceiling has to bind the real
    // wire bytes even once narrative truncation is in play, not just the untruncated case above.
    [Fact]
    public void OversizedAddressedThread_EmittedStdoutLine_FitsTheCharacterCeiling()
    {
        using var repo = new TempGitRepo();
        // Full inclusion (20 * 400 = 8,000 narrative characters) exceeds the ceiling, but the
        // structural-only floor for 20 addressed threads is small enough that dropping some
        // narrative brings the response back under budget — unlike the exceeded-ceiling case
        // below, where even zero narrative still doesn't fit.
        WriteBlockWithComments(repo, "b-oversized-2", "B-0005", CardOwner.Worker, "in-review", "Brief body.", commentCount: 20, commentBodyLength: 400);

        var (result, emittedLine) = ContextWithEmittedLine(repo, "worker");
        var budget = result.GetProperty("budget");

        Assert.True(budget.GetProperty("truncated").GetBoolean());
        Assert.True(emittedLine.Length <= WorkingContextBudget.CharacterCeiling,
            $"the emitted stdout line is {emittedLine.Length} characters — over the {WorkingContextBudget.CharacterCeiling}-character ceiling.");
        Assert.Equal(emittedLine.Length, budget.GetProperty("characterCount").GetInt32());
    }

    // §10 block B review nit: the overage message must name whichever of the register or the
    // brief actually drove the overage, not blame the register unconditionally when the top
    // item's own body is the oversized one.
    [Fact]
    public void RegisterAndBriefAloneExceedCeiling_OverageStatement_NamesTheBriefWhenItIsTheDriver()
    {
        using var repo = new TempGitRepo();
        WriteRule(repo, "r-0001", "R-0001", "open"); // small — "Body." — never the driver here.
        var hugeBody = new string('x', 10_000);
        WriteBlockWithComments(repo, "b-huge-2", "B-0006", CardOwner.Worker, "in-review", hugeBody, commentCount: 1, commentBodyLength: 50);

        var budget = Context(repo, "worker").GetProperty("budget");

        var overageStatement = budget.GetProperty("overageStatement").GetString()!;
        Assert.Contains("top item's own brief", overageStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("the register is the larger", overageStatement, StringComparison.Ordinal);
    }

    private static (string Path, string Id) WriteBlock(TempGitRepo repo, string fileStem, string id, CardOwner owner, string status)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
        return (path, id);
    }

    private static (string Path, string Id) WriteBlockWithComments(
        TempGitRepo repo, string fileStem, string id, CardOwner owner, string status, string body, int commentCount, int commentBodyLength)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", FixedNow, FixedNow);
        var comments = Enumerable.Range(1, commentCount)
            .Select(i => new CardComment(
                $"c-{i:D4}", CardOwner.Architect, FixedNow.AddMinutes(i), new string('n', commentBodyLength), null, owner, null, []))
            .ToArray();
        var blockFields = new BlockCardFields("abc123", null, [], 1, [], []);
        var card = new CardFile(frontmatter, body, comments, [], BlockFields: blockFields);
        WriteCard(path, card);
        return (path, id);
    }

    private static void WriteRule(TempGitRepo repo, string fileStem, string id, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Rule, "A register card", status, CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static JsonElement Context(TempGitRepo repo, string role) => ContextWithEmittedLine(repo, role).Result;

    /// <summary>
    /// Returns both the parsed result and the exact line <see cref="CommandDispatcher"/> wrote to
    /// stdout — the <see cref="CliEnvelope"/>-wrapped JSON, with its trailing newline stripped
    /// (a line terminator is not response content). §10 block B review, blocker 1: a test that
    /// only ever inspects an intermediate object cannot catch a defect in what actually ships, so
    /// this is what <see cref="Success_EmittedStdoutLine_FitsTheCharacterCeiling_AndMatchesBudget"/>
    /// asserts against instead.
    /// </summary>
    private static (JsonElement Result, string EmittedLine) ContextWithEmittedLine(TempGitRepo repo, string role)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["context", "--role", role], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var emittedLine = output.ToString().TrimEnd('\r', '\n');
        using var doc = JsonDocument.Parse(emittedLine);
        return (doc.RootElement.GetProperty("result").Clone(), emittedLine);
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-context-budget-cli-tests-" + Guid.NewGuid().ToString("N"));
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
