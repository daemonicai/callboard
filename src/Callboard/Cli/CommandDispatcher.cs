using System.Text.Json;
using Callboard.Cards;
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
/// <para>
/// <b>Parse, then execute (obligation O-3, DEVLOG §5).</b> <see cref="CommandParser.Parse"/> is the
/// only place argv tokens are read: it walks the command tree, consumes what each verb recognises
/// from the shared <see cref="ArgumentCursor"/>, and returns a <see cref="ParseResult"/> — either a
/// <see cref="ParseResult.Refused"/> the parse phase already decided, or a
/// <see cref="ParseResult.Ready"/> wrapping a <see cref="ParsedCommand"/>: an <em>inert</em> record
/// describing what was asked for, holding whatever values the verb needs. <see cref="Run"/> checks
/// <see cref="ArgumentCursor.HasUnconsumedTokens"/> against the parse result — via
/// <see cref="EnforceNoUnconsumedArguments"/> — and only once that has passed does it exhaustively
/// match the resulting <see cref="ParsedCommand"/> to call the one handler function that performs
/// its side effect.
/// </para>
/// <para>
/// <b>What is actually guaranteed, stated precisely (DEVLOG §5, third review round).</b> The
/// handler functions (<see cref="RunVersion"/>, <see cref="RunIndexRebuild"/>) are <c>private</c>
/// members of <em>this</em> class, while <see cref="CommandParser"/> — which builds every
/// <see cref="ParsedCommand"/> — is a separate top-level class with no access to them: calling
/// <c>RunIndexRebuild(...)</c> from inside <c>CommandParser</c> is <c>CS0122</c>
/// ("inaccessible due to its protection level"), the same grade of guarantee that closed obligation
/// O-1. That rules out a parse arm calling a handler and discarding or stashing the result, because
/// a parse arm cannot name a handler at all — for an ordinary call, at compile time. It does
/// <em>not</em> rule out a handler running early by three other routes, none introduced by this
/// class and none closed by it: (1) code added inside <em>this</em> class — where the handlers
/// live — calling <c>RunIndexRebuild(...)</c> from somewhere other than <see cref="Run"/>'s
/// dispatch match, which would still compile; (2) reflection — <c>private</c> is a compile-time
/// access modifier, not a runtime one, so <c>typeof(CommandDispatcher).GetMethod("RunIndexRebuild",
/// BindingFlags.NonPublic | BindingFlags.Static)</c> compiles and invokes the handler directly,
/// same as the codebase already concedes for <see cref="Cards.BlockCardFields"/>'s private backing
/// fields — none of these routes are reachable from this codebase's own call sites, which is the
/// guarantee actually worth having here; and (3) recursion through <see cref="Run"/> itself — it
/// must stay <c>internal</c> because <c>Program.cs</c> and the test project both call it, so a
/// parse arm could in principle call <c>Run</c> again with a self-constructed argv array and
/// compile clean, a route that predates this section entirely and is unrelated to the parse/handler
/// split. The property this section can actually claim is narrower than "no handler ever runs
/// early" on every one of these axes: it is "an ordinary compile-time call from the parse phase
/// cannot reach a handler", because the parse phase and the handlers are now different types and
/// only the handlers' own class can name them by an ordinary call — reflection and recursive
/// <see cref="Run"/> remain open regardless. <see cref="RunIndexRebuild"/> also still takes only the
/// already-extracted <see langword="string"/> working directory, never <see cref="CommandContext"/>
/// or <see cref="ArgumentCursor"/>, so it specifically cannot observe an unparsed cursor — that part
/// is <c>CS0103</c> and unrelated to which class anything lives in.
/// </para>
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
    /// <see cref="CommandParser.Parse"/> never has to change shape to hand a new command whichever
    /// of these it needs. Only members an already-briefed need has asked for belong here — this is
    /// not a place to speculate ahead of a section. There is deliberately no output/error writer
    /// here: a handler's output is its <see cref="ICommandResult"/>, and <see cref="Run"/> is the
    /// only place permitted to write to stdout or stderr — handing every handler those writers
    /// would turn "exactly one JSON line on stdout" from something only the dispatcher can enforce
    /// into something every future handler must individually refrain from breaking (§3 obligation
    /// 2: enforced structurally by a banned-API analyzer forbidding <c>System.Console</c>
    /// everywhere but <c>Program.cs</c>, not by this comment).
    /// <para>
    /// <see cref="CommandContext"/> — and the <see cref="ArgumentCursor"/> it carries — belongs to
    /// the <em>parse</em> phase only (O-3, DEVLOG §5). A <see cref="ParsedCommand"/> never carries
    /// this type; see the class doc comment.
    /// </para>
    /// </summary>
    internal sealed record CommandContext(
        ArgumentCursor Arguments,
        TextReader Input,
        bool IsInputRedirected,
        string WorkingDirectory,
        Func<DateTimeOffset> Clock);

    /// <summary>
    /// Closed union over what the parse phase produces for one command: either it has already
    /// decided to refuse — an unknown command, a missing or unknown subcommand, or, once the funnel
    /// applies it in <see cref="EnforceNoUnconsumedArguments"/>, an unrecognised trailing token —
    /// or it is <see cref="Ready"/>, wrapping a <see cref="ParsedCommand"/> that still hasn't been
    /// dispatched to a handler. Private constructor and an abstract <see cref="Match{TResult}"/>,
    /// same shape as <see cref="CommandOutcome"/>, so a third case is a compile error everywhere
    /// this is consumed rather than a silently-ignored default. Declared <see langword="internal"/>
    /// (not <see langword="private"/>) so <see cref="CommandParser"/>, a separate top-level class,
    /// can construct and return it — the split that keeps the handler functions unreachable from
    /// parsing needs this type shared, unlike the handlers themselves, which stay <c>private</c> to
    /// this class.
    /// </summary>
    internal abstract record ParseResult
    {
        private ParseResult()
        {
        }

        internal abstract TResult Match<TResult>(
            Func<Ready, TResult> onReady,
            Func<Refused, TResult> onRefused);

        internal sealed record Ready(ParsedCommand Command) : ParseResult
        {
            internal override TResult Match<TResult>(Func<Ready, TResult> onReady, Func<Refused, TResult> onRefused) =>
                onReady(this);
        }

        internal sealed record Refused(CommandOutcome.Refusal Refusal) : ParseResult
        {
            internal override TResult Match<TResult>(Func<Ready, TResult> onReady, Func<Refused, TResult> onRefused) =>
                onRefused(this);
        }
    }

    /// <summary>
    /// Closed union over the commands the parse phase can hand to <see cref="Run"/> for dispatch —
    /// one case per verb, each carrying only the already-extracted values that verb's handler
    /// needs and nothing else. No case references a handler function, stores a delegate, or
    /// otherwise carries a <see cref="CommandOutcome"/>. <see cref="Run"/> is the only place these
    /// are matched to a handler, and only after <see cref="EnforceNoUnconsumedArguments"/> has
    /// passed. Declared <see langword="internal"/>, same reason as <see cref="ParseResult"/>:
    /// <see cref="CommandParser"/> builds these values and needs to see the type to do so.
    /// </summary>
    internal abstract record ParsedCommand
    {
        private ParsedCommand()
        {
        }

        internal abstract TResult Match<TResult>(
            Func<Version, TResult> onVersion,
            Func<IndexRebuild, TResult> onIndexRebuild,
            Func<BlockTransition, TResult> onBlockTransition,
            Func<BlockGate, TResult> onBlockGate,
            Func<BlockAddBlocker, TResult> onBlockAddBlocker,
            Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker,
            Func<SectionVerdict, TResult> onSectionVerdict,
            Func<SectionClose, TResult> onSectionClose,
            Func<SectionStatus, TResult> onSectionStatus);

        internal sealed record Version : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onVersion(this);
        }

        internal sealed record IndexRebuild(string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onIndexRebuild(this);
        }

        /// <param name="FilePath">The card file to transition — a path, not a symbolic id: no
        /// section before §5 built an id-to-path lookup that does not depend on the (non-
        /// authoritative, possibly-absent) derived index, and inventing one is out of this
        /// block's scope. <see cref="Callboard.Cards.CardStore"/>'s own write surface is already
        /// path-addressed throughout (<c>WriteCard</c>, <c>AppendComment</c>,
        /// <c>TransferOwnership</c>), so this stays consistent with it rather than being the one
        /// verb that resolves identities.</param>
        /// <param name="TransitionName">The wire name of the edge to apply
        /// (<see cref="Callboard.Cards.BlockFlowTransition.Name"/>) — not yet validated against the
        /// card's actual current state, since that needs the file read the execute phase performs.</param>
        /// <param name="ActingRole">Parsed and validated during the parse phase (a
        /// <see cref="Callboard.CardOwner"/> wire value needs no file access to check) —
        /// recorded, not authorised: §5 records who declares they are acting, restricting who may
        /// is 8.13/9.4's job.</param>
        /// <param name="BaseCommit">The <c>--base</c> flag's value, or <see langword="null"/> if not
        /// supplied. Legality (required only for a transition landing on <c>briefed</c>, and
        /// immutable once recorded) depends on the card's current state, so it is checked in the
        /// execute phase, not here.</param>
        internal sealed record BlockTransition(
            string FilePath, string TransitionName, CardOwner ActingRole, string? BaseCommit, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onBlockTransition(this);
        }

        /// <param name="Label">The gate's label (e.g. <c>build</c>) — validated during parse
        /// (<see cref="GateResult.IsValidLabel"/> needs no file access) since it is decidable from
        /// argv alone, the same O-3 discipline <see cref="BlockTransition.ActingRole"/>
        /// follows.</param>
        /// <param name="ExitCode">The exit code the gate actually returned, parsed during parse —
        /// "is this text a valid integer" needs no file access either.</param>
        internal sealed record BlockGate(
            string FilePath, string Label, int ExitCode, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onBlockGate(this);
        }

        /// <param name="BlockingCardId">The id of the card this block is now blocked by. Not
        /// resolved to an actual card during parse or execute — see the block D DEVLOG brief:
        /// nothing in this section builds an id-to-card lookup, so this stays a plain string, the
        /// same way <see cref="BlockTransition.FilePath"/> stays a path rather than a resolved
        /// identity.</param>
        internal sealed record BlockAddBlocker(
            string FilePath, string BlockingCardId, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onBlockAddBlocker(this);
        }

        internal sealed record BlockRemoveBlocker(
            string FilePath, string BlockingCardId, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onBlockRemoveBlocker(this);
        }

        /// <param name="RangeFrom">The commit range's start the supervisor is recording a verdict
        /// against — data on the entity, never re-derived from git (§5 block E brief).</param>
        /// <param name="RangeTo">The commit range's end.</param>
        internal sealed record SectionVerdict(
            string FilePath, Callboard.Cards.SectionVerdict Verdict, string RangeFrom, string RangeTo, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onSectionVerdict(this);
        }

        internal sealed record SectionClose(
            string FilePath, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onSectionClose(this);
        }

        /// <summary>
        /// <c>section status</c>: read-only, work-lifecycle's "the system answers from the section
        /// entity without requiring its cards to be read" scenario (§5 block E) — carries only the
        /// card file path, the same "a path, not a symbolic id" convention every other verb here
        /// follows.
        /// </summary>
        internal sealed record SectionStatus(string FilePath) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionStatus, TResult> onSectionStatus) =>
                onSectionStatus(this);
        }
    }

    /// <summary>
    /// The lock timeout every CLI-invoked card write uses by default (§5 block C) — 5 seconds,
    /// unless <see cref="Run"/>'s own <c>lockTimeout</c> parameter overrides it. That parameter
    /// exists for exactly one reason: a CLI-level test proving the <c>tool-failure</c> disposition
    /// on a genuine lock timeout needs a seam short enough to run in milliseconds, the same reason
    /// <see cref="Run"/> already takes an overridable <c>clock</c> rather than reading
    /// <see cref="DateTimeOffset.UtcNow"/> directly (reviewer finding, second remediation round:
    /// the domain-level tool-failure tests proved <c>CardStore</c> constructs the right union case,
    /// not that the CLI routes it correctly — a real, short-timeout run through <see cref="Run"/>
    /// is what closes that gap, not another domain-level test).
    /// </summary>
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    internal static int Run(
        string[] args,
        TextWriter output,
        TextReader input,
        TextWriter error,
        bool isInputRedirected,
        string workingDirectory,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? lockTimeout = null)
    {
        var command = args.Length > 0 ? args[0] : string.Empty;
        var remainingArgs = args.Length > 0 ? args[1..] : Array.Empty<string>();
        var arguments = new ArgumentCursor(remainingArgs);
        var resolvedClock = clock ?? (static () => DateTimeOffset.UtcNow);
        var resolvedLockTimeout = lockTimeout ?? DefaultLockTimeout;

        try
        {
            var context = new CommandContext(arguments, input, isInputRedirected, workingDirectory, resolvedClock);
            var parseResult = EnforceNoUnconsumedArguments(CommandParser.Parse(command, context), arguments);
            var outcome = parseResult.Match(
                onReady: ready => ready.Command.Match(
                    onVersion: static _ => RunVersion(),
                    onIndexRebuild: parsed => RunIndexRebuild(parsed.WorkingDirectory),
                    onBlockTransition: parsed => RunBlockTransition(parsed, resolvedLockTimeout),
                    onBlockGate: parsed => RunBlockGate(parsed, resolvedLockTimeout),
                    onBlockAddBlocker: parsed => RunBlockAddBlocker(parsed, resolvedLockTimeout),
                    onBlockRemoveBlocker: parsed => RunBlockRemoveBlocker(parsed, resolvedLockTimeout),
                    onSectionVerdict: parsed => RunSectionVerdict(parsed, resolvedLockTimeout),
                    onSectionClose: parsed => RunSectionClose(parsed, resolvedLockTimeout),
                    onSectionStatus: static parsed => RunSectionStatus(parsed)),
                onRefused: refused => refused.Refusal);

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
    /// caller passed. Read <em>after</em> <see cref="CommandParser.Parse"/> (and, on the failure
    /// path, after whatever ran before an exception escaped) so a two-token verb like
    /// <c>index rebuild</c> reports both tokens, while an unrecognised subcommand — never taken
    /// from the cursor, because <see cref="CommandParser.ParseIndex"/> only takes what it matches —
    /// reports just <c>index</c>. A machine caller has to be able to tell which command produced an
    /// envelope; <c>args[0]</c> alone stopped being enough to say that the moment this section
    /// introduced a two-token verb.
    /// </summary>
    private static string RecognisedCommand(string command, ArgumentCursor arguments) =>
        arguments.ConsumedTokens.Count == 0
            ? command
            : $"{command} {string.Join(' ', arguments.ConsumedTokens)}";

    /// <summary>
    /// The argument-boundary enforcement point (§3 obligation 3, carried from §1, restructured for
    /// O-3 in §5 so it runs on the <em>parse</em> result and gates whether <see cref="Run"/> ever
    /// dispatches the wrapped <see cref="ParsedCommand"/> to a handler, rather than on an outcome a
    /// handler already produced). This is the <em>only</em> place unconsumed tokens are checked,
    /// and every command funnels through it — <see cref="Run"/> calls it once, on whatever
    /// <see cref="CommandParser.Parse"/> returned, using the same <see cref="ArgumentCursor"/>
    /// every parse arm drew from, and only after this returns does <see cref="Run"/> ever dispatch
    /// a <see cref="ParseResult.Ready"/>'s command to a handler. A parse arm has no way to opt out:
    /// there is no wrapper to remember to call, and nothing a parse arm returns can make this
    /// method skip the check. Only overrides a <see cref="ParseResult.Ready"/> — a verb whose parse
    /// arm already refused (unknown command, missing or unknown subcommand) keeps its own, more
    /// specific reason: an unknown command should read "no such command", not "unrecognised
    /// argument", even when a trailing token happens to be present too. Uses
    /// <see cref="ParseResult.Match{TResult}"/>, not a type test, so this stays exhaustive over the
    /// closed union.
    /// </summary>
    private static ParseResult EnforceNoUnconsumedArguments(ParseResult result, ArgumentCursor arguments) =>
        !arguments.HasUnconsumedTokens
            ? result
            : result.Match(
                onReady: _ => new ParseResult.Refused(new CommandOutcome.Refusal(
                    "unrecognised-argument",
                    $"unrecognised: '{arguments.FirstUnconsumed}'.")),
                onRefused: refused => refused);

    /// <summary>
    /// Establishes the argument-boundary convention every later verb follows: a command declares
    /// what it accepts by how much it takes from the <see cref="ArgumentCursor"/> during
    /// <see cref="CommandParser.Parse"/>, before <see cref="EnforceNoUnconsumedArguments"/> checks
    /// what remains. <c>version</c> takes nothing, so its handler — dispatched from
    /// <see cref="Run"/>'s match over <see cref="ParsedCommand"/> — contains no argument check at
    /// all. <see langword="private"/>: <see cref="CommandParser"/> cannot name this method (see the
    /// class doc comment), which is the point.
    /// </summary>
    private static CommandOutcome RunVersion() =>
        new CommandOutcome.Success(new VersionResult { Version = CurrentVersion });

    /// <summary>
    /// The first real verb after <c>version</c>: rebuilds the derived index from the primary
    /// record alone via <see cref="IndexPopulator.Populate"/>. Takes no <see cref="Cards.CardLock"/>
    /// — design.md D4 / ADR-0004: the index is never authoritative and never a lock, so nothing
    /// else may be made to wait on it. A card that fails to parse is reported in a successful
    /// result's <see cref="IndexRebuildResult.Failures"/>, never a refusal — record-retrieval's
    /// degraded-mode requirement, that a corrupt card must not stop the loop. A SQLite I/O failure
    /// while writing the index is not caught here either: it propagates to <see cref="Run"/>'s own
    /// <see langword="catch"/> and becomes a tool failure, because the board isn't refusing —
    /// enforcement is merely unavailable. Called only from <see cref="Run"/>'s dispatch match over
    /// the <see cref="ParsedCommand.IndexRebuild"/> <see cref="CommandParser.ParseIndexRebuild"/>
    /// builds, and only after <see cref="EnforceNoUnconsumedArguments"/> has already confirmed no
    /// trailing token remains (O-3): this write can no longer happen and then be refused away.
    /// <see langword="private"/>: <see cref="CommandParser"/> cannot name this method (see the class
    /// doc comment) — calling it from a parse arm is <c>CS0122</c>, not merely discouraged.
    /// </summary>
    private static CommandOutcome RunIndexRebuild(string workingDirectory)
    {
        var repoRoot = RepoRootResolver.Resolve(workingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{workingDirectory}'; run callboard from inside the repository.");
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
            IdentityCounterViolations = [.. population.IdentityCounterViolations.Select(static violation => new IndexRebuildIdentityCounterViolation
            {
                Kind = violation.Kind.ToWireString(),
                CounterValue = violation.CounterValue,
                ObservedMaxId = violation.ObservedMaxId,
                Reason = violation.Reason,
            })],
        });
    }

    /// <summary>
    /// The first verb whose side effect writes a card (§5 block C, O-3's card-writing trigger).
    /// Applies one block flow transition under the card's lock via
    /// <see cref="CardStore.ApplyBlockTransition"/> and maps its closed-union outcome to a
    /// <see cref="CommandOutcome"/> — an undefined transition, a missing or immutable
    /// <c>base</c>, and a target that isn't a block card each get their own refusal code, read
    /// from <see cref="BlockFlowTransitions.AvailableFrom"/> for the undefined-transition case
    /// rather than a second hand-maintained list of the same edges. <see langword="private"/>:
    /// <see cref="CommandParser"/> cannot name this method (see the class doc comment).
    /// </summary>
    private static CommandOutcome RunBlockTransition(ParsedCommand.BlockTransition parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outcome = CardStore.ApplyBlockTransition(
            repoRoot, parsed.FilePath, parsed.TransitionName, parsed.ActingRole, parsed.Timestamp, parsed.BaseCommit, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onApplied: applied => new CommandOutcome.Success(new BlockTransitionResult
            {
                FilePath = parsed.FilePath,
                Transition = applied.Transition.Name,
                From = applied.Transition.From.ToWireString(),
                To = applied.Transition.To.ToWireString(),
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
                Base = applied.Card.BlockFields.Base,
                Round = applied.Card.BlockFields.Round,
            }),
            onUndefinedTransition: undefined => new CommandOutcome.Refusal(
                "undefined-transition",
                $"no transition '{parsed.TransitionName}' from '{undefined.CurrentState.ToWireString()}'. " +
                $"Available: {(undefined.Available.Count == 0 ? "none" : string.Join(", ", undefined.Available.Select(static t => t.Name)))}."),
            onBaseNotRecorded: static _ => new CommandOutcome.Refusal(
                "base-not-recorded",
                "a brief must name the commit it was carved against — pass --base or record one before briefing."),
            onBaseImmutable: immutable => new CommandOutcome.Refusal(
                "base-immutable",
                $"'base' is already recorded as '{immutable.Recorded}' and cannot change across rounds; supplied '{immutable.Attempted}'."),
            onNotABlockCard: notABlock => WrongCardKind(parsed.FilePath, CardKind.Block, notABlock.Kind, "flow transitions only apply to a block card"),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to transition."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            // Neither refusal-shaped (reviewer finding, first remediation round): a corrupt card
            // or a broken write is not the caller being wrong, it is enforcement being
            // unavailable — the same disposition index rebuild's own SQLite I/O failures reach by
            // simply not being caught anywhere between the write and Run's own catch. Throwing
            // here, rather than returning a Refusal, is what routes this to the same tool-failure
            // exit (ToolFailureExitCode) through that same catch, instead of a mapping at this
            // call site silently re-collapsing the two dispositions the type above went to the
            // trouble of keeping apart.
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a block card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>block gate</c> (§5 block D): records one gate's exit code via <see cref="CardStore.
    /// RecordGateResult"/> and maps its closed-union outcome to a <see cref="CommandOutcome"/> —
    /// same three-way refusal/tool-failure/reported-failure split <see cref="RunBlockTransition"/>
    /// already applies, for the same reason. <see langword="private"/>: <see cref="CommandParser"/>
    /// cannot name this method (see the class doc comment).
    /// </summary>
    private static CommandOutcome RunBlockGate(ParsedCommand.BlockGate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outcome = CardStore.RecordGateResult(
            repoRoot, parsed.FilePath, parsed.Label, parsed.ExitCode, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new BlockGateResult
            {
                FilePath = parsed.FilePath,
                Label = recorded.Result.Label,
                ExitCode = recorded.Result.ExitCode,
                // Routed through GateStatus (§5 remediation, DEVLOG §5 finding N2) rather than
                // re-deriving "== 0" inline a second time — GateStatus.Passed is the one place
                // that collapse is named, per its own doc comment.
                Passed = recorded.Card.BlockFields.GateStatusOf(recorded.Result.Label).Passed,
                ActingRole = recorded.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onNotABlockCard: notABlock => WrongCardKind(parsed.FilePath, CardKind.Block, notABlock.Kind, "gate results only apply to a block card"),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record a gate result on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            // Neither refusal-shaped — same reasoning as RunBlockTransition's own mapping.
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a block card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>block add-blocker</c> (§5 block D): adds a blocking card id via <see cref="CardStore.
    /// AddBlockedBy"/>. Never touches the card's <c>status</c> — see <see cref="CardBlockedByOutcome"/>'s
    /// doc comment. <see langword="private"/>: <see cref="CommandParser"/> cannot name this
    /// method.
    /// </summary>
    private static CommandOutcome RunBlockAddBlocker(ParsedCommand.BlockAddBlocker parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outcome = CardStore.AddBlockedBy(repoRoot, parsed.FilePath, parsed.BlockingCardId, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapBlockedByOutcome(outcome, parsed.FilePath, parsed.Timestamp);
    }

    /// <summary>
    /// <c>block remove-blocker</c> (§5 block D): the counterpart of
    /// <see cref="RunBlockAddBlocker"/> — work-lifecycle's "clearing what blocked it requires no
    /// state restoration" is why this method, like <see cref="RunBlockAddBlocker"/>, never
    /// constructs a status-carrying write at all.
    /// </summary>
    private static CommandOutcome RunBlockRemoveBlocker(ParsedCommand.BlockRemoveBlocker parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outcome = CardStore.RemoveBlockedBy(repoRoot, parsed.FilePath, parsed.BlockingCardId, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapBlockedByOutcome(outcome, parsed.FilePath, parsed.Timestamp);
    }

    /// <summary>
    /// Shared by <see cref="RunBlockAddBlocker"/> and <see cref="RunBlockRemoveBlocker"/> — both
    /// verbs return <see cref="CardBlockedByOutcome"/> and map every case the same way, including
    /// the op-specific ones each can never itself produce (<see cref="RunBlockAddBlocker"/> never
    /// sees <see cref="CardBlockedByOutcome.NotBlockedBy"/>; <see cref="RunBlockRemoveBlocker"/>
    /// never sees <see cref="CardBlockedByOutcome.AlreadyBlockedBy"/>) — the exhaustive
    /// <see cref="CardBlockedByOutcome.Match{TResult}"/> still forces both handled here regardless
    /// of which verb is actually calling.
    /// </summary>
    private static CommandOutcome MapBlockedByOutcome(
        CardBlockedByOutcome outcome, string filePath, DateTimeOffset timestamp) =>
        outcome.Match<CommandOutcome>(
            onUpdated: updated => new CommandOutcome.Success(new BlockedByResult
            {
                FilePath = filePath,
                BlockedBy = [.. updated.Card.BlockFields.BlockedBy],
                Blocked = updated.Card.BlockFields.BlockedBy.Length > 0,
                ActingRole = updated.ActingRole.ToWireString(),
                Timestamp = timestamp,
            }),
            onAlreadyBlockedBy: already => new CommandOutcome.Refusal(
                "already-blocked-by",
                $"'{filePath}' is already recorded as blocked by '{already.BlockingCardId}'."),
            onNotBlockedBy: notBlockedBy => new CommandOutcome.Refusal(
                "not-blocked-by",
                $"'{filePath}' is not recorded as blocked by '{notBlockedBy.BlockingCardId}'."),
            onNotABlockCard: notABlock => WrongCardKind(filePath, CardKind.Block, notABlock.Kind, "blocked_by only applies to a block card"),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to update blocked_by on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a block card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));

    /// <summary>
    /// <c>section verdict</c> (§5 block E, work-lifecycle: "Sections are entities" — "the verdict,
    /// the range and the acting role are recorded against that section entity"): appends one
    /// supervisor verdict via <see cref="CardStore.RecordSectionVerdict"/>. Same three-way
    /// refusal/tool-failure/reported-failure split every §5 write verb already applies.
    /// <see langword="private"/>: <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunSectionVerdict(ParsedCommand.SectionVerdict parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outcome = CardStore.RecordSectionVerdict(
            repoRoot, parsed.FilePath, parsed.Verdict, parsed.RangeFrom, parsed.RangeTo, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new SectionVerdictResult
            {
                FilePath = parsed.FilePath,
                Verdict = recorded.Entry.Verdict.ToWireString(),
                RangeFrom = recorded.Entry.RangeFrom,
                RangeTo = recorded.Entry.RangeTo,
                ActingRole = recorded.Entry.By.ToWireString(),
                Timestamp = recorded.Entry.Timestamp,
            }),
            onNotASectionCard: notASection => WrongCardKind(parsed.FilePath, CardKind.Section, notASection.Kind, "verdicts only apply to a section card"),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record a verdict on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a section card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>section close</c> (§5 block E, work-lifecycle: "closing it SHALL record the acting role
    /// and the time"): closes the section via <see cref="CardStore.CloseSection"/>. Records who
    /// closed it and when only — the closing <em>conditions</em> (§9: open obligations, undeferred
    /// questions, unresolved threads) are not this handler's job, the same boundary
    /// <see cref="CardSectionCloseOutcome"/>'s own doc comment states.
    /// <see langword="private"/>: <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunSectionClose(ParsedCommand.SectionClose parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outcome = CardStore.CloseSection(repoRoot, parsed.FilePath, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onClosed: closed => new CommandOutcome.Success(new SectionCloseResult
            {
                FilePath = parsed.FilePath,
                ClosedBy = (closed.Card.SectionFields.ClosedBy ?? parsed.ActingRole).ToWireString(),
                ClosedAt = closed.Card.SectionFields.ClosedAt ?? parsed.Timestamp,
            }),
            onAlreadyClosed: already => new CommandOutcome.Refusal(
                "already-closed",
                $"'{already.FilePath}' is already closed."),
            onNotASectionCard: notASection => WrongCardKind(parsed.FilePath, CardKind.Section, notASection.Kind, "only a section card can be closed by this verb"),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to close."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a section card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>section status</c> (§5 block E, work-lifecycle: "the system answers from the section
    /// entity without requiring its cards to be read"): reads exactly one file —
    /// <paramref name="parsed"/>'s own <see cref="ParsedCommand.SectionStatus.FilePath"/> — via
    /// <see cref="CardStore.ReadCard"/> and answers from that card's own frontmatter alone. No
    /// directory listing, no <see cref="CardStore.ReadAllCards"/>, and no lookup of any other card
    /// this section may have raised: there is nothing in this method's body that could resolve
    /// "the cards this section raised" even if it wanted to — see <see cref="SectionCardFields"/>'s
    /// own doc comment for the structural argument this handler is the CLI-facing half of.
    /// <see langword="private"/>: <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunSectionStatus(ParsedCommand.SectionStatus parsed)
    {
        if (!File.Exists(parsed.FilePath))
        {
            return new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{parsed.FilePath}' to read a status from.");
        }

        var read = CardStore.ReadCard(parsed.FilePath);
        return read.Match<CommandOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;

                // Shares CardStore.IsSectionCard rather than re-implementing the eight-arm match
                // (§5 remediation, DEVLOG §5 finding N7) — the earlier inline copy was exactly the
                // "two doors, one predicate restated" shape 4.x's own remediation already fixed
                // elsewhere on this type.
                if (!CardStore.IsSectionCard(card))
                {
                    return WrongCardKind(parsed.FilePath, CardKind.Section, card.Frontmatter.Kind, "'section status' only reads a section card");
                }

                if (!SectionFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var status))
                {
                    throw new InvalidOperationException(
                        $"card '{parsed.FilePath}' has an unrecognised section status: '{card.Frontmatter.Status}'.");
                }

                return new CommandOutcome.Success(new SectionStatusResult
                {
                    FilePath = parsed.FilePath,
                    Status = status.ToWireString(),
                    Base = card.SectionFields.Base,
                    ClosedBy = card.SectionFields.ClosedBy?.ToWireString(),
                    ClosedAt = card.SectionFields.ClosedAt,
                    VerdictCount = card.SectionFields.Verdicts.Length,
                });
            },
            onFailure: failure => throw new InvalidOperationException(
                $"card '{parsed.FilePath}' could not be read as a section card: {failure.Reason}"));
    }

    /// <summary>
    /// The one refusal every "this verb only applies to a <c>block</c>/<c>section</c> card" site
    /// mints (§5 remediation, DEVLOG §5 finding N3) — collapsed from the two near-synonymous codes
    /// <c>not-a-block-card</c> and <c>not-a-section-card</c>, which differed only in <em>which</em>
    /// kind was expected, the same "differing only in which thing" shape the reviewer already
    /// rejected for a would-be <c>missing-role</c> code. One code naming both
    /// <paramref name="expected"/> and <paramref name="actual"/> says strictly more than either
    /// narrower code did and scales to every kind this codebase adds, rather than minting one more
    /// per kind. Six construction sites route through this: <see cref="RunBlockTransition"/>,
    /// <see cref="RunBlockGate"/>, <see cref="MapBlockedByOutcome"/> (shared by both
    /// <c>blocked_by</c> verbs), <see cref="RunSectionVerdict"/>, <see cref="RunSectionClose"/> and
    /// <see cref="RunSectionStatus"/>.
    /// </summary>
    private static CommandOutcome.Refusal WrongCardKind(string filePath, CardKind expected, CardKind actual, string verbDescription) =>
        new CommandOutcome.Refusal(
            "wrong-card-kind",
            $"'{filePath}' is a '{actual.ToWireString()}' card, not a '{expected.ToWireString()}' card; {verbDescription}.");

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
