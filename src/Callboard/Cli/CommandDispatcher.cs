using System.Text.Json;

namespace Callboard.Cli;

/// <summary>
/// Parses argv, runs the named command, and writes the one JSON envelope every command emits.
/// Every command is non-interactive: it reads only what <paramref name="input"/> gives it, up
/// front, and never prompts. <see cref="Run"/> takes explicit <see cref="TextWriter"/> and
/// <see cref="TextReader"/> so the whole path is testable without spawning a process.
/// </summary>
internal static class CommandDispatcher
{
    private const string CurrentVersion = "0.1.0";

    /// <summary>Exit code for <see cref="CommandOutcome.Success"/> (ADR-0001).</summary>
    internal const int SuccessExitCode = 0;

    /// <summary>
    /// Exit code for <see cref="CommandOutcome.Refusal"/>. Every refusal — an unrecognised
    /// command included — exits non-zero, so a refusal is observable from the exit code alone.
    /// </summary>
    internal const int RefusalExitCode = 1;

    internal static int Run(string[] args, TextWriter output, TextReader input)
    {
        var command = args.Length > 0 ? args[0] : string.Empty;

        var outcome = command switch
        {
            "version" => RunVersion(),
            _ => new CommandOutcome.Refusal(
                "unknown-command",
                $"no such command: '{command}'. Known commands: version."),
        };

        WriteEnvelope(output, command, outcome);

        return ExitCodeFor(outcome);
    }

    private static CommandOutcome RunVersion() =>
        new CommandOutcome.Success(new VersionResult { Version = CurrentVersion });

    private static int ExitCodeFor(CommandOutcome outcome) => outcome.Match(
        onSuccess: static _ => SuccessExitCode,
        onRefusal: static _ => RefusalExitCode);

    private static void WriteEnvelope(TextWriter output, string command, CommandOutcome outcome)
    {
        var envelope = outcome.Match(
            onSuccess: success => new CliEnvelope
            {
                Ok = true,
                Command = command,
                Result = success.Result.ToJsonElement(),
            },
            onRefusal: refusal => new CliEnvelope
            {
                Ok = false,
                Command = command,
                Refusal = new CliRefusal { Code = refusal.Code, Message = refusal.Message },
            });

        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.CliEnvelope));
    }
}
