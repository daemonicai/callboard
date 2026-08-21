using System.Reflection;
using System.Text;
using System.Text.Json;
using Callboard.Cli;
using Callboard.Index;

namespace Callboard.Tests;

public sealed class CommandDispatcherTests
{
    [Fact]
    public void Version_EmitsJsonEnvelopeWithOkResultShape()
    {
        var output = new StringWriter();

        var exitCode = Run(["version"], output);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("version", root.GetProperty("command").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("result").GetProperty("version").GetString()));
        Assert.False(root.TryGetProperty("refusal", out _));
    }

    [Fact]
    public void Version_ExitsZero()
    {
        var exitCode = Run(["version"], TextWriter.Null);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void UnknownCommand_RefusesWithNonZeroExitCode()
    {
        var output = new StringWriter();

        var exitCode = Run(["frobnicate"], output);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.NotEqual(0, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown-command", root.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("result", out _));
    }

    // EnforceNoUnconsumedArguments only overrides a Success — a handler's own Refusal is always
    // the more specific reason and must not be replaced by a generic "unrecognised" complaint. An
    // unknown command with a trailing token must still read "no such command", or an agent acting
    // on the message would go fix the wrong thing.
    [Fact]
    public void UnknownCommand_WithTrailingToken_StillRefusesAsUnknownCommand_NotUnrecognisedArgument()
    {
        var output = new StringWriter();

        var exitCode = Run(["frobnicate", "extra"], output);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unknown-command", refusal.GetProperty("code").GetString());
        Assert.Contains("frobnicate", refusal.GetProperty("message").GetString());
    }

    // The envelope's `command` field names what was actually recognised, not just args[0] — the
    // gap the architect found running the built binary: everything up to now asserted on outcomes
    // and exit codes, never on what the envelope itself says a two-token verb's command was.
    [Fact]
    public void Envelope_NamesTheFullyRecognisedCommand_ForIndexRebuild()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        RunInRepo(["index", "rebuild"], output, repo.Path);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("index rebuild", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Envelope_NamesOnlyIndex_WhenNoSubcommandWasGiven()
    {
        var output = new StringWriter();

        Run(["index"], output);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("index", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Envelope_NamesOnlyIndex_WhenTheSubcommandWasNotRecognised()
    {
        var output = new StringWriter();

        Run(["index", "bogus"], output);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("index", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Envelope_NamesOnlyTheUnrecognisedTopLevelCommand()
    {
        var output = new StringWriter();

        Run(["bogus", "extra"], output);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("bogus", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Envelope_NamesVersion_Unchanged()
    {
        var output = new StringWriter();

        Run(["version"], output);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("version", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void NoCommand_RefusesWithNonZeroExitCode()
    {
        var exitCode = Run([], TextWriter.Null);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
    }

    [Fact]
    public void EveryEnvelope_IsExactlyOneLineOfJson()
    {
        var output = new StringWriter();

        Run(["version"], output);

        var text = output.ToString();
        var trimmed = text.TrimEnd('\r', '\n');
        Assert.DoesNotContain('\n', trimmed);
    }

    [Fact]
    public void CommandOutcomeSuccess_CarriesACommandResult_NotAnUntypedObject()
    {
        var successType = typeof(CommandOutcome).GetNestedType("Success", BindingFlags.NonPublic)!;
        var resultProperty = successType.GetProperty("Result")!;

        Assert.NotEqual(typeof(object), resultProperty.PropertyType);
        Assert.True(typeof(ICommandResult).IsAssignableFrom(resultProperty.PropertyType));
    }

    [Fact]
    public void Version_ResultSerialisesThroughItsOwnPerTypeMethod()
    {
        var result = new VersionResult { Version = "9.9.9" };

        var element = ((ICommandResult)result).ToJsonElement();

        Assert.Equal("9.9.9", element.GetProperty("version").GetString());
    }

    // Blocker 1 (§1 remediation): an argument no command consumed is a refusal, the same
    // convention as an unrecognised command, established here so §2+ don't each invent their own
    // flag handling. §3 obligation 3 made this structural: CommandDispatcher.Run funnels every
    // outcome through EnforceNoUnconsumedArguments once, after Dispatch (and therefore RunVersion,
    // which takes no arguments at all) has already returned.
    [Fact]
    public void Version_WithUnrecognisedArgument_RefusesWithNonZeroExitCode()
    {
        var output = new StringWriter();

        var exitCode = Run(["version", "--oops"], output);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("unrecognised-argument", root.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("result", out _));
    }

    // §3 obligation 3: asserts RunVersion itself takes no parameters, i.e. it contains no
    // argument-count check of its own — the funnel in EnforceNoUnconsumedArguments is the only
    // place that check exists.
    [Fact]
    public void RunVersion_HasNoArgumentCheckInItsOwnBody()
    {
        var method = typeof(CommandDispatcher).GetMethod("RunVersion", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Empty(method.GetParameters());
    }

    // §1 remediation follow-up, restructured for §3 obligation 4: proves the stdin guard is
    // reachable from a command handler through the CommandContext it already receives, with no
    // change to Dispatch's signature required for a new body-reading command to reach it.
    [Fact]
    public void StdinGuard_IsReachableThroughCommandContext_WithoutChangingDispatchSignature()
    {
        var context = new CommandDispatcher.CommandContext(
            Arguments: new ArgumentCursor([]),
            Input: TextReader.Null,
            IsInputRedirected: false,
            WorkingDirectory: ".");

        var refusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);

        Assert.NotNull(refusal);
        Assert.Equal("stdin-not-redirected", refusal!.Code);
        Assert.Null(stdin);
    }

    // Blocker 3 (§1 remediation): an escaping exception still yields exactly one JSON envelope
    // on stdout, exits with the dedicated tool-failure code (not the refusal code), and puts
    // diagnostic detail on stderr rather than stdout.
    [Fact]
    public void UnexpectedException_StillEmitsExactlyOneJsonEnvelope_AndExitsWithToolFailureCode()
    {
        var output = new ThrowsOnFirstWriteLine();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(["version"], output, TextReader.Null, error, isInputRedirected: true, workingDirectory: ".");

        Assert.Equal(CommandDispatcher.ToolFailureExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.SuccessExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.RefusalExitCode, exitCode);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("version", root.GetProperty("command").GetString());
        Assert.False(root.TryGetProperty("result", out _));

        Assert.False(string.IsNullOrWhiteSpace(error.ToString()));
    }

    // --- index rebuild -----------------------------------------------------------------------

    [Fact]
    public void Index_WithNoSubcommand_RefusesNamingTheSubcommandsItHas()
    {
        var output = new StringWriter();

        var exitCode = Run(["index"], output);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-subcommand", refusal.GetProperty("code").GetString());
        Assert.Contains("rebuild", refusal.GetProperty("message").GetString());
    }

    [Fact]
    public void Index_WithUnknownSubcommand_Refuses()
    {
        var output = new StringWriter();

        var exitCode = Run(["index", "frobnicate"], output);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unknown-subcommand", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void IndexRebuild_OnAnEmptyCardsRoot_Succeeds()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["index", "rebuild"], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(0, result.GetProperty("indexedCardCount").GetInt32());
        Assert.Equal(0, result.GetProperty("indexedCommentCount").GetInt32());
        Assert.Empty(result.GetProperty("failures").EnumerateArray());
        Assert.True(File.Exists(result.GetProperty("databasePath").GetString()));
    }

    [Fact]
    public void IndexRebuild_ReportsParseFailuresInASuccessfulResult()
    {
        using var repo = new TempGitRepo();
        var registerDirectory = Path.Combine(repo.Path, "callboard", "register");
        Directory.CreateDirectory(registerDirectory);
        File.WriteAllText(Path.Combine(registerDirectory, "corrupt.md"), "not a valid card file");
        var output = new StringWriter();

        var exitCode = RunInRepo(["index", "rebuild"], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        var failure = Assert.Single(result.GetProperty("failures").EnumerateArray());
        Assert.Contains("corrupt.md", failure.GetProperty("filePath").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("reason").GetString()));
    }

    // The argument-boundary check runs once, after Dispatch returns (CommandDispatcher
    // .EnforceNoUnconsumedArguments) — the single funnel point every command's outcome passes
    // through, chosen over a per-arm wrapper specifically because a wrapper is something a new
    // dispatch arm can skip and still compile (the reviewer proved this live against the
    // wrapper-per-arm version). One consequence: a handler with a side effect (index rebuild's
    // database write) still runs before the trailing token is caught and the result discarded —
    // that trade-off is accepted, not overlooked, because the index is disposable and rebuildable
    // (design.md D4): an errant write from an ultimately-refused command is harmless and the next
    // correct invocation simply redoes it. What matters, and what this test proves, is that the
    // caller-visible outcome is unconditionally a refusal with a non-zero exit — never a Success
    // a handler happened to return.
    [Fact]
    public void IndexRebuild_WithTrailingToken_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["index", "rebuild", "extra"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Characterisation test for obligation O-3 (DEVLOG §3 remediation): pins today's accepted
    // trade-off — RunIndexRebuild's database write already ran by the time the trailing token is
    // caught, because EnforceNoUnconsumedArguments runs after Dispatch returns, not before a leaf
    // handler runs. Accepted here because the index is disposable (design.md D4); it is NOT
    // acceptable for the first CLI verb whose side effect writes the primary record. The section
    // that discharges O-3 must invert this test — assert the database was NOT written when the
    // command refuses — as proof the parse/execute split now runs before the handler's side
    // effect, not after it.
    [Fact]
    public void IndexRebuild_WithTrailingToken_RefusesButHasAlreadyWrittenTheIndex()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["index", "rebuild", "extra"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.True(File.Exists(IndexPaths.DatabasePath(repo.Path)));
    }

    [Fact]
    public void IndexRebuild_OutsideAnyGitRepository_Refuses()
    {
        using var directory = new TempDirectory();
        var output = new StringWriter();

        var exitCode = RunInRepo(["index", "rebuild"], output, directory.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void IndexRebuild_ExitsZeroOnSuccessAndNonZeroOnRefusal()
    {
        using var repo = new TempGitRepo();

        var successExitCode = RunInRepo(["index", "rebuild"], TextWriter.Null, repo.Path);
        var refusalExitCode = RunInRepo(["index", "rebuild", "extra"], TextWriter.Null, repo.Path);

        Assert.Equal(0, successExitCode);
        Assert.NotEqual(0, refusalExitCode);
    }

    [Fact]
    public void IndexRebuild_EmitsExactlyOneJsonLineOnStdout()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        RunInRepo(["index", "rebuild"], output, repo.Path);

        var trimmed = output.ToString().TrimEnd('\r', '\n');
        Assert.DoesNotContain('\n', trimmed);
    }

    // A SQLite I/O failure must surface as a tool failure, never a refusal (§3: "opposite
    // instructions to the caller"). Forcing Directory.CreateDirectory to fail by putting a plain
    // file where the index's containing directory needs to be reproduces it without touching
    // IndexPopulator directly.
    [Fact]
    public void IndexRebuild_OnSqliteIoFailure_IsAToolFailureNotARefusal()
    {
        using var repo = new TempGitRepo();
        var indexParentDirectory = Path.Combine(repo.Path, "callboard");
        Directory.CreateDirectory(indexParentDirectory);
        File.WriteAllText(Path.Combine(indexParentDirectory, ".index"), "blocking the index directory");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(["index", "rebuild"], output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path);

        Assert.Equal(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("tool-failure", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.ToString()));
    }

    private static int Run(string[] args, TextWriter output) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: ".");

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory);

    private sealed class ThrowsOnFirstWriteLine : TextWriter
    {
        private readonly StringBuilder _written = new();
        private bool _hasThrown;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("simulated output failure");
            }

            _written.AppendLine(value);
        }

        public override string ToString() => _written.ToString();
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-test-{Guid.NewGuid():N}");

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
        private readonly TempDirectory _directory = new();

        internal string Path => _directory.Path;

        internal TempGitRepo() => Directory.CreateDirectory(System.IO.Path.Combine(_directory.Path, ".git"));

        public void Dispose() => _directory.Dispose();
    }
}
