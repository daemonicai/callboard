using System.Reflection;
using System.Text;
using System.Text.Json;
using Callboard.Cli;

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
    // flag handling.
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

    // Blocker 2 (§1 remediation): the composition-root guard a body-reading command applies
    // before touching stdin. No §1 command reads a body, so this is exercised directly rather
    // than through the CLI end to end — its contract is what §2's first body-reading command
    // will rely on.
    [Fact]
    public void RequireStdinRedirected_WhenStdinIsNotRedirected_RefusesInsteadOfBlocking()
    {
        var refusal = CommandDispatcher.RequireStdinRedirected(isInputRedirected: false);

        Assert.NotNull(refusal);
        Assert.Equal("stdin-not-redirected", refusal!.Code);
    }

    [Fact]
    public void RequireStdinRedirected_WhenStdinIsRedirected_LetsTheCommandProceed()
    {
        var refusal = CommandDispatcher.RequireStdinRedirected(isInputRedirected: true);

        Assert.Null(refusal);
    }

    // Wiring check (§1 remediation follow-up): proves the stdin guard is reachable from a
    // command handler through the CommandContext it already receives, with no change to
    // Dispatch's signature required for a new body-reading command to reach it — not merely
    // that RequireStdinRedirected exists in isolation.
    [Fact]
    public void StdinGuard_IsReachableThroughCommandContext_WithoutChangingDispatchSignature()
    {
        var context = new CommandDispatcher.CommandContext(
            RemainingArgs: Array.Empty<string>(),
            Input: TextReader.Null,
            IsInputRedirected: false);

        var refusal = CommandDispatcher.RequireStdinRedirected(context.IsInputRedirected);

        Assert.NotNull(refusal);
        Assert.Equal("stdin-not-redirected", refusal!.Code);
    }

    // Blocker 3 (§1 remediation): an escaping exception still yields exactly one JSON envelope
    // on stdout, exits with the dedicated tool-failure code (not the refusal code), and puts
    // diagnostic detail on stderr rather than stdout.
    [Fact]
    public void UnexpectedException_StillEmitsExactlyOneJsonEnvelope_AndExitsWithToolFailureCode()
    {
        var output = new ThrowsOnFirstWriteLine();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(["version"], output, TextReader.Null, error, isInputRedirected: true);

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

    private static int Run(string[] args, TextWriter output) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true);

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
}
