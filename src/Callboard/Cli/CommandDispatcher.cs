using System.Text.Json;

namespace Callboard.Cli;

/// <summary>
/// Parses argv, runs the named command, and writes the one JSON envelope every command emits.
/// Every command is non-interactive: it reads only what the <see cref="CommandContext"/> gives
/// it, up front, and never prompts. <see cref="Run"/> takes explicit <see cref="TextWriter"/> and
/// <see cref="TextReader"/> arguments — plus a separate diagnostic <see cref="TextWriter"/> and
/// the caller's stdin-redirect state — so the whole path is testable without spawning a process
/// or a real console. Two invariants hold on every exit path, including the one where the tool
/// itself breaks: exactly one JSON line reaches stdout, and the process exits non-zero whenever
/// that line was not an unqualified success.
/// </summary>
internal static class CommandDispatcher
{
    private const string CurrentVersion = "0.1.0";

    /// <summary>Exit code for <see cref="CommandOutcome.Success"/> (ADR-0001).</summary>
    internal const int SuccessExitCode = 0;

    /// <summary>
    /// Exit code for <see cref="CommandOutcome.Refusal"/>. Every refusal — an unrecognised
    /// command or argument included — exits non-zero, so a refusal is observable from the exit
    /// code alone. A refusal means the board is working correctly and the caller must stop.
    /// </summary>
    internal const int RefusalExitCode = 1;

    /// <summary>
    /// Exit code when the tool itself fails before it can decide success or refusal — an
    /// escaping exception, for instance. This is deliberately distinct from
    /// <see cref="RefusalExitCode"/>: a refusal means the process is working correctly and the
    /// caller must stop, while a tool failure means enforcement is unavailable and
    /// record-retrieval requires the loop to proceed unenforced rather than blocked. Those are
    /// opposite instructions to the caller, so they cannot share a code — and because a failure
    /// here means the JSON envelope itself may not be trustworthy, the exit code is sometimes
    /// the only signal a caller has.
    /// </summary>
    internal const int ToolFailureExitCode = 2;

    /// <summary>
    /// Everything a command handler needs to execute: the argv tokens it hasn't been told are
    /// unrecognised yet, the envelope writer, the stdin reader, the diagnostic writer, and
    /// whether stdin is actually redirected. Bundled rather than passed as five loose parameters
    /// because every verb from §2 on needs some subset of these — a body-reading command reads
    /// <see cref="Input"/> after checking <see cref="IsInputRedirected"/> via
    /// <see cref="RequireStdinRedirected"/>, and <see cref="Dispatch"/> never has to change shape
    /// to hand a new command whichever of these it needs. Only members an already-briefed need
    /// has asked for belong here — this is not a place to speculate ahead of a section.
    /// </summary>
    internal sealed record CommandContext(
        string[] RemainingArgs,
        TextWriter Output,
        TextReader Input,
        TextWriter Error,
        bool IsInputRedirected);

    internal static int Run(
        string[] args,
        TextWriter output,
        TextReader input,
        TextWriter error,
        bool isInputRedirected)
    {
        var command = args.Length > 0 ? args[0] : string.Empty;

        try
        {
            var remainingArgs = args.Length > 0 ? args[1..] : Array.Empty<string>();
            var context = new CommandContext(remainingArgs, output, input, error, isInputRedirected);
            var outcome = Dispatch(command, context);

            WriteEnvelope(output, command, outcome);

            return ExitCodeFor(outcome);
        }
        catch (Exception ex)
        {
            WriteToolFailureEnvelope(output, command, ex);
            error.WriteLine(ex.ToString());

            return ToolFailureExitCode;
        }
    }

    /// <summary>
    /// The guard a body-reading command applies before calling
    /// <see cref="StdinBodyReader.ReadBody"/>: invoked with stdin left as a TTY, it must refuse
    /// rather than block on <c>ReadToEnd</c> waiting for an EOF that interactive use will never
    /// send — a command that waits on a human at a TTY is interactive, which ADR-0001 forbids
    /// for every command. <see cref="StdinBodyReader"/>'s <c>TextReader</c> signature stays
    /// reader-agnostic and testable, so this check has to live at the composition root instead —
    /// a handler calls it with <c>context.IsInputRedirected</c> from the <see cref="CommandContext"/>
    /// it already receives, so a new body-reading command reaches it without any change to
    /// <see cref="Dispatch"/>'s signature. No command in this section reads a body, so nothing
    /// calls this in production yet; it exists now so the first one that does has the guard
    /// ready rather than reinventing it.
    /// </summary>
    internal static CommandOutcome.Refusal? RequireStdinRedirected(bool isInputRedirected) =>
        isInputRedirected
            ? null
            : new CommandOutcome.Refusal(
                "stdin-not-redirected",
                "this command reads its body from stdin; redirect it (a pipe or `< file`) rather than running interactively.");

    private static CommandOutcome Dispatch(string command, CommandContext context) => command switch
    {
        "version" => RunVersion(context),
        _ => new CommandOutcome.Refusal(
            "unknown-command",
            $"no such command: '{command}'. Known commands: version."),
    };

    /// <summary>
    /// Establishes the argument-boundary convention every later verb follows: a command declares
    /// what it accepts, and any token it does not consume is a refusal rather than a silent
    /// no-op. <c>version</c> accepts nothing, so any remaining argument refuses. Like any other
    /// command handler it takes the <see cref="CommandContext"/>, not a bespoke parameter list.
    /// </summary>
    private static CommandOutcome RunVersion(CommandContext context) =>
        context.RemainingArgs.Length == 0
            ? new CommandOutcome.Success(new VersionResult { Version = CurrentVersion })
            : new CommandOutcome.Refusal(
                "unrecognised-argument",
                $"'version' accepts no arguments; unrecognised: '{context.RemainingArgs[0]}'.");

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

    /// <summary>
    /// The failure boundary (blocker 3, §1 remediation): an escaping exception is not a refusal
    /// — the board isn't saying no, enforcement simply broke — so it is never routed through
    /// <see cref="CommandOutcome.Refusal"/>. It still has to reach the caller as the one JSON
    /// line every invocation promises, so this builds the envelope directly. Full diagnostic
    /// detail goes to the companion error writer instead of stdout, so a machine caller can keep
    /// piping stdout straight to a parser even on this path.
    /// </summary>
    private static void WriteToolFailureEnvelope(TextWriter output, string command, Exception exception)
    {
        var envelope = new CliEnvelope
        {
            Ok = false,
            Command = command,
            Refusal = new CliRefusal
            {
                Code = "tool-failure",
                Message = $"callboard failed unexpectedly: {exception.Message}",
            },
        };

        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.CliEnvelope));
    }
}
