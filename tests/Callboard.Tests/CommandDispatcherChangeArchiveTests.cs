using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.3/7.4 at the CLI boundary: <c>change archive</c>. Domain-level correctness (bytes unmoved,
/// resolver end to end, index rebuild) is <see cref="CardChangeArchiveTests"/>'s job — this proves
/// the verb is actually wired: parsed, dispatched, and every <see cref="ChangeArchiveOutcome"/>
/// case reaches its own refusal code.
/// </summary>
public sealed class CommandDispatcherChangeArchiveTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ChangeArchive_LiveChangeWithAnOpenObligation_Succeeds_AndSettlesIt()
    {
        using var repo = new TempGitRepo();

        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", Path.Combine(repo.CardsDirectory, "s-0001.md"), "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            sectionOutput, repo.Path, "Body.");
        var sectionId = ExtractResultId(sectionOutput);

        var obligationOutput = new StringWriter();
        RunInRepo(
            [
                "obligation", "create", Path.Combine(repo.CardsDirectory, "o-0001.md"), "--title", "Settle the migration",
                "--role", "architect", "--change", ChangeName, "--owed-by", sectionId,
            ],
            obligationOutput, repo.Path, "Body.");
        var obligationId = ExtractResultId(obligationOutput);

        var archiveOutput = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], archiveOutput, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(archiveOutput.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(ChangeName, result.GetProperty("changeName").GetString());
        Assert.Equal("architect", result.GetProperty("archivedBy").GetString());
        var settled = result.GetProperty("settledObligationIds").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([obligationId], settled);
        Assert.False(Directory.Exists(repo.CardsDirectory));
    }

    [Fact]
    public void ChangeArchive_NoSuchLiveChange_Refuses_WithChangeNotFound()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", "no-such-change", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("change-not-found", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_ReservedNameArchive_Refuses_WithInvalidChangeName()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", "archive", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("invalid-change-name", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_TwiceOnTheSameChange_Refuses_WithAlreadyArchived()
    {
        using var repo = new TempGitRepo();
        RunInRepo(
            ["section", "create", Path.Combine(repo.CardsDirectory, "s-0002.md"), "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            new StringWriter(), repo.Path, "Body.");

        var first = new StringWriter();
        var firstExitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], first, repo.Path, string.Empty);
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExitCode);

        // Recreate a live change under the same name so the second call has something to try to
        // archive — proving the refusal, not merely "no live change" again.
        Directory.CreateDirectory(repo.CardsDirectory);
        var second = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], second, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(second.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("already-archived", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_MissingRole_Refuses()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.CardsDirectory);

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_MissingChangeName_Refuses()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
    }

    private static string ExtractResultId(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-change-archive-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
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
