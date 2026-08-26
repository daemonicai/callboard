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
            .Select(entry => entry.GetProperty("id").GetString())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["H-0001", "R-0001"], constraintIds);
    }

    [Fact]
    public void EmptyQueue_TopItemIsAbsent()
    {
        using var repo = new TempGitRepo();

        var result = Context(repo, "worker");

        Assert.Empty(result.GetProperty("queue").EnumerateArray());
        Assert.False(result.TryGetProperty("topItem", out _));
    }

    private static (string Path, string Id) WriteBlock(TempGitRepo repo, string fileStem, string id, CardOwner owner, string status, DateTimeOffset updated)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", FixedNow, updated);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
        return (path, id);
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
