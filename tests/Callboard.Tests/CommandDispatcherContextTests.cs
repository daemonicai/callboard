using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §10 block A at the CLI boundary: <c>context --role &lt;role&gt;</c>. The domain assembly itself
/// is <see cref="WorkingContextAssemblerTests"/>'s job — this file proves the CLI wiring: the
/// refusals at the parse door, and that the JSON envelope carries the four parts field-for-field.
/// </summary>
public sealed class CommandDispatcherContextTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingRole_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["context"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void OutsideAnyGitRepository_Refuses_WithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["context", "--role", "worker"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: directory.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void UnrecognisedRole_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["context", "--role", "wizard"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-role", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void Success_EmitsAllFourPartsInOrder_WithTheStatedQueueOrder()
    {
        using var repo = new TempGitRepo();
        WriteRule(repo, "r-0001", "R-0001", "open");
        WriteHazard(repo, "h-0001", "H-0001", "open");
        var (blockPath, blockId) = WriteBlock(repo, "b-0001", "B-0001", CardOwner.Worker, "briefed", FixedNow);

        var result = Context(repo, "worker");

        Assert.Equal("worker", result.GetProperty("role").GetString());
        Assert.Single(result.GetProperty("liveRules").EnumerateArray());
        Assert.Single(result.GetProperty("liveHazards").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("queueOrder").GetString()));

        var queue = result.GetProperty("queue").EnumerateArray().ToArray();
        var top = Assert.Single(queue);
        Assert.Equal(blockId, top.GetProperty("id").GetString());
        Assert.Equal(blockPath, top.GetProperty("filePath").GetString());

        var topItem = result.GetProperty("topItem");
        Assert.Equal(blockId, topItem.GetProperty("id").GetString());
        Assert.Equal("Body.", topItem.GetProperty("body").GetString());
        Assert.Empty(topItem.GetProperty("unresolvedThreadsAddressedToCaller").EnumerateArray());
        Assert.False(topItem.TryGetProperty("previousRoundVerdict", out _));

        // §10 block A review, change 3 — both the repository-scoped rule and hazard bind the top
        // item, and the response states the binding rule alongside them.
        Assert.False(string.IsNullOrWhiteSpace(topItem.GetProperty("constraintsRule").GetString()));
        var constraintIds = topItem.GetProperty("constraints").EnumerateArray()
            .Select(entry => entry.GetString())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["H-0001", "R-0001"], constraintIds);

        // §10 remediation S4 — the top item, unblocked here, reports neither blocked-ness nor
        // halted-ness.
        Assert.Empty(topItem.GetProperty("blockedBy").EnumerateArray());
        Assert.False(topItem.GetProperty("halted").GetBoolean());
        Assert.False(topItem.TryGetProperty("haltedByQuestionId", out _));
        Assert.False(topItem.TryGetProperty("haltedByQuestionTitle", out _));
    }

    [Fact]
    public void EmptyQueue_TopItemIsAbsent()
    {
        using var repo = new TempGitRepo();

        var result = Context(repo, "worker");

        Assert.Empty(result.GetProperty("queue").EnumerateArray());
        Assert.False(result.TryGetProperty("topItem", out _));
    }

    // §10 remediation S4: a top item blocked by an open Product Owner question reports halted —
    // and context and state agree on the same record (the divergence the supervisor caught).
    [Fact]
    public void TopItem_BlockedByOpenProductOwnerQuestion_ReportsHalted_AndAgreesWithState()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "Q-0001", CardOwner.ProductOwner, "open");
        var (_, blockId) = WriteBlockedBlock(repo, "b-halted", "B-0002", CardOwner.Worker, "briefed", FixedNow, ["Q-0001"]);

        var topItem = Context(repo, "worker").GetProperty("topItem");
        Assert.Equal(blockId, topItem.GetProperty("id").GetString());
        Assert.Equal(["Q-0001"], topItem.GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.True(topItem.GetProperty("halted").GetBoolean());
        Assert.Equal("Q-0001", topItem.GetProperty("haltedByQuestionId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(topItem.GetProperty("haltedByQuestionTitle").GetString()));

        var blockedCard = State(repo).GetProperty("blockedCards").EnumerateArray().Single(e => e.GetProperty("id").GetString() == blockId);
        Assert.True(blockedCard.GetProperty("halted").GetBoolean());
        Assert.Equal(
            topItem.GetProperty("haltedByQuestionId").GetString(),
            blockedCard.GetProperty("haltedByQuestionId").GetString());
    }

    // §10 remediation, round two, S2 — a deferred Product Owner question still halts (Product
    // Owner ruling: deferring does not lift the halt). Same shape as the open-question agreement
    // test above, deferred rather than open — context and state must still agree.
    [Fact]
    public void TopItem_BlockedByDeferredProductOwnerQuestion_ReportsHalted_AndAgreesWithState()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "Q-0004", CardOwner.ProductOwner, "deferred");
        var (_, blockId) = WriteBlockedBlock(repo, "b-halted-deferred", "B-0005", CardOwner.Worker, "briefed", FixedNow, ["Q-0004"]);

        var topItem = Context(repo, "worker").GetProperty("topItem");
        Assert.Equal(blockId, topItem.GetProperty("id").GetString());
        Assert.Equal(["Q-0004"], topItem.GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.True(topItem.GetProperty("halted").GetBoolean());
        Assert.Equal("Q-0004", topItem.GetProperty("haltedByQuestionId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(topItem.GetProperty("haltedByQuestionTitle").GetString()));

        var blockedCard = State(repo).GetProperty("blockedCards").EnumerateArray().Single(e => e.GetProperty("id").GetString() == blockId);
        Assert.True(blockedCard.GetProperty("halted").GetBoolean());
        Assert.Equal(
            topItem.GetProperty("haltedByQuestionId").GetString(),
            blockedCard.GetProperty("haltedByQuestionId").GetString());
    }

    // An answered Product Owner question halts nothing — deferral is the only non-terminal state
    // that still blocks; answered is genuinely closed.
    [Fact]
    public void TopItem_BlockedByAnsweredProductOwnerQuestion_ReportsNotHalted_AndAgreesWithState()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "Q-0005", CardOwner.ProductOwner, "answered");
        var (_, blockId) = WriteBlockedBlock(repo, "b-answered", "B-0006", CardOwner.Worker, "briefed", FixedNow, ["Q-0005"]);

        var topItem = Context(repo, "worker").GetProperty("topItem");
        Assert.Equal(["Q-0005"], topItem.GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(topItem.GetProperty("halted").GetBoolean());
        Assert.False(topItem.TryGetProperty("haltedByQuestionId", out _));
        Assert.False(topItem.TryGetProperty("haltedByQuestionTitle", out _));

        var blockedCard = State(repo).GetProperty("blockedCards").EnumerateArray().Single(e => e.GetProperty("id").GetString() == blockId);
        Assert.False(blockedCard.GetProperty("halted").GetBoolean());
    }

    // A top item blocked only by another role's question is blocked but not halted — and again
    // context and state must agree.
    [Fact]
    public void TopItem_BlockedByNonProductOwnerQuestion_ReportsBlocked_ButNotHalted_AndAgreesWithState()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "Q-0002", CardOwner.Architect, "open");
        var (_, blockId) = WriteBlockedBlock(repo, "b-blocked", "B-0003", CardOwner.Worker, "briefed", FixedNow, ["Q-0002"]);

        var topItem = Context(repo, "worker").GetProperty("topItem");
        Assert.Equal(["Q-0002"], topItem.GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(topItem.GetProperty("halted").GetBoolean());
        Assert.False(topItem.TryGetProperty("haltedByQuestionId", out _));
        Assert.False(topItem.TryGetProperty("haltedByQuestionTitle", out _));

        var blockedCard = State(repo).GetProperty("blockedCards").EnumerateArray().Single(e => e.GetProperty("id").GetString() == blockId);
        Assert.False(blockedCard.GetProperty("halted").GetBoolean());
    }

    private static (string Path, string Id) WriteBlock(TempGitRepo repo, string fileStem, string id, CardOwner owner, string status, DateTimeOffset updated)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", FixedNow, updated);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
        return (path, id);
    }

    private static (string Path, string Id) WriteBlockedBlock(
        TempGitRepo repo, string fileStem, string id, CardOwner owner, string status, DateTimeOffset updated, IReadOnlyList<string> blockedBy)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", FixedNow, updated);
        var blockFields = new BlockCardFields(null, null, [], null, blockedBy, []);
        var card = new CardFile(frontmatter, "Body.", [], [], BlockFields: blockFields);
        WriteCard(path, card);
        return (path, id);
    }

    private static void WriteQuestion(TempGitRepo repo, string id, CardOwner owner, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, id.ToLowerInvariant() + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Question, "A question", status, owner, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
    }

    private static void WriteRule(TempGitRepo repo, string fileStem, string id, string status) =>
        WriteRegisterCard(repo, fileStem, id, CardKind.Rule, status);

    private static void WriteHazard(TempGitRepo repo, string fileStem, string id, string status) =>
        WriteRegisterCard(repo, fileStem, id, CardKind.Hazard, status);

    private static void WriteRegisterCard(TempGitRepo repo, string fileStem, string id, CardKind kind, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, kind, "A register card", status, CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);
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

    private static JsonElement State(TempGitRepo repo)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["state"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-context-cli-nongit-{Guid.NewGuid():N}");

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-context-cli-tests-" + Guid.NewGuid().ToString("N"));
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
