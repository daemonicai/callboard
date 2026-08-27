using System.Linq;
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
/// dispatch visitor, which would still compile; (2) reflection — <c>private</c> is a compile-time
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

        internal abstract TResult Accept<TResult>(ICommandVisitor<TResult> visitor);

        internal sealed record Version : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        internal sealed record IndexRebuild(string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>block create</c> (§13, work-lifecycle: "Every block card is minted by the tool") —
        /// the creation door for a task-implementing <c>block</c> card, placed at <see
        /// cref="Cards.BlockFlowState.Drafting"/>. The other door (a section verdict's
        /// <c>--finding-new</c>, creating a remediation card at <c>briefed</c>) is <see
        /// cref="SectionVerdict"/>'s own <c>NewFindingManifestPaths</c> — this record is only the
        /// second, named door, never a general block-creation surface (Product Owner ruling: three
        /// named verbs, not a general creation surface).
        /// </summary>
        /// <param name="Tasks">The task references this block implements (e.g. <c>13.1</c>), in
        /// argv order — checked non-empty per item and non-empty overall during parse, the same
        /// repeatable-flag shape <c>--claims</c>/<c>--finding-recurred</c> already established.</param>
        internal sealed record BlockCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, IReadOnlyList<string> Tasks, string ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>block base --base &lt;sha&gt;</c> (§13, work-lifecycle: "Blocks carry their brief
        /// context") — the recording door <c>block transition</c>'s own refusal has named since §5
        /// without one existing. Path-addressed, not <c>--id</c> (Architect ruling item 1) — the
        /// same convention <see cref="BlockGate"/> and <see cref="BlockTransition"/> already use.
        /// </summary>
        internal sealed record BlockBase(
            string FilePath, string BaseCommit, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <param name="BlockingCardId">The id of the card this block is now blocked by. Not
        /// resolved to an actual card during parse or execute — see the block D DEVLOG brief:
        /// nothing in this section builds an id-to-card lookup, so this stays a plain string, the
        /// same way <see cref="BlockTransition.FilePath"/> stays a path rather than a resolved
        /// identity.</param>
        internal sealed record BlockAddBlocker(
            string FilePath, string BlockingCardId, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        internal sealed record BlockRemoveBlocker(
            string FilePath, string BlockingCardId, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        internal sealed record SectionClose(
            string FilePath, CardOwner ActingRole, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>obligation create</c> (§7 block A/C). Scope is always <see cref="CardScope.Change"/>,
        /// so <c>--change</c> is required, the same way <c>section create</c>'s is.
        /// <see cref="OwedById"/> (§7 block C, register: "An obligation SHALL name the section
        /// expected to discharge it") is required at parse time — <c>CommandParser.
        /// ParseObligationCreate</c> refuses a missing <c>--section</c> before this record can even
        /// be constructed, so every <see cref="ObligationCreate"/> reaching this handler already
        /// carries one.
        /// </summary>
        internal sealed record ObligationCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string ChangeName, string OwedById, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>decision create</c> (§7 block A). Scope is always <see cref="CardScope.Capability"/>,
        /// which <see cref="Cards.CardLayout.DirectoryFor"/> resolves without a change name — so, unlike
        /// <see cref="ObligationCreate"/>/<see cref="SectionCreate"/>, there is no <c>--change</c> flag.
        /// </summary>
        internal sealed record DecisionCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>section create</c> (§7 block A, Product Owner ruling: "section create is in §7's
        /// scope"). Scope is always <see cref="CardScope.Change"/> — the same fixed scope
        /// <see cref="CardScopeRules.Validate"/> already gives <c>section</c> in 4.4's table.
        /// </summary>
        internal sealed record SectionCreate(
            string FilePath, string Title, CardOwner ActingRole, string Body, string ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            string FilePath, string Title, CardOwner ActingRole, CardOwner OwedByRole, string Body, string? SectionId, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>question answer</c> (§9 block D, process-enforcement: "An answer must be written
        /// down"). <see cref="DecisionId"/> is resolved through <see cref="Cards.
        /// CardIdentityResolver"/> in <c>RunQuestionAnswer</c> before <see cref="Cards.CardStore.
        /// AnswerQuestion"/> is ever called, the same "argv names it, execute resolves it against
        /// the record" split every other card-reference field on this surface follows.
        /// <see cref="DecisionId"/>/<see cref="InlineAnswer"/> are never both <see langword="null"/>
        /// — <see cref="Callboard.Cli.CommandParser.ParseQuestionAnswer"/> already refused a call
        /// naming neither.
        /// </summary>
        internal sealed record QuestionAnswer(
            string FilePath, CardOwner ActingRole, string? DecisionId, string? InlineAnswer, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>question defer</c> (§9 block D — the question status vocabulary entire, including
        /// <c>deferred</c>). <see cref="Target"/> is free text, never resolved against the record —
        /// see <see cref="Cards.QuestionCardFields.DeferredTarget"/>'s own doc comment for why.
        /// </summary>
        internal sealed record QuestionDefer(
            string FilePath, CardOwner ActingRole, string Target, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>obligation promote</c> (§9 block F, register: "Promotion SHALL NOT be limited to
        /// rules... An <c>obligation</c> that outlives the change it was raised in SHALL be
        /// promotable to a wider scope on the same terms"). Same identity-addressing shape as
        /// <see cref="RulePromote"/> — <see cref="Id"/> is a card id resolved through
        /// <see cref="Cards.CardIdentityResolver"/> at execute time, never a caller-supplied path.
        /// <see cref="ChangeName"/> is required unconditionally for the same reason block A2's
        /// remediation made it required on <see cref="RulePromote"/>: a refusal against a still
        /// change-scoped obligation (the ordinary case this verb serves) cannot anchor its own
        /// <see cref="Cards.CardRefusalEntry"/> without it.
        /// </summary>
        internal sealed record ObligationPromote(
            string Id, CardOwner ActingRole, string ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>obligation decline</c> (§9 block F, register: "An obligation that will not be met
        /// SHALL be closable by declining it with a recorded reason"). <see cref="Reason"/> is
        /// required at this door — the same "a threaded value must be required at the door a real
        /// caller uses" lesson block A2's remediation drew for <see cref="RulePromote"/>'s
        /// <c>--change</c> — never optional here even though <see cref="Cards.CardStore.
        /// DeclineObligation"/> defends the same requirement again on its own refusal path.
        /// </summary>
        internal sealed record ObligationDecline(
            string Id, CardOwner ActingRole, string Reason, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>comment add</c> (§13, card-model: "The verbs that dispose of a thread SHALL NOT be
        /// the only ones that can start one"). <see cref="CardId"/> is resolved without a kind
        /// filter, the same way <see cref="CommentResolve"/>'s own <see cref="CommentResolve.
        /// CardId"/> already is (Architect ruling item 3: "any card kind accepts a comment").
        /// <see cref="To"/> and <see cref="ReplyTo"/> are both optional (ruling 1: an unaddressed
        /// comment is a note on the record, legitimate on its own); <see cref="Body"/> is read from
        /// stdin and required non-empty, the same discipline <see cref="CommentResolve"/>'s own
        /// door applies as of §10 block D.
        /// </summary>
        internal sealed record CommentAdd(
            string CardId, CardOwner ActingRole, string Body, CardOwner? To, string? ReplyTo, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>comment resolve</c> (§9 remediation, round two — S4: give <c>9.6</c>'s "resolve" and
        /// <c>9.3</c>'s "resolve the following thread(s)" a real verb). Addressed the same way every
        /// other card-to-card reference field on this surface is (Architect ruling): <see cref="CardId"/>
        /// is a card id resolved through <see cref="Cards.CardIdentityResolver"/> at execute time, and
        /// <see cref="CommentId"/> names the target <see cref="Cards.CardComment.Id"/> directly on the
        /// resolved card — no second, repo-wide resolver is invented for a comment id (unlike <see
        /// cref="Cards.NitResolver"/>, which exists only because a nit's raising verb never took a card
        /// id in the first place). <see cref="Body"/> is the resolving comment's own narrative, always
        /// read from stdin (possibly empty — the same "always read, sometimes empty" shape <see
        /// cref="NitDisposition"/> already has).
        /// </summary>
        internal sealed record CommentResolve(
            string CardId, string CommentId, CardOwner ActingRole, string Body, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>comment decline --reason</c> (§9 remediation, round two — S4). Same addressing as <see
        /// cref="CommentResolve"/>; <see cref="Reason"/> is required at this door, unconditionally —
        /// the same "required at the door a real caller uses" lesson block A2's remediation drew for
        /// <see cref="RulePromote"/>'s <c>--change</c> and block F's <see cref="ObligationDecline"/>
        /// already applies to its own <c>--reason</c> — never optional here even though <see
        /// cref="Cards.CardStore.ResolveComment"/> defends the same requirement again on its own
        /// refusal path.
        /// </summary>
        internal sealed record CommentDecline(
            string CardId, string CommentId, CardOwner ActingRole, string Reason, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>comment promote --to question|decision</c> (§9 remediation, round two — S4). The one
        /// verb in this trio that writes two cards — a new <c>question</c>/<c>decision</c> card named
        /// by <see cref="RaiseFilePath"/>/<see cref="Title"/>, plus the resolving comment on the
        /// existing card <see cref="CommentResolve"/> also addresses — reusing <see cref="Cards.
        /// CardStore.RecordFinding"/>'s two-card, two-lock discipline rather than inventing a fourth
        /// multi-card write shape (§8a supervisor finding on <c>CardStore.cs</c>). <see cref="OwedByRole"/>
        /// is required only when <see cref="ToKind"/> is <see cref="CardKind.Question"/> — the role
        /// that owes the answer, which becomes the new card's owner, the same <c>question create</c>
        /// discipline (it is <see langword="null"/> for <see cref="CardKind.Decision"/>, whose owner is
        /// <see cref="ActingRole"/>, same as <c>decision create</c>). <see cref="Body"/> is the new
        /// card's own content, read from stdin, required redirected — the same discipline <c>question
        /// create</c>/<c>nit raise</c> already have for a brand-new card's body.
        /// </summary>
        internal sealed record CommentPromote(
            string CardId, string CommentId, CardOwner ActingRole, CardKind ToKind, string RaiseFilePath, string Title,
            CardOwner? OwedByRole, string Body, string? ChangeName, string WorkingDirectory, DateTimeOffset Timestamp) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>rule review [--ceiling &lt;n&gt;]</c> (§10 block E, carried item B from §7's close,
        /// register: "Register size triggers review, never eviction"). Read-only, so no timestamp,
        /// no lock and no <see cref="CardOwner"/> — the same shape <see cref="State"/> takes, for
        /// the same reason: nothing here writes to any card. <see cref="Ceiling"/> is always a
        /// concrete value (the caller's <c>--ceiling</c>, or <see cref="CommandDispatcher.
        /// DefaultRuleReviewCeiling"/> when the flag is absent) — <see cref="CeilingIsDefault"/>
        /// is what lets the response state <em>which</em> one applied, since a ceiling is only
        /// "stated" (the register's own wording) if the caller can see whether it came from the
        /// flag or the default.
        /// </summary>
        internal sealed record RuleReview(int Ceiling, bool CeilingIsDefault, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
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
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>context --role &lt;role&gt;</c> (§10 block A, working-context: "given a role, the
        /// system SHALL return that role's complete working context"). Read-only, so no timestamp
        /// and no lock — the only two things this carries are what <see cref="Cards.
        /// WorkingContextAssembler.Build"/> needs. No <c>--change</c>: <see cref="Cards.CardLayout.
        /// ResolveLiveRecordDirectories"/> self-discovers every live change directory from
        /// <see cref="WorkingDirectory"/>'s repository root alone, so the queue is composable
        /// without one (Architect ruling, §10 block A brief: "if you find the queue cannot be
        /// composed without knowing the change, refuse ... rather than guessing a default" — it
        /// can, so no flag was added for one this surface would never use).
        /// </summary>
        internal sealed record Context(CardOwner Role, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>state</c> (§10 block C, working-context: "a summary of overall process state").
        /// Not role-scoped — unlike <see cref="Context"/>, this carries no <see cref="CardOwner"/>
        /// at all, matching the spec's own scenario ("any role requests the state summary").
        /// Read-only, so no timestamp and no lock, for the same reason <see cref="Context"/> has
        /// neither. No <c>--change</c>, for the same reason <see cref="Context"/> has none: <see
        /// cref="Cards.CardLayout.ResolveLiveRecordDirectories"/> self-discovers every live change.
        /// </summary>
        internal sealed record State(string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>card show &lt;id&gt;</c> (§11 block B, record-retrieval: "the system SHALL return a
        /// card's full content, including every comment on it, given the card's identity"). Kind-
        /// agnostic by design — <see cref="Id"/> is resolved through <see cref="Cards.
        /// CardIdentityResolver.Resolve"/> without a kind filter, the same reason <see
        /// cref="ResolveAnyCardReference"/> exists for the <c>comment</c> verbs: an id can name any
        /// card kind, so retrieval by identity cannot be a kind-specific verb. Read-only, so no
        /// timestamp and no lock, for the same reason <see cref="Context"/>/<see cref="State"/> have
        /// neither (ADR-0004: a pure read takes no lock).
        /// </summary>
        internal sealed record CardShow(string Id, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>section export &lt;section-id&gt; --out &lt;path&gt; [--force]</c> (§11 block C,
        /// record-retrieval: "The system SHALL render a section ... as a single readable
        /// document"). <see cref="SectionId"/> is resolved through <see cref="Cards.
        /// CardIdentityResolver.Resolve"/>, the same identity-addressing every other §11 verb uses,
        /// never a hand-rolled directory walk. A pure read of the record — no timestamp, no acting
        /// role, no lock on any card; the only write this drives is <see cref="OutputPath"/> itself,
        /// via temp-file-then-rename (D7).
        /// </summary>
        internal sealed record SectionExport(string SectionId, string OutputPath, bool Force, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>change export &lt;change-name&gt; --out &lt;path&gt; [--force]</c> (§11 block C) —
        /// <see cref="SectionExport"/>'s whole-change sibling. <see cref="ChangeName"/> names a
        /// directory (<see cref="Cards.CardLayout.ChangesDirectory"/>), not a card id, the same
        /// reason <see cref="ChangeArchive.ChangeName"/> is a directory name rather than an
        /// identity to resolve.
        /// </summary>
        internal sealed record ChangeExport(string ChangeName, string OutputPath, bool Force, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }

        /// <summary>
        /// <c>view --out &lt;path&gt; [--force]</c> (§12 block B, record-retrieval: "a local,
        /// read-only, human-readable view of the board" — a verb the spec does not name; the
        /// Architect's ruling). No positional argument — the view covers the whole live record,
        /// self-discovered the same way <see cref="State"/> and <see cref="Context"/> are, never
        /// one section or change at a time. A pure read: no timestamp, no acting role, no lock on
        /// any card; the only write this drives is <see cref="OutputPath"/> itself, the same
        /// temp-file-then-rename discipline <see cref="SectionExport"/>/<see cref="ChangeExport"/>
        /// already use (D7).
        /// </summary>
        internal sealed record View(string OutputPath, bool Force, string WorkingDirectory) : ParsedCommand
        {
            internal override TResult Accept<TResult>(ICommandVisitor<TResult> visitor) => visitor.Visit(this);
        }
    }

    /// <summary>
    /// One <c>Visit</c> overload per <see cref="ParsedCommand"/> case, in the same order the
    /// verbs were added — the visitor shape carrying the invariant a 36-parameter
    /// <c>Match&lt;TResult&gt;</c> used to carry: adding a 37th case to <see cref="ParsedCommand"/>
    /// adds a member here, which is <c>CS0535</c> ("does not implement interface member") at every
    /// implementer, the same "unhandled case is a compile error" guarantee <see cref="CommandOutcome"/>
    /// and the other closed unions in this codebase get from a private constructor plus an
    /// abstract match method — this interface is that method's replacement for
    /// <see cref="ParsedCommand"/> specifically, not a different convention. <see cref="Run"/>'s
    /// single call site (<see cref="ParsedCommand.Accept{TResult}"/>) is the only place these are
    /// ever implemented against a real <typeparamref name="TResult"/>.
    /// </summary>
    internal interface ICommandVisitor<out TResult>
    {
        TResult Visit(ParsedCommand.Version command);

        TResult Visit(ParsedCommand.IndexRebuild command);

        TResult Visit(ParsedCommand.BlockCreate command);

        TResult Visit(ParsedCommand.BlockTransition command);

        TResult Visit(ParsedCommand.BlockGate command);

        TResult Visit(ParsedCommand.BlockBase command);

        TResult Visit(ParsedCommand.BlockAddBlocker command);

        TResult Visit(ParsedCommand.BlockRemoveBlocker command);

        TResult Visit(ParsedCommand.SectionVerdict command);

        TResult Visit(ParsedCommand.SectionClose command);

        TResult Visit(ParsedCommand.SectionAuthorise command);

        TResult Visit(ParsedCommand.SectionStatus command);

        TResult Visit(ParsedCommand.FindingRecord command);

        TResult Visit(ParsedCommand.FindingStatus command);

        TResult Visit(ParsedCommand.RuleCreate command);

        TResult Visit(ParsedCommand.HazardCreate command);

        TResult Visit(ParsedCommand.ObligationCreate command);

        TResult Visit(ParsedCommand.DecisionCreate command);

        TResult Visit(ParsedCommand.SectionCreate command);

        TResult Visit(ParsedCommand.QuestionCreate command);

        TResult Visit(ParsedCommand.RegisterDischarge command);

        TResult Visit(ParsedCommand.DecisionSupersede command);

        TResult Visit(ParsedCommand.ChangeArchive command);

        TResult Visit(ParsedCommand.RulePromote command);

        TResult Visit(ParsedCommand.RuleAuthor command);

        TResult Visit(ParsedCommand.RuleCompact command);

        TResult Visit(ParsedCommand.RuleProposeCompact command);

        TResult Visit(ParsedCommand.RulePromoteConstitution command);

        TResult Visit(ParsedCommand.RuleReview command);

        TResult Visit(ParsedCommand.BlockApprove command);

        TResult Visit(ParsedCommand.NitRaise command);

        TResult Visit(ParsedCommand.NitDisposition command);

        TResult Visit(ParsedCommand.QuestionAnswer command);

        TResult Visit(ParsedCommand.QuestionDefer command);

        TResult Visit(ParsedCommand.ObligationPromote command);

        TResult Visit(ParsedCommand.ObligationDecline command);

        TResult Visit(ParsedCommand.CommentAdd command);

        TResult Visit(ParsedCommand.CommentResolve command);

        TResult Visit(ParsedCommand.CommentPromote command);

        TResult Visit(ParsedCommand.CommentDecline command);

        TResult Visit(ParsedCommand.Context command);

        TResult Visit(ParsedCommand.State command);

        TResult Visit(ParsedCommand.CardShow command);

        TResult Visit(ParsedCommand.SectionExport command);

        TResult Visit(ParsedCommand.ChangeExport command);

        TResult Visit(ParsedCommand.View command);
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

    /// <summary>
    /// <see cref="Run"/>'s single <see cref="ICommandVisitor{TResult}"/> implementation — the
    /// visitor arm that used to be 36 named parameters at <see cref="ParsedCommand.Accept{TResult}"/>'s
    /// one call site is now one short method per case, listed in the same order those parameters
    /// were declared in so the diff from the old shape reads as a move. Carries what every handler
    /// call needs beyond the parsed command itself: <paramref name="LockTimeout"/> — the same
    /// value every arm of the old delegate list closed over — and <paramref name="
    /// RecognisedCommandName"/>, the exact <see cref="CliEnvelope.Command"/> string <see
    /// cref="WriteEnvelope"/> will embed for this invocation (§10 block B review, blocker 1: it
    /// echoes every consumed argument, not just the verb — <c>"context --role worker"</c>, not
    /// <c>"context"</c> — so <c>context</c> is the one handler that needs it before it can price
    /// its own response against the line that actually ships). Computed once in <see cref="Run"/>,
    /// after parsing has finished consuming every token it will consume, so it is already the
    /// final value <see cref="WriteEnvelope"/> recomputes the same way afterward.
    /// </summary>
    private readonly record struct CommandRunner(TimeSpan LockTimeout, string RecognisedCommandName) : ICommandVisitor<CommandOutcome>
    {
        public CommandOutcome Visit(ParsedCommand.Version command) => RunVersion();

        public CommandOutcome Visit(ParsedCommand.IndexRebuild command) => RunIndexRebuild(command.WorkingDirectory);

        public CommandOutcome Visit(ParsedCommand.BlockTransition command) => RunBlockTransition(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.BlockGate command) => RunBlockGate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.BlockBase command) => RunBlockBase(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.BlockAddBlocker command) => RunBlockAddBlocker(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.BlockRemoveBlocker command) => RunBlockRemoveBlocker(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.SectionVerdict command) => RunSectionVerdict(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.SectionClose command) => RunSectionClose(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.SectionAuthorise command) => RunSectionAuthorise(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.SectionStatus command) => RunSectionStatus(command);

        public CommandOutcome Visit(ParsedCommand.FindingRecord command) => RunFindingRecord(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.FindingStatus command) => RunFindingStatus(command);

        public CommandOutcome Visit(ParsedCommand.RuleCreate command) => RunRuleCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.HazardCreate command) => RunHazardCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.ObligationCreate command) => RunObligationCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.DecisionCreate command) => RunDecisionCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.BlockCreate command) => RunBlockCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.SectionCreate command) => RunSectionCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.QuestionCreate command) => RunQuestionCreate(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RegisterDischarge command) => RunRegisterDischarge(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.DecisionSupersede command) => RunDecisionSupersede(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.ChangeArchive command) => RunChangeArchive(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RulePromote command) => RunRulePromote(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RuleAuthor command) => RunRuleAuthor(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RuleCompact command) => RunRuleCompact(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RuleProposeCompact command) => RunRuleProposeCompact(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RulePromoteConstitution command) => RunRulePromoteConstitution(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.RuleReview command) => RunRuleReview(command);

        public CommandOutcome Visit(ParsedCommand.BlockApprove command) => RunBlockApprove(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.NitRaise command) => RunNitRaise(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.NitDisposition command) => RunNitDisposition(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.QuestionAnswer command) => RunQuestionAnswer(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.QuestionDefer command) => RunQuestionDefer(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.ObligationPromote command) => RunObligationPromote(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.ObligationDecline command) => RunObligationDecline(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.CommentAdd command) => RunCommentAdd(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.CommentResolve command) => RunCommentResolve(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.CommentPromote command) => RunCommentPromote(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.CommentDecline command) => RunCommentDecline(command, LockTimeout);

        public CommandOutcome Visit(ParsedCommand.Context command) => RunContext(command, RecognisedCommandName);

        public CommandOutcome Visit(ParsedCommand.State command) => RunState(command);

        public CommandOutcome Visit(ParsedCommand.CardShow command) => RunCardShow(command);

        public CommandOutcome Visit(ParsedCommand.SectionExport command) => RunSectionExport(command);

        public CommandOutcome Visit(ParsedCommand.ChangeExport command) => RunChangeExport(command);

        public CommandOutcome Visit(ParsedCommand.View command) => RunView(command);
    }

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

            // Parsing has already consumed every token it will consume by this point, so this is
            // already the final value — the same one WriteEnvelope's own call below recomputes —
            // which is what lets CommandRunner hand it to `context` before that handler needs to
            // price its own response against the line that will actually ship (§10 block B review,
            // blocker 1).
            var recognisedCommandName = RecognisedCommand(command, arguments);
            var outcome = parseResult.Match(
                onReady: ready => ready.Command.Accept(new CommandRunner(resolvedLockTimeout, recognisedCommandName)),
                onRefused: refused => refused.Refusal);

            WriteEnvelope(output, recognisedCommandName, outcome);

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
    /// enforcement is merely unavailable. Called only from <see cref="Run"/>'s dispatch visitor over
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
                "a brief must name the commit it was carved against — pass --base or record one with 'block base' before briefing.",
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
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onUnresolvedThreadsAddressedToActor: unresolved => UnresolvedThreadsAddressedToActor(filePath, unresolved.ActorRole, unresolved.ThreadIds) with
            {
                Rule = unresolved.RefusingRule,
                Remedy = unresolved.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to transition."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            // Refusal-shaped, not tool-failure (§12 block A, round two — supersedes this site's own
            // prior comment): a corrupt card is enforcement running and refusing, not enforcement
            // being unavailable — the tool read the record, the record is definitively bad, and the
            // reason names the field, the value, the kind and the recognised values. onToolFailure
            // below is the one that stays a throw: that disposition is genuinely "the tool could
            // not check", the same place index rebuild's own SQLite I/O failures land.
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason),
            onBlockedByOpenProductOwnerQuestion: blocked => new CommandOutcome.Refusal(
                "blocked-by-open-product-owner-question",
                $"'{filePath}' is blocked by open product-owner question '{blocked.QuestionId}' (\"{blocked.QuestionTitle}\") and cannot advance.",
                blocked.RefusingRule, blocked.Remedy));
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
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record a gate result on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>block base --base &lt;sha&gt;</c> (§13, work-lifecycle: "Blocks carry their brief
    /// context") — the door <c>block transition</c>'s own <c>base-not-recorded</c> refusal has
    /// named since §5 without one existing. <see cref="Cards.CardStore.RecordBase"/> carries the
    /// discipline; this handler only resolves the path and maps the outcome. <c>base-immutable</c>
    /// is the same refusal code <c>block transition</c>'s own base-mismatch check already uses
    /// (Architect ruling item 2) — see <see cref="Cards.CardBlockRecordBaseOutcome"/>'s own doc
    /// comment for why the two are not near-duplicates despite sharing it.
    /// </summary>
    private static CommandOutcome RunBlockBase(ParsedCommand.BlockBase parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.RecordBase(
            repoRoot, filePath, parsed.BaseCommit, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onRecorded: recorded => new CommandOutcome.Success(new BlockBaseResult
            {
                FilePath = filePath,
                Base = recorded.Base,
                ActingRole = recorded.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onNotABlockCard: notABlock => WrongCardKind(filePath, CardKind.Block, notABlock.Kind, "a brief's base only applies to a block card") with
            {
                Rule = notABlock.RefusingRule,
                Remedy = notABlock.Remedy,
            },
            onNotAtDrafting: notAtDrafting => new CommandOutcome.Refusal(
                "not-at-drafting",
                $"'{filePath}' is at '{notAtDrafting.CurrentState.ToWireString()}', not 'drafting' — base is recorded only before the block is first briefed.",
                notAtDrafting.RefusingRule, notAtDrafting.Remedy),
            onBaseImmutable: immutable => new CommandOutcome.Refusal(
                "base-immutable",
                $"'base' is already recorded as '{immutable.RecordedBase}' and cannot change; supplied '{immutable.AttemptedBase}'.",
                immutable.RefusingRule, immutable.Remedy),
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(filePath, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record a base on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
    /// never sees <see cref="CardBlockedByOutcome.AlreadyBlockedBy"/> or
    /// <see cref="CardBlockedByOutcome.BlockerUnresolvable"/>, deliberately — §11 block A keeps
    /// removal accepting any id, resolvable or not) — the exhaustive
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
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to update blocked_by on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason),
            onBlockerUnresolvable: unresolvable => new CommandOutcome.Refusal(
                "blocker-unresolvable",
                $"'{filePath}' names '{unresolvable.BlockerId}' as a blocker, but {unresolvable.Reason}."));

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
            // §9 block B (reviewer/architect ruling): unlike the pre-lock RoleNotPermitted
            // checks elsewhere, RecordApprovalUnderExistingLock's role check runs after a
            // successful ReadCard under the lock it already holds, so it is card-addressed and
            // records — see CardApprovalOutcome.RoleNotPermitted's own doc comment.
            onRoleNotPermitted: roleNotPermitted => RoleNotPermitted(
                "recording an approval", roleNotPermitted.AttemptedRole, [CardOwner.Reviewer, CardOwner.Supervisor]) with
            {
                Rule = roleNotPermitted.RefusingRule,
                Remedy = roleNotPermitted.Remedy,
            },
            onUndefinedTransition: undefined => new CommandOutcome.Refusal(
                "undefined-transition",
                $"no transition 'approve' from '{undefined.CurrentState.ToWireString()}'. " +
                $"Available: {(undefined.Available.Count == 0 ? "none" : string.Join(", ", undefined.Available.Select(static t => t.Name)))}.",
                undefined.RefusingRule, undefined.Remedy),
            onUndispositionedNits: undispositioned => new CommandOutcome.Refusal(
                "undispositioned-nits",
                $"'{resolved.FilePath}' cannot leave 'in-review' — the following nit(s) have no disposition: " +
                $"{string.Join(", ", undispositioned.NitIds)}.",
                undispositioned.RefusingRule, undispositioned.Remedy),
            onNotABlockCard: notABlock => WrongCardKind(resolved.FilePath!, CardKind.Block, notABlock.Kind, "'block approve' only applies to a block card") with
            {
                Rule = notABlock.RefusingRule,
                Remedy = notABlock.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(resolved.FilePath!, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(resolved.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onUnresolvedThreadsAddressedToActor: unresolved => UnresolvedThreadsAddressedToActor(resolved.FilePath!, unresolved.ActorRole, unresolved.ThreadIds) with
            {
                Rule = unresolved.RefusingRule,
                Remedy = unresolved.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to approve."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason),
            onBlockedByOpenProductOwnerQuestion: blocked => new CommandOutcome.Refusal(
                "blocked-by-open-product-owner-question",
                $"'{resolved.FilePath}' is blocked by open product-owner question '{blocked.QuestionId}' (\"{blocked.QuestionTitle}\") and cannot be approved.",
                blocked.RefusingRule, blocked.Remedy));
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
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(resolved.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
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
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onUnresolvedThreadsAddressedToActor: unresolved => UnresolvedThreadsAddressedToActor(filePath, unresolved.ActorRole, unresolved.ThreadIds) with
            {
                Rule = unresolved.RefusingRule,
                Remedy = unresolved.Remedy,
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
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
            onNotASectionCard: notASection => WrongCardKind(filePath, CardKind.Section, notASection.Kind, "verdicts only apply to a section card") with
            {
                Rule = notASection.RefusingRule,
                Remedy = notASection.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(disagreement.FilePath, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(handEntered.FilePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to record a verdict on."),
            // §9 block B, standing instruction 2: split from CardNotFound above — this occurrence
            // is post-lock, with the section card already resolved and anchored, so it records.
            onRecurringTargetNotFound: recurringNotFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{recurringNotFound.FilePath}' to record a verdict on.",
                recurringNotFound.RefusingRule, recurringNotFound.Remedy),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onRecurringFindingNotApproved: notApproved => new CommandOutcome.Refusal(
                "recurring-finding-not-approved",
                $"'{notApproved.CardId}' ('{notApproved.FilePath}') is not 'approved' (it is " +
                $"'{notApproved.CurrentState.ToWireString()}') — 'finding-recurred' only returns a remediation " +
                "card that is currently approved.",
                notApproved.RefusingRule, notApproved.Remedy),
            onRecurringFindingTargetsTaskImplementingBlock: taskImplementing => new CommandOutcome.Refusal(
                "recurring-finding-targets-task-implementing-block",
                $"'{taskImplementing.CardId}' ('{taskImplementing.FilePath}') carries tasks — it is a task-" +
                "implementing block, not a remediation card, and 'finding-recurred' never targets one. Raise the " +
                "finding as new instead, with '--finding-new'.",
                taskImplementing.RefusingRule, taskImplementing.Remedy),
            onFindingAlreadyOwned: alreadyOwned => new CommandOutcome.Refusal(
                "finding-already-owned",
                $"finding '{alreadyOwned.Key}' is already owned by '{alreadyOwned.OwningCardId}' " +
                $"('{alreadyOwned.OwningCardFilePath}') — a recurrence SHALL NOT create a second card for a " +
                $"finding a card already owns. Use '--finding-recurred {alreadyOwned.OwningCardId}' instead, or " +
                "give the new finding a different '--finding-new' key.",
                alreadyOwned.RefusingRule, alreadyOwned.Remedy),
            onNewFindingCardAlreadyExists: alreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{alreadyExists.FilePath}'.",
                alreadyExists.RefusingRule, alreadyExists.Remedy),
            onRemediationBoundExceeded: boundExceeded => new CommandOutcome.Refusal(
                "remediation-bound-exceeded",
                $"the section already carries {boundExceeded.VerdictNumber - 1} 'request-changes' verdicts " +
                "(a section admits two without ceremony) and this would be number " +
                $"{boundExceeded.VerdictNumber} — {boundExceeded.AuthorisationsRecorded} authorisation" +
                $"{(boundExceeded.AuthorisationsRecorded == 1 ? "" : "s")} recorded, " +
                $"{Math.Max(boundExceeded.UnspentAuthorisations, 0)} unspent. A recorded Product Owner " +
                "authorisation ('section authorise --role product-owner --reason <text>') would satisfy it.",
                boundExceeded.RefusingRule, boundExceeded.Remedy),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>section close</c> (§5 block E, work-lifecycle: "closing it SHALL record the acting role
    /// and the time"; §8a block A, "Approval is provisional until the section closes"; §9 block E,
    /// "Section close settles its obligations/questions/addressed threads", "Work cannot proceed
    /// past a stop-and-ask"): lands every approved block the section owns and closes the section,
    /// via <see cref="CardStore.CloseSection"/> — every closing condition is decided there; this
    /// handler only maps the outcome. <see langword="private"/>: <see cref="CommandParser"/> cannot
    /// name this method.
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
                $"'{already.FilePath}' is already closed.",
                already.RefusingRule, already.Remedy),
            onNotASectionCard: notASection => WrongCardKind(filePath, CardKind.Section, notASection.Kind, "only a section card can be closed by this verb") with
            {
                Rule = notASection.RefusingRule,
                Remedy = notASection.Remedy,
            },
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(disagreement.BlockFilePath, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(handEntered.FilePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onBlockNotApproved: notApproved => new CommandOutcome.Refusal(
                "block-not-approved",
                $"block '{notApproved.BlockId}' ('{notApproved.BlockFilePath}') is '{notApproved.ActualState.ToWireString()}', not 'approved' — every block in a " +
                "section must be approved before the section can close.",
                notApproved.RefusingRule, notApproved.Remedy),
            onBlockGateFailed: gateFailed => new CommandOutcome.Refusal(
                "block-gate-failed",
                $"block '{gateFailed.BlockId}' ('{gateFailed.BlockFilePath}') carries gate '{gateFailed.GateLabel}' recorded at exit code {gateFailed.ExitCode}, " +
                "not 0 — every gate a block carries must have passed before the section can close.",
                gateFailed.RefusingRule, gateFailed.Remedy),
            onBlockGateAbsent: gateAbsent => new CommandOutcome.Refusal(
                "block-gate-absent",
                $"block '{gateAbsent.BlockId}' ('{gateAbsent.BlockFilePath}') has no exit code recorded this round for gate '{gateAbsent.GateLabel}' — " +
                "an absent gate is not a pass by default. Record it with 'block gate' before closing.",
                gateAbsent.RefusingRule, gateAbsent.Remedy),
            onOpenObligations: openObligations => new CommandOutcome.Refusal(
                "section-close-open-obligations",
                $"section '{openObligations.SectionId}' still owes {openObligations.Obligations.Count} open obligation(s): " +
                string.Join(", ", openObligations.Obligations.Select(static o => $"{o.Id} (\"{o.Title}\")")) +
                " — each must be discharged ('obligation discharge'), promoted to a wider scope " +
                "('obligation promote'), or declined with a recorded reason ('obligation decline') before this section can close.",
                openObligations.RefusingRule, openObligations.Remedy),
            onOpenUndeferredQuestion: openQuestion => new CommandOutcome.Refusal(
                "section-close-open-question",
                $"question '{openQuestion.QuestionId}' (\"{openQuestion.QuestionTitle}\") is open and raised in section '{openQuestion.SectionId}' — " +
                "answer or defer it before this section can close.",
                openQuestion.RefusingRule, openQuestion.Remedy),
            onUnresolvedAddressedThread: unresolvedThread => new CommandOutcome.Refusal(
                "section-close-unresolved-thread",
                $"card '{unresolvedThread.CardId}' ('{unresolvedThread.CardFilePath}') carries unresolved addressed thread(s): " +
                $"{string.Join(", ", unresolvedThread.ThreadIds)} — resolve, promote to a 'question', promote to a 'decision', or " +
                "decline with a recorded reason, before this section can close.",
                unresolvedThread.RefusingRule, unresolvedThread.Remedy),
            onBlockedByOpenProductOwnerQuestion: blocked => new CommandOutcome.Refusal(
                "blocked-by-open-product-owner-question",
                $"block '{blocked.BlockId}' ('{blocked.BlockFilePath}') is blocked by open question '{blocked.QuestionId}' " +
                $"(\"{blocked.QuestionTitle}\"), owned by the product owner — it cannot land while this section closes.",
                blocked.RefusingRule, blocked.Remedy),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found",
                $"no card file exists at '{notFound.FilePath}' to close."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
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
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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

                // §9 block E, architect ruling on 9.6's ageing-thread prompt: this is the earlier,
                // during-the-section's-life surfacing the requirement's own purpose clause calls
                // for, not the section-close gate itself. Read-only scan of the section's own
                // directory — no lock, matching FindBlockingOpenProductOwnerQuestion's own
                // precedent for a read that decides nothing load-bearing.
                var sectionDirectory = Path.GetDirectoryName(filePath)!;
                var ageingThreads = CardStore.FindAgeingAddressedThreads(sectionDirectory, card.Frontmatter.Id);

                return new CommandOutcome.Success(new SectionStatusResult
                {
                    FilePath = filePath,
                    Status = status.ToWireString(),
                    Base = card.SectionFields.Base,
                    ClosedBy = card.SectionFields.ClosedBy?.ToWireString(),
                    ClosedAt = card.SectionFields.ClosedAt,
                    VerdictCount = card.SectionFields.Verdicts.Length,
                    AgeingThreads = [.. ageingThreads.Select(static ageing => new AgeingThreadResult
                    {
                        BlockId = ageing.CardId,
                        BlockFilePath = ageing.CardFilePath,
                        ThreadId = ageing.ThreadId,
                        AddressedTo = ageing.AddressedTo.ToWireString(),
                    })],
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
    /// <c>--section</c> (renamed from <c>--owed-by</c>, §9 block D carried item F — see
    /// <see cref="Callboard.Cli.CommandParser.ParseObligationCreate"/>'s own doc comment) is
    /// validated here, before any card is created — the same resolve-through-
    /// <see cref="Cards.CardIdentityResolver"/>, refuse-on-anything-else-than-a-section discipline
    /// <see cref="ValidateSection"/> already applies to <c>finding record --section</c>, reused via
    /// <see cref="ResolveCardReference"/> rather than re-derived (Architect ruling: a refusal naming
    /// the same fact earns the same code).
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
            repoRoot, parsed.OwedById, CardKind.Section, CardStore.IsSectionCard, "'--section'",
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
    /// <c>block create</c> (§13, work-lifecycle: "Every block card is minted by the tool") — the
    /// creation door for a task-implementing <c>block</c> card: scope always <see cref="CardScope.
    /// Change"/> (a block lives in the change that carved it, the same fixed scope <see
    /// cref="RunSectionCreate"/> already gives <c>section</c>), initial status always <see
    /// cref="BlockFlowState.Drafting"/>'s wire text — the leftmost node of work-lifecycle's own flow
    /// diagram, reachable by creation and nothing else. <see cref="BlockCardFields.Round"/> is
    /// fixed at <c>1</c> and <see cref="BlockCardFields.Base"/>/<see cref="BlockCardFields.
    /// ReviewedState"/>/<see cref="BlockCardFields.BlockedBy"/>/<see cref="BlockCardFields.
    /// GateResults"/> all start empty — the same shape <see cref="CardStore.
    /// RecordSectionVerdictUnderExistingLock"/>'s own <c>--finding-new</c> door builds at <c>
    /// briefed</c>, except at <c>drafting</c> and carrying <see cref="ParsedCommand.BlockCreate.
    /// Tasks"/> in place of a finding key.
    /// </summary>
    private static CommandOutcome RunBlockCreate(ParsedCommand.BlockCreate parsed, TimeSpan lockTimeout)
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
            repoRoot, filePath, CardKind.Block, CardScope.Change, parsed.Title,
            BlockFlowState.Drafting.ToWireString(), parsed.ActingRole, parsed.Body,
            registerFields: null, parsed.Timestamp, lockTimeout, parsed.ChangeName,
            blockFields: new BlockCardFields(null, null, parsed.Tasks, 1, [], []));

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
    /// always-fixed scope. The initial status is <see cref="QuestionStatus.Open"/>'s own wire text
    /// (§9 block D — a question's actual status vocabulary, open/answered/deferred, is
    /// <see cref="QuestionStatus"/>/<see cref="QuestionStatusWireFormat"/>; this call only ever
    /// writes the one state a brand-new card needs, the same "carries the vocabulary, not a second
    /// copy of it" discipline every wire-format type in this codebase follows).
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
    /// <see cref="MapCardCreateOutcome"/>'s <c>owedByRoleOverride</c> is also passed explicitly here
    /// (§9 block D, carried item G) — a question has no <see cref="RegisterCardFields"/>, so without
    /// it the response would omit the one field naming who must act on the created question.
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

        var sectionId = string.Empty;
        if (!string.IsNullOrEmpty(parsed.SectionId))
        {
            var resolvedSection = ResolveCardReference(
                repoRoot, parsed.SectionId, CardKind.Section, CardStore.IsSectionCard, "'--section'",
                "create the section first with 'section create'");
            if (resolvedSection.Refusal is not null)
            {
                return resolvedSection.Refusal;
            }

            sectionId = parsed.SectionId;
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.CreateCard(
            repoRoot, filePath, CardKind.Question, CardScope.Repository, parsed.Title,
            QuestionStatus.Open.ToWireString(), parsed.OwedByRole, parsed.Body,
            registerFields: null, parsed.Timestamp, lockTimeout, changeName: null, section: sectionId);

        return MapCardCreateOutcome(outcome, filePath, parsed.ActingRole, owedByRoleOverride: parsed.OwedByRole.ToWireString());
    }

    /// <summary>
    /// <c>question answer</c> (§9 block D, process-enforcement: "An answer must be written down").
    /// <see cref="ParsedCommand.QuestionAnswer.DecisionId"/> — when named — is resolved through
    /// <see cref="ResolveCardReference"/> against <see cref="CardKind.Decision"/> before <see cref="
    /// Cards.CardStore.AnswerQuestion"/> is ever called, the same "argv names it, execute resolves
    /// it against the record" split <see cref="RunObligationCreate"/>'s own <c>--section</c>
    /// resolution already follows.
    /// </summary>
    private static CommandOutcome RunQuestionAnswer(ParsedCommand.QuestionAnswer parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        if (parsed.DecisionId is not null)
        {
            var resolvedDecision = ResolveCardReference(
                repoRoot, parsed.DecisionId, CardKind.Decision, CardStore.IsDecisionCard, "'--decision'",
                "create the decision first with 'decision create'");
            if (resolvedDecision.Refusal is not null)
            {
                return resolvedDecision.Refusal;
            }
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.AnswerQuestion(
            repoRoot, filePath, parsed.DecisionId, parsed.InlineAnswer, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onAnswered: answered => new CommandOutcome.Success(new QuestionAnswerResult
            {
                FilePath = filePath,
                Id = answered.Card.Frontmatter.Id,
                Status = answered.Card.Frontmatter.Status,
                DecisionId = answered.Card.QuestionFields.AnswerDecisionId,
                InlineAnswer = answered.Card.QuestionFields.AnswerInline,
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onNotAQuestionCard: notAQuestion => WrongCardKind(filePath, CardKind.Question, notAQuestion.Kind, "'question answer' only applies to a question card") with
            {
                Rule = notAQuestion.RefusingRule,
                Remedy = notAQuestion.Remedy,
            },
            onNotOpen: notOpen => new CommandOutcome.Refusal(
                "question-not-open",
                $"'{filePath}' is already '{notOpen.CurrentStatus.ToWireString()}' and cannot be answered again.",
                notOpen.RefusingRule, notOpen.Remedy),
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to answer."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>question defer</c> (§9 block D — the question status vocabulary entire, including
    /// <c>deferred</c>).
    /// </summary>
    private static CommandOutcome RunQuestionDefer(ParsedCommand.QuestionDefer parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var filePath = ResolveFilePath(parsed.WorkingDirectory, parsed.FilePath);
        var outcome = CardStore.DeferQuestion(
            repoRoot, filePath, parsed.Target, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onDeferred: deferred => new CommandOutcome.Success(new QuestionDeferResult
            {
                FilePath = filePath,
                Id = deferred.Card.Frontmatter.Id,
                Status = deferred.Card.Frontmatter.Status,
                DeferredTarget = deferred.Card.QuestionFields.DeferredTarget!,
                ActingRole = parsed.ActingRole.ToWireString(),
                Timestamp = parsed.Timestamp,
            }),
            onNotAQuestionCard: notAQuestion => WrongCardKind(filePath, CardKind.Question, notAQuestion.Kind, "'question defer' only applies to a question card") with
            {
                Rule = notAQuestion.RefusingRule,
                Remedy = notAQuestion.Remedy,
            },
            onNotOpen: notOpen => new CommandOutcome.Refusal(
                "question-not-open",
                $"'{filePath}' is already '{notOpen.CurrentStatus.ToWireString()}' and cannot be deferred.",
                notOpen.RefusingRule, notOpen.Remedy),
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to defer."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
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
    ///
    /// <para>
    /// <b><paramref name="owedByRoleOverride"/> (§9 block D, carried item G).</b> A created
    /// <c>question</c> has no <see cref="RegisterCardFields"/> at all — <see cref="RunQuestionCreate"/>
    /// passes <c>registerFields: null</c>, so <c>created.Card.RegisterFields.OwedBy</c> reads
    /// <see langword="null"/> for a question the same way it does for every other non-obligation
    /// kind — leaving the one field that names who must act on a created question absent from a
    /// caller's response entirely. <see cref="RunQuestionCreate"/> passes <see cref="ParsedCommand.
    /// QuestionCreate.OwedByRole"/>'s wire string here explicitly; every other kind passes
    /// <see langword="null"/> and this falls back to <c>created.Card.RegisterFields.OwedBy</c>
    /// unchanged, so this method's output is byte-identical for the five kinds that already worked.
    /// </para>
    /// </summary>
    private static CommandOutcome MapCardCreateOutcome(CardCreateOutcome outcome, string filePath, CardOwner actingRole, string? owedByRoleOverride = null) =>
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
                OwedBy = owedByRoleOverride ?? created.Card.RegisterFields.OwedBy,
                Section = created.Card.Frontmatter.Section,
                ActingRole = actingRole.ToWireString(),
                Timestamp = created.Card.Frontmatter.Created,
                Tasks = CardStore.IsBlockCard(created.Card) ? created.Card.BlockFields.Tasks : null,
            }),
            onScopeRefused: refused => new CommandOutcome.Refusal("scope-refused", refused.Reason),
            onAlreadyExists: already => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{already.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onIdentityAlreadyBorne: borne => new CommandOutcome.Refusal(
                "identity-already-borne",
                $"the '{borne.Kind.ToWireString()}' identity counter issued '{borne.Id}', but the record already " +
                $"carries a card bearing it: {string.Join(", ", borne.CardFilePaths)}.",
                borne.RefusingRule, borne.Remedy),
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
            onNotARegisterCard: notARegister => new CommandOutcome.Refusal(
                "not-a-register-card",
                $"'{filePath}' is a '{notARegister.Kind.ToWireString()}' card, not one of the register kinds " +
                "(rule, hazard, obligation, decision); discharge only applies to a register card.",
                notARegister.RefusingRule, notARegister.Remedy),
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to discharge."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
            onOrphanedObligations: orphaned => new CommandOutcome.Refusal(
                "orphaned-obligations",
                $"'{parsed.ChangeName}' cannot be archived: {orphaned.Obligations.Count} open obligation(s) are owed by " +
                "no remaining section: " +
                string.Join(", ", orphaned.Obligations.Select(static o => $"{o.Id} (\"{o.Title}\")")) +
                " — discharge, promote, or decline each before archiving.",
                orphaned.RefusingRule, orphaned.Remedy),
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
            onNotADecisionCard: notADecision => WrongCardKind(
                notADecision.FilePath, CardKind.Decision, notADecision.Kind, "'decision supersede' only applies to decision cards") with
            {
                Rule = notADecision.RefusingRule,
                Remedy = notADecision.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(handEntered.FilePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to supersede."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
            onNotARuleCard: notARule => WrongCardKind(
                rule.FilePath!, CardKind.Rule, notARule.Kind, "'rule promote' only applies to rule cards") with
            {
                Rule = notARule.RefusingRule,
                Remedy = notARule.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(rule.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onTargetAlreadyExists: targetAlreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{targetAlreadyExists.FilePath}'.",
                targetAlreadyExists.RefusingRule, targetAlreadyExists.Remedy),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to promote."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>obligation promote</c> (§9 block F, register: "Promotion SHALL NOT be limited to rules...
    /// An <c>obligation</c> that outlives the change it was raised in SHALL be promotable to a
    /// wider scope on the same terms"). Exact mirror of <see cref="RunRulePromote"/> — resolves
    /// <c>--id</c> through <see cref="ResolveCardReference"/> and maps <see cref="Cards.
    /// CardObligationPromoteOutcome"/>, the obligation-scoped sibling of <see cref="Cards.
    /// CardRulePromoteOutcome"/>.
    /// </summary>
    private static CommandOutcome RunObligationPromote(ParsedCommand.ObligationPromote parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var obligation = ResolveCardReference(
            repoRoot, parsed.Id, CardKind.Obligation, CardStore.IsObligationCard, "'--id'",
            "create it first with 'obligation create'");
        if (obligation.Refusal is not null)
        {
            return obligation.Refusal;
        }

        var outcome = CardStore.PromoteObligation(repoRoot, obligation.FilePath!, parsed.ActingRole, parsed.Timestamp, lockTimeout, parsed.ChangeName);

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
                "obligation-not-change-scoped",
                $"'{notChangeScoped.FilePath}' is '{notChangeScoped.Scope.ToWireString()}'-scoped; only a " +
                "'change'-scoped obligation can be promoted to 'repository' scope.",
                notChangeScoped.RefusingRule, notChangeScoped.Remedy),
            onNotAnObligationCard: notAnObligation => WrongCardKind(
                obligation.FilePath!, CardKind.Obligation, notAnObligation.Kind, "'obligation promote' only applies to obligation cards") with
            {
                Rule = notAnObligation.RefusingRule,
                Remedy = notAnObligation.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(obligation.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onTargetAlreadyExists: targetAlreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{targetAlreadyExists.FilePath}'.",
                targetAlreadyExists.RefusingRule, targetAlreadyExists.Remedy),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to promote."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>obligation decline</c> (§9 block F, register: "An obligation that will not be met SHALL
    /// be closable by declining it with a recorded reason"). <c>--reason</c> is already required at
    /// the parse door (<see cref="CommandParser.ParseObligationDecline"/>); this handler resolves
    /// <c>--id</c> and maps <see cref="Cards.CardObligationDeclineOutcome"/>.
    /// </summary>
    private static CommandOutcome RunObligationDecline(ParsedCommand.ObligationDecline parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var obligation = ResolveCardReference(
            repoRoot, parsed.Id, CardKind.Obligation, CardStore.IsObligationCard, "'--id'",
            "create it first with 'obligation create'");
        if (obligation.Refusal is not null)
        {
            return obligation.Refusal;
        }

        var outcome = CardStore.DeclineObligation(repoRoot, obligation.FilePath!, parsed.ActingRole, parsed.Reason, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onDeclined: declined => new CommandOutcome.Success(new ObligationDeclineResult
            {
                FilePath = obligation.FilePath!,
                Id = declined.Card.Frontmatter.Id,
                // DeclinedReason is always set on a Declined outcome — CardStore.
                // DeclineObligationUnderExistingLock writes it in the same expression that produces
                // this outcome, and only ever after checking the reason is non-empty.
                Reason = declined.Card.RegisterFields.DeclinedReason!,
                ActingRole = parsed.ActingRole.ToWireString(),
                DeclinedAt = declined.Card.Frontmatter.Updated,
            }),
            onReasonRequired: reasonRequired => new CommandOutcome.Refusal(
                "reason-required", $"'{reasonRequired.FilePath}' was not declined: a reason is required.",
                reasonRequired.RefusingRule, reasonRequired.Remedy),
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(obligation.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onAlreadyDischarged: already => new CommandOutcome.Refusal(
                "already-discharged", $"'{already.FilePath}' is already discharged; there is nothing further to decline.",
                already.RefusingRule, already.Remedy),
            onNotAnObligationCard: notAnObligation => WrongCardKind(
                obligation.FilePath!, CardKind.Obligation, notAnObligation.Kind, "'obligation decline' only applies to obligation cards") with
            {
                Rule = notAnObligation.RefusingRule,
                Remedy = notAnObligation.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to decline."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>comment add</c> (§13, card-model: "The verbs that dispose of a thread SHALL NOT be the
    /// only ones that can start one"). <see cref="ResolveAnyCardReference"/> resolves <c>--id</c>
    /// without a kind restriction, the same reason <see cref="RunCommentResolve"/> below does
    /// (Architect ruling item 3). <see cref="Cards.CardComment.IsNit"/>/<see cref="Cards.
    /// CardComment.Required"/>/<see cref="Cards.CardComment.Sites"/> are left at their defaults
    /// (ruling 2: "SHALL NOT be able to mint a nit" — those three fields are <c>nit raise</c>'s,
    /// with its own disposition lifecycle this verb never touches).
    /// </summary>
    private static CommandOutcome RunCommentAdd(ParsedCommand.CommentAdd parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var card = ResolveAnyCardReference(repoRoot, parsed.CardId, "'--id'");
        if (card.Refusal is not null)
        {
            return card.Refusal;
        }

        var comment = new CardComment(
            Id: $"comment-{Guid.NewGuid():N}",
            Author: parsed.ActingRole,
            Timestamp: parsed.Timestamp,
            Body: parsed.Body,
            ReplyTo: parsed.ReplyTo,
            To: parsed.To,
            Resolves: null,
            UnknownHeaderFields: []);

        var outcome = CardStore.AddComment(repoRoot, card.FilePath!, comment, lockTimeout, parsed.ChangeName);

        return outcome.Match<CommandOutcome>(
            onAdded: added => new CommandOutcome.Success(new CommentAddResult
            {
                FilePath = card.FilePath!,
                CardId = added.Card.Frontmatter.Id,
                CommentId = added.Comment.Id,
                ActingRole = parsed.ActingRole.ToWireString(),
                To = added.Comment.To?.ToWireString(),
                ReplyTo = added.Comment.ReplyTo,
                AddedAt = added.Comment.Timestamp,
            }),
            onReplyToNotFound: replyToNotFound => new CommandOutcome.Refusal(
                "reply-to-not-found",
                $"'--reply-to' names comment '{replyToNotFound.ReplyToId}', but '{card.FilePath}' carries no such comment.",
                replyToNotFound.RefusingRule, replyToNotFound.Remedy),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to comment on."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onRoundDisagreesWithHistory: disagreement => RoundDisagreesWithHistory(card.FilePath!, disagreement.StoredRound, disagreement.ExpectedRound) with
            {
                Rule = disagreement.RefusingRule,
                Remedy = disagreement.Remedy,
            },
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(card.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));
    }

    /// <summary>
    /// <c>comment resolve</c> (§9 remediation, round two — S4: give <c>9.6</c>'s "resolve" and
    /// <c>9.3</c>'s "resolve the following thread(s)" a real verb). <see cref="ResolveAnyCardReference"/>
    /// resolves <c>--id</c> without a kind restriction — an addressed thread can live on any card
    /// kind (card-model: comments are a top-level sequence on every <c>CardFile</c>, not nested
    /// under a particular kind's own fields).
    /// </summary>
    private static CommandOutcome RunCommentResolve(ParsedCommand.CommentResolve parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var card = ResolveAnyCardReference(repoRoot, parsed.CardId, "'--id'");
        if (card.Refusal is not null)
        {
            return card.Refusal;
        }

        var outcome = CardStore.ResolveComment(
            repoRoot, card.FilePath!, parsed.CommentId, parsed.ActingRole, parsed.Body, requireReason: true, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapCommentResolveOutcome(outcome, card.FilePath!);
    }

    /// <summary>
    /// <c>comment decline --reason</c> (§9 remediation, round two — S4). <c>--reason</c> is already
    /// required at the parse door (<see cref="CommandParser.ParseCommentDecline"/>); this handler
    /// resolves <c>--id</c>/<c>--comment-id</c> and shares <see cref="MapCommentResolveOutcome"/>
    /// with <see cref="RunCommentResolve"/> — both verbs end at the same <see cref="Cards.
    /// CardCommentResolveOutcome"/>.
    /// </summary>
    private static CommandOutcome RunCommentDecline(ParsedCommand.CommentDecline parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var card = ResolveAnyCardReference(repoRoot, parsed.CardId, "'--id'");
        if (card.Refusal is not null)
        {
            return card.Refusal;
        }

        var outcome = CardStore.ResolveComment(
            repoRoot, card.FilePath!, parsed.CommentId, parsed.ActingRole, parsed.Reason, requireReason: true, parsed.Timestamp, lockTimeout, parsed.ChangeName);

        return MapCommentResolveOutcome(outcome, card.FilePath!);
    }

    /// <summary>Shared by <see cref="RunCommentResolve"/> and <see cref="RunCommentDecline"/> —
    /// both verbs end at <see cref="Cards.CardCommentResolveOutcome"/>, differing only in whether
    /// <see cref="Cards.CardCommentResolveOutcome.ReasonRequired"/> is reachable.</summary>
    private static CommandOutcome MapCommentResolveOutcome(CardCommentResolveOutcome outcome, string filePath) =>
        outcome.Match<CommandOutcome>(
            onResolved: resolved => new CommandOutcome.Success(new CommentResolveResult
            {
                FilePath = filePath,
                CardId = resolved.Card.Frontmatter.Id,
                CommentId = resolved.ResolvingComment.Resolves!,
                ResolvingCommentId = resolved.ResolvingComment.Id,
                ActingRole = resolved.ResolvingComment.Author.ToWireString(),
                ResolvedAt = resolved.ResolvingComment.Timestamp,
            }),
            onCommentNotFound: notFound => new CommandOutcome.Refusal(
                "comment-not-found", $"comment '{notFound.CommentId}' does not exist on '{filePath}'.",
                notFound.RefusingRule, notFound.Remedy),
            onRoleNotPermitted: roleNotPermitted => new CommandOutcome.Refusal(
                "role-not-permitted",
                $"'{filePath}' thread disposition denied for role '{roleNotPermitted.AttemptedRole.ToWireString()}'.",
                roleNotPermitted.RefusingRule, roleNotPermitted.Remedy),
            onAlreadyResolved: already => new CommandOutcome.Refusal(
                "comment-already-resolved", $"comment '{already.CommentId}' on '{filePath}' is already resolved.",
                already.RefusingRule, already.Remedy),
            onReasonRequired: reasonRequired => new CommandOutcome.Refusal(
                "reason-required", $"'{reasonRequired.FilePath}' has no reason recorded.",
                reasonRequired.RefusingRule, reasonRequired.Remedy),
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(filePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
            onToolFailure: toolFailure => throw new InvalidOperationException(toolFailure.Reason));

    /// <summary>
    /// <c>comment promote --to question|decision</c> (§9 remediation, round two — S4: give
    /// <c>9.6</c>'s "promote to a 'question'"/"promote to a 'decision'" a real verb). Two-card
    /// write; <see cref="Cards.CardStore.PromoteComment"/> carries the discipline, this handler only
    /// resolves the addressing and maps the outcome.
    /// </summary>
    private static CommandOutcome RunCommentPromote(ParsedCommand.CommentPromote parsed, TimeSpan lockTimeout)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var card = ResolveAnyCardReference(repoRoot, parsed.CardId, "'--id'");
        if (card.Refusal is not null)
        {
            return card.Refusal;
        }

        var raiseFilePath = ResolveFilePath(parsed.WorkingDirectory, parsed.RaiseFilePath);
        var outcome = CardStore.PromoteComment(
            repoRoot, card.FilePath!, parsed.CommentId, raiseFilePath, parsed.ToKind, parsed.Title,
            parsed.ActingRole, parsed.OwedByRole, parsed.Body, parsed.ChangeName, parsed.Timestamp, lockTimeout);

        return outcome.Match<CommandOutcome>(
            onPromoted: promoted => new CommandOutcome.Success(new CommentPromoteResult
            {
                FilePath = card.FilePath!,
                CardId = promoted.OriginalCard.Frontmatter.Id,
                CommentId = parsed.CommentId,
                RaisedCardId = promoted.RaisedCard.Frontmatter.Id,
                RaisedCardFilePath = raiseFilePath,
                RaisedCardKind = promoted.RaisedCard.Frontmatter.Kind.ToWireString(),
                ActingRole = parsed.ActingRole.ToWireString(),
                PromotedAt = promoted.OriginalCard.Frontmatter.Updated,
            }),
            onCommentNotFound: notFound => new CommandOutcome.Refusal(
                "comment-not-found", $"comment '{notFound.CommentId}' does not exist on '{card.FilePath}'.",
                notFound.RefusingRule, notFound.Remedy),
            onRoleNotPermitted: roleNotPermitted => new CommandOutcome.Refusal(
                "role-not-permitted",
                $"'{card.FilePath}' thread disposition denied for role '{roleNotPermitted.AttemptedRole.ToWireString()}'.",
                roleNotPermitted.RefusingRule, roleNotPermitted.Remedy),
            onAlreadyResolved: already => new CommandOutcome.Refusal(
                "comment-already-resolved", $"comment '{already.CommentId}' on '{card.FilePath}' is already resolved.",
                already.RefusingRule, already.Remedy),
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(card.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            },
            onRaisedCardAlreadyExists: raisedAlreadyExists => new CommandOutcome.Refusal(
                "card-already-exists", $"a card already exists at '{raisedAlreadyExists.FilePath}'."),
            onRaisedCardLayoutMismatch: raisedLayoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", raisedLayoutMismatch.Reason),
            onCardNotFound: notFound => new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}'."),
            onLayoutMismatch: layoutMismatch => new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason),
            onCardCorrupt: corrupt => new CommandOutcome.Refusal("card-corrupt", corrupt.Reason),
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
            onIdentityAlreadyBorne: borne => new CommandOutcome.Refusal(
                "identity-already-borne",
                $"the '{borne.Kind.ToWireString()}' identity counter issued '{borne.Id}', but the record already " +
                $"carries a card bearing it: {string.Join(", ", borne.CardFilePaths)}.",
                borne.RefusingRule, borne.Remedy),
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

            // This branch is unreachable: §12 block A's parse door (CardFileParser.ValidateStatus)
            // never hands back a rule-kind CardFile whose status does not parse against
            // RegisterLifecycleStateWireFormat, and `resolved` above is already narrowed to
            // CardKind.Rule via ResolveCardReference's IsRuleCard predicate. Kept as a defensive
            // refusal rather than removed, so a rule read some other way still refuses instead of
            // proceeding on an unparsed state.
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
            onIdentityAlreadyBorne: borne => new CommandOutcome.Refusal(
                "identity-already-borne",
                $"the '{borne.Kind.ToWireString()}' identity counter issued '{borne.Id}', but the record already " +
                $"carries a card bearing it: {string.Join(", ", borne.CardFilePaths)}.",
                borne.RefusingRule, borne.Remedy),
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
            },
            // Reachable: the target rule card's frontmatter can carry a hand-entered reserved
            // derived-state key regardless of card kind (§10 block C) — this shared surface
            // refuses before appending the promotion-attempt comment.
            onHandEnteredDerivedState: handEntered => HandEnteredDerivedState(resolved.FilePath!, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
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
            onNotARuleCard: notARule => (WrongCardKind(
                notARule.FilePath, CardKind.Rule, notARule.Kind, "compaction only applies to rule cards") with
            {
                Rule = notARule.RefusingRule,
                Remedy = notARule.Remedy,
            }, null),
            onHandEnteredDerivedState: handEntered => (HandEnteredDerivedState(handEntered.FilePath, handEntered.Key) with
            {
                Rule = handEntered.RefusingRule,
                Remedy = handEntered.Remedy,
            }, null),
            onCardNotFound: notFound => (new CommandOutcome.Refusal(
                "card-not-found", $"no card file exists at '{notFound.FilePath}' to compact."), null),
            onLayoutMismatch: layoutMismatch => (new CommandOutcome.Refusal(
                "card-layout-mismatch", layoutMismatch.Reason), null),
            onCardCorrupt: corrupt => (new CommandOutcome.Refusal("card-corrupt", corrupt.Reason), null),
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

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — the one refusal <see cref="Cards.CardWriteResult.HandEnteredDerivedState"/>
    /// maps to for the CLI: <paramref name="filePath"/> carries a reserved derived-state key
    /// (<see cref="Cards.DerivedStateFieldKeys.All"/>) in its frontmatter, hand-entered outside
    /// the tool, and this write refuses rather than re-emitting it.</summary>
    private static CommandOutcome.Refusal HandEnteredDerivedState(string filePath, string key) =>
        new CommandOutcome.Refusal(
            "hand-entered-derived-state",
            $"'{filePath}' carries a hand-entered '{key}' field in its frontmatter; refusing to act on this " +
            "card. This state is derived at request time, never stored — remove the field and request it " +
            "with 'callboard state' instead.");

    /// <summary>process-enforcement: "A verdict cannot leave threads unanswered" (§9 block B) —
    /// the one refusal every door out of <c>in-review</c> mints when the acting role's own inbox
    /// still carries a live addressed thread. Names the file, the acting role and the unresolved
    /// thread ids, the same "state the fact, not just the rule" shape <see cref="
    /// RoundDisagreesWithHistory"/> already establishes.</summary>
    private static CommandOutcome.Refusal UnresolvedThreadsAddressedToActor(string filePath, CardOwner actorRole, IReadOnlyList<string> threadIds) =>
        new CommandOutcome.Refusal(
            "unresolved-threads-addressed-to-actor",
            $"'{filePath}' carries thread(s) addressed to '{actorRole.ToWireString()}' that are still unresolved: " +
            $"{string.Join(", ", threadIds)}. Resolve them (with 'comment resolve') before recording this verdict.");

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
    /// §7 block B) and <c>RunObligationCreate</c> (<c>--section</c>, §7 block C, renamed from
    /// <c>--owed-by</c> in §9 block D) both resolve
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

    /// <summary>
    /// <see cref="ResolveCardReference"/>'s kind-agnostic sibling, for the three <c>comment</c>
    /// verbs (§9 remediation, round two — S4): an addressed thread lives on any card kind
    /// (card-model: comments are a top-level sequence on every <c>CardFile</c>), so <c>--id</c>
    /// resolution here never filters on <see cref="CardKind"/> the way every kind-specific verb's
    /// own resolution does. Same three refusal codes, same reasoning, minus the kind check.
    /// </summary>
    private static (CommandOutcome? Refusal, string? FilePath) ResolveAnyCardReference(string repoRoot, string id, string flagLabel)
    {
        var (refusal, filePath, _) = ResolveAnyCardReferenceWithCard(repoRoot, id, flagLabel);
        return (refusal, filePath);
    }

    /// <summary>
    /// <see cref="ResolveAnyCardReference"/>'s own implementation, plus the resolved
    /// <see cref="CardFile"/> itself — added for <c>card show</c> (§11 block B), which needs the
    /// card it resolved to build its response and must not read the record a second time (or take
    /// a lock, ADR-0004) to get it. <see cref="ResolveAnyCardReference"/> is kept as a thin wrapper
    /// over this rather than duplicated, so the three refusal codes and their wording stay in
    /// exactly one place.
    /// </summary>
    private static (CommandOutcome? Refusal, string? FilePath, CardFile? Card) ResolveAnyCardReferenceWithCard(string repoRoot, string id, string flagLabel) =>
        CardIdentityResolver.Resolve(repoRoot, id).Match<(CommandOutcome?, string?, CardFile?)>(
            onFound: (filePath, card) => (null, filePath, card),
            onNotFound: notFoundId => (
                new CommandOutcome.Refusal(
                    "card-id-not-found",
                    $"{flagLabel} names id '{notFoundId}', but no card in the record carries it."),
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

    /// <summary>
    /// <c>context --role &lt;role&gt;</c> (§10 block A, working-context: "given a role, the system
    /// SHALL return that role's complete working context, composed of exactly" four parts). A pure
    /// read — no lock, no timestamp — over <see cref="Cards.WorkingContextAssembler.Build"/>'s
    /// result, mapped onto <see cref="ContextResult"/> field for field in the same order. This
    /// handler never calls <see cref="Cards.RuleCitations.CountCitations"/> (carried item D — this
    /// is a per-brief path). <see langword="private"/>: <see cref="CommandParser"/> cannot name
    /// this method.
    /// </summary>
    private static CommandOutcome RunContext(ParsedCommand.Context parsed, string recognisedCommandName)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var context = WorkingContextAssembler.Build(repoRoot, parsed.Role);

        return new CommandOutcome.Success(BuildBudgetedContextResult(parsed.Role, context, recognisedCommandName));
    }

    /// <summary>
    /// <c>state</c> (§10 block C, working-context: "a summary of overall process state" — every
    /// figure "derived at the time of the request"). A pure read — no lock, no timestamp, no role —
    /// over <see cref="DerivedStateAssembler.Build"/>'s result. <see langword="private"/>: <see
    /// cref="CommandParser"/> cannot name this method.
    ///
    /// <para>
    /// <b>Deliberately unbounded (§10 block C brief).</b> <see cref="WorkingContextBudget"/>'s
    /// character ceiling is stated for the working-context response specifically (D6) — the spec
    /// sets no budget for this response, and <c>state</c> reports identity-only facts (card ids,
    /// titles, section/question/blocker references), never a card's <c>Body</c> or its narrative
    /// comment thread the way <c>context</c>'s brief and addressed threads do, so the response
    /// grows with the number of open sections/obligations/questions/blocked cards, not with
    /// narrative volume. Left unbounded here rather than silently reused under <c>context</c>'s
    /// ceiling — the two shapes measure different things, and a truncation rule copied from one to
    /// the other without its own justification would be exactly the kind of "chose without saying
    /// so" this brief asked not to do.
    /// </para>
    /// </summary>
    private static CommandOutcome RunState(ParsedCommand.State parsed)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var state = DerivedStateAssembler.Build(repoRoot);

        return new CommandOutcome.Success(new StateResult
        {
            OpenSections = [.. state.OpenSections.Select(static entry => new StateOpenSectionResult
            {
                Id = entry.Card.Frontmatter.Id,
                FilePath = entry.FilePath,
                Title = entry.Card.Frontmatter.Title,
                ChangeName = entry.ChangeName,
            })],
            TaskCompletion = [.. state.TaskCompletion.Select(static entry => new StateTaskCompletionResult
            {
                ChangeName = entry.ChangeName,
                TasksFileFound = entry.TasksFileFound,
                Ticked = entry.Ticked,
                Total = entry.Total,
            })],
            LiveObligations = [.. state.LiveObligations.Select(static entry => new StateObligationResult
            {
                Id = entry.Card.Frontmatter.Id,
                FilePath = entry.FilePath,
                Title = entry.Card.Frontmatter.Title,
                OwedBySectionId = entry.OwedBySectionId,
            })],
            OpenQuestions = [.. state.OpenQuestions.Select(static entry => new StateQuestionResult
            {
                Id = entry.Card.Frontmatter.Id,
                FilePath = entry.FilePath,
                Title = entry.Card.Frontmatter.Title,
                OwesAnswer = entry.OwesAnswer.ToWireString(),
            })],
            BlockedCards = [.. state.BlockedCards.Select(static entry => new StateBlockedCardResult
            {
                Id = entry.Card.Frontmatter.Id,
                FilePath = entry.FilePath,
                Title = entry.Card.Frontmatter.Title,
                BlockedByIds = entry.BlockedByIds,
                Halted = entry.Halted,
                HaltedByQuestionId = entry.HaltedByQuestionId,
                HaltedByQuestionTitle = entry.HaltedByQuestionTitle,
            })],
        });
    }

    /// <summary>
    /// <c>card show &lt;id&gt;</c> (§11 block B, record-retrieval: "the system SHALL return a card's
    /// full content, including every comment on it, given the card's identity"). A pure read — no
    /// lock, no timestamp — resolved through <see cref="ResolveAnyCardReferenceWithCard"/> (never a
    /// hand-rolled directory walk, §7 carried item C), which is kind-agnostic because an id can name
    /// any card kind. This reports; it never records (§9 ruling 1: a pure read asserts nothing about
    /// the record) — every failure path below is <see cref="ResolveAnyCardReferenceWithCard"/>'s own
    /// bare <see cref="CommandOutcome.Refusal"/>, with no <c>Rule</c>/<c>Remedy</c> and so no
    /// <c>RefuseAndRecord</c> anywhere on this path.
    ///
    /// <para>
    /// <b>No liveness filter (§11 block B brief).</b> A closed card is retrievable by identity
    /// exactly as a live one is — retrieval by identity is not a default query, so §11 block D's
    /// "closed cards leave default queries" has nothing to say here. <see cref="Cards.
    /// CardIdentityResolver.Resolve"/> itself has no notion of open/closed at all, so this simply
    /// never asks it one.
    /// </para>
    /// </summary>
    private static CommandOutcome RunCardShow(ParsedCommand.CardShow parsed)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var (refusal, filePath, card) = ResolveAnyCardReferenceWithCard(repoRoot, parsed.Id, "'card show'");
        if (refusal is not null)
        {
            return refusal;
        }

        return new CommandOutcome.Success(BuildCardShowResult(filePath!, card!));
    }

    /// <summary>
    /// <c>section export &lt;section-id&gt; --out &lt;path&gt; [--force]</c> (§11 block C,
    /// record-retrieval: "The system SHALL render a section ... as a single readable document
    /// ... containing its cards, threads, verdicts and findings in reading order"). A pure read —
    /// no lock, no timestamp, no acting role — resolved through <see cref="
    /// ResolveAnyCardReferenceWithCard"/>, exactly <see cref="RunCardShow"/>'s own shape. The only
    /// write on this path is <see cref="ParsedCommand.SectionExport.OutputPath"/> itself, via
    /// <see cref="Cards.RecordExportWriter.WriteAtomically"/> (D7): a <see cref="Cards.
    /// RecordExportWriteOutcome.ToolFailure"/> is thrown, never hand-mapped to a refusal, the same
    /// "a caller wired over this type must let it reach a tool-failure exit" discipline every other
    /// <c>ToolFailure</c> case in this dispatcher already follows (ADR-0001).
    /// </summary>
    private static CommandOutcome RunSectionExport(ParsedCommand.SectionExport parsed)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var (refusal, _, card) = ResolveAnyCardReferenceWithCard(repoRoot, parsed.SectionId, "'section export'");
        if (refusal is not null)
        {
            return refusal;
        }

        var sectionCard = card!;
        if (!CardStore.IsSectionCard(sectionCard))
        {
            return new CommandOutcome.Refusal(
                "not-a-section-card",
                $"'section export' names id '{parsed.SectionId}', but that card's kind is " +
                $"'{sectionCard.Frontmatter.Kind.ToWireString()}', not 'section'.");
        }

        var outputPath = Path.GetFullPath(Path.Combine(repoRoot, parsed.OutputPath));
        var cardsInReadingOrder = RecordExportAssembler.CardsForSection(repoRoot, sectionCard);
        var document = RecordExportRenderer.Render(
            $"Section export: {sectionCard.Frontmatter.Id} — {sectionCard.Frontmatter.Title}", cardsInReadingOrder);

        return RecordExportWriter.WriteAtomically(outputPath, document, parsed.Force).Match<CommandOutcome>(
            onWritten: () => new CommandOutcome.Success(new SectionExportResult
            {
                SectionId = sectionCard.Frontmatter.Id,
                Title = sectionCard.Frontmatter.Title,
                OutputPath = outputPath,
                CardCount = cardsInReadingOrder.Count,
            }),
            onTargetExists: () => new CommandOutcome.Refusal(
                "export-target-exists",
                $"'{outputPath}' already exists; pass '--force' to overwrite it — an export is derived and regenerable."),
            onToolFailure: static reason => throw new InvalidOperationException(reason));
    }

    /// <summary>
    /// <c>change export &lt;change-name&gt; --out &lt;path&gt; [--force]</c> (§11 block C) —
    /// <see cref="RunSectionExport"/>'s whole-change sibling. <see cref="ParsedCommand.
    /// ChangeExport.ChangeName"/> is a directory name, not a card id — validated and resolved the
    /// same way <see cref="RunChangeArchive"/> resolves its own first argument, never through
    /// <see cref="Cards.CardIdentityResolver"/> (a change has no card of its own to resolve).
    /// </summary>
    private static CommandOutcome RunChangeExport(ParsedCommand.ChangeExport parsed)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        string relativeChangeDirectory;
        try
        {
            relativeChangeDirectory = CardLayout.ChangesDirectory(parsed.ChangeName);
        }
        catch (ArgumentException ex)
        {
            return new CommandOutcome.Refusal("invalid-change-name", ex.Message);
        }

        var changeDirectory = Path.GetFullPath(
            Path.Combine(repoRoot, relativeChangeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(changeDirectory))
        {
            return new CommandOutcome.Refusal(
                "change-not-found",
                $"no live change named '{parsed.ChangeName}' exists under '{CardLayout.ChangesRootDirectory}'.");
        }

        var outputPath = Path.GetFullPath(Path.Combine(repoRoot, parsed.OutputPath));
        var cardsInReadingOrder = RecordExportAssembler.CardsForChange(repoRoot, changeDirectory);
        var document = RecordExportRenderer.Render($"Change export: {parsed.ChangeName}", cardsInReadingOrder);

        return RecordExportWriter.WriteAtomically(outputPath, document, parsed.Force).Match<CommandOutcome>(
            onWritten: () => new CommandOutcome.Success(new ChangeExportResult
            {
                ChangeName = parsed.ChangeName,
                OutputPath = outputPath,
                CardCount = cardsInReadingOrder.Count,
            }),
            onTargetExists: () => new CommandOutcome.Refusal(
                "export-target-exists",
                $"'{outputPath}' already exists; pass '--force' to overwrite it — an export is derived and regenerable."),
            onToolFailure: static reason => throw new InvalidOperationException(reason));
    }

    /// <summary>
    /// <c>view --out &lt;path&gt; [--force]</c> (§12 block B, record-retrieval: "a local,
    /// read-only, human-readable view of the board"). A pure read over <see cref="
    /// BoardViewAssembler.Build"/>'s result, rendered by <see cref="BoardViewRenderer.
    /// Render"/> and written the same temp-file-then-rename way <see cref="RunSectionExport"/>
    /// writes its own output (D7) — the only write this path drives.
    /// </summary>
    private static CommandOutcome RunView(ParsedCommand.View parsed)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var outputPath = Path.GetFullPath(Path.Combine(repoRoot, parsed.OutputPath));
        var view = BoardViewAssembler.Build(repoRoot);
        var document = BoardViewRenderer.Render(view);
        var cardCount = view.Lanes.Concat(view.RegisterLanes)
            .SelectMany(static lane => lane.Columns)
            .Sum(static column => column.OwnerGroups.Sum(static group => group.Cards.Count));

        return RecordExportWriter.WriteAtomically(outputPath, document, parsed.Force).Match<CommandOutcome>(
            onWritten: () => new CommandOutcome.Success(new ViewResult
            {
                OutputPath = outputPath,
                CardCount = cardCount,
            }),
            onTargetExists: () => new CommandOutcome.Refusal(
                "export-target-exists",
                $"'{outputPath}' already exists; pass '--force' to overwrite it — the view is derived and regenerable."),
            onToolFailure: static reason => throw new InvalidOperationException(reason));
    }

    /// <summary>
    /// Maps a resolved <see cref="CardFile"/> onto <see cref="CardShowResult"/> group for group,
    /// in the same order <see cref="CardFile"/> itself declares them — see that type's own doc
    /// comment for why each kind-specific field group is always present, empty where inapplicable,
    /// rather than this method branching on <see cref="Cards.CardFrontmatter.Kind"/> to decide what
    /// to populate.
    /// </summary>
    private static CardShowResult BuildCardShowResult(string filePath, CardFile card) => new()
    {
        Id = card.Frontmatter.Id,
        Kind = card.Frontmatter.Kind.ToWireString(),
        FilePath = filePath,
        Title = card.Frontmatter.Title,
        Status = card.Frontmatter.Status,
        Owner = card.Frontmatter.Owner.ToWireString(),
        Scope = card.Frontmatter.Scope.ToWireString(),
        Section = card.Frontmatter.Section,
        Created = card.Frontmatter.Created,
        Updated = card.Frontmatter.Updated,
        UnknownFrontmatterFields = MapUnknownFields(card.UnknownFrontmatterFields),
        Body = card.Body,
        Handovers = [.. card.Handovers.Select(static handover => new CardShowHandoverResult
        {
            By = handover.By.ToWireString(),
            To = handover.To.ToWireString(),
            Timestamp = handover.Timestamp,
            UnknownFields = MapUnknownFields(handover.UnknownFields),
        })],
        BlockFields = new CardShowBlockFieldsResult
        {
            Base = card.BlockFields.Base,
            ReviewedState = card.BlockFields.ReviewedState,
            Tasks = [.. card.BlockFields.Tasks],
            Round = card.BlockFields.Round,
            BlockedBy = [.. card.BlockFields.BlockedBy],
            GateResults = [.. card.BlockFields.GateResults.Select(static gate => new CardShowGateResultResult
            {
                Label = gate.Label,
                ExitCode = gate.ExitCode,
                Round = gate.Round,
            })],
            FindingKey = card.BlockFields.FindingKey,
        },
        Transitions = [.. card.Transitions.Select(static transition => new CardShowTransitionResult
        {
            By = transition.By.ToWireString(),
            Name = transition.Name,
            From = transition.From.ToWireString(),
            To = transition.To.ToWireString(),
            Timestamp = transition.Timestamp,
            UnknownFields = MapUnknownFields(transition.UnknownFields),
        })],
        Claims = [.. card.Claims.Select(static claim => new CardShowApprovalClaimResult
        {
            Id = claim.Id,
            Round = claim.Round,
            Text = claim.Text,
            UnknownFields = MapUnknownFields(claim.UnknownFields),
        })],
        Limits = [.. card.Limits.Select(static limit => new CardShowApprovalLimitResult
        {
            Round = limit.Round,
            Text = limit.Text,
            UnknownFields = MapUnknownFields(limit.UnknownFields),
        })],
        SectionFields = new CardShowSectionFieldsResult
        {
            Base = card.SectionFields.Base,
            ClosedBy = card.SectionFields.ClosedBy?.ToWireString(),
            ClosedAt = card.SectionFields.ClosedAt,
            Verdicts = [.. card.SectionFields.Verdicts.Select(static verdict => new CardShowSectionVerdictResult
            {
                By = verdict.By.ToWireString(),
                Verdict = verdict.Verdict.ToWireString(),
                RangeFrom = verdict.RangeFrom,
                RangeTo = verdict.RangeTo,
                Timestamp = verdict.Timestamp,
                UnknownFields = MapUnknownFields(verdict.UnknownFields),
            })],
            Authorisations = [.. card.SectionFields.Authorisations.Select(static authorisation => new CardShowSectionAuthorisationResult
            {
                By = authorisation.By.ToWireString(),
                Reason = authorisation.Reason,
                Timestamp = authorisation.Timestamp,
                UnknownFields = MapUnknownFields(authorisation.UnknownFields),
            })],
        },
        FindingFields = BuildCardShowFindingFields(card.FindingFields),
        RegisterFields = new CardShowRegisterFieldsResult
        {
            Condition = card.RegisterFields.Condition,
            Cadence = card.RegisterFields.Cadence,
            DischargedBy = card.RegisterFields.DischargedBy?.ToWireString(),
            DischargedAt = card.RegisterFields.DischargedAt,
            OwedBy = card.RegisterFields.OwedBy,
            DeclinedReason = card.RegisterFields.DeclinedReason,
            Supersedes = card.RegisterFields.Supersedes,
            SupersededBy = card.RegisterFields.SupersededBy,
            EarnedFrom = [.. card.RegisterFields.EarnedFrom],
            Absorbs = [.. card.RegisterFields.Absorbs],
        },
        QuestionFields = new CardShowQuestionFieldsResult
        {
            AnsweredBy = card.QuestionFields.AnsweredBy?.ToWireString(),
            AnsweredAt = card.QuestionFields.AnsweredAt,
            AnswerDecisionId = card.QuestionFields.AnswerDecisionId,
            AnswerInline = card.QuestionFields.AnswerInline,
            DeferredBy = card.QuestionFields.DeferredBy?.ToWireString(),
            DeferredAt = card.QuestionFields.DeferredAt,
            DeferredTarget = card.QuestionFields.DeferredTarget,
        },
        Refusals = [.. card.Refusals.Select(static entry => new CardShowRefusalResult
        {
            By = entry.By.ToWireString(),
            Rule = entry.Rule,
            Remedy = entry.Remedy,
            Timestamp = entry.Timestamp,
            UnknownFields = MapUnknownFields(entry.UnknownFields),
        })],
        Comments = [.. card.Comments.Select(static comment => new CardShowCommentResult
        {
            Id = comment.Id,
            Author = comment.Author.ToWireString(),
            Timestamp = comment.Timestamp,
            Body = comment.Body,
            ReplyTo = comment.ReplyTo,
            To = comment.To?.ToWireString(),
            Resolves = comment.Resolves,
            IsNit = comment.IsNit,
            Required = comment.Required,
            Sites = comment.Sites,
            Disposition = comment.Disposition?.ToWireString(),
            UnknownHeaderFields = MapUnknownFields(comment.UnknownHeaderFields),
        })],
    };

    /// <summary>
    /// <see cref="Cards.FindingExtent"/> and <see cref="Cards.FindingBlindSpotDeclaration"/> are
    /// closed unions with no <c>ToWireString</c> extension of their own (nothing before this verb
    /// ever needed to put either on the wire) — flattened here via <c>Match</c> to a discriminator
    /// plus the one payload field the matched case carries, every other payload field left at its
    /// empty default, the same "one shape, all cases present" idiom the rest of this mapping
    /// follows.
    /// </summary>
    private static CardShowFindingFieldsResult BuildCardShowFindingFields(FindingCardFields fields)
    {
        var (extentKind, extentInstrument, extentItems) = fields.Extent.Match(
            onInstrument: static command => ("instrument", (string?)command, (IReadOnlyList<string>)[]),
            onExplicit: static items => ("explicit", (string?)null, (IReadOnlyList<string>)[.. items]),
            onBlockScope: static () => ("blockScope", (string?)null, (IReadOnlyList<string>)[]));

        var (blindSpotKind, blindSpotRaisedAsId) = fields.BlindSpot.Match(
            onNone: static () => ("none", (string?)null),
            onRaisedAs: static cardId => ("raisedAs", (string?)cardId));

        return new CardShowFindingFieldsResult
        {
            Instrument = fields.Instrument,
            ExtentKind = extentKind,
            ExtentInstrument = extentInstrument,
            ExtentItems = extentItems,
            VerifiedAt = fields.VerifiedAt,
            BlindSpotKind = blindSpotKind,
            BlindSpotRaisedAsId = blindSpotRaisedAsId,
            ExtentFingerprintFiles = fields.ExtentFingerprint?.Files
                .Select(static file => new CardShowFingerprintFileResult
                {
                    RelativePath = file.RelativePath,
                    ContentHash = file.ContentHash,
                })
                .ToList(),
            Disposition = fields.Disposition.Match(onMeasured: static () => "measured", onArguedClean: static () => "arguedClean"),
        };
    }

    private static IReadOnlyList<CardShowUnknownFieldResult> MapUnknownFields(IReadOnlyList<(string Key, string RawValue)> fields) =>
        [.. fields.Select(static field => new CardShowUnknownFieldResult { Key = field.Key, RawValue = field.RawValue })];

    /// <summary>
    /// The default <c>rule review</c> ceiling, used whenever <c>--ceiling</c> is absent (§10 block
    /// E). Not tuned to any fixture — the reasoning is that the register (<see cref="Cards.
    /// WorkingContext.LiveRulesAndHazards"/>) ships first and unconditionally in every <c>context</c>
    /// response, so its size comes out of every brief's budget (<see cref="WorkingContextBudget.
    /// CharacterCeiling"/>, 8,100 characters, block B's own measured figure) before any work-
    /// specific content is assembled at all. A register much beyond roughly fifty live rules starts
    /// crowding out the brief it exists to inform, which is the point at which a human should look
    /// at compacting it — hence 50, not some other round number.
    /// </summary>
    internal const int DefaultRuleReviewCeiling = 50;

    /// <summary>
    /// <c>rule review [--ceiling &lt;n&gt;]</c> (§10 block E, carried item B, register: "Register
    /// size triggers review, never eviction" — the ceiling SHALL NOT act as a hard cap, citation
    /// counts surface candidates only, and an uncited rule SHALL be queued for a human and SHALL
    /// NOT be retired automatically). A pure read: no lock, no timestamp, no write of any kind to
    /// any card — <see cref="Cards.RuleCitations.CeilingPassed"/> is a predicate over two integers
    /// and <see cref="Cards.RuleCitations.UncitedOpenRules"/> only names cards, so nothing this
    /// method calls can retire, discharge or otherwise mutate a rule card. <see cref="ParsedCommand.
    /// RuleReview.Ceiling"/> and <see cref="ParsedCommand.RuleReview.CeilingIsDefault"/> are both
    /// echoed back on <see cref="RuleReviewResult"/> unchanged — stating the ceiling only means
    /// something if the caller can see which value applied.
    ///
    /// <para>
    /// <b><see cref="Cards.RuleCitations.CountCitations"/>, indirectly via <see cref="Cards.
    /// RuleCitations.UncitedOpenRules"/>, is the sanctioned caller here.</b> Carried item D forbids
    /// it on the per-brief <c>context</c>/<c>state</c> paths, since it is O(rules × cards) and those
    /// run on every working-context request; this command is the deliberate, on-demand review those
    /// citation counts exist for in the first place, paid for only when a caller actually asks for
    /// it. Do not mistake this call site for the one the carried item warns against.
    /// </para>
    /// </summary>
    private static CommandOutcome RunRuleReview(ParsedCommand.RuleReview parsed)
    {
        var repoRoot = RepoRootResolver.Resolve(parsed.WorkingDirectory);
        if (repoRoot is null)
        {
            return new CommandOutcome.Refusal(
                "repo-root-not-found",
                $"no git repository found above '{parsed.WorkingDirectory}'; run callboard from inside the repository.");
        }

        var liveRuleCount = RuleCitations.CountLiveOpenRules(repoRoot);
        var uncited = RuleCitations.UncitedOpenRules(repoRoot);

        return new CommandOutcome.Success(new RuleReviewResult
        {
            Ceiling = parsed.Ceiling,
            CeilingSource = parsed.CeilingIsDefault ? "default" : "flag",
            LiveRuleCount = liveRuleCount,
            CeilingPassed = RuleCitations.CeilingPassed(liveRuleCount, parsed.Ceiling),
            UncitedOpenRules = [.. uncited.Select(static entry => new RuleReviewUncitedRuleResult
            {
                Id = entry.Card.Frontmatter.Id,
                FilePath = entry.FilePath,
                Title = entry.Card.Frontmatter.Title,
            })],
        });
    }

    /// <summary>
    /// Assembles <see cref="ContextResult"/> under §10 block B's character budget (D6: "register,
    /// then brief, then unresolved threads addressed to the caller, then narrative"). The
    /// register and the brief are never touched; narrative — the comment bodies on threads
    /// addressed to the caller — starts fully included and shrinks from the low-priority end
    /// (oldest first stays, most recently addressed goes first — the order <see cref="
    /// WorkingContextTopItem.UnresolvedThreadIdsAddressedToCaller"/> already returns them in,
    /// since comments are appended chronologically) until the response actually emitted
    /// (<see cref="MeasureEmittedLength"/> — the <see cref="CliEnvelope"/>-wrapped line, not a
    /// bare <see cref="ContextResult"/>, and using <paramref name="recognisedCommandName"/>, the
    /// exact <see cref="CliEnvelope.Command"/> string this invocation will actually carry) fits
    /// the ceiling. Every candidate is priced by building it and measuring the real serialisation,
    /// never estimated from a comment's raw string length (§10 block B review, blockers 1/2: a
    /// per-comment cost model that ignores JSON key/quote overhead and escaping, and a bare-
    /// <see cref="ContextResult"/> measurement that ignores the envelope's own <c>command</c> field
    /// echoing every consumed argument — not just the verb — are exactly the class of defect this
    /// build measures its way around instead of risking again).
    /// </summary>
    private static ContextResult BuildBudgetedContextResult(CardOwner role, WorkingContext context, string recognisedCommandName)
    {
        var allCommentIds = context.TopItem?.UnresolvedThreadIdsAddressedToCaller ?? [];

        var kept = allCommentIds.Count;
        ContextResult result;
        while (true)
        {
            var omitted = allCommentIds.Skip(kept).ToList();
            var truncated = omitted.Count > 0;
            var truncationStatement = truncated ? DescribeTruncation(omitted) : null;
            result = FinalizeBudget(role, context, omitted, truncated, truncationStatement, exceededCeiling: false, overageStatement: null, recognisedCommandName);

            if (result.Budget.CharacterCount <= WorkingContextBudget.CharacterCeiling || kept == 0)
            {
                break;
            }

            kept--;
        }

        if (result.Budget.CharacterCount <= WorkingContextBudget.CharacterCeiling)
        {
            return result;
        }

        // Every narrative comment is already dropped (kept reached 0) and the response still
        // doesn't fit — the one case working-context accepts the response failing its own stated
        // budget: neither the register nor the brief may be shortened. This states the situation,
        // names whichever of the two actually drives the overage rather than blaming the register
        // unconditionally (§10 block B review nit), and — now that block E has given the
        // register-size review a CLI surface — names `rule review` as the remedy (§9 ruling 3: a
        // refusal/overage message may only name a command that exists).
        var finalCharacterCount = result.Budget.CharacterCount;
        var overage = finalCharacterCount - WorkingContextBudget.CharacterCeiling;
        var driver = DescribeOverageDriver(context);
        var overageStatement =
            $"the response is {finalCharacterCount} characters — {overage} over the " +
            $"{WorkingContextBudget.CharacterCeiling}-character ceiling — even with every narrative " +
            $"comment body already dropped ({allCommentIds.Count}). The register, the brief, and each " +
            $"addressed thread's structural facts may never be shortened; {driver} needs size reduction " +
            "to bring this back under budget. 'callboard rule review' starts the register's own size " +
            "review — the sanctioned path to reducing it.";
        var allOmitted = allCommentIds;
        var allOmittedStatement = allCommentIds.Count > 0 ? DescribeTruncation(allOmitted) : null;
        return FinalizeBudget(role, context, allOmitted, allCommentIds.Count > 0, allOmittedStatement, exceededCeiling: true, overageStatement, recognisedCommandName);
    }

    /// <summary>
    /// The rough JSON size of one addressed thread's structural entry (<c>commentId</c>,
    /// <c>author</c>, <c>timestamp</c>, <c>truncated</c>) once its body is withheld — a diagnostic
    /// estimate for <see cref="DescribeOverageDriver"/>'s prose, not part of the character count
    /// itself (that is always the real measured <see cref="MeasureEmittedLength"/> value).
    /// </summary>
    private const int ApproximateStructuralCharsPerThread = 100;

    /// <summary>
    /// Names whichever of the register, the brief, or the addressed threads' own structural
    /// overhead actually drives an overage, for <see cref="BuildBudgetedContextResult"/>'s message
    /// — never a fixed guess (§10 block B review nit: a message that always blames the register
    /// even when the top item's own body is oversized misnames its own cause). A card carrying
    /// enough addressed threads can exceed the ceiling on structural facts alone — <c>id</c>/
    /// <c>author</c>/<c>timestamp</c> are kept even once every body is dropped — so that is a third
    /// candidate driver, not only register-versus-brief.
    /// </summary>
    private static string DescribeOverageDriver(WorkingContext context)
    {
        var registerLength = context.LiveRulesAndHazards.Sum(static entry => entry.Card.Frontmatter.Title.Length + entry.Card.Body.Length);

        var topItem = context.TopItem;
        var briefLength = topItem is null
            ? 0
            : topItem.Card.Body.Length
                + (topItem.Card.BlockFields.Base?.Length ?? 0)
                + topItem.Card.BlockFields.Tasks.Sum(static task => task.Length);

        var addressedThreadCount = topItem?.UnresolvedThreadIdsAddressedToCaller.Count ?? 0;
        var threadStructuralLength = addressedThreadCount * ApproximateStructuralCharsPerThread;

        if (addressedThreadCount > 0 && threadStructuralLength >= registerLength && threadStructuralLength >= briefLength)
        {
            return $"the volume of the {addressedThreadCount} addressed threads' own structural facts (kept even with every body dropped)";
        }

        return registerLength >= briefLength ? "the register" : "the top item's own brief";
    }

    /// <summary>
    /// Builds <paramref name="context"/>'s <see cref="ContextResult"/> and measures the exact
    /// line it would put on stdout (<see cref="MeasureEmittedLength"/>), then rebuilds it with
    /// that length recorded as <see cref="ContextBudgetResult.CharacterCount"/> — iterated to a
    /// fixed point, not substituted once, since <see cref="ContextBudgetResult.CharacterCount"/>
    /// is itself a field of the object being measured: writing a longer number into it can grow
    /// the serialised length by a digit or two, which the very first substitution would then
    /// under-report (§10 block B review, blocker 2's smaller instance). Converges in at most a
    /// couple of rounds — digit count only grows, and only at a power of ten — so four attempts is
    /// generous headroom, not a magic number tuned to one fixture.
    /// </summary>
    private static ContextResult FinalizeBudget(
        CardOwner role, WorkingContext context, IReadOnlyList<string> omittedNarrativeCommentIds, bool truncated,
        string? truncationStatement, bool exceededCeiling, string? overageStatement, string recognisedCommandName)
    {
        var characterCount = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var probe = BuildContextResult(role, context, omittedNarrativeCommentIds, characterCount, truncated, truncationStatement, exceededCeiling, overageStatement);
            var measured = MeasureEmittedLength(probe, recognisedCommandName);
            if (measured == characterCount)
            {
                return probe;
            }

            characterCount = measured;
        }

        return BuildContextResult(role, context, omittedNarrativeCommentIds, characterCount, truncated, truncationStatement, exceededCeiling, overageStatement);
    }

    /// <summary>
    /// The character length of the line this response would actually put on stdout — the
    /// <see cref="CliEnvelope"/>-wrapped serialisation <see cref="WriteEnvelope"/> itself writes,
    /// not the bare <see cref="ContextResult"/> in isolation (§10 block B review, blocker 1: the
    /// envelope's own <c>{"ok":...,"command":...,"result":</c> wrapper and closing brace are
    /// fixed overhead a measurement of the bare result silently misses, so the stated ceiling
    /// would bound something that never ships). <paramref name="recognisedCommandName"/> must be
    /// the exact string <see cref="WriteEnvelope"/> will embed as <see cref="CliEnvelope.Command"/>
    /// — <see cref="ArgumentCursor.ConsumedTokens"/> means that string echoes every consumed
    /// argument for this invocation (<c>"context --role worker"</c>), not just the verb, so a
    /// hard-coded <c>"context"</c> would silently under-measure the real line.
    /// </summary>
    private static int MeasureEmittedLength(ContextResult result, string recognisedCommandName)
    {
        var envelope = new CliEnvelope { Ok = true, Command = recognisedCommandName, Result = result.ToJsonElement() };
        return JsonSerializer.Serialize(envelope, CliJsonContext.Default.CliEnvelope).Length;
    }

    private static string DescribeTruncation(IReadOnlyCollection<string> omittedNarrativeCommentIds)
    {
        var count = omittedNarrativeCommentIds.Count;
        var noun = count == 1 ? "comment body" : "comment bodies";
        var verb = count == 1 ? "was" : "were";
        return $"{count} narrative {noun} addressed to the caller {verb} dropped to fit the " +
            $"{WorkingContextBudget.CharacterCeiling}-character ceiling: {string.Join(", ", omittedNarrativeCommentIds)}.";
    }

    private static ContextResult BuildContextResult(
        CardOwner role, WorkingContext context, IReadOnlyList<string> omittedNarrativeCommentIds, int characterCount,
        bool truncated, string? truncationStatement, bool exceededCeiling, string? overageStatement) => new()
        {
            Role = role.ToWireString(),
            LiveRules = [.. context.LiveRulesAndHazards.Where(static entry => CardStore.IsRuleCard(entry.Card)).Select(ToContextRegisterCardResult)],
            LiveHazards = [.. context.LiveRulesAndHazards.Where(static entry => CardStore.IsHazardCard(entry.Card)).Select(ToContextRegisterCardResult)],
            QueueOrder = WorkingContextAssembler.QueueOrderDescription,
            Queue = [.. context.Queue.Select(ToContextQueueEntryResult)],
            TopItem = context.TopItem is { } topItem ? ToContextTopItemResult(topItem, omittedNarrativeCommentIds) : null,
            Budget = new ContextBudgetResult
            {
                TokenBudget = WorkingContextBudget.TokenBudget,
                CharactersPerToken = WorkingContextBudget.CharactersPerToken,
                MarginFraction = WorkingContextBudget.MarginFraction,
                CharacterCeiling = WorkingContextBudget.CharacterCeiling,
                CharacterCount = characterCount,
                Statement = WorkingContextBudget.Statement,
                Truncated = truncated,
                TruncationStatement = truncationStatement,
                OmittedNarrativeCommentIds = omittedNarrativeCommentIds,
                ExceededCeiling = exceededCeiling,
                OverageStatement = overageStatement,
            },
        };

    private static ContextRegisterCardResult ToContextRegisterCardResult((string FilePath, CardFile Card) entry) => new()
    {
        Id = entry.Card.Frontmatter.Id,
        FilePath = entry.FilePath,
        Title = entry.Card.Frontmatter.Title,
        Body = entry.Card.Body,
    };

    private static ContextQueueEntryResult ToContextQueueEntryResult(WorkingContextQueueEntry entry) => new()
    {
        Id = entry.Card.Frontmatter.Id,
        Kind = entry.Card.Frontmatter.Kind.ToWireString(),
        FilePath = entry.FilePath,
        Title = entry.Card.Frontmatter.Title,
        Owner = entry.Card.Frontmatter.Owner.ToWireString(),
        Updated = entry.Card.Frontmatter.Updated,
    };

    private static ContextTopItemResult ToContextTopItemResult(WorkingContextTopItem topItem, IReadOnlyList<string> omittedNarrativeCommentIds)
    {
        var card = topItem.Card;
        var omittedSet = new HashSet<string>(omittedNarrativeCommentIds, StringComparer.Ordinal);

        ContextVerdictResult? verdict = null;
        if (topItem.PreviousRoundClaims.Count > 0 || topItem.PreviousRoundLimits.Count > 0)
        {
            var round = topItem.PreviousRoundClaims.Count > 0 ? topItem.PreviousRoundClaims[0].Round : topItem.PreviousRoundLimits[0].Round;
            verdict = new ContextVerdictResult
            {
                Round = round,
                Claims = [.. topItem.PreviousRoundClaims.Select(static claim => claim.Text)],
                Limits = [.. topItem.PreviousRoundLimits.Select(static limit => limit.Text)],
            };
        }

        return new ContextTopItemResult
        {
            Id = card.Frontmatter.Id,
            Kind = card.Frontmatter.Kind.ToWireString(),
            FilePath = topItem.FilePath,
            Title = card.Frontmatter.Title,
            Owner = card.Frontmatter.Owner.ToWireString(),
            Body = card.Body,
            Base = card.BlockFields.Base,
            ReferencedTasks = [.. card.BlockFields.Tasks],
            ConstraintsRule = WorkingContextAssembler.ConstraintsRuleDescription,
            Constraints = [.. topItem.BindingConstraints.Select(static constraint => constraint.Card.Frontmatter.Id)],
            UnresolvedThreadsAddressedToCaller = [.. topItem.UnresolvedThreadIdsAddressedToCaller.Select(threadId => ToContextThreadResult(card, threadId, omittedSet))],
            PreviousRoundVerdict = verdict,
            BlockedBy = topItem.BlockedByIds,
            Halted = topItem.Halted,
            HaltedByQuestionId = topItem.HaltedByQuestionId,
            HaltedByQuestionTitle = topItem.HaltedByQuestionTitle,
        };
    }

    /// <summary>
    /// One unresolved thread addressed to the caller — structural facts (id, author, timestamp)
    /// are always carried; <see cref="ContextThreadResult.Body"/> is withheld exactly when
    /// <paramref name="commentId"/> is in <paramref name="omittedNarrativeCommentIds"/> (§10
    /// block B: "a thread's structural facts ... are routing, not narrative, and are kept").
    /// </summary>
    private static ContextThreadResult ToContextThreadResult(CardFile card, string commentId, IReadOnlySet<string> omittedNarrativeCommentIds)
    {
        var comment = card.Comments.First(comment => comment.Id == commentId);
        var truncated = omittedNarrativeCommentIds.Contains(commentId);
        return new ContextThreadResult
        {
            CommentId = comment.Id,
            Author = comment.Author.ToWireString(),
            Timestamp = comment.Timestamp,
            Body = truncated ? null : comment.Body,
            Truncated = truncated,
        };
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
