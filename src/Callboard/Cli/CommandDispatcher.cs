using System.Text.Json;
using Callboard.Index;

namespace Callboard.Cli;

/// <summary>
/// Parses argv, runs the named command, and writes the one JSON envelope every command emits.
/// Every command is non-interactive: it reads only what the <see cref="CommandContext"/> gives
/// it, up front, and never prompts. <see cref="Run"/> takes explicit <see cref="TextWriter"/> and
/// <see cref="TextReader"/> arguments — plus a separate diagnostic <see cref="TextWriter"/>, the
/// caller's stdin-redirect state, and its working directory — so the whole path is testable
/// without spawning a process or a real console. Two invariants hold on every exit path, including
/// the one where the tool itself breaks: exactly one JSON line reaches stdout, and the process
/// exits non-zero whenever that line was not an unqualified success.
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
    /// the only signal a caller has. <c>index rebuild</c>'s SQLite I/O failures reach the caller
    /// this way too, by simply not being caught anywhere between the write and this method's own
    /// <see langword="catch"/> — a tool failure, not a refusal, because the board isn't saying no,
    /// the index is merely unavailable.
    /// </summary>
    internal const int ToolFailureExitCode = 2;

    /// <summary>
    /// Everything a command handler needs to execute: the argv tokens it hasn't consumed yet (via
    /// <see cref="ArgumentCursor"/>, never a raw array — see obligation 3 below), the stdin reader,
    /// whether stdin is actually redirected, and the working directory the process was invoked
    /// from (needed to resolve the real repository root — <see cref="RepoRootResolver"/> — for any
    /// command that touches the record or the derived index). Bundled rather than passed as loose
    /// parameters because every verb from §2 on needs some subset of these, and
    /// <see cref="Dispatch"/> never has to change shape to hand a new command whichever of these
    /// it needs. Only members an already-briefed need has asked for belong here — this is not a
    /// place to speculate ahead of a section. There is deliberately no output/error writer here:
    /// a handler's output is its <see cref="ICommandResult"/>, and <see cref="Run"/> is the only
    /// place permitted to write to stdout or stderr — handing every handler those writers would
    /// turn "exactly one JSON line on stdout" from something only the dispatcher can enforce into
    /// something every future handler must individually refrain from breaking (§3 obligation 2:
    /// enforced structurally by a banned-API analyzer forbidding <c>System.Console</c> everywhere
    /// but <c>Program.cs</c>, not by this comment).
    /// </summary>
    internal sealed record CommandContext(
        ArgumentCursor Arguments,
        TextReader Input,
        bool IsInputRedirected,
        string WorkingDirectory);

    internal static int Run(
        string[] args,
        TextWriter output,
        TextReader input,
        TextWriter error,
        bool isInputRedirected,
        string workingDirectory)
    {
        var command = args.Length > 0 ? args[0] : string.Empty;
        var remainingArgs = args.Length > 0 ? args[1..] : Array.Empty<string>();
        var arguments = new ArgumentCursor(remainingArgs);

        try
        {
            var context = new CommandContext(arguments, input, isInputRedirected, workingDirectory);
            var outcome = EnforceNoUnconsumedArguments(Dispatch(command, context), arguments);

            WriteEnvelope(output, RecognisedCommand(command, arguments), outcome);

            return ExitCodeFor(outcome);
        }
        catch (Exception ex)
        {
            WriteToolFailureEnvelope(output, RecognisedCommand(command, arguments), ex);
            error.WriteLine(ex.ToString());

            return ToolFailureExitCode;
        }
    }

    /// <summary>
    /// The envelope's <c>command</c> field: <paramref name="command"/> plus every token
    /// <paramref name="arguments"/> actually recognised, space-joined — never the raw argv a
    /// caller passed. Read <em>after</em> <see cref="Dispatch"/> (and, on the failure path, after
    /// whatever ran before an exception escaped) so a two-token verb like <c>index rebuild</c>
    /// reports both tokens, while an unrecognised subcommand — never taken from the cursor,
    /// because <see cref="RunIndex"/> only takes what it matches — reports just <c>index</c>. A
    /// machine caller has to be able to tell which command produced an envelope; <c>args[0]</c>
    /// alone stopped being enough to say that the moment this section introduced a two-token verb.
    /// </summary>
    private static string RecognisedCommand(string command, ArgumentCursor arguments) =>
        arguments.ConsumedTokens.Count == 0
            ? command
            : $"{command} {string.Join(' ', arguments.ConsumedTokens)}";

    private static CommandOutcome Dispatch(string command, CommandContext context) => command switch
    {
        "version" => RunVersion(),
        "index" => RunIndex(context),
        _ => new CommandOutcome.Refusal(
            "unknown-command",
            $"no such command: '{command}'. Known commands: version, index."),
    };

    /// <summary>
    /// The argument-boundary enforcement point (§3 obligation 3, carried from §1, restructured
    /// after the reviewer proved a per-arm wrapper is a bypassable convention, not a guarantee: a
    /// dispatch arm that skips the wrapper compiles and runs clean). This is the <em>only</em>
    /// place unconsumed tokens are checked, and every command funnels through it — <see cref="Run"/>
    /// calls it once, on whatever <see cref="Dispatch"/> returned, using the same
    /// <see cref="ArgumentCursor"/> every handler drew from. A dispatch arm has no way to opt out:
    /// there is no wrapper to remember to call, and nothing a handler returns can make this method
    /// skip the check. Only overrides a <see cref="CommandOutcome.Success"/> — a handler that
    /// ignored a token must not have its success stand. A <see cref="CommandOutcome.Refusal"/>
    /// passes through untouched: the handler already stopped the caller, and its own reason is
    /// always more specific than a generic unconsumed-token complaint (an unknown command should
    /// read "no such command", not "unrecognised argument", even when a trailing token happens to
    /// be present too). Uses <see cref="CommandOutcome.Match{TResult}"/>, not a type test, so this
    /// stays exhaustive over the closed union.
    /// </summary>
    private static CommandOutcome EnforceNoUnconsumedArguments(CommandOutcome outcome, ArgumentCursor arguments) =>
        !arguments.HasUnconsumedTokens
            ? outcome
            : outcome.Match(
                onSuccess: _ => new CommandOutcome.Refusal(
                    "unrecognised-argument",
                    $"unrecognised: '{arguments.FirstUnconsumed}'."),
                onRefusal: refusal => refusal);

    /// <summary>
    /// Establishes the argument-boundary convention every later verb follows: a command declares
    /// what it accepts by how much it takes from the <see cref="ArgumentCursor"/> before
    /// <see cref="EnforceNoUnconsumedArguments"/> checks what remains after <see cref="Dispatch"/>
    /// returns. <c>version</c> takes nothing, so its body contains no argument check at all.
    /// </summary>
    private static CommandOutcome RunVersion() =>
        new CommandOutcome.Success(new VersionResult { Version = CurrentVersion });

    /// <summary>
    /// <c>index</c>'s only job is routing to a subcommand — currently just <c>rebuild</c>. No
    /// subcommand, or one this dispatcher does not recognise, refuses and names what does exist.
    /// Peeks rather than taking: a token this method does not recognise is left in
    /// <see cref="CommandContext.Arguments"/> unconsumed, both so
    /// <see cref="CommandDispatcher.EnforceNoUnconsumedArguments"/> still sees it (not load-bearing
    /// here — the refusal already stands on its own) and, the reason it matters, so
    /// <see cref="RecognisedCommand"/> never reports a subcommand the dispatcher rejected as part
    /// of the recognised command name.
    /// </summary>
    private static CommandOutcome RunIndex(CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'index' requires a subcommand. Known subcommands: rebuild.");
            case "rebuild":
                context.Arguments.TryTake();
                return RunIndexRebuild(context);
            case var subcommand:
                return new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'index' subcommand: '{subcommand}'. Known subcommands: rebuild.");
        }
    }

    /// <summary>
    /// The first real verb after <c>version</c>: rebuilds the derived index from the primary
    /// record alone via <see cref="IndexPopulator.Populate"/>. Takes no <see cref="Cards.CardLock"/>
    /// — design.md D4 / ADR-0004: the index is never authoritative and never a lock, so nothing
    /// else may be made to wait on it. A card that fails to parse is reported in a successful
    /// result's <see cref="IndexRebuildResult.Failures"/>, never a refusal — record-retrieval's
    /// degraded-mode requirement, that a corrupt card must not stop the loop. A SQLite I/O failure
    /// while writing the index is not caught here either: it propagates to <see cref="Run"/>'s own
    /// <see langword="catch"/> and becomes a tool failure, because the board isn't refusing —
    /// enforcement is merely unavailable.
    /// </summary>
    private static CommandOutcome RunIndexRebuild(CommandContext context)
    {
        var repoRoot = RepoRootResolver.Resolve(context.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{context.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var databasePath = IndexPaths.DatabasePath(repoRoot);
        var population = IndexPopulator.Populate(repoRoot, databasePath);

        return new CommandOutcome.Success(new IndexRebuildResult
        {
            DatabasePath = databasePath,
            IndexedCardCount = population.IndexedCardCount,
            IndexedCommentCount = population.IndexedCommentCount,
            Failures = [.. population.Failures.Select(static failure => new IndexRebuildFailure
            {
                FilePath = failure.FilePath,
                Reason = failure.Reason,
            })],
        });
    }

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
