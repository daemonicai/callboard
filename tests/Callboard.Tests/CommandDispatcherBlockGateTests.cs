using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 5.6 at the CLI boundary: <c>block gate</c>. Same "own repo-root-not-found site, own tests"
/// discipline §5 block C's fourth remediation round established — every refusal code minted for
/// this verb gets its own CLI-level test, verified by reverting the exact line it guards.
/// </summary>
public sealed class CommandDispatcherBlockGateTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void BlockGate_Recording_Succeeds_AndTheEnvelopeReportsWhatHappened()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0001", "B-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "build", "0", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("build", result.GetProperty("label").GetString());
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.True(result.GetProperty("passed").GetBoolean());
        Assert.Equal("worker", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(read.BlockFields.GateStatusOf("build").Passed);
    }

    [Fact]
    public void BlockGate_NonZeroExitCode_Succeeds_ButReportsNotPassed()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0002", "B-0002");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "test", "1", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("passed").GetBoolean());
    }

    // Reverting the exact line that constructs "missing-argument" for a missing file path — this
    // test would go red only if that specific construction stopped firing.
    [Fact]
    public void BlockGate_MissingFilePath_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_MissingLabel_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0003", "B-0003");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_InvalidLabel_RefusesWithInvalidGateLabelCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0004", "B-0004");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "bu=ild", "0"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-gate-label", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_MissingExitCode_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0005", "B-0005");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "build"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_InvalidExitCode_RefusesWithInvalidExitCodeCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0006", "B-0006");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "build", "not-a-number"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-exit-code", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_RoleFlagWithNoValue_RefusesWithMissingFlagValueCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0007", "B-0007");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "build", "0", "--role"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-flag-value", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_ChangeFlagWithNoValue_RefusesWithMissingFlagValueCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0008", "B-0008");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "build", "0", "--role", "worker", "--change"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-flag-value", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_MissingRoleFlagEntirely_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0009", "B-0009");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "build", "0", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_UnrecognisedRoleValue_RefusesWithUnrecognisedRoleCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0010", "B-0010");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "gate", path, "build", "0", "--role", "wizard"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-role", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_OutsideAnyGitRepository_RefusesWithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "build", "0", "--role", "worker", "--change", ChangeName], output, directory.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_NotABlockCard_RefusesWithNotABlockCardCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "build", "0", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_CardNotFound_RefusesWithCardNotFoundCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "missing.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "build", "0", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_LayoutMismatch_RefusesWithCardLayoutMismatchCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0011", "B-0011");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "build", "0", "--role", "worker", "--change", "a-different-change"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-layout-mismatch", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockGate_CorruptCard_ExitsAsRefusal_NotAToolFailure()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["block", "gate", path, "build", "0", "--role", "worker", "--change", ChangeName],
            output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
    }

    [Fact]
    public void BlockGate_LockTimeout_ExitsAsToolFailure_NotARefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0012", "B-0012");
        var before = File.ReadAllBytes(path);
        var holder = CardLock.Acquire(path, TimeSpan.FromSeconds(5)).Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"setup: expected to acquire the lock, timed out: {timedOut.Message}"));

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CommandDispatcher.Run(
                ["block", "gate", path, "build", "0", "--role", "worker", "--change", ChangeName],
                output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path,
                clock: static () => FixedNow, lockTimeout: TimeSpan.FromMilliseconds(200));

            Assert.Equal(CommandDispatcher.ToolFailureExitCode, exitCode);
            using var doc = JsonDocument.Parse(output.ToString());
            Assert.Equal("tool-failure", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            holder.Dispose();
        }
    }

    // §5 remediation, round 2 (reviewer finding against the shipped block D binary): a card
    // carrying gate_results in the pre-B2 two-part shape ("label=exitcode", exactly what the
    // shipped block D binary wrote) must still accept a new gate recording through the CLI, not
    // exit 2/tool-failure the moment any write path touches it. Broken and watched red first:
    // before the parser's legacy branch landed, this reproduced exactly the reviewer's report —
    // exitCode 2, "code":"tool-failure".
    [Fact]
    public void BlockGate_RecordingOnACardWithLegacyTwoPartGateResults_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteLegacyBlockCard(repo.Path, "b-0900", "B-0900");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "gate", path, "test", "1", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(read.BlockFields.GateStatusOf("build").Passed, "the legacy result must still read back as round 1's evidence.");
        Assert.False(read.BlockFields.GateStatusOf("test").Passed);
    }

    private static string WriteLegacyBlockCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var raw =
            "---\n" +
            $"id: {id}\nkind: block\ntitle: Title\nstatus: building\nowner: worker\nscope: change\nsection: 5\n" +
            $"created: {FixedNow:O}\nupdated: {FixedNow:O}\n" +
            "gate_results: build=0\n" + // exactly the pre-B2 two-part shape the shipped block D binary wrote
            "---\n" +
            "Body.\n";
        File.WriteAllText(path, raw, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteInitialBlockCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "building", CardOwner.Worker, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-block-gate-cli-nongit-{Guid.NewGuid():N}");

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

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-gate-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
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
