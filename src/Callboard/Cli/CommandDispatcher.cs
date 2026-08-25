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
            Func<SectionAuthorise, TResult> onSectionAuthorise,
            Func<SectionStatus, TResult> onSectionStatus,
            Func<FindingRecord, TResult> onFindingRecord,
            Func<FindingStatus, TResult> onFindingStatus,
        Func<RuleCreate, TResult> onRuleCreate,
        Func<HazardCreate, TResult> onHazardCreate,
        Func<ObligationCreate, TResult> onObligationCreate,
        Func<DecisionCreate, TResult> onDecisionCreate,
        Func<SectionCreate, TResult> onSectionCreate,
        Func<QuestionCreate, TResult> onQuestionCreate,
        Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution,
            Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition);

        internal sealed record Version : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onVersion(this);
        }

        internal sealed record IndexRebuild(string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
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
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
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
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
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
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onBlockAddBlocker(this);
        }

        internal sealed record BlockRemoveBlocker(
            string FilePath, string BlockingCardId, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onBlockRemoveBlocker(this);
        }

        /// <summary>
        /// <c>block approve</c> (§8 block A, review-certification: "Approve is binary and certifies
        /// one state" / "Certification enumerates its claims"). The only door to
        /// <see cref="Cards.BlockFlowState.Approved"/> — <c>block transition ... approve</c> refuses
        /// outright during parse (Architect ruling, §8 block A brief item 1). <see cref="Id"/> is
        /// resolved through <see cref="Cards.CardIdentityResolver"/> at execute time, the same
        /// identity-addressing convention <c>rule promote</c> and <c>decision supersede</c> already
        /// established (§7's settled ruling: <c>--id</c> binds).
        /// </summary>
        /// <param name="ReviewedState">The exact state this approval certifies, including any
        /// uncommitted working-tree content it covers — caller-supplied text, verified against
        /// nothing (§8's standing fact: the tool does not shell out). Refused if empty or
        /// whitespace-only, checked during parse.</param>
        /// <param name="Claims">The claims this approval enumerates, checked non-empty per item
        /// during parse. May be empty only when <see cref="Limits"/> is not.</param>
        /// <param name="Limits">The limits this approval states — what the certification does not
        /// establish — checked non-empty per item during parse.</param>
        internal sealed record BlockApprove(
            string Id, CardOwner ActingRole, string ReviewedState, IReadOnlyList<string> Claims, IReadOnlyList<string> Limits, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onBlockApprove(this);
        }

        /// <param name="RangeFrom">The commit range's start the supervisor is recording a verdict
        /// against — data on the entity, never re-derived from git (§5 block E brief).</param>
        /// <param name="RangeTo">The commit range's end.</param>
        /// <param name="RecurringFindingCardIds">The <c>--finding-recurred</c> occurrences, in argv
        /// order (§8a block B) — each resolved through <see cref="Cards.CardIdentityResolver"/> at
        /// execute time, never here (identity resolution needs the record on disk).</param>
        /// <param name="NewFindings">One entry per <c>--finding-new &lt;manifest-file&gt;</c>
        /// occurrence, in argv order, already parsed (§8a block B revision — see <see cref="Cards.
        /// NewFindingCardManifest"/> for the manifest format and why one self-describing file per
        /// finding replaced an earlier positionally-zipped quartet).</param>
        internal sealed record SectionVerdict(
            string FilePath,
            Callboard.Cards.SectionVerdict Verdict,
            string RangeFrom,
            string RangeTo,
            CardOwner ActingRole,
            string? ChangeName,
            IReadOnlyList<string> RecurringFindingCardIds,
            IReadOnlyList<Callboard.Cards.NewFindingCardRequest> NewFindings,
            string WorkingDirectory,
            DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onSectionVerdict(this);
        }

        internal sealed record SectionClose(
            string FilePath, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onSectionClose(this);
        }

        /// <summary>
        /// <c>section authorise</c> (§8a block C, work-lifecycle: "Remediation beyond the second
        /// round requires recorded authorisation"): the one door to
        /// <see cref="Cards.SectionAuthorisationEntry"/> — <see cref="ActingRole"/> is checked
        /// against <see cref="Cards.CardOwner.ProductOwner"/> at execute time (needs no file
        /// access, but the check is still done by <see cref="Cards.CardStore.
        /// RecordSectionAuthorisation"/>, not here, the same "recorded, not authorised at parse"
        /// split every other role-checked write already follows).
        /// </summary>
        /// <param name="Reason">Why the bound was pushed further — a short argv flag, the same
        /// <c>--state &lt;text&gt;</c> precedent <c>block approve</c> set, not a stdin body.
        /// Checked non-empty during parse.</param>
        internal sealed record SectionAuthorise(
            string FilePath, string Reason, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onSectionAuthorise(this);
        }

        /// <summary>
        /// <c>section status</c>: read-only, work-lifecycle's "the system answers from the section
        /// entity without requiring its cards to be read" scenario (§5 block E) — carries the card
        /// file path, the same "a path, not a symbolic id" convention every other verb here follows,
        /// plus <see cref="WorkingDirectory"/> (§7 block B: this verb was one of the path-taking
        /// handlers that never carried it at all, so its own <c>FilePath</c> could only ever resolve
        /// against the real process CWD).
        /// </summary>
        internal sealed record SectionStatus(string FilePath, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onSectionStatus(this);
        }

        /// <summary>
        /// <c>finding record</c> (§6 block B, findings: "Clean findings are cards"): the first verb
        /// that creates a card, rather than reading or mutating one that already exists. Everything
        /// argv-decidable is decided during parse, the same O-3 discipline every other verb here
        /// follows — including <see cref="Body"/>, read from stdin during the parse phase (a
        /// read-only extraction, not the card-writing side effect O-3 guards): <see cref="RaiseRequest"/>'s
        /// own constructor already refuses a <see cref="Callboard.Cards.CardKind"/> other than
        /// <c>obligation</c>/<c>hazard</c>, and <see cref="Callboard.Cards.FindingExtent"/>'s and
        /// <see cref="Callboard.Cards.CardOwner"/>'s own wire-format checks need no file access
        /// either.
        /// </summary>
        /// <param name="FilePath">Where the finding card is written — a path, not a symbolic id, the
        /// same convention every other verb here follows (§6 block B brief: identity addressing is
        /// §7/§8's open decision).</param>
        /// <param name="RaiseRequest">What to raise the declared blind spot as, or
        /// <see langword="null"/> when <c>--blind-spot none</c> was declared. Never a pre-existing
        /// card id — the tool allocates the raised card's identity itself, at execute time.</param>
        internal sealed record FindingRecord(
            string FilePath,
            string Title,
            string Section,
            string ChangeName,
            Callboard.Cards.CardOwner ActingRole,
            string Body,
            string? Instrument,
            Callboard.Cards.FindingExtent Extent,
            string? VerifiedAt,
            Callboard.Cards.FindingBlindSpotRaiseRequest? RaiseRequest,
            Callboard.Cards.FindingDisposition Disposition,
            string WorkingDirectory,
            DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onFindingRecord(this);
        }

        /// <summary>
        /// <c>finding status</c> (§6 block C, findings: "Findings stale when their extent moves" /
        /// "Findings that argue rather than measure are dispositioned separately"): read-only, the
        /// same shape as <see cref="SectionStatus"/> — one card file path, nothing else — and for
        /// the same reason (§6 block C brief: "Do not repeat that here: 6.5 and 6.6 are both
        /// requirements about what the system says, so they need a surface that says it"). This is
        /// §6's own read verb, not §5's — §5 closed with no CLI query verb reading
        /// <c>GateStatus.Absent</c> back, and this block does not repeat that gap for
        /// <see cref="Callboard.Cards.FindingStalenessStatus"/>.
        /// </summary>
        internal sealed record FindingStatus(string FilePath, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onFindingStatus(this);
        }

        /// <summary>
        /// <c>rule create</c> (§7 block A, register: "Register kinds have a two-state lifecycle").
        /// <see cref="Scope"/> is caller-chosen (<c>--scope change|repository</c>) — the one register
        /// kind CardScopeRules.Validate accepts more than one scope for — and is still routed through
        /// that table at execute time rather than trusted here (O-3: only what is argv-decidable, the
        /// wire-format validity of the flag's own text, is checked during parse).
        /// </summary>
        internal sealed record RuleCreate(
            string FilePath, string Title, CardOwner ActingRole, CardScope Scope, string Body, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRuleCreate(this);
        }

        /// <summary>
        /// <c>hazard create</c> (§7 block A, register: "Hazards carry a verification condition").
        /// <see cref="Condition"/> and <see cref="Cadence"/> are both required — checked during parse
        /// (argv-decidable), the load-bearing refusal register's "the system refuses and states the
        /// condition it requires" scenario asks for. Scope is always <see cref="CardScope.Repository"/>,
        /// so there is no <c>--change</c> flag for this verb.
        /// </summary>
        internal sealed record HazardCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string Condition, string Cadence, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onHazardCreate(this);
        }

        /// <summary>
        /// <c>obligation create</c> (§7 block A/C). Scope is always <see cref="CardScope.Change"/>,
        /// so <c>--change</c> is required, the same way <c>section create</c>'s is.
        /// <see cref="OwedById"/> (§7 block C, register: "An obligation SHALL name the section
        /// expected to discharge it") is required at parse time — <c>CommandParser.
        /// ParseObligationCreate</c> refuses a missing <c>--owed-by</c> before this record can even
        /// be constructed, so every <see cref="ObligationCreate"/> reaching this handler already
        /// carries one.
        /// </summary>
        internal sealed record ObligationCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string ChangeName, string OwedById, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onObligationCreate(this);
        }

        /// <summary>
        /// <c>decision create</c> (§7 block A). Scope is always <see cref="CardScope.Capability"/>,
        /// which <see cref="Cards.CardLayout.DirectoryFor"/> resolves without a change name — so, unlike
        /// <see cref="ObligationCreate"/>/<see cref="SectionCreate"/>, there is no <c>--change</c> flag.
        /// </summary>
        internal sealed record DecisionCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onDecisionCreate(this);
        }

        /// <summary>
        /// <c>section create</c> (§7 block A, Product Owner ruling: "section create is in §7's
        /// scope"). Scope is always <see cref="CardScope.Change"/> — the same fixed scope
        /// <see cref="CardScopeRules.Validate"/> already gives <c>section</c> in 4.4's table.
        /// </summary>
        internal sealed record SectionCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onSectionCreate(this);
        }

        /// <summary>
        /// <c>question create</c> (§7 remediation, blocker 1: register defines <c>question</c> as
        /// repository-scoped, "continues to surface to the role that owes its answer", outliving
        /// the change that raised it — the shape a repository compaction proposal needs to be
        /// durable, attributed and routable to the Product Owner). <b>Creation only</b>, the same
        /// boundary block A drew for its five kinds: no answer verb, no defer verb, and no status
        /// transition beyond the one a brand-new card needs — §9 owns a question's own lifecycle
        /// (9.7's open-undeferred-question refusal, 9.9's answer-without-decision-reference refusal,
        /// 9.10's blocked-by-an-open-question refusal), and this case gives it something to refuse
        /// against without deciding any of that here. Scope is always <see cref="CardScope.
        /// Repository"/> (card-model: "<c>hazard</c> and <c>question</c> cards SHALL be
        /// repository-scoped") — no <c>--change</c>, the same shape <see cref="DecisionCreate"/>
        /// already has for its own always-fixed scope.
        ///
        /// <para>
        /// <b><see cref="OwedByRole"/> (§7 second remediation).</b> Card-model's <c>owner</c> is
        /// "the single role whose turn it is to act", and for a question that is the role who owes
        /// the answer — not <see cref="ActingRole"/>, the role who raised it. The two are usually
        /// different roles and this case carries both rather than assuming they match, the way the
        /// shared <see cref="ParseCardCreate"/> shape does for the four register kinds and
        /// <see cref="SectionCreate"/>, where owner-as-raiser is correct.
        /// </para>
        /// </summary>
        internal sealed record QuestionCreate(
            string FilePath, string Title, CardOwner ActingRole, CardOwner OwedByRole, string Body, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onQuestionCreate(this);
        }

        /// <summary>
        /// <c>rule|hazard|obligation|decision discharge</c> (§7 block A) — one case for all four
        /// register kinds, since <see cref="Cards.CardStore.DischargeRegisterCard"/>'s own behaviour
        /// does not depend on which of the four the target card actually is; <see cref="Kind"/> is
        /// carried only so <c>CommandParser</c>'s four subcommand names can share one build path
        /// without the CLI having to re-derive which verb was actually typed from the file alone.
        /// </summary>
        internal sealed record RegisterDischarge(
            CardKind Kind, string FilePath, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRegisterDischarge(this);
        }

        /// <summary>
        /// <c>decision supersede</c> (§7 block C, register: "A decision MAY name the decision it
        /// supersedes and the decision that supersedes it"). Both <see cref="SupersedingId"/> and
        /// <see cref="SupersededId"/> are card ids, not file paths — block B's resolver is what
        /// makes this the first §7 verb that identifies both its targets purely by identity,
        /// consistent with the Product Owner's identity-addressing ruling rather than a third shape
        /// bolted onto the file-path-positional convention every earlier verb uses. Both decisions
        /// already exist (created by an earlier <c>decision create</c>); this verb links them.
        /// </summary>
        internal sealed record DecisionSupersede(
            string SupersedingId, string SupersededId, CardOwner ActingRole, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onDecisionSupersede(this);
        }

        /// <summary>
        /// <c>change archive</c> (§7 block D, register: "the register lives above the change").
        /// <see cref="ChangeName"/> names the whole change directory being archived, not one card —
        /// see <see cref="CommandParser.ParseChangeArchive"/>'s own doc comment for why this is the
        /// one verb whose positional token is not a file path. <see cref="CompactFamilyId"/>/
        /// <see cref="CompactAbsorbedIds"/> are §7 block F's hook (register: "Compaction of
        /// change-scoped rules SHALL be performed by the architect at archive") — both
        /// <see langword="null"/> together for an archive that compacts nothing (block D's own
        /// change-with-nothing-to-compact case, still required to work), or both set together
        /// (<see cref="CommandParser.ParseChangeArchive"/> refuses one without the other).
        /// </summary>
        internal sealed record ChangeArchive(
            string ChangeName, CardOwner ActingRole, string? CompactFamilyId, IReadOnlyList<string>? CompactAbsorbedIds,
            string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onChangeArchive(this);
        }

        /// <summary>
        /// <c>rule promote</c> (§7 block E, register: "Promoting a change-scoped rule to repository
        /// scope SHALL move the same card, retaining its identity, text and thread"). <see cref="Id"/>
        /// is a card id, not a file path — the same identity-addressing convention
        /// <see cref="DecisionSupersede"/> already established for a §7 verb that names its target by
        /// what it <em>is</em> rather than where it currently happens to live, which matters
        /// specifically here because promotion is exactly the operation that changes where the card
        /// lives. Resolved through <see cref="Cards.CardIdentityResolver"/> at execute time, never a
        /// caller-supplied path.
        /// </summary>
        internal sealed record RulePromote(
            string Id, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRulePromote(this);
        }

        /// <summary>
        /// <c>rule author</c> (§7 block E, register: "Authoring a rule from findings SHALL create a
        /// new card and SHALL record which findings it was earned from"). A brand-new card — the same
        /// creation shape <see cref="RuleCreate"/> already has (a caller-chosen
        /// <see cref="Scope"/>, since <c>rule</c> is still the one kind with two legal scopes) — plus
        /// <see cref="EarnedFrom"/>, required and non-empty: a rule authored by this verb always names
        /// at least one finding it generalises, which is the entire distinction between this verb and
        /// plain <see cref="RuleCreate"/> (an architect stating a rule with no evidence chain behind
        /// it at all). Each id is resolved through <see cref="Cards.CardIdentityResolver"/> and
        /// confirmed to be a <c>finding</c> card at execute time — never written to, only read.
        /// </summary>
        internal sealed record RuleAuthor(
            string FilePath, string Title, CardOwner ActingRole, CardScope Scope, string Body, string? ChangeName, IReadOnlyList<string> EarnedFrom, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRuleAuthor(this);
        }

        /// <summary>
        /// <c>rule compact</c> (§7 block F, register: "The system SHALL support compacting several
        /// rules into a family rule stating what they share. A family rule SHALL record the rules
        /// it absorbs, and every absorbed rule SHALL remain retrievable"). <see cref="FamilyId"/>
        /// and every entry of <see cref="AbsorbedIds"/> are card ids, not file paths — the same
        /// identity-addressing convention <see cref="DecisionSupersede"/> already established: both
        /// name an already-existing rule (the family created earlier by <c>rule create</c>, the
        /// members raised or promoted earlier), and this verb links them. <see cref="ChangeName"/>
        /// is required — this block restricts compaction to change-scoped rules within one named
        /// change (register: repository-scoped compaction is proposed and decided by the Product
        /// Owner, block G's territory, not applied directly here).
        /// </summary>
        internal sealed record RuleCompact(
            string FamilyId, IReadOnlyList<string> AbsorbedIds, string ChangeName, CardOwner ActingRole, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRuleCompact(this);
        }

        /// <summary>
        /// <c>rule propose-compact</c> (§7 block G, 7.9, register: "Compaction of repository-scoped
        /// rules SHALL be proposed by an agent and decided by the Product Owner ... records the
        /// proposal with its candidate text, backing set and citation counts, and applies nothing
        /// until the Product Owner decides"). Carries no family id and writes to no existing rule —
        /// unlike <see cref="RuleCompact"/>, there is no family card yet, only a candidate: read
        /// from stdin the same way <see cref="RuleAuthor"/>'s body is, since it is new proposed text,
        /// not a reference to something already on disk. <see cref="BackingIds"/> reuses <see cref="
        /// RuleCompact.AbsorbedIds"/>'s comma-list convention and vocabulary (the rules the family
        /// would absorb, if this proposal is ever decided) — the Product Owner's own decision act,
        /// and any resulting write to the backing rules, is out of this block's scope (§7 block G
        /// brief item 5: "proposing and applying are different acts with different deciders").
        ///
        /// <para>
        /// <b>§7 remediation, blocker 1: this now creates one <c>question</c> card, owned by the
        /// Product Owner, recording the candidate text, the backing set and the citation counts —
        /// the durable record register's own "records the proposal" requires.</b> <see cref="
        /// ProposalFilePath"/> is that card's caller-supplied path, the same "the caller always
        /// names a new card's file" convention every other creation verb in this codebase follows
        /// (block A's five verbs, and <see cref="QuestionCreate"/> itself) — this is the one place
        /// that convention could have been broken (the tool inventing a path for a card the caller
        /// never asked to create directly) and deliberately was not.
        /// </para>
        /// </summary>
        internal sealed record RuleProposeCompact(
            string CandidateText, IReadOnlyList<string> BackingIds, string ProposalFilePath, CardOwner ActingRole, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRuleProposeCompact(this);
        }

        /// <summary>
        /// <c>rule promote-constitution</c> (§7 block G, 7.12, register: "The system SHALL hold
        /// repository-scoped rules and SHALL NOT write to the project's agent instruction file.
        /// Promoting a repository-scoped rule into that file SHALL remain a Product Owner act ...
        /// the system refuses and records the promotion as awaiting a Product Owner decision").
        /// <see cref="Id"/> is resolved through <see cref="CardIdentityResolver"/> at execute time
        /// (reviewer round 1 remediation) — see <see cref="CommandDispatcher.
        /// RunRulePromoteConstitution"/>'s own doc comment for why: the "records" half of the
        /// requirement needs the resolved card to append a durable comment to, so resolution is no
        /// longer skippable the way <see cref="Cards.CardStore.CompactRules"/>'s role check skips
        /// straight past it. The refusal itself remains unconditional for every role regardless.
        /// </summary>
        internal sealed record RulePromoteConstitution(string Id, CardOwner ActingRole, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onRulePromoteConstitution(this);
        }

        /// <summary>
        /// <c>nit raise</c> (§8 block B, review-certification: "A nit SHALL be raised as an
        /// addressed comment, not as a card"). Named by <c>--id</c> — the block card it is raised
        /// against, resolved through <see cref="Cards.CardIdentityResolver"/> at execute time.
        /// </summary>
        /// <param name="Id">The block card's id.</param>
        /// <param name="Sites">The sites the nit names, in the order given — recorded even though
        /// nothing in this block reads them back (Architect ruling, §8 block B brief item 2).</param>
        internal sealed record NitRaise(
            string Id, CardOwner ActingRole, bool Required, IReadOnlyList<string> Sites, string Body, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onNitRaise(this);
        }

        /// <summary>
        /// <c>nit disposition</c> (§8 block B, review-certification: "Nits carry a disposition").
        /// Named by <c>--id</c> — the nit's own comment id, resolved through
        /// <see cref="Cards.NitResolver"/> at execute time (a nit is a comment, not a card).
        /// </summary>
        /// <param name="Id">The nit's own id.</param>
        /// <param name="Body">The reason — load-bearing for <c>decline</c>, which becomes a
        /// decision card whose whole content is this text (review-certification).</param>
        /// <param name="RaiseRequest">What to raise the disposition as, for <c>defer</c>/
        /// <c>decline</c>; <see langword="null"/> for <c>fix-before-land</c>.</param>
        internal sealed record NitDisposition(
            string Id, CardOwner ActingRole, Callboard.Cards.NitDisposition Disposition, string Body, Callboard.Cards.NitDispositionRaiseRequest? RaiseRequest, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Match<TResult>(Func<Version, TResult> onVersion, Func<IndexRebuild, TResult> onIndexRebuild, Func<BlockTransition, TResult> onBlockTransition, Func<BlockGate, TResult> onBlockGate, Func<BlockAddBlocker, TResult> onBlockAddBlocker, Func<BlockRemoveBlocker, TResult> onBlockRemoveBlocker, Func<SectionVerdict, TResult> onSectionVerdict, Func<SectionClose, TResult> onSectionClose, Func<SectionAuthorise, TResult> onSectionAuthorise, Func<SectionStatus, TResult> onSectionStatus, Func<FindingRecord, TResult> onFindingRecord, Func<FindingStatus, TResult> onFindingStatus, Func<RuleCreate, TResult> onRuleCreate, Func<HazardCreate, TResult> onHazardCreate, Func<ObligationCreate, TResult> onObligationCreate, Func<DecisionCreate, TResult> onDecisionCreate, Func<SectionCreate, TResult> onSectionCreate, Func<QuestionCreate, TResult> onQuestionCreate, Func<RegisterDischarge, TResult> onRegisterDischarge, Func<DecisionSupersede, TResult> onDecisionSupersede, Func<ChangeArchive, TResult> onChangeArchive, Func<RulePromote, TResult> onRulePromote, Func<RuleAuthor, TResult> onRuleAuthor, Func<RuleCompact, TResult> onRuleCompact, Func<RuleProposeCompact, TResult> onRuleProposeCompact, Func<RulePromoteConstitution, TResult> onRulePromoteConstitution, Func<BlockApprove, TResult> onBlockApprove, Func<NitRaise, TResult> onNitRaise, Func<NitDisposition, TResult> onNitDisposition) =>
                onNitDisposition(this);
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
                    onBlockApprove: parsed => RunBlockApprove(parsed, resolvedLockTimeout),
                    onNitRaise: parsed => RunNitRaise(parsed, resolvedLockTimeout),
                    onNitDisposition: parsed => RunNitDisposition(parsed, resolvedLockTimeout),
                    onSectionVerdict: parsed => RunSectionVerdict(parsed, resolvedLockTimeout),
                    onSectionClose: parsed => RunSectionClose(parsed, resolvedLockTimeout),
                    onSectionAuthorise: parsed => RunSectionAuthorise(parsed, resolvedLockTimeout),
                    onSectionStatus: static parsed => RunSectionStatus(parsed),
                    onFindingRecord: parsed => RunFindingRecord(parsed, resolvedLockTimeout),
                    onFindingStatus: static parsed => RunFindingStatus(parsed),
                    onRuleCreate: parsed => RunRuleCreate(parsed, resolvedLockTimeout),
                    onHazardCreate: parsed => RunHazardCreate(parsed, resolvedLockTimeout),
                    onObligationCreate: parsed => RunObligationCreate(parsed, resolvedLockTimeout),
                    onDecisionCreate: parsed => RunDecisionCreate(parsed, resolvedLockTimeout),
                    onSectionCreate: parsed => RunSectionCreate(parsed, resolvedLockTimeout),
                    onQuestionCreate: parsed => RunQuestionCreate(parsed, resolvedLockTimeout),
                    onRegisterDischarge: parsed => RunRegisterDischarge(parsed, resolvedLockTimeout),
                    onDecisionSupersede: parsed => RunDecisionSupersede(parsed, resolvedLockTimeout),
                    onChangeArchive: parsed => RunChangeArchive(parsed, resolvedLockTimeout),
                    onRulePromote: parsed => RunRulePromote(parsed, resolvedLockTimeout),
                    onRuleAuthor: parsed => RunRuleAuthor(parsed, resolvedLockTimeout),
                    onRuleCompact: parsed => RunRuleCompact(parsed, resolvedLockTimeout),
                    onRuleProposeCompact: parsed => RunRuleProposeCompact(parsed, resolvedLockTimeout),
                    onRulePromoteConstitution: parsed => RunRulePromoteConstitution(parsed, resolvedLockTimeout)),
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
    /// from <see cref="BlockFlowTransitions.GenericallyInvocableFrom"/> for the undefined-transition
    /// case (§8a remediation — never <see cref="BlockFlowTransitions.AvailableFrom"/>, the wider
    /// table this method must not resolve one-door edges against) rather than a second
    /// hand-maintained list of the same edges. <see langword="private"/>:
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

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.ApplyBlockTransition(
            repoRoot, filePath, parsed.TransitionName, parsed.ActingRole, parsed.Timestamp, parsed.BaseCommit, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onApplied: applied => new CommandOutcome.Success(new BlockTransitionResult
            {
                FilePath = filePath,
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
                $"Available: {(undefined.Available.Count == 0 ? "none" : string.Join(", ", undefined.Available.Select(static t => t.Name)))}.",
                undefined.RefusingRule, undefined.Remedy),
            onBaseNotRecorded: baseNotRecorded => new CommandOutcome.Refusal(
                "base-not-recorded",
                "a brief must name the commit it was carved against — pass --base or record one before briefing.",
                baseNotRecorded.RefusingRule, baseNotRecorded.Remedy),
            onBaseImmutable: immutable => new CommandOutcome.Refusal(
                "base-immutable",
                $"'base' is already recorded as '{immutable.Recorded}' and cannot change across rounds; supplied '{immutable.Attempted}'.",
                immutable.RefusingRule, immutable.Remedy),
            onUndispositionedNits: undispositioned => new CommandOutcome.Refusal(
                "undispositioned-nits",
                $"'{filePath}' cannot leave 'in-review' — the following nit(s) have no disposition: " +
                $"{string.Join(", ", undispositioned.NitIds)}.",
                undispositioned.RefusingRule, undispositioned.Remedy),
            onNotABlockCard: notABlock => WrongCardKind(filePath, CardKind.Block, notABlock.Kind, "flow transitions only apply to a block card") with
            {
                Rule = notABlock.RefusingRule,
                Remedy = notABlock.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(filePath, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
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

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.RecordGateResult(
            repoRoot, filePath, parsed.Label, parsed.ExitCode, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new BlockGateResult
            {
                FilePath = filePath,
                Label = recorded.Result.Label,
                ExitCode = recorded.Result.ExitCode,
                // Routed through GateStatus (§5 remediation, DEVLOG §5 finding N2) rather than
                // re-deriving "== 0" inline a second time — GateStatus.Passed is the one place
                // that collapse is named, per its own doc comment.
                Passed = recorded.Card.BlockFields.GateStatusOf(recorded.Result.Label).Passed,
                ActingRole = recorded.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onNotABlockCard: notABlock => WrongCardKind(filePath, CardKind.Block, notABlock.Kind, "gate results only apply to a block card") with
            {
                Rule = notABlock.RefusingRule,
                Remedy = notABlock.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(filePath, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
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

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.AddBlockedBy(repoRoot, filePath, parsed.BlockingCardId, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapBlockedByOutcome(outcome, filePath, parsed.Timestamp);
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

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.RemoveBlockedBy(repoRoot, filePath, parsed.BlockingCardId, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapBlockedByOutcome(outcome, filePath, parsed.Timestamp);
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
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(filePath, disagreement.StoredRound, disagreement.ExpectedRound),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to update blocked_by on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a block card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));

    /// <summary>
    /// <c>block approve</c> (§8 block A, review-certification: "Approve is binary and certifies one
    /// state" / "Certification enumerates its claims"). Resolves <c>--id</c> through
    /// <see cref="ResolveCardReference"/> and records the approval via <see cref="Cards.CardStore.
    /// RecordApproval"/> — the same three-way refusal/tool-failure/reported-failure split every §5
    /// write verb already applies, plus <see cref="Cards.CardApprovalOutcome.RoleNotPermitted"/>
    /// (review-certification: "Approval is role-bounded"). <see langword="private"/>:
    /// <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunBlockApprove(ParsedCommand.BlockApprove parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var resolved = ResolveCardReference(
            repoRoot, parsed.Id, CardKind.Block, CardStore.IsBlockCard, "'--id'",
            "there is no block card carrying it");
        if (resolved.Refusal is not null)
        {
            return resolved.Refusal;
        }

        var outcome = CardStore.RecordApproval(
            repoRoot, resolved.FilePath!, parsed.ReviewedState, parsed.Claims, parsed.Limits,
            parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onApproved: approved => new CommandOutcome.Success(new BlockApproveResult
            {
                FilePath = resolved.FilePath!,
                Id = approved.Card.Frontmatter.Id,
                ReviewedState = parsed.ReviewedState,
                Claims = [.. approved.Claims.Select(static claim => new BlockApprovalClaimResult { Id = claim.Id, Text = claim.Text })],
                Limits = [.. approved.Limits.Select(static limit => limit.Text)],
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
                Round = approved.Card.BlockFields.Round,
            }),
            onRoleNotPermitted: roleNotPermitted => RoleNotPermitted(
                "recording an approval", roleNotPermitted.AttemptedRole, [CardOwner.Reviewer, CardOwner.Supervisor]),
            onUndefinedTransition: undefined => new CommandOutcome.Refusal(
                "undefined-transition",
                $"no transition 'approve' from '{undefined.CurrentState.ToWireString()}'. " +
                $"Available: {(undefined.Available.Count == 0 ? "none" : string.Join(", ", undefined.Available.Select(static t => t.Name)))}."),
            onUndispositionedNits: undispositioned => new CommandOutcome.Refusal(
                "undispositioned-nits",
                $"'{resolved.FilePath}' cannot leave 'in-review' — the following nit(s) have no disposition: " +
                $"{string.Join(", ", undispositioned.NitIds)}."),
            onNotABlockCard: notABlock => WrongCardKind(resolved.FilePath!, CardKind.Block, notABlock.Kind, "'block approve' only applies to a block card"),
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(resolved.FilePath!, disagreement.StoredRound, disagreement.ExpectedRound),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to approve."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a block card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>nit raise</c> (§8 block B, review-certification: "A nit SHALL be raised as an addressed
    /// comment, not as a card"). Resolves <c>--id</c> (the block card) through <see cref="
    /// ResolveCardReference"/>, then appends the nit as a <see cref="CardComment"/> via
    /// <see cref="CardStore.RaiseNit"/> — not the plain <see cref="CardStore.AppendComment"/> any
    /// more (§8 remediation, review-certification: "A nit SHALL be raised only against a block that
    /// is under review"): raising a nit needs its own state check <see cref="CardStore.
    /// AppendComment"/> has no way to express, since that surface is shared with comments that
    /// carry no such bound.
    /// <see langword="private"/>: <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunNitRaise(ParsedCommand.NitRaise parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var resolved = ResolveCardReference(
            repoRoot, parsed.Id, CardKind.Block, CardStore.IsBlockCard, "'--id'",
            "there is no block card carrying it");
        if (resolved.Refusal is not null)
        {
            return resolved.Refusal;
        }

        var nitId = $"nit-{Guid.NewGuid():N}";
        var comment = new CardComment(
            Id: nitId, Author: parsed.ActingRole, Timestamp: parsed.Timestamp, Body: parsed.Body,
            ReplyTo: null, To: CardOwner.Architect, Resolves: null, UnknownHeaderFields: [],
            IsNit: true, Required: parsed.Required, Sites: parsed.Sites);

        var outcome = CardStore.RaiseNit(repoRoot, resolved.FilePath!, comment, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRaised: _ => new CommandOutcome.Success(new NitRaiseResult
            {
                NitId = nitId,
                FilePath = resolved.FilePath!,
                BlockId = parsed.Id,
                Required = parsed.Required,
                Sites = parsed.Sites,
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onNotABlockCard: notABlock => WrongCardKind(resolved.FilePath!, CardKind.Block, notABlock.Kind, "nits only apply to a block card") with
            {
                Rule = notABlock.RefusingRule,
                Remedy = notABlock.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(resolved.FilePath!, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to raise a nit against."),
            onNotUnderReview: notUnderReview => new CommandOutcome.Refusal(
                "nit-target-not-in-review",
                $"block '{parsed.Id}' is '{notUnderReview.CurrentState.ToWireString()}', not 'in-review'; a nit may only " +
                "be raised against a block under review. If this observation needs fixing, record it as an obligation " +
                "naming the section expected to discharge it — that judgement is not automated.",
                notUnderReview.RefusingRule, notUnderReview.Remedy),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>nit disposition</c> (§8 block B, review-certification: "Nits carry a disposition").
    /// Resolves <c>--id</c> (the nit's own comment id) through <see cref="NitResolver"/> — never
    /// <see cref="ResolveCardReference"/>/<see cref="CardIdentityResolver"/>, which only ever match a
    /// card's own <c>id</c> frontmatter field, one level above where a nit's id actually lives.
    /// <see langword="private"/>: <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunNitDisposition(ParsedCommand.NitDisposition parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var resolution = NitResolver.Resolve(repoRoot, parsed.Id);
        var resolutionRefusal = resolution.Match<CommandOutcome?>(
            onFound: static (_, _, _) => null,
            onNotFound: notFoundId => new CommandOutcome.Refusal(
                "nit-id-not-found",
                $"'--id' names id '{notFoundId}', but no live nit in the record carries it."),
            onDuplicate: (duplicateId, filePaths) => new CommandOutcome.Refusal(
                "duplicate-nit-id",
                $"'--id' names id '{duplicateId}', but {filePaths.Count} card files claim a nit by it " +
                $"({string.Join(", ", filePaths)}); refusing to guess which one is the target."),
            onUnreadable: (unreadableId, filePaths) => new CommandOutcome.Refusal(
                "nit-id-unresolvable",
                $"'--id' names id '{unreadableId}', but {filePaths.Count} card file(s) elsewhere in the record " +
                $"could not be read, so its presence cannot be confirmed or ruled out: {string.Join(", ", filePaths)}."));
        if (resolutionRefusal is not null)
        {
            return resolutionRefusal;
        }

        var filePath = resolution.Match(
            onFound: static (path, _, _) => path,
            onNotFound: static _ => throw new InvalidOperationException("unreachable: refusal already returned above."),
            onDuplicate: static (_, _) => throw new InvalidOperationException("unreachable: refusal already returned above."),
            onUnreadable: static (_, _) => throw new InvalidOperationException("unreachable: refusal already returned above."));

        var outcome = CardStore.DispositionNit(
            repoRoot, filePath, parsed.Id, parsed.Disposition, parsed.Body, parsed.ActingRole, parsed.Timestamp,
            lockTimeout, parsed.ChangeName, parsed.RaiseRequest);

        return outcome.Match<CommandOutcome>(
            onDispositioned: dispositioned => new CommandOutcome.Success(new NitDispositionResult
            {
                NitId = parsed.Id,
                FilePath = filePath,
                Disposition = parsed.Disposition.ToWireString(),
                Transitioned = dispositioned.Transitioned,
                Round = dispositioned.Card.BlockFields.Round,
                RaisedCardId = dispositioned.RaisedCard?.Frontmatter.Id,
                RaisedCardFilePath = parsed.RaiseRequest?.FilePath,
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onRoleNotPermitted: roleNotPermitted => RoleNotPermitted(
                "dispositioning a nit", roleNotPermitted.AttemptedRole, CardOwner.Architect),
            onNotABlockCard: notABlock => WrongCardKind(filePath, CardKind.Block, notABlock.Kind, "nits only apply to a block card") with
            {
                Rule = notABlock.RefusingRule,
                Remedy = notABlock.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(filePath, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to disposition a nit on."),
            onNitNotFound: nitNotFound => new CommandOutcome.Refusal(
                "nit-id-not-found", $"'--id' names id '{nitNotFound.NitId}', but no live nit in the record carries it.",
                nitNotFound.RefusingRule, nitNotFound.Remedy),
            onAlreadyDispositioned: alreadyDispositioned => new CommandOutcome.Refusal(
                "nit-already-dispositioned", $"nit '{alreadyDispositioned.NitId}' already carries a disposition.",
                alreadyDispositioned.RefusingRule, alreadyDispositioned.Remedy),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onRaisedCardLayoutMismatch: raisedLayoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", raisedLayoutMismatch.Reason),
            onRaisedCardAlreadyExists: raisedAlreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{raisedAlreadyExists.FilePath}'.",
                raisedAlreadyExists.RefusingRule, raisedAlreadyExists.Remedy),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a block card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>section verdict</c> (§5 block E / §8a block B, work-lifecycle: "Sections are entities" —
    /// "the verdict, the range and the acting role are recorded against that section entity";
    /// "Section remediation follows the finding, not the verdict"): resolves every
    /// <c>--finding-recurred</c> id to a file path — the same "resolve at the CLI layer, pass a
    /// path" shape <c>block approve --id</c> already established — then appends the verdict, moves
    /// every recurring card and creates the first-time finding's card (if any), all in one call to
    /// <see cref="CardStore.RecordSectionVerdict"/>. Same three-way refusal/tool-failure/reported-
    /// failure split every §5 write verb already applies, plus §8a block B's own routing refusals.
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

        var recurringFindingCardPaths = new List<string>(parsed.RecurringFindingCardIds.Count);
        foreach (var recurringId in parsed.RecurringFindingCardIds)
        {
            var resolved = ResolveCardReference(
                repoRoot, recurringId, CardKind.Block, CardStore.IsBlockCard, "'--finding-recurred'",
                "raise it as a new finding instead, with '--finding-new'");
            if (resolved.Refusal is not null)
            {
                return resolved.Refusal;
            }

            recurringFindingCardPaths.Add(resolved.FilePath!);
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var newFindings = parsed.NewFindings
            .Select(request => request with { FilePath = ResolveFilePath(parsed.WorkingDirectory, request.FilePath) })
            .ToList();
        var outcome = CardStore.RecordSectionVerdict(
            repoRoot, filePath, parsed.Verdict, parsed.RangeFrom, parsed.RangeTo, parsed.ActingRole, parsed.Timestamp, lockTimeout,
            parsed.ChangeName, recurringFindingCardPaths, newFindings);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new SectionVerdictResult
            {
                FilePath = filePath,
                Verdict = recorded.Entry.Verdict.ToWireString(),
                RangeFrom = recorded.Entry.RangeFrom,
                RangeTo = recorded.Entry.RangeTo,
                ActingRole = recorded.Entry.By.ToWireString(),
                Timestamp = recorded.Entry.Timestamp,
                RecurredCardIds = [.. recorded.RecurredCards.Select(static c => c.Frontmatter.Id)],
                NewCardIds = [.. recorded.NewCards.Select(static c => c.Frontmatter.Id)],
            }),
            onNotASectionCard: notASection => WrongCardKind(filePath, CardKind.Section, notASection.Kind, "verdicts only apply to a section card"),
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(disagreement.FilePath, disagreement.StoredRound, disagreement.ExpectedRound),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record a verdict on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onRecurringFindingNotApproved: notApproved => new CommandOutcome.Refusal(
                "recurring-finding-not-approved",
                $"'{notApproved.CardId}' ('{notApproved.FilePath}') is not 'approved' (it is " +
                $"'{notApproved.CurrentState.ToWireString()}') — 'finding-recurred' only returns a remediation " +
                "card that is currently approved."),
            onRecurringFindingTargetsTaskImplementingBlock: taskImplementing => new CommandOutcome.Refusal(
                "recurring-finding-targets-task-implementing-block",
                $"'{taskImplementing.CardId}' ('{taskImplementing.FilePath}') carries tasks — it is a task-" +
                "implementing block, not a remediation card, and 'finding-recurred' never targets one. Raise the " +
                "finding as new instead, with '--finding-new'."),
            onFindingAlreadyOwned: alreadyOwned => new CommandOutcome.Refusal(
                "finding-already-owned",
                $"finding '{alreadyOwned.Key}' is already owned by '{alreadyOwned.OwningCardId}' " +
                $"('{alreadyOwned.OwningCardFilePath}') — a recurrence SHALL NOT create a second card for a " +
                $"finding a card already owns. Use '--finding-recurred {alreadyOwned.OwningCardId}' instead, or " +
                "give the new finding a different '--finding-new' key."),
            onNewFindingCardAlreadyExists: alreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{alreadyExists.FilePath}'."),
            onRemediationBoundExceeded: boundExceeded => new CommandOutcome.Refusal(
                "remediation-bound-exceeded",
                $"the section already carries {boundExceeded.VerdictNumber - 1} 'request-changes' verdicts " +
                "(a section admits two without ceremony) and this would be number " +
                $"{boundExceeded.VerdictNumber} — {boundExceeded.AuthorisationsRecorded} authorisation" +
                $"{(boundExceeded.AuthorisationsRecorded == 1 ? "" : "s")} recorded, " +
                $"{Math.Max(boundExceeded.UnspentAuthorisations, 0)} unspent. A recorded Product Owner " +
                "authorisation ('section authorise --role product-owner --reason <text>') would satisfy it."),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>section close</c> (§5 block E, work-lifecycle: "closing it SHALL record the acting role
    /// and the time"; §8a block A, "Approval is provisional until the section closes"): lands every
    /// approved block the section owns and closes the section, via <see cref="CardStore.
    /// CloseSection"/>. The two landing refusals (not-approved, non-zero-or-absent gate) are §8a
    /// block A's; the closing <em>conditions</em> §9 owns (open obligations, undeferred questions,
    /// unresolved threads) are still not this handler's job, the same boundary <see cref="
    /// CardSectionCloseOutcome"/>'s own doc comment states.
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

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CloseSection(repoRoot, filePath, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onClosed: closed => new CommandOutcome.Success(new SectionCloseResult
            {
                FilePath = filePath,
                ClosedBy = (closed.Card.SectionFields.ClosedBy ?? parsed.ActingRole).ToWireString(),
                ClosedAt = closed.Card.SectionFields.ClosedAt ?? parsed.Timestamp,
                LandedBlockIds = [.. closed.LandedBlocks.Select(static block => block.Frontmatter.Id)],
            }),
            onAlreadyClosed: already => new CommandOutcome.Refusal(
                "already-closed",
                $"'{already.FilePath}' is already closed."),
            onNotASectionCard: notASection => WrongCardKind(filePath, CardKind.Section, notASection.Kind, "only a section card can be closed by this verb"),
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(disagreement.BlockFilePath, disagreement.StoredRound, disagreement.ExpectedRound),
            onBlockNotApproved: notApproved => new CommandOutcome.Refusal(
                "block-not-approved",
                $"block '{notApproved.BlockId}' ('{notApproved.BlockFilePath}') is '{notApproved.ActualState.ToWireString()}', not 'approved' — every block in a " +
                "section must be approved before the section can close."),
            onBlockGateFailed: gateFailed => new CommandOutcome.Refusal(
                "block-gate-failed",
                $"block '{gateFailed.BlockId}' ('{gateFailed.BlockFilePath}') carries gate '{gateFailed.GateLabel}' recorded at exit code {gateFailed.ExitCode}, " +
                "not 0 — every gate a block carries must have passed before the section can close."),
            onBlockGateAbsent: gateAbsent => new CommandOutcome.Refusal(
                "block-gate-absent",
                $"block '{gateAbsent.BlockId}' ('{gateAbsent.BlockFilePath}') has no exit code recorded this round for gate '{gateAbsent.GateLabel}' — " +
                "an absent gate is not a pass by default. Record it with 'block gate' before closing."),
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
    /// <c>section authorise</c> (§8a block C, work-lifecycle: "Remediation beyond the second round
    /// requires recorded authorisation"): the one door to <see cref="Cards.
    /// SectionAuthorisationEntry"/>, via <see cref="Cards.CardStore.RecordSectionAuthorisation"/>.
    /// The role check (<see cref="Cards.CardOwner.ProductOwner"/> only) is the store's own first
    /// decision, not this handler's — see that method's own doc comment. <see langword="private"/>:
    /// <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunSectionAuthorise(ParsedCommand.SectionAuthorise parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.RecordSectionAuthorisation(repoRoot, filePath, parsed.Reason, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new SectionAuthorisationResult
            {
                FilePath = filePath,
                Reason = recorded.Entry.Reason,
                ActingRole = recorded.Entry.By.ToWireString(),
                Timestamp = recorded.Entry.Timestamp,
            }),
            onRoleNotPermitted: roleNotPermitted => RoleNotPermitted(
                "recording a section authorisation", roleNotPermitted.AttemptedRole, CardOwner.ProductOwner),
            onNotASectionCard: notASection => WrongCardKind(filePath, CardKind.Section, notASection.Kind, "authorisations only apply to a section card") with
            {
                Rule = notASection.RefusingRule,
                Remedy = notASection.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record an authorisation on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onNotAtBound: notAtBound => new CommandOutcome.Refusal(
                "authorisation-not-at-bound",
                "an authorisation is recorded against a refused 'request-changes' verdict, not in advance of one " +
                $"— the section carries {notAtBound.PriorRequestChanges} 'request-changes' verdict" +
                $"{(notAtBound.PriorRequestChanges == 1 ? "" : "s")} and {notAtBound.UnspentAuthorisations} unspent " +
                $"authorisation{(notAtBound.UnspentAuthorisations == 1 ? "" : "s")}, so it is not currently at the " +
                "bound. Record this once 'section verdict' has actually refused a verdict for want of one.",
                notAtBound.RefusingRule, notAtBound.Remedy),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read: {corrupt.Reason}"),
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
        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        if (!File.Exists(filePath))
        {
            return new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{filePath}' to read a status from.");
        }

        var read = CardStore.ReadCard(filePath);
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
                    return WrongCardKind(filePath, CardKind.Section, card.Frontmatter.Kind, "'section status' only reads a section card");
                }

                if (!SectionFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var status))
                {
                    throw new InvalidOperationException(
                        $"card '{filePath}' has an unrecognised section status: '{card.Frontmatter.Status}'.");
                }

                return new CommandOutcome.Success(new SectionStatusResult
                {
                    FilePath = filePath,
                    Status = status.ToWireString(),
                    Base = card.SectionFields.Base,
                    ClosedBy = card.SectionFields.ClosedBy?.ToWireString(),
                    ClosedAt = card.SectionFields.ClosedAt,
                    VerdictCount = card.SectionFields.Verdicts.Length,
                });
            },
            onFailure: failure => throw new InvalidOperationException(
                $"card '{filePath}' could not be read as a section card: {failure.Reason}"));
    }

    /// <summary>
    /// <c>finding record</c> (§6 block B): records a clean finding, and — when
    /// <paramref name="parsed"/>'s own <see cref="ParsedCommand.FindingRecord.RaiseRequest"/> is not
    /// <see langword="null"/> — the <c>obligation</c> or <c>hazard</c> its declared blind spot is
    /// raised as, via <see cref="Cards.CardStore.RecordFinding"/>. findings: "The system SHALL
    /// refuse to record a clean finding unless the recording role declares a blind spot or
    /// explicitly asserts that there is none" is enforced one layer up, in
    /// <see cref="CommandParser.ParseFindingRecord"/> — by the time a
    /// <see cref="ParsedCommand.FindingRecord"/> reaches this handler, a declaration has already
    /// been supplied (block A already made "not declared" unrepresentable on a constructed finding;
    /// this refusal belongs at the input boundary, not re-checked here). <see langword="private"/>:
    /// <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunFindingRecord(ParsedCommand.FindingRecord parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var sectionRefusal = ValidateSection(repoRoot, parsed.Section);
        if (sectionRefusal is not null)
        {
            return sectionRefusal;
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var raiseRequest = parsed.RaiseRequest is null
            ? null
            : new FindingBlindSpotRaiseRequest(
                parsed.RaiseRequest.Kind,
                ResolveFilePath(parsed.WorkingDirectory, parsed.RaiseRequest.FilePath),
                parsed.RaiseRequest.Title,
                parsed.RaiseRequest.Body);

        var outcome = CardStore.RecordFinding(
            repoRoot,
            filePath,
            parsed.Title,
            parsed.ActingRole,
            parsed.Section,
            parsed.Body,
            parsed.Instrument,
            parsed.Extent,
            parsed.VerifiedAt,
            raiseRequest,
            parsed.Disposition,
            parsed.Timestamp,
            lockTimeout,
            parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new FindingRecordResult
            {
                FilePath = filePath,
                Id = recorded.Finding.Frontmatter.Id,
                Title = recorded.Finding.Frontmatter.Title,
                BlindSpot = recorded.Finding.FindingFields.BlindSpot.Match(
                    onNone: static () => "none",
                    onRaisedAs: static _ => "raised-as"),
                RaisedCardId = recorded.RaisedCard?.Frontmatter.Id,
                RaisedCardFilePath = raiseRequest?.FilePath,
                RaisedCardKind = recorded.RaisedCard?.Frontmatter.Kind.ToWireString(),
                Disposition = recorded.Finding.FindingFields.Disposition.Match(
                    onMeasured: static () => "measured",
                    onArguedClean: static () => "argued-clean"),
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onFindingAlreadyExists: already => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{already.FilePath}' — the finding's own target path."),
            onBlindSpotCardAlreadyExists: already => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{already.FilePath}' — the raised blind-spot card's target path."),
            onFindingLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onBlindSpotLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>finding status</c> (§6 block C, findings' "Findings stale when their extent moves" /
    /// "Findings that argue rather than measure are dispositioned separately"; §6 block D, findings'
    /// "A `finding` SHALL … degrade at section close"): reads
    /// <paramref name="parsed"/>'s own <see cref="ParsedCommand.FindingStatus.FilePath"/> via
    /// <see cref="CardStore.ReadCard"/>, then answers <see cref="FindingStalenessEvaluator.Evaluate"/>
    /// and <see cref="FindingDegradationEvaluator.Evaluate"/> against it — the CLI-JSON surface §6
    /// block C's brief calls for so that "the answer must not under-report" is asserted against
    /// emitted output directly, not only at the domain layer (§5's own gap: no CLI verb ever read
    /// <c>GateStatus.Absent</c> back). Staleness and degradation are answered independently and
    /// emitted as two separate fields (§6 block D ruling) — neither evaluator's outcome influences
    /// the other's. When <see cref="FindingDegradationEvaluator.Evaluate"/> answers <see
    /// cref="FindingDegradationEvaluation.Ambiguous"/>, this mints <c>duplicate-card-id</c> rather
    /// than emitting any result at all — §7 block B rewired the evaluator onto
    /// <see cref="CardIdentityResolver"/>, so this case is now "more than one file claims the id
    /// this finding's own <c>section</c> field names" (the same fact <see cref="ValidateSection"/>
    /// refuses under the identical code when it is caught at record time instead), not "two
    /// <c>section</c> cards happen to share a free-text label". <see langword="private"/>:
    /// <see cref="CommandParser"/> cannot name this method.
    /// </summary>
    private static CommandOutcome RunFindingStatus(ParsedCommand.FindingStatus parsed)
    {
        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        if (!File.Exists(filePath))
        {
            return new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{filePath}' to read a status from.");
        }

        var read = CardStore.ReadCard(filePath);
        return read.Match<CommandOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (!CardStore.IsFindingCard(card))
                {
                    return WrongCardKind(filePath, CardKind.Finding, card.Frontmatter.Kind, "'finding status' only reads a finding card");
                }

                var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
                if (repoRoot is null)
                {
                    return new CommandOutcome.Refusal(
                        "repo-root-not-found",
                        $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
                }

                var staleness = FindingStalenessEvaluator.Evaluate(card.FindingFields, repoRoot);
                var (statusText, reason) = staleness.Match(
                    onCurrent: static () => ("current", (string?)null),
                    onStale: static reason => ("stale", (string?)reason),
                    onNotMeasurable: static reason => ("not-measurable", (string?)reason),
                    onNotApplicable: static reason => ("not-applicable", (string?)reason));

                var degradation = FindingDegradationEvaluator.Evaluate(card, repoRoot);
                return degradation.Match<CommandOutcome>(
                    onResolved: status =>
                    {
                        var (degradationText, degradationReason) = status.Match(
                            onLive: static () => ("live", (string?)null),
                            onDegraded: static () => ("degraded", (string?)null),
                            onUnreadable: static reason => ("unreadable", (string?)reason));

                        return new CommandOutcome.Success(new FindingStatusResult
                        {
                            FilePath = filePath,
                            Id = card.Frontmatter.Id,
                            Title = card.Frontmatter.Title,
                            Disposition = card.FindingFields.Disposition.Match(
                                onMeasured: static () => "measured",
                                onArguedClean: static () => "argued-clean"),
                            VerifiedAt = card.FindingFields.VerifiedAt,
                            Staleness = statusText,
                            StalenessReason = reason,
                            Degradation = degradationText,
                            DegradationReason = degradationReason,
                        });
                    },
                    onAmbiguous: (id, filePaths) => new CommandOutcome.Refusal(
                        "duplicate-card-id",
                        $"{filePaths.Count} card files claim id '{id}' — named by this finding's own 'section' " +
                        $"field — ({string.Join(", ", filePaths)}); refusing to guess which one is the section " +
                        "rather than reading it from whichever file happened to sort first."));
            },
            onFailure: failure => throw new InvalidOperationException(
                $"card '{filePath}' could not be read as a finding card: {failure.Reason}"));
    }

    /// <summary>
    /// <c>rule create</c> (§7 block A). <see cref="CardStore.CreateCard"/> does the real work —
    /// this handler is just the repo-root resolution and outcome mapping every §7 block A creation
    /// verb repeats, via <see cref="MapCardCreateOutcome"/>.
    /// </summary>
    private static CommandOutcome RunRuleCreate(ParsedCommand.RuleCreate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Rule, parsed.Scope, parsed.Title,
            RegisterLifecycleState.Open.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: null, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole);
    }

    /// <summary>
    /// <c>hazard create</c> (§7 block A, register: "Hazards carry a verification condition").
    /// <c>--condition</c>/<c>--cadence</c> are already required by the time this runs — see
    /// <see cref="CommandParser.ParseHazardCreate"/>, the load-bearing refusal site for register's
    /// "the system refuses and states the condition it requires" scenario, checked at parse time
    /// (argv-decidable, O-3) rather than here.
    /// </summary>
    private static CommandOutcome RunHazardCreate(ParsedCommand.HazardCreate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Hazard, CardScope.Repository, parsed.Title,
            RegisterLifecycleState.Open.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: new RegisterCardFields(parsed.Condition, parsed.Cadence, null, null),
            parsed.Timestamp, lockTimeout, changeName: null);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole);
    }

    /// <summary>
    /// <c>obligation create</c> (§7 block A/C). Scope is always <see cref="CardScope.Change"/>.
    /// <c>--owed-by</c> is validated here, before any card is created — the same
    /// resolve-through-<see cref="Cards.CardIdentityResolver"/>, refuse-on-anything-else-than-a-
    /// section discipline <see cref="ValidateSection"/> already applies to <c>--section</c>, reused
    /// via <see cref="ResolveCardReference"/> rather than re-derived (Architect ruling: a refusal
    /// naming the same fact earns the same code).
    /// </summary>
    private static CommandOutcome RunObligationCreate(ParsedCommand.ObligationCreate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var owedBySection = ResolveCardReference(
            repoRoot, parsed.OwedById, CardKind.Section, CardStore.IsSectionCard, "'--owed-by'",
            "create the section first with 'section create'");
        if (owedBySection.Refusal is not null)
        {
            return owedBySection.Refusal;
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Obligation, CardScope.Change, parsed.Title,
            RegisterLifecycleState.Open.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: new RegisterCardFields(null, null, null, null, OwedBy: parsed.OwedById),
            parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole);
    }

    /// <summary><c>decision create</c> (§7 block A). Scope is always <see cref="CardScope.Capability"/>.</summary>
    private static CommandOutcome RunDecisionCreate(ParsedCommand.DecisionCreate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Decision, CardScope.Capability, parsed.Title,
            RegisterLifecycleState.Open.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: null, parsed.Timestamp, lockTimeout, changeName: null);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole);
    }

    /// <summary>
    /// <c>section create</c> (§7 block A, Product Owner ruling: "<c>section create</c> is in §7's
    /// scope"). Scope is always <see cref="CardScope.Change"/> — the initial status is
    /// <see cref="SectionFlowState.Open"/>'s wire text, read through
    /// <see cref="SectionFlowStateWireFormat"/> rather than <see cref="RegisterLifecycleStateWireFormat"/>,
    /// because a <c>section</c> is not one of the four register kinds even though both vocabularies'
    /// open states happen to share the same wire text.
    /// </summary>
    private static CommandOutcome RunSectionCreate(ParsedCommand.SectionCreate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Section, CardScope.Change, parsed.Title,
            SectionFlowState.Open.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: null, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole);
    }

    /// <summary>
    /// <c>question create</c> (§7 remediation, blocker 1; owner fixed in the second remediation).
    /// Scope is always <see cref="CardScope.Repository"/> — <see cref="CardScopeRules.Validate"/>
    /// already refuses anything else for <see cref="CardKind.Question"/>, so there is no
    /// <c>--change</c> here, the same shape <see cref="RunDecisionCreate"/> has for its own
    /// always-fixed scope. The initial status is the plain literal <c>"open"</c> — the same
    /// convention <see cref="CardStore.RecordFinding"/> already uses for a kind with no
    /// <see cref="RegisterLifecycleState"/>/<see cref="SectionFlowState"/> vocabulary of its own;
    /// §9 is where a question's actual status vocabulary (open/answered/deferred) gets decided, not
    /// here — this call only ever writes the one state a brand-new card needs.
    ///
    /// <para>
    /// <b><see cref="Cards.CardStore.CreateCard"/>'s <c>actingRole</c> parameter becomes the card's
    /// <c>owner</c> (§7 second remediation) — so this passes <see cref="ParsedCommand.
    /// QuestionCreate.OwedByRole"/> there, not <see cref="ParsedCommand.QuestionCreate.ActingRole"/>.
    /// </b> The card is owned by whoever owes the answer, exactly what card-model's ownership
    /// routing and register's "continues to surface to the role that owes its answer" both require.
    /// <see cref="ParsedCommand.QuestionCreate.ActingRole"/> — the raiser — is not lost: it is
    /// reported back through <see cref="MapCardCreateOutcome"/>'s own explicit <c>actingRole</c>
    /// argument, passed below, rather than read off the written card the way it used to be, which
    /// is what keeps the two facts from collapsing into one the way they did before this fix.
    /// </para>
    /// </summary>
    private static CommandOutcome RunQuestionCreate(ParsedCommand.QuestionCreate parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Question, CardScope.Repository, parsed.Title,
            "open", parsed.OwedByRole, parsed.Body,
            registerFields: null, parsed.Timestamp, lockTimeout, changeName: null);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole);
    }

    /// <summary>
    /// Shared by every §7 block A creation verb — the one place a <see cref="CardCreateOutcome"/>
    /// becomes a <see cref="CommandOutcome"/>, so the six verbs cannot drift on what a given
    /// disposition means. <paramref name="actingRole"/> is the caller's own <c>--role</c> value,
    /// reported verbatim as <see cref="CardCreateResult.ActingRole"/> (§7 second remediation) —
    /// previously read back off <c>created.Card.Frontmatter.Owner</c>, which happened to equal the
    /// acting role for every kind this method served until <see cref="ParsedCommand.QuestionCreate"/>
    /// gave <c>owner</c> a different value (<see cref="ParsedCommand.QuestionCreate.OwedByRole"/>).
    /// Passing it explicitly keeps this method's output byte-identical for the five kinds where the
    /// two facts still coincide, and correct for the one where they no longer do — see
    /// <see cref="RunQuestionCreate"/>.
    /// </summary>
    private static CommandOutcome MapCardCreateOutcome(CardCreateOutcome outcome, string filePath, CardOwner actingRole) =>
        outcome.Match<CommandOutcome>(
            onCreated: created => new CommandOutcome.Success(new CardCreateResult
            {
                FilePath = filePath,
                Id = created.Card.Frontmatter.Id,
                Title = created.Card.Frontmatter.Title,
                Kind = created.Card.Frontmatter.Kind.ToWireString(),
                Scope = created.Card.Frontmatter.Scope.ToWireString(),
                Status = created.Card.Frontmatter.Status,
                Condition = created.Card.RegisterFields.Condition,
                Cadence = created.Card.RegisterFields.Cadence,
                OwedBy = created.Card.RegisterFields.OwedBy,
                ActingRole = actingRole.ToWireString(),
                Timestamp = created.Card.Frontmatter.Created,
            }),
            onScopeRefused: refused => new CommandOutcome.Refusal("scope-refused", refused.Reason),
            onAlreadyExists: already => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{already.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));

    /// <summary>
    /// <c>rule|hazard|obligation|decision discharge</c> (§7 block A, register: "Register kinds have
    /// a two-state lifecycle"). One handler for all four subcommands — see
    /// <see cref="ParsedCommand.RegisterDischarge"/>'s own doc comment for why.
    /// </summary>
    private static CommandOutcome RunRegisterDischarge(ParsedCommand.RegisterDischarge parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.DischargeRegisterCard(repoRoot, filePath, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onDischarged: discharged => new CommandOutcome.Success(new CardRegisterDischargeResult
            {
                FilePath = filePath,
                Id = discharged.Card.Frontmatter.Id,
                Kind = discharged.Card.Frontmatter.Kind.ToWireString(),
                ActingRole = (discharged.Card.RegisterFields.DischargedBy ?? parsed.ActingRole).ToWireString(),
                DischargedAt = discharged.Card.RegisterFields.DischargedAt ?? parsed.Timestamp,
            }),
            onAlreadyDischarged: already => new CommandOutcome.Refusal(
                "already-discharged", $"'{already.FilePath}' is already discharged.", already.RefusingRule, already.Remedy),
            onInvalidStatus: invalid => new CommandOutcome.Refusal(
                "invalid-register-status",
                $"'{invalid.FilePath}' has status '{invalid.Status}', which is not a valid register lifecycle " +
                $"state ({RegisterLifecycleStateWireFormat.RecognisedValues}) — register cards SHALL NOT occupy flow states.",
                invalid.RefusingRule, invalid.Remedy),
            onNotARegisterCard: notARegister => new CommandOutcome.Refusal(
                "not-a-register-card",
                $"'{filePath}' is a '{notARegister.Kind.ToWireString()}' card, not one of the register kinds " +
                "(rule, hazard, obligation, decision); discharge only applies to a register card.",
                notARegister.RefusingRule, notARegister.Remedy),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to discharge."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a register card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>change archive</c> (§7 block D, register: "the register lives above the change" — "SHALL
    /// act as a filter that closes its change-scoped cards and leaves cards of wider scope
    /// untouched"). Everything <c>change archive</c> itself decides lives in <see cref="Cards.
    /// CardStore.ArchiveChange"/> — see that method's own doc comment for the two-phase
    /// settle-then-move shape and what happens on a failure partway; this handler resolves the
    /// repository root and maps the resulting <see cref="ChangeArchiveOutcome"/> to a CLI response.
    ///
    /// <para>
    /// <b>§7 block F's compaction hook runs first, composed at this layer rather than inside <see
    /// cref="Cards.CardStore.ArchiveChange"/> itself</b> (block F brief item 6: "hook it rather than
    /// building a second archive path" — <see cref="Cards.CardStore.ArchiveChange"/> has nothing in
    /// it this needed to unwind; it is exactly as block D left it). When <see cref="ParsedCommand.
    /// ChangeArchive.CompactFamilyId"/> is set, the family and every absorbed rule are resolved as
    /// <c>rule</c> cards and <see cref="Cards.CardStore.CompactRules"/> runs to completion (or
    /// refuses) before <see cref="Cards.CardStore.ArchiveChange"/> is ever called, so a refused
    /// compaction leaves the change entirely untouched — nothing archived, nothing compacted.
    /// <b>Register's "performed by the architect" role constraint is enforced inside <see cref="
    /// Cards.CardStore.CompactRules"/> itself, not here</b> (§7 block F remediation, Architect
    /// ruling: a role check at this entry point only, with none on the standalone <c>rule compact</c>
    /// verb, left the constraint reachable by any role through the other door — "the constraint
    /// belongs to the operation, not to one entry point"). This handler resolves ids and maps the
    /// outcome; it does not decide who may compact. <see cref="Cards.CardStore.CompactRules"/>'s own
    /// scope restriction (change-scoped rules only, anchored against <see cref="ParsedCommand.
    /// ChangeArchive.ChangeName"/>) is what confirms every compacted rule actually belongs to the
    /// change being archived — reused, not re-checked here.
    /// </para>
    /// </summary>
    private static CommandOutcome RunChangeArchive(ParsedCommand.ChangeArchive parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        string? compactedFamilyId = null;
        IReadOnlyList<string>? compactedRuleIds = null;

        if (parsed.CompactFamilyId is not null)
        {
            var family = ResolveCardReference(
                repoRoot, parsed.CompactFamilyId, CardKind.Rule, CardStore.IsRuleCard, "'--compact-family'",
                "create it first with 'rule create'");
            if (family.Refusal is not null)
            {
                return family.Refusal;
            }

            var absorbedFilePaths = new List<string>(parsed.CompactAbsorbedIds!.Count);
            foreach (var absorbedId in parsed.CompactAbsorbedIds)
            {
                var absorbed = ResolveCardReference(
                    repoRoot, absorbedId, CardKind.Rule, CardStore.IsRuleCard, "'--absorbs'",
                    "create it first with 'rule create'");
                if (absorbed.Refusal is not null)
                {
                    return absorbed.Refusal;
                }

                absorbedFilePaths.Add(absorbed.FilePath!);
            }

            var compactOutcome = CardStore.CompactRules(
                repoRoot, family.FilePath!, absorbedFilePaths, parsed.ChangeName, parsed.ActingRole, parsed.Timestamp, lockTimeout);

            var (compactRefusal, compacted) = ResolveRuleCompactOutcome(compactOutcome);
            if (compactRefusal is not null)
            {
                return compactRefusal;
            }

            compactedFamilyId = compacted!.FamilyCard.Frontmatter.Id;
            compactedRuleIds = compacted.FamilyCard.RegisterFields.Absorbs;
        }

        var outcome = CardStore.ArchiveChange(repoRoot, parsed.ChangeName, parsed.ActingRole, parsed.Timestamp, lockTimeout);

        return outcome.Match<CommandOutcome>(
            onArchived: archived => new CommandOutcome.Success(new ChangeArchiveResult
            {
                ChangeName = archived.ChangeName,
                ArchivedDirectory = archived.ArchivedDirectory,
                SettledObligationIds = archived.SettledObligationIds,
                CompactedFamilyId = compactedFamilyId,
                CompactedRuleIds = compactedRuleIds,
                ActingRole = parsed.ActingRole.ToWireString(),
                ArchivedAt = parsed.Timestamp,
            }),
            onChangeNotFound: notFound => new CommandOutcome.Refusal(
                "change-not-found", $"no live change directory exists for '{notFound.ChangeName}'."),
            onAlreadyArchived: already => new CommandOutcome.Refusal(
                "already-archived", $"'{already.ChangeName}' is already archived."),
            onInvalidChangeName: invalid => new CommandOutcome.Refusal("invalid-change-name", invalid.Reason),
            onCardsUnreadable: unreadable => new CommandOutcome.Refusal(
                "cards-unreadable",
                $"'{parsed.ChangeName}' cannot be archived: " +
                $"{unreadable.FilePaths.Count} card file(s) could not be read: {string.Join(", ", unreadable.FilePaths)}."),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>decision supersede</c> (§7 block C, register: "A decision MAY name the decision it
    /// supersedes and the decision that supersedes it"). Both <see cref="ParsedCommand.
    /// DecisionSupersede.SupersedingId"/> and <see cref="ParsedCommand.DecisionSupersede.SupersededId"/>
    /// are resolved through <see cref="ResolveCardReference"/> — the same resolver every other §7
    /// block B/C reference goes through — before <see cref="Cards.CardStore.SupersedeDecision"/> is
    /// ever called, so the two-card write only starts once both ids are known to name real
    /// <c>decision</c> cards. Self-supersession is checked first, on the raw id text, ahead of
    /// either resolution: comparing the two ids the caller actually typed is cheaper than resolving
    /// both first, and is exactly the same fact either way (ids are unique, so two equal id strings
    /// can only ever resolve to the same card).
    /// </summary>
    private static CommandOutcome RunDecisionSupersede(ParsedCommand.DecisionSupersede parsed, TimeSpan lockTimeout)
    {
        if (string.Equals(parsed.SupersedingId, parsed.SupersededId, StringComparison.Ordinal))
        {
            return new CommandOutcome.Refusal(
                "self-supersession",
                $"'decision supersede' names '{parsed.SupersedingId}' as both the superseding and the superseded " +
                "decision; a decision superseding itself is not a coherent record.");
        }

        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var superseding = ResolveCardReference(
            repoRoot, parsed.SupersedingId, CardKind.Decision, CardStore.IsDecisionCard, "the superseding decision id",
            "create it first with 'decision create'");
        if (superseding.Refusal is not null)
        {
            return superseding.Refusal;
        }

        var superseded = ResolveCardReference(
            repoRoot, parsed.SupersededId, CardKind.Decision, CardStore.IsDecisionCard, "'--supersedes'",
            "create it first with 'decision create'");
        if (superseded.Refusal is not null)
        {
            return superseded.Refusal;
        }

        var outcome = CardStore.SupersedeDecision(
            repoRoot, superseding.FilePath!, superseded.FilePath!, parsed.ActingRole, parsed.Timestamp, lockTimeout);

        return outcome.Match<CommandOutcome>(
            onSuperseded: result => new CommandOutcome.Success(new DecisionSupersedeResult
            {
                SupersedingId = result.SupersedingCard.Frontmatter.Id,
                SupersedingFilePath = superseding.FilePath!,
                SupersededId = result.SupersededCard.Frontmatter.Id,
                SupersededFilePath = superseded.FilePath!,
                ActingRole = (result.SupersededCard.RegisterFields.DischargedBy ?? parsed.ActingRole).ToWireString(),
                DischargedAt = result.SupersededCard.RegisterFields.DischargedAt ?? parsed.Timestamp,
            }),
            onSelfSupersession: selfSupersession => new CommandOutcome.Refusal(
                "self-supersession",
                $"'{selfSupersession.Id}' cannot supersede itself; a decision superseding itself is not a coherent record."),
            onResolvedSelfSupersession: resolvedSelfSupersession => new CommandOutcome.Refusal(
                "self-supersession",
                $"'{resolvedSelfSupersession.Id}' cannot supersede itself; a decision superseding itself is not a coherent record.",
                resolvedSelfSupersession.RefusingRule, resolvedSelfSupersession.Remedy),
            onSupersededAlreadyDischarged: alreadyDischarged => new CommandOutcome.Refusal(
                "already-discharged",
                $"'{alreadyDischarged.FilePath}' is already discharged; superseding an already-discharged decision " +
                "is a refusal, not a re-supersession.",
                alreadyDischarged.RefusingRule, alreadyDischarged.Remedy),
            onSupersedingAlreadyDischarged: alreadyDischarged => new CommandOutcome.Refusal(
                "already-discharged",
                $"'{alreadyDischarged.FilePath}' is already discharged (already superseded by something else); " +
                "a discharged decision cannot newly become another decision's successor.",
                alreadyDischarged.RefusingRule, alreadyDischarged.Remedy),
            onInvalidStatus: invalid => new CommandOutcome.Refusal(
                "invalid-register-status",
                $"'{invalid.FilePath}' has status '{invalid.Status}', which is not a valid register lifecycle " +
                $"state ({RegisterLifecycleStateWireFormat.RecognisedValues}) — register cards SHALL NOT occupy flow states.",
                invalid.RefusingRule, invalid.Remedy),
            onNotADecisionCard: notADecision => WrongCardKind(
                notADecision.FilePath, CardKind.Decision, notADecision.Kind, "'decision supersede' only applies to decision cards") with
            {
                Rule = notADecision.RefusingRule,
                Remedy = notADecision.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to supersede."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a decision card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>rule promote</c> (§7 block E, register: "Promoting a change-scoped rule to repository
    /// scope SHALL move the same card, retaining its identity, text and thread"). <see cref="Cards.
    /// CardStore.PromoteRule"/> does the real work — this handler resolves <c>--id</c> through
    /// <see cref="ResolveCardReference"/> (the same resolver every other §7 card-to-card reference
    /// goes through) and maps the resulting <see cref="CardRulePromoteOutcome"/>.
    /// </summary>
    private static CommandOutcome RunRulePromote(ParsedCommand.RulePromote parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var rule = ResolveCardReference(
            repoRoot, parsed.Id, CardKind.Rule, CardStore.IsRuleCard, "'--id'",
            "create it first with 'rule create'");
        if (rule.Refusal is not null)
        {
            return rule.Refusal;
        }

        var outcome = CardStore.PromoteRule(repoRoot, rule.FilePath!, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onPromoted: promoted => new CommandOutcome.Success(new RulePromoteResult
            {
                Id = promoted.Card.Frontmatter.Id,
                OldFilePath = promoted.OldFilePath,
                NewFilePath = promoted.NewFilePath,
                Scope = promoted.Card.Frontmatter.Scope.ToWireString(),
                ActingRole = parsed.ActingRole.ToWireString(),
                PromotedAt = promoted.Card.Frontmatter.Updated,
            }),
            onAlreadyRepositoryScoped: already => new CommandOutcome.Refusal(
                "already-repository-scoped", $"'{already.FilePath}' is already repository-scoped; there is nothing to promote.",
                already.RefusingRule, already.Remedy),
            onNotChangeScoped: notChangeScoped => new CommandOutcome.Refusal(
                "rule-not-change-scoped",
                $"'{notChangeScoped.FilePath}' is '{notChangeScoped.Scope.ToWireString()}'-scoped; only a " +
                "'change'-scoped rule can be promoted to 'repository' scope.",
                notChangeScoped.RefusingRule, notChangeScoped.Remedy),
            onInvalidStatus: invalid => new CommandOutcome.Refusal(
                "invalid-register-status",
                $"'{invalid.FilePath}' has status '{invalid.Status}', which is not a valid register lifecycle " +
                $"state ({RegisterLifecycleStateWireFormat.RecognisedValues}) — register cards SHALL NOT occupy flow states.",
                invalid.RefusingRule, invalid.Remedy),
            onNotARuleCard: notARule => WrongCardKind(
                rule.FilePath!, CardKind.Rule, notARule.Kind, "'rule promote' only applies to rule cards") with
            {
                Rule = notARule.RefusingRule,
                Remedy = notARule.Remedy,
            },
            onTargetAlreadyExists: targetAlreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{targetAlreadyExists.FilePath}'.",
                targetAlreadyExists.RefusingRule, targetAlreadyExists.Remedy),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to promote."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a rule card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>rule author</c> (§7 block E, register: "Authoring a rule from findings SHALL create a new
    /// card and SHALL record which findings it was earned from"). Every id in <see cref="ParsedCommand.
    /// RuleAuthor.EarnedFrom"/> is resolved through <see cref="ResolveCardReference"/> and confirmed
    /// to name a <c>finding</c> card <em>before</em> <see cref="Cards.CardStore.CreateCard"/> is ever
    /// called — a finding that fails to resolve refuses the whole authoring attempt without writing
    /// anything, which is also what keeps this verb from ever writing to a finding card: nothing
    /// past this resolution loop touches any of the resolved paths again.
    /// </summary>
    private static CommandOutcome RunRuleAuthor(ParsedCommand.RuleAuthor parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        foreach (var findingId in parsed.EarnedFrom)
        {
            var finding = ResolveCardReference(
                repoRoot, findingId, CardKind.Finding, CardStore.IsFindingCard, "'--earned-from'",
                "record it first with 'finding record'");
            if (finding.Refusal is not null)
            {
                return finding.Refusal;
            }
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Rule, parsed.Scope, parsed.Title,
            RegisterLifecycleState.Open.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: new RegisterCardFields(null, null, null, null, EarnedFrom: parsed.EarnedFrom),
            parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onCreated: created => new CommandOutcome.Success(new RuleAuthorResult
            {
                FilePath = filePath,
                Id = created.Card.Frontmatter.Id,
                Title = created.Card.Frontmatter.Title,
                Scope = created.Card.Frontmatter.Scope.ToWireString(),
                Status = created.Card.Frontmatter.Status,
                EarnedFrom = created.Card.RegisterFields.EarnedFrom,
                ActingRole = created.Card.Frontmatter.Owner.ToWireString(),
                Timestamp = created.Card.Frontmatter.Created,
            }),
            onScopeRefused: refused => new CommandOutcome.Refusal("scope-refused", refused.Reason),
            onAlreadyExists: already => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{already.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>rule compact</c> (§7 block F, register: "The system SHALL support compacting several
    /// rules into a family rule stating what they share ... every absorbed rule SHALL remain
    /// retrievable"). <see cref="ParsedCommand.RuleCompact.FamilyId"/> and every entry of
    /// <see cref="ParsedCommand.RuleCompact.AbsorbedIds"/> are resolved through <see cref="
    /// ResolveCardReference"/> — the same resolver every other §7 card-to-card reference goes
    /// through — before <see cref="Cards.CardStore.CompactRules"/> is ever called. Self-absorption
    /// and a duplicate absorbed id are both checked first, on the raw id text, ahead of either
    /// resolution — the same cheap-before-expensive ordering <see cref="RunDecisionSupersede"/>
    /// already uses for self-supersession (a duplicate or self-naming raw id always resolves to the
    /// same path <see cref="Cards.CardStore.CompactRules"/>'s own path-level checks would catch
    /// anyway, so checking here is strictly earlier, not a second mechanism).
    /// </summary>
    private static CommandOutcome RunRuleCompact(ParsedCommand.RuleCompact parsed, TimeSpan lockTimeout)
    {
        if (parsed.AbsorbedIds.Count == 0)
        {
            return new CommandOutcome.Refusal(
                "empty-absorb-set",
                "'rule compact' requires '--absorbs <rule-id>[,<rule-id>...]' — a family with no members is not a family.");
        }

        foreach (var absorbedId in parsed.AbsorbedIds)
        {
            if (string.Equals(parsed.FamilyId, absorbedId, StringComparison.Ordinal))
            {
                return new CommandOutcome.Refusal(
                    "self-absorption",
                    $"'rule compact' names '{parsed.FamilyId}' as both the family and one of the rules it absorbs; " +
                    "a family absorbing itself is not a coherent record.");
            }
        }

        var seenAbsorbedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var absorbedId in parsed.AbsorbedIds)
        {
            if (!seenAbsorbedIds.Add(absorbedId))
            {
                return new CommandOutcome.Refusal(
                    "duplicate-absorbed-rule", $"'--absorbs' names '{absorbedId}' more than once.");
            }
        }

        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var family = ResolveCardReference(
            repoRoot, parsed.FamilyId, CardKind.Rule, CardStore.IsRuleCard, "'--id'",
            "create it first with 'rule create'");
        if (family.Refusal is not null)
        {
            return family.Refusal;
        }

        var absorbedFilePaths = new List<string>(parsed.AbsorbedIds.Count);
        foreach (var absorbedId in parsed.AbsorbedIds)
        {
            var absorbed = ResolveCardReference(
                repoRoot, absorbedId, CardKind.Rule, CardStore.IsRuleCard, "'--absorbs'",
                "create it first with 'rule create'");
            if (absorbed.Refusal is not null)
            {
                return absorbed.Refusal;
            }

            absorbedFilePaths.Add(absorbed.FilePath!);
        }

        var outcome = CardStore.CompactRules(
            repoRoot, family.FilePath!, absorbedFilePaths, parsed.ChangeName, parsed.ActingRole, parsed.Timestamp, lockTimeout);

        var (refusal, compacted) = ResolveRuleCompactOutcome(outcome);
        if (refusal is not null)
        {
            return refusal;
        }

        return new CommandOutcome.Success(new RuleCompactResult
        {
            FamilyId = compacted!.FamilyCard.Frontmatter.Id,
            FamilyFilePath = family.FilePath!,
            Absorbs = compacted.FamilyCard.RegisterFields.Absorbs,
            AbsorbedFilePaths = absorbedFilePaths,
            ActingRole = parsed.ActingRole.ToWireString(),
            CompactedAt = parsed.Timestamp,
        });
    }

    /// <summary>
    /// <c>rule propose-compact</c> (§7 block G, 7.9, register: "Repository compaction is proposed,
    /// not applied ... records the proposal with its candidate text, backing set and citation
    /// counts, and applies nothing until the Product Owner decides"). Every entry of <see cref="
    /// ParsedCommand.RuleProposeCompact.BackingIds"/> is resolved through <see cref="
    /// ResolveCardReference"/> exactly as <see cref="RunRuleCompact"/>'s <c>--absorbs</c> is, then
    /// checked open and repository-scoped in place — inline here rather than through a second
    /// <c>CardStore</c> outcome union, because there is no write, no lock and no rollback to model:
    /// a resolved-but-wrong-scope or resolved-but-discharged rule refuses the whole proposal exactly
    /// as it refuses <see cref="RunRuleCompact"/>'s write, reusing the same <c>card-layout-mismatch</c>
    /// / <c>already-discharged</c> / <c>invalid-register-status</c> codes for the same facts rather
    /// than minting proposal-only siblings.
    ///
    /// <para>
    /// <b>Nothing here writes to any resolved rule.</b> No <see cref="Cards.CardLock"/> naming a
    /// backing rule is ever acquired, no <see cref="Cards.CardStore.AtomicWrite"/>-shaped call ever
    /// touches one, and no field on any resolved <see cref="Cards.CardFile"/> is ever assigned back
    /// — every rule this method reads is read once, for its scope and status and to compute its
    /// <see cref="Cards.RuleCitations.CountCitations"/>, and never touched again.
    /// <see cref="CommandDispatcherRuleProposeCompactTests"/>'s own tests prove this on the bytes.
    /// </para>
    ///
    /// <para>
    /// <b>§7 remediation, blocker 1: the one thing this method now does write is a brand-new
    /// <c>question</c> card, owned by the Product Owner.</b> Register's own words are "records the
    /// proposal" — a result object that vanishes with the process is not a record, and 7.12 already
    /// established the fix's shape for the sibling verb this section's own reviewer found the same
    /// defect in. The card's body carries the candidate text, the backing set and the citation
    /// counts read above — see <see cref="BuildProposalCardBody"/>. Creating it uses the identical
    /// <see cref="Cards.CardStore.CreateCard"/> path <see cref="RunQuestionCreate"/> does, at the
    /// caller-supplied <see cref="ParsedCommand.RuleProposeCompact.ProposalFilePath"/> — no new way
    /// to put a card on disk, and still nothing written to any backing rule.
    /// </para>
    /// </summary>
    private static CommandOutcome RunRuleProposeCompact(ParsedCommand.RuleProposeCompact parsed, TimeSpan lockTimeout)
    {
        if (parsed.BackingIds.Count == 0)
        {
            return new CommandOutcome.Refusal(
                "empty-absorb-set",
                "'rule propose-compact' requires '--absorbs <rule-id>[,<rule-id>...]' — a family with no members is not a family.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parsed.BackingIds)
        {
            if (!seenIds.Add(id))
            {
                return new CommandOutcome.Refusal(
                    "duplicate-absorbed-rule", $"'--absorbs' names '{id}' more than once.");
            }
        }

        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var backingFilePaths = new List<string>(parsed.BackingIds.Count);
        var citationCounts = new List<int>(parsed.BackingIds.Count);
        foreach (var id in parsed.BackingIds)
        {
            var resolved = ResolveCardReference(
                repoRoot, id, CardKind.Rule, CardStore.IsRuleCard, "'--absorbs'",
                "create it first with 'rule create'");
            if (resolved.Refusal is not null)
            {
                return resolved.Refusal;
            }

            var card = resolved.Card!;
            var isRepositoryScoped = card.Frontmatter.Scope.Match(
                onSection: static () => false,
                onChange: static () => false,
                onCapability: static () => false,
                onRepository: static () => true);
            if (!isRepositoryScoped)
            {
                return new CommandOutcome.Refusal(
                    "card-layout-mismatch",
                    $"'{resolved.FilePath}' is '{card.Frontmatter.Scope.ToWireString()}'-scoped; a repository " +
                    "compaction proposal's backing set names repository-scoped rules only — change-scoped " +
                    "compaction happens at archive, performed by the architect, not proposed here.");
            }

            if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state))
            {
                return new CommandOutcome.Refusal(
                    "invalid-register-status",
                    $"'{resolved.FilePath}' has status '{card.Frontmatter.Status}', which is not a valid register " +
                    $"lifecycle state ({RegisterLifecycleStateWireFormat.RecognisedValues}) — register cards SHALL " +
                    "NOT occupy flow states.");
            }

            if (!ReferenceEquals(state, RegisterLifecycleState.Open))
            {
                return new CommandOutcome.Refusal(
                    "already-discharged",
                    $"'{resolved.FilePath}' is already discharged; it cannot back a compaction proposal.");
            }

            backingFilePaths.Add(resolved.FilePath!);
            citationCounts.Add(RuleCitations.CountCitations(repoRoot, card.Frontmatter.Id, resolved.FilePath!));
        }

        var proposalFilePath = ResolveFilePath(parsed.WorkingDirectory, parsed.ProposalFilePath);
        var proposalBody = BuildProposalCardBody(parsed, backingFilePaths, citationCounts);
        var createOutcome = CardStore.CreateCard(
            repoRoot, proposalFilePath, CardKind.Question, CardScope.Repository,
            "Repository rule compaction proposal", "open", CardOwner.ProductOwner, proposalBody,
            registerFields: null, parsed.Timestamp, lockTimeout, changeName: null);

        return createOutcome.Match<CommandOutcome>(
            onCreated: created => new CommandOutcome.Success(new RuleProposeCompactResult
            {
                CandidateText = parsed.CandidateText,
                Backing = parsed.BackingIds,
                BackingFilePaths = backingFilePaths,
                CitationCounts = citationCounts,
                ProposalId = created.Card.Frontmatter.Id,
                ProposalFilePath = proposalFilePath,
                ActingRole = parsed.ActingRole.ToWireString(),
                ProposedAt = parsed.Timestamp,
            }),
            onScopeRefused: refused => new CommandOutcome.Refusal("scope-refused", refused.Reason),
            onAlreadyExists: already => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{already.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// The recorded proposal's body (§7 remediation, blocker 1) — candidate text, then who proposed
    /// it and when, then the backing set with each rule's citation count as counted at request
    /// time. Plain Markdown, not a structured field: <see cref="CardKind.Question"/> carries no
    /// <see cref="Cards.RegisterCardFields"/> of its own (that type is the four register kinds'),
    /// and card-model's "legible without the tool" promise means the Product Owner reading this
    /// file directly must see exactly the same three facts a JSON caller reads off <see cref="
    /// RuleProposeCompactResult"/> — this is the one copy of them that outlives the process.
    /// </summary>
    private static string BuildProposalCardBody(
        ParsedCommand.RuleProposeCompact parsed, IReadOnlyList<string> backingFilePaths, IReadOnlyList<int> citationCounts)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(parsed.CandidateText.TrimEnd('\n')).Append("\n\n---\n\n");
        builder.Append("Proposed by '").Append(parsed.ActingRole.ToWireString()).Append("' at ")
            .Append(parsed.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append(".\n\n");
        builder.Append("Backing rules:\n");
        for (var i = 0; i < parsed.BackingIds.Count; i++)
        {
            var citationWord = citationCounts[i] == 1 ? "citation" : "citations";
            builder.Append("- ").Append(parsed.BackingIds[i]).Append(" (").Append(citationCounts[i])
                .Append(' ').Append(citationWord).Append(", ").Append(backingFilePaths[i]).Append(")\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>rule promote-constitution</c> (§7 block G, 7.12, register: "The system SHALL hold
    /// repository-scoped rules and SHALL NOT write to the project's agent instruction file ... the
    /// system refuses and records the promotion as awaiting a Product Owner decision"). Refuses
    /// unconditionally, for every acting role including <see cref="Callboard.Cards.CardOwner.
    /// ProductOwner"/> — register gives this operation no success scenario at all ("any agent
    /// attempts ... refuses"), and <c>--role</c> is caller-supplied, not authenticated, so nothing
    /// here ever branches on it to decide whether to write. Nothing in this method opens the
    /// project's agent instruction file, acquires a lock on it, or references its path at all —
    /// that file is never named anywhere in this call.
    ///
    /// <para>
    /// <b>Remediation (§7 block G, reviewer round 1): "records" now means a durable, attributed,
    /// append-only comment on the named rule's own card — not a sentence appended to the refusal
    /// response, which vanished the moment the process exited and asserted a record that did not
    /// exist.</b> <see cref="ParsedCommand.RulePromoteConstitution.Id"/> is resolved through
    /// <see cref="ResolveCardReference"/> (a <c>rule</c> card only — the promotion targets a rule),
    /// and one <see cref="Cards.CardComment"/> is appended to it via the existing <see cref="Cards.
    /// CardStore.AppendComment"/> — the first CLI verb to reach that surface — addressed
    /// (<c>To</c>) to <see cref="Callboard.Cards.CardOwner.ProductOwner"/> so the pending request
    /// surfaces through the same ownership-addressed routing an open question already gets, without
    /// building the "a Product-Owner-owned card" alternative the Architect reserved for themselves.
    /// Writing a comment to the <em>rule's own card</em> is not writing to the agent instruction
    /// file — the two are different files, and this method still never touches the second one.
    /// </para>
    ///
    /// <para>
    /// <b>Ordering: resolve first, then always refuse.</b> Unlike <see cref="Cards.CardStore.
    /// CompactRules"/>'s role check (checked first, before resolution, because a real write with
    /// real side effects sits behind it), this operation never succeeds for any resolved role — so
    /// there is no asymmetric risk in resolving first: a caller naming an id that does not resolve
    /// gets the resolver's own refusal (<c>card-id-not-found</c>/<c>wrong-card-kind</c>/etc.), and
    /// a caller naming a real rule gets the comment recorded and then the same unconditional
    /// <see cref="RoleNotPermitted"/> refusal every role gets.
    /// </para>
    ///
    /// <para>
    /// <b>Repeated attempts append, they do not deduplicate or overwrite.</b> Comments are
    /// append-only everywhere else in this codebase (card-model: "Append-only addressed comment
    /// threads") and this is no exception — a second attempt to promote the same rule appends a
    /// second comment, leaving both attempts on the record rather than collapsing them, the same
    /// way a rule leaned on twice in one card's thread still leaves two literal mentions even though
    /// <see cref="Cards.RuleCitations"/> counts the card once. <see cref="Cards.CardComment.Id"/> is
    /// a fresh <see cref="Guid"/> each call specifically so two attempts (even under a test's fixed
    /// clock, where the timestamp alone would collide) never collide on id.
    /// </para>
    /// </summary>
    private static CommandOutcome RunRulePromoteConstitution(ParsedCommand.RulePromoteConstitution parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var resolved = ResolveCardReference(
            repoRoot, parsed.Id, CardKind.Rule, CardStore.IsRuleCard, "'--id'",
            "create it first with 'rule create'");
        if (resolved.Refusal is not null)
        {
            return resolved.Refusal;
        }

        var refusal = RoleNotPermitted(
            $"promoting rule '{parsed.Id}' into the project's agent instruction file",
            parsed.ActingRole,
            CardOwner.ProductOwner);
        refusal = refusal with { Message = refusal.Message + " The promotion is recorded on the rule's own card as awaiting a Product Owner decision." };

        var comment = new CardComment(
            Id: $"promote-constitution-{Guid.NewGuid():N}",
            Author: parsed.ActingRole,
            Timestamp: parsed.Timestamp,
            Body: $"'{parsed.ActingRole.ToWireString()}' attempted to promote this rule into the project's agent " +
                "instruction file at " + parsed.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) +
                "; refused. This request awaits a Product Owner decision.",
            ReplyTo: null,
            To: CardOwner.ProductOwner,
            Resolves: null,
            UnknownHeaderFields: []);

        var writeResult = CardStore.AppendComment(repoRoot, resolved.FilePath!, comment, lockTimeout);
        return writeResult.Match<CommandOutcome>(
            onSuccess: _ => refusal,
            onNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to record the promotion request on."),
            onAlreadyExists: alreadyExists => throw new InvalidOperationException(
                $"unexpected 'already exists' appending a comment to '{alreadyExists.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read to record the promotion request: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason),
            // A rule card, never a block card — AppendCommentUnderExistingLock's round check is
            // gated on IsBlockCard, so this arm is unreachable from this call site specifically,
            // but CardWriteResult's Match is exhaustive over every caller of the shared surface.
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(resolved.FilePath!, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            });
    }

    /// <summary>
    /// The general shape every <see cref="Cards.CardRuleCompactOutcome"/> maps through — shared by
    /// <see cref="RunRuleCompact"/> and the <c>change archive --compact-family/--absorbs</c> hook
    /// (<see cref="RunChangeArchive"/>), so the two ways compaction can be invoked cannot drift on
    /// what a given disposition means (§7 block F brief item 6: "hook it rather than building a
    /// second archive path"). Same tuple shape as <see cref="ResolveCardReference"/>: a refusal to
    /// surface, or the successful <see cref="Cards.CardRuleCompactOutcome.Compacted"/> payload —
    /// each caller builds its own success response from that payload, since <see cref="
    /// RunRuleCompact"/>'s and <see cref="RunChangeArchive"/>'s responses report different things
    /// around the same underlying write.
    /// </summary>
    private static (CommandOutcome? Refusal, CardRuleCompactOutcome.Compacted? Compacted) ResolveRuleCompactOutcome(CardRuleCompactOutcome outcome) =>
        outcome.Match<(CommandOutcome?, CardRuleCompactOutcome.Compacted?)>(
            onCompacted: compacted => (null, compacted),
            onRoleNotPermitted: roleNotPermitted => (RoleNotPermitted(
                "compacting change-scoped rules", roleNotPermitted.AttemptedRole, roleNotPermitted.RequiredRole), null),
            onEmptyAbsorbSet: _ => (new CommandOutcome.Refusal(
                "empty-absorb-set", "compaction requires at least one rule to absorb; a family with no members is not a family."), null),
            onSelfAbsorption: selfAbsorption => (new CommandOutcome.Refusal(
                "self-absorption", $"'{selfAbsorption.Id}' cannot absorb itself; a family absorbing itself is not a coherent record."), null),
            onResolvedSelfAbsorption: resolvedSelfAbsorption => (new CommandOutcome.Refusal(
                "self-absorption", $"'{resolvedSelfAbsorption.Id}' cannot absorb itself; a family absorbing itself is not a coherent record.",
                resolvedSelfAbsorption.RefusingRule, resolvedSelfAbsorption.Remedy), null),
            onDuplicateAbsorbedRule: duplicate => (new CommandOutcome.Refusal(
                "duplicate-absorbed-rule", $"'{duplicate.Id}' was named more than once in the absorb set."), null),
            onResolvedDuplicateAbsorbedRule: resolvedDuplicate => (new CommandOutcome.Refusal(
                "duplicate-absorbed-rule", $"'{resolvedDuplicate.Id}' was named more than once in the absorb set.",
                resolvedDuplicate.RefusingRule, resolvedDuplicate.Remedy), null),
            onFamilyAlreadyDischarged: already => (new CommandOutcome.Refusal(
                "already-discharged",
                $"'{already.FilePath}' is already discharged; it cannot newly act as a family.",
                already.RefusingRule, already.Remedy), null),
            onAbsorbedAlreadyDischarged: already => (new CommandOutcome.Refusal(
                "already-discharged",
                $"'{already.FilePath}' is already discharged; absorbing an already-discharged rule is a refusal, not a re-absorption.",
                already.RefusingRule, already.Remedy), null),
            onInvalidStatus: invalid => (new CommandOutcome.Refusal(
                "invalid-register-status",
                $"'{invalid.FilePath}' has status '{invalid.Status}', which is not a valid register lifecycle " +
                $"state ({RegisterLifecycleStateWireFormat.RecognisedValues}) — register cards SHALL NOT occupy flow states.",
                invalid.RefusingRule, invalid.Remedy), null),
            onNotARuleCard: notARule => (WrongCardKind(
                notARule.FilePath, CardKind.Rule, notARule.Kind, "compaction only applies to rule cards") with
            {
                Rule = notARule.RefusingRule,
                Remedy = notARule.Remedy,
            }, null),
            onCardNotFound: notFound => (new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to compact."), null),
            onLayoutMismatch: layoutMismatch => (new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason), null),
            onCardCorrupt: corrupt => throw new InvalidOperationException(
                $"card '{corrupt.FilePath}' could not be read as a rule card: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));

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

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) —
    /// the one refusal every writer that mutates a block card mints when its stored <c>round</c>
    /// does not equal one plus the round-incrementing transitions in its own history. Names both
    /// figures and does not reconcile them — neither is privileged, since a stored count ahead of
    /// the history and a history ahead of the count are different failures, and guessing which is
    /// right would silently destroy the evidence of whichever was correct.</summary>
    private static CommandOutcome.Refusal RoundDisagreesWithHistory(string filePath, int storedRound, int expectedRound) =>
        new CommandOutcome.Refusal(
            "round-disagrees-with-history",
            $"'{filePath}' has stored round {storedRound}, but its own transition history implies round " +
            $"{expectedRound} (one plus its round-incrementing transitions); refusing to act on this card. " +
            "Neither figure is altered — correct the discrepancy directly on the card.");

    /// <summary>
    /// The one refusal every role-authorisation site mints (§7 block F remediation, Architect
    /// ruling: "one <c>role-not-permitted</c> code, whose message names the operation, the role
    /// that attempted it and the role required") — the <see cref="WrongCardKind"/> shape applied to
    /// roles instead of kinds: <c>change-scoped-compaction-requires-architect</c> named the same
    /// fact ("this role may not perform this operation") as any future role-gated refusal would,
    /// differing only in which role and which operation, both of which this message states rather
    /// than a bespoke code name. Block G's 7.12 (the agent-instruction-file refusal) is expected to
    /// route through this too, per the same ruling.
    /// </summary>
    private static CommandOutcome.Refusal RoleNotPermitted(string operation, CardOwner attemptedRole, CardOwner requiredRole) =>
        RoleNotPermitted(operation, attemptedRole, (IReadOnlyList<CardOwner>)[requiredRole]);

    /// <summary>
    /// The multi-role form of <see cref="RoleNotPermitted(string, Cards.CardOwner, Cards.CardOwner)"/>
    /// (§8 block A: review-certification's "Approval is role-bounded" permits two roles —
    /// <c>reviewer</c> and <c>supervisor</c> — not one). Same <c>role-not-permitted</c> code, same
    /// "one code whose message names the operation, the role that attempted it and the role(s)
    /// required" ruling; the single-role overload above is exactly this with a one-element list, so
    /// the two can never drift on wording.
    /// </summary>
    private static CommandOutcome.Refusal RoleNotPermitted(string operation, CardOwner attemptedRole, IReadOnlyList<CardOwner> requiredRoles) =>
        new CommandOutcome.Refusal(
            "role-not-permitted",
            $"{operation} is restricted to the '{string.Join("', '", requiredRoles.Select(static role => role.ToWireString()))}' " +
            $"role{(requiredRoles.Count == 1 ? "" : "s")}; '{attemptedRole.ToWireString()}' attempted it.");

    /// <summary>
    /// The <c>workingDirectory</c> seam (§7 block B, <c>## NEXT</c> item 2). Every path-taking
    /// handler used to pass a command's own file-path argument straight to <see cref="File.Exists"/>
    /// / <see cref="Cards.CardStore.ReadCard"/> / the write path, which resolves a relative argument
    /// against the real process working directory (<see cref="Directory.GetCurrentDirectory"/>) —
    /// never against <paramref name="workingDirectory"/>, the value <c>Run</c>'s own caller supplied
    /// and every handler otherwise treats as "where this invocation runs". In the shipped binary the
    /// two can never diverge (<c>Program.cs</c> seeds <c>workingDirectory</c> from
    /// <see cref="Directory.GetCurrentDirectory"/> itself), so this is a testability fix, not a
    /// behaviour change: a test can now set <paramref name="workingDirectory"/> to a temp directory
    /// and pass a relative <paramref name="filePath"/> without mutating the real process CWD (the
    /// <c>CurrentDirectoryMutatingTests</c> collection §6 needed for exactly that is gone — see the
    /// DEVLOG). An already-rooted <paramref name="filePath"/> passes through unchanged — the common
    /// case in this codebase's own tests, which almost always build an absolute temp-rooted path —
    /// so this has no effect on any caller that already supplies one.
    /// </summary>
    private static string ResolveFilePath(string workingDirectory, string filePath) =>
        Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(Path.Combine(workingDirectory, filePath));

    /// <summary>
    /// Validated <c>--section</c> (§7 block B, Product Owner ruling item 2): "a card raised within
    /// a section names it by the section card's id, and that id must resolve to a real section
    /// card." Thin wrapper over <see cref="ResolveCardReference"/> — the only caller today is
    /// <see cref="RunFindingRecord"/>, and this is kept as its own named function rather than
    /// inlined so that call site does not have to spell out the flag label and create-hint text
    /// itself.
    /// </summary>
    private static CommandOutcome? ValidateSection(string repoRoot, string sectionId) =>
        ResolveCardReference(
            repoRoot, sectionId, CardKind.Section, CardStore.IsSectionCard, "'--section'",
            "create the section first with 'section create'").Refusal;

    /// <summary>
    /// The general shape every §7 block B/C card-to-card reference field shares: resolve
    /// <paramref name="id"/> via <see cref="CardIdentityResolver.Resolve"/> — never by
    /// re-implementing directory enumeration or label matching at a call site — and either a
    /// refusal to surface, or the resolved file path and card when <paramref name="id"/> genuinely
    /// names a card of <paramref name="expectedKind"/>. <see cref="ValidateSection"/> (<c>--section</c>,
    /// §7 block B) and <c>RunObligationCreate</c> (<c>--owed-by</c>, §7 block C) both resolve
    /// against <see cref="CardKind.Section"/>; <c>RunDecisionSupersede</c> (§7 block C) resolves
    /// both its ids against <see cref="CardKind.Decision"/>. One resolver, reused by every caller,
    /// so a duplicate id or an unreadable file answers the same way regardless of which flag named
    /// it.
    ///
    /// <para>
    /// <b>Refusal codes, one per resolver case (Architect ruling: spec-named refusals get their own
    /// code).</b> All three failure shapes the resolver itself can report get distinct codes:
    /// <c>card-id-not-found</c> ("no card carries this id" — a different fact from the existing
    /// <c>card-not-found</c>, which means "no file at this path"), <c>duplicate-card-id</c> (shared
    /// with <see cref="Cards.FindingDegradationEvaluator"/>'s own duplicate-resolution refusal in
    /// <see cref="RunFindingStatus"/> — the same underlying fact, "more than one file claims this
    /// id", earns the same code rather than two spellings of it), and <c>card-id-unresolvable</c>
    /// (§6 remediation B3, re-applied: some file elsewhere in the record could not be read, so the
    /// id's absence cannot be confirmed). A resolved id naming a card that is not
    /// <paramref name="expectedKind"/> reuses the existing <c>wrong-card-kind</c> code via
    /// <see cref="WrongCardKind"/> — that is exactly what it already means.
    /// </para>
    /// </summary>
    private static (CommandOutcome? Refusal, string? FilePath, CardFile? Card) ResolveCardReference(
        string repoRoot, string id, CardKind expectedKind, Func<CardFile, bool> matchesExpectedKind, string flagLabel, string createHint) =>
        CardIdentityResolver.Resolve(repoRoot, id).Match<(CommandOutcome?, string?, CardFile?)>(
            onFound: (filePath, card) =>
                matchesExpectedKind(card)
                    ? (null, filePath, card)
                    : (WrongCardKind(filePath, expectedKind, card.Frontmatter.Kind, $"{flagLabel} must name a '{expectedKind.ToWireString()}' card"), null, null),
            onNotFound: notFoundId => (
                new CommandOutcome.Refusal(
                    "card-id-not-found",
                    $"{flagLabel} names id '{notFoundId}', but no card in the record carries it — {createHint}."),
                null, null),
            onDuplicate: (duplicateId, filePaths) => (
                new CommandOutcome.Refusal(
                    "duplicate-card-id",
                    $"{flagLabel} names id '{duplicateId}', but {filePaths.Count} card files claim it ({string.Join(", ", filePaths)}); " +
                    "refusing to guess which one is the target."),
                null, null),
            onUnreadable: (unreadableId, filePaths) => (
                new CommandOutcome.Refusal(
                    "card-id-unresolvable",
                    $"{flagLabel} names id '{unreadableId}', but {filePaths.Count} card file(s) elsewhere in the record could not " +
                    $"be read, so its presence cannot be confirmed or ruled out: {string.Join(", ", filePaths)}."),
                null, null));

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
                Refusal = new CliRefusal { Code = refusal.Code, Message = refusal.Message, Rule = refusal.Rule, Remedy = refusal.Remedy },
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
