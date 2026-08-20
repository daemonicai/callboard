using System.Reflection;
using System.Text.Json;
using Callboard.Cli;

namespace Callboard.Tests;

public sealed class CommandDispatcherTests
{
    [Fact]
    public void Version_EmitsJsonEnvelopeWithOkResultShape()
    {
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(["version"], output, TextReader.Null);

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
        var exitCode = CommandDispatcher.Run(["version"], TextWriter.Null, TextReader.Null);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void UnknownCommand_RefusesWithNonZeroExitCode()
    {
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(["frobnicate"], output, TextReader.Null);

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
        var exitCode = CommandDispatcher.Run([], TextWriter.Null, TextReader.Null);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
    }

    [Fact]
    public void EveryEnvelope_IsExactlyOneLineOfJson()
    {
        var output = new StringWriter();

        CommandDispatcher.Run(["version"], output, TextReader.Null);

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
}
