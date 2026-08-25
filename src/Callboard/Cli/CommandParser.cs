using Callboard.Cards;

namespace Callboard.Cli;

/// <summary>
/// The parse phase (O-3, DEVLOG §5): consumes argv tokens from a <see cref="CommandDispatcher.
/// CommandContext"/> and builds a <see cref="CommandDispatcher.ParseResult"/> — either a refusal,
/// or a <see cref="CommandDispatcher.ParsedCommand"/> describing what was asked for. Deliberately
/// a separate top-level class from <see cref="CommandDispatcher"/>, not a nested one: the handler
/// functions (<c>CommandDispatcher.RunVersion</c>, <c>CommandDispatcher.RunIndexRebuild</c>) are
/// <see langword="private"/> members of <see cref="CommandDispatcher"/>, and a nested type can see
/// its enclosing type's private members regardless of the nested type's own accessibility — so
/// nesting this here would not have stopped a parse arm from naming a handler. As a sibling
/// top-level class, it cannot: see the <see cref="CommandDispatcher"/> class doc comment for
/// exactly what that does and does not rule out. Every method here is <see langword="private"/> to
/// this class except <see cref="Parse"/>, which <see cref="CommandDispatcher.Run"/> calls — the
/// only caller, and the only place a resulting <see cref="CommandDispatcher.ParsedCommand"/> is
/// ever matched to a handler.
/// </summary>
internal static class CommandParser
{
    /// <summary>
    /// Routes on <paramref name="command"/>, consuming from <paramref name="context"/>'s cursor
    /// whatever each arm recognises, and either refuses outright or hands back a
    /// <see cref="CommandDispatcher.ParseResult.Ready"/> wrapping an inert
    /// <see cref="CommandDispatcher.ParsedCommand"/>. No arm here calls a handler — it cannot, see
    /// the class doc comment — which is exactly what makes deferring dispatch past
    /// <see cref="CommandDispatcher.EnforceNoUnconsumedArguments"/> meaningful.
    /// <see cref="CommandDispatcher.Run"/> is the only caller.
    /// </summary>
    internal static CommandDispatcher.ParseResult Parse(string command, CommandDispatcher.CommandContext context) => command switch
    {
        "version" => new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.Version()),
        "index" => ParseIndex(context),
        "block" => ParseBlock(context),
        "section" => ParseSection(context),
        "finding" => ParseFinding(context),
        "rule" => ParseRule(context),
        "hazard" => ParseHazard(context),
        "obligation" => ParseObligation(context),
        "decision" => ParseDecision(context),
        "question" => ParseQuestion(context),
        "change" => ParseChange(context),
        "nit" => ParseNit(context),
        _ => new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
            "unknown-command",
            $"no such command: '{command}'. Known commands: version, index, block, section, finding, rule, hazard, obligation, decision, question, change, nit.")),
    };

    /// <summary>
    /// <c>index</c>'s only job is routing to a subcommand — currently just <c>rebuild</c>. No
    /// subcommand, or one this dispatcher does not recognise, refuses and names what does exist.
    /// Peeks rather than taking: a token this method does not recognise is left in
    /// <see cref="CommandDispatcher.CommandContext.Arguments"/> unconsumed, both so
    /// <see cref="CommandDispatcher.EnforceNoUnconsumedArguments"/> still sees it (not
    /// load-bearing here — the refusal already stands on its own) and, the reason it matters, so
    /// <c>CommandDispatcher.RecognisedCommand</c> never reports a subcommand that was rejected as
    /// part of the recognised command name.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseIndex(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'index' requires a subcommand. Known subcommands: rebuild."));
            case "rebuild":
                context.Arguments.TryTake();
                return ParseIndexRebuild(context.WorkingDirectory);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'index' subcommand: '{subcommand}'. Known subcommands: rebuild."));
        }
    }

    /// <summary>
    /// Builds <c>index rebuild</c>'s <see cref="CommandDispatcher.ParsedCommand.IndexRebuild"/> —
    /// data only. Takes only the <see langword="string"/> working directory
    /// <see cref="ParseIndex"/> already had — not the <see cref="CommandDispatcher.CommandContext"/>
    /// and not the <see cref="ArgumentCursor"/> — so nothing in this method's body can name the
    /// cursor at all: pulling another token here is <c>CS0103</c> (unknown identifier), not a
    /// mistake this design leaves to discipline. This method also cannot name
    /// <c>CommandDispatcher.RunIndexRebuild</c> at all — that call is <c>CS0122</c> — which is the
    /// class doc comment's structural half of O-3's evidence for this verb.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseIndexRebuild(string workingDirectory) =>
        new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.IndexRebuild(workingDirectory));

    /// <summary>
    /// <c>block</c>'s only job is routing to a subcommand: <c>transition</c>, <c>gate</c> (§5
    /// block D), <c>add-blocker</c> and <c>remove-blocker</c> (§5 block D). Same peek-don't-take
    /// shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlock(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'block' requires a subcommand. Known subcommands: transition, gate, add-blocker, remove-blocker, approve."));
            case "transition":
                context.Arguments.TryTake();
                return ParseBlockTransition(context);
            case "gate":
                context.Arguments.TryTake();
                return ParseBlockGate(context);
            case "add-blocker":
                context.Arguments.TryTake();
                return ParseBlockedByMutation(context, static (filePath, blockingCardId, role, changeName, workingDirectory, timestamp) =>
                    new CommandDispatcher.ParsedCommand.BlockAddBlocker(filePath, blockingCardId, role, changeName, workingDirectory, timestamp));
            case "remove-blocker":
                context.Arguments.TryTake();
                return ParseBlockedByMutation(context, static (filePath, blockingCardId, role, changeName, workingDirectory, timestamp) =>
                    new CommandDispatcher.ParsedCommand.BlockRemoveBlocker(filePath, blockingCardId, role, changeName, workingDirectory, timestamp));
            case "approve":
                context.Arguments.TryTake();
                return ParseBlockApprove(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'block' subcommand: '{subcommand}'. Known subcommands: transition, gate, add-blocker, remove-blocker, approve."));
        }
    }

    /// <summary>
    /// Builds <c>block transition</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockTransition"/>:
    /// two positional tokens (card file path, transition name) followed by <c>--role</c> (required),
    /// and the optional <c>--base</c>/<c>--change</c> flags. Everything decidable from argv alone is
    /// decided here — including <c>--role</c>'s validity, a <see cref="CardOwner"/> wire-format check
    /// that needs no file access — so only what genuinely depends on the card's on-disk state
    /// (whether the named transition is legal from its current status, whether <c>base</c> is
    /// already recorded) is left to the execute phase (O-3). A flag this method does not recognise
    /// is left unconsumed, the same "peek, don't take" discipline <see cref="ParseIndex"/> already
    /// uses, so the funnel's own <c>unrecognised-argument</c> refusal covers it without a second,
    /// verb-specific code for the same fact.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockTransition(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block transition' requires a card file path and a transition name."));
        }

        var transitionName = context.Arguments.TryTake();
        if (transitionName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block transition' requires a transition name."));
        }

        // §8 block A brief item 1 (Architect ruling): 'block approve' is the only door to
        // 'approved' — an approval through this generic path would move a block to that state
        // carrying no certification at all, exactly what review-certification exists to prevent.
        // Argv-decidable (the transition name alone settles it, no card access needed), so refused
        // here rather than left to the execute phase.
        if (string.Equals(transitionName, "approve", StringComparison.Ordinal))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "approve-via-transition-refused",
                "'approve' cannot be applied through 'block transition' — it would move the block to 'approved' " +
                "with no certification recorded. Use 'block approve' instead, which stamps the certification in " +
                "the same write as the transition."));
        }

        // §8 block B (Architect ruling, same reasoning as 'approve' above): 'fix-before-land' is
        // raised only as the side effect of dispositioning a nit — a bare transition through this
        // path would move a block to 'briefed' with no nit actually dispositioned as
        // 'fix-before-land', exactly the neglect review-certification's "SHALL NOT lapse by
        // neglect" exists to prevent. Argv-decidable, so refused here rather than left to execute.
        if (string.Equals(transitionName, "fix-before-land", StringComparison.Ordinal))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "fix-before-land-via-transition-refused",
                "'fix-before-land' cannot be applied through 'block transition' — it is only raised as the " +
                "side effect of dispositioning a nit. Use 'nit disposition --disposition fix-before-land' " +
                "instead."));
        }

        // §8a block A revision (Product Owner ruling: "approved is terminal", amendment-requested
        // cut entirely): 'amendment-requested' is no longer a named edge on BlockFlowTransitions at
        // all, so a 'block transition ... amendment-requested' call needs no special-cased refusal
        // here — it reaches ApplyBlockTransitionUnderExistingLock like any other unrecognised name
        // and is refused there as an ordinary undefined-transition, the same as any string that was
        // never a real edge.

        // §8a block A (Architect ruling, same reasoning as the two refusals above): 'land' is not
        // individually invocable — a block reaches 'landed' only as a consequence of its whole
        // section closing (work-lifecycle: "Approval is provisional until the section closes").
        // Argv-decidable, so refused here rather than left to execute. Names the one door that
        // remains, not merely that this one is shut.
        if (string.Equals(transitionName, "land", StringComparison.Ordinal))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "land-via-transition-refused",
                "'land' cannot be applied through 'block transition' — a block reaches 'landed' only as a " +
                "consequence of its whole section closing. Use 'section close' instead, which lands every " +
                "approved block in the section as one operation."));
        }

        // §8a block B (Architect ruling, same reasoning as 'approve'/'fix-before-land'/'land'
        // above): 'finding-recurred' is a supervisor returning a remediation card it already owns —
        // raised only through 'section verdict --finding-recurred', in the same write as the
        // verdict entry itself. A bare transition through this path would move a card to 'briefed'
        // with no verdict recorded at all, and could not tell a remediation card from a
        // task-implementing block the way 'section verdict' does. Argv-decidable, so refused here.
        if (string.Equals(transitionName, "finding-recurred", StringComparison.Ordinal))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "finding-recurred-via-transition-refused",
                "'finding-recurred' cannot be applied through 'block transition' — it is only raised as the " +
                "effect of a supervisor's verdict on the finding this card owns. Use " +
                "'section verdict --finding-recurred <card-id>' instead."));
        }

        string? roleText = null;
        string? baseCommit = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--base"] = value => baseCommit = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'block transition' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.BlockTransition(
            filePath, transitionName, role, baseCommit, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>block gate</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockGate"/>: three
    /// positional tokens (card file path, gate label, exit code) followed by <c>--role</c>
    /// (required) and the optional <c>--change</c> flag. The label
    /// (<see cref="GateResult.IsValidLabel"/>) and the exit code (a valid integer) are both
    /// argv-decidable, so both are validated here rather than left to the execute phase (O-3), the
    /// same discipline <see cref="ParseBlockTransition"/> already applies to <c>--role</c>.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockGate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block gate' requires a card file path, a gate label and an exit code."));
        }

        var label = context.Arguments.TryTake();
        if (label is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block gate' requires a gate label and an exit code."));
        }

        if (!GateResult.IsValidLabel(label))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-gate-label",
                $"'{label}' is not a valid gate label — a label cannot be empty, whitespace-only, or contain '=' or ','."));
        }

        var exitCodeText = context.Arguments.TryTake();
        if (exitCodeText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block gate' requires an exit code."));
        }

        if (!int.TryParse(exitCodeText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exitCode))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-exit-code",
                $"'{exitCodeText}' is not a valid exit code — it must be an integer."));
        }

        var flags = ParseRoleAndChangeFlags(context, "'block gate'");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.BlockGate(
            filePath, label, exitCode, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds either <c>block add-blocker</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockAddBlocker"/>
    /// or <c>block remove-blocker</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockRemoveBlocker"/>
    /// — the two verbs take identical argv shape (a card file path, a blocking card id,
    /// <c>--role</c>, an optional <c>--change</c>) and differ only in which case they construct,
    /// so <paramref name="build"/> is the one place that difference lives.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockedByMutation(
        CommandDispatcher.CommandContext context,
        Func<string, string, CardOwner, string?, string, DateTimeOffset, CommandDispatcher.ParsedCommand> build)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "this command requires a card file path and a blocking card id."));
        }

        var blockingCardId = context.Arguments.TryTake();
        if (blockingCardId is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "this command requires a blocking card id."));
        }

        if (!BlockCardFields.IsValidListItem(blockingCardId))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-blocking-card-id",
                "a blocking card id cannot be empty or whitespace-only — card identities are never empty."));
        }

        var flags = ParseRoleAndChangeFlags(context, "this command");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(
            build(filePath, blockingCardId, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>block approve</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockApprove"/> (§8
    /// block A, review-certification: "Approve is binary and certifies one state" / "Certification
    /// enumerates its claims"). Addressed by <c>--id</c>, resolved through <see cref="Cards.
    /// CardIdentityResolver"/> at execute time (§7's settled ruling: <c>--id</c> binds) — no
    /// positional file-path argument, unlike every §1–§6 verb. Everything argv-decidable is decided
    /// here: <c>--role</c>'s wire-format validity, <c>--state</c> non-empty/whitespace
    /// (review-certification: "An approval SHALL name that state explicitly" — an empty name names
    /// nothing), each <c>--claims</c>/<c>--limits</c> item non-empty, and the "no claims and no
    /// limits" refusal itself — the spec's own conjunction (§8 block A brief item 5): claims-only and
    /// limits-only both pass. Role <em>permission</em> (reviewer/supervisor only,
    /// review-certification: "Approval is role-bounded") is left to the execute phase, the same
    /// split <c>rule compact</c>'s Architect-only restriction already uses (<see cref="Cards.
    /// CardStore.CompactRules"/>) — it is a fact about who may perform this operation, not about the
    /// shape of the argument.
    ///
    /// <para>
    /// <b><c>--claims</c>/<c>--limits</c> are repeatable, not comma-joined (§8 remediation blocker
    /// 3).</b> Certification text SHALL be "actionable by a reviewer who did not author it"
    /// (review-certification: "Certification enumerates its claims") — exactly the free-form prose
    /// most likely to contain a comma. A single comma-joined value routed through
    /// <see cref="CardFileFormat.SplitFrontmatterList"/> silently split one claim's own prose into
    /// two claims, each with its own id, with no refusal at all: storage was never the problem
    /// (block A gave each claim its own line and id), only this CLI boundary. Same repeatable shape
    /// <c>nit raise --site</c> and (previously) <c>--changed</c> already established: one flag
    /// occurrence per item, taken in argv order.
    /// </para>
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockApprove(CommandDispatcher.CommandContext context)
    {
        string? id = null;
        string? roleText = null;
        string? state = null;
        string? changeName = null;
        var claims = new List<string>();
        var limits = new List<string>();

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--id"] = value => id = value,
            ["--role"] = value => roleText = value,
            ["--state"] = value => state = value,
            ["--claims"] = value => claims.Add(value),
            ["--limits"] = value => limits.Add(value),
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (id is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'block approve' requires '--id <card-id>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'block approve' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "state-required",
                "'block approve' requires '--state <text>' naming the exact state certified, including any " +
                "uncommitted working-tree content it covers."));
        }

        foreach (var claim in claims)
        {
            if (!CardApprovalClaim.IsValidText(claim))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "invalid-claim", "'--claims' cannot name an empty or whitespace-only item."));
            }
        }

        foreach (var limit in limits)
        {
            if (!CardApprovalLimit.IsValidText(limit))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "invalid-limit", "'--limits' cannot name an empty or whitespace-only item."));
            }
        }

        if (claims.Count == 0 && limits.Count == 0)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "no-claims-or-limits",
                "'block approve' requires at least one of '--claims <text>' or '--limits <text>' (each " +
                "repeatable) — certification text is read by a later reviewer who did not write it, so an " +
                "approval enumerating nothing is refused."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.BlockApprove(
            id, role, state, claims, limits, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// The one flag-loop every §5 verb's flag parsing now goes through (§5 remediation, DEVLOG §5
    /// finding B1/N residue) — <paramref name="setters"/> names every flag this call recognises and
    /// what to do with its value; a token that matches none of them is left unconsumed, the same
    /// "peek, don't take" discipline <see cref="ParseIndex"/> uses, so the funnel's own
    /// <c>unrecognised-argument</c> refusal covers it. <see cref="ParseBlockTransition"/> used to
    /// keep its own hand-copied loop alongside this one — same shape, drawn from the same cursor,
    /// with nothing tying the two together — because it also parses <c>--base</c> in the same pass;
    /// that duplication is exactly where the acting-role fix (finding B1) landing in only two of
    /// three verbs became invisible to a block reviewer looking at one call site at a time. Both
    /// now build a <paramref name="setters"/> map over this one loop instead.
    /// </summary>
    private static CommandOutcome.Refusal? ConsumeKnownFlags(
        CommandDispatcher.CommandContext context, IReadOnlyDictionary<string, Action<string>> setters, IReadOnlyDictionary<string, Action>? booleanSetters = null)
    {
        while (context.Arguments.Peek() is { } flag)
        {
            // §8 block B: nit raise's '--required' is presence-only, taking no value — the one
            // shape none of §1–§7's flags needed. Checked ahead of `setters` in the same loop
            // (not a separate pass before/after it) so a boolean flag can appear anywhere among a
            // verb's other flags, the same "any order" freedom ConsumeKnownFlags already gives
            // every value-taking flag.
            if (booleanSetters is not null && booleanSetters.TryGetValue(flag, out var booleanSetter))
            {
                context.Arguments.TryTake();
                booleanSetter();
                continue;
            }

            if (!setters.TryGetValue(flag, out var setter))
            {
                break;
            }

            context.Arguments.TryTake();
            var value = context.Arguments.TryTake();
            if (value is null)
            {
                return new CommandOutcome.Refusal("missing-flag-value", $"'{flag}' requires a value.");
            }

            setter(value);
        }

        return null;
    }

    /// <summary>
    /// The <c>--role</c> (required)/<c>--change</c> (optional) flag pair every §5 block D verb
    /// takes, factored out once both <see cref="ParseBlockGate"/> and
    /// <see cref="ParseBlockedByMutation"/> needed it — built over the same <see cref="
    /// ConsumeKnownFlags"/> loop <see cref="ParseBlockTransition"/> now uses for its own,
    /// <c>--base</c>-inclusive flag set, so the two can no longer drift on how a flag is consumed.
    /// </summary>
    private static (CardOwner? Role, string? ChangeName, CommandOutcome.Refusal? Refusal) ParseRoleAndChangeFlags(
        CommandDispatcher.CommandContext context, string commandLabel)
    {
        string? roleText = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return (null, null, flagRefusal);
        }

        if (roleText is null)
        {
            return (null, null, new CommandOutcome.Refusal("missing-argument", $"{commandLabel} requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return (null, null, new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return (role, changeName, null);
    }

    /// <summary>
    /// The shape every §7 block A creation verb shares once its kind-specific fields (if any) are
    /// out of the way: one positional token (card file path), <c>--role</c> and <c>--title</c>
    /// (both required), <c>--change</c> (required exactly when <paramref name="requireChange"/> is
    /// <see langword="true"/>), and a body read from stdin — the same read-only-extraction
    /// discipline <see cref="ParseFindingRecord"/> already applies. <paramref name="build"/> is the
    /// one place the resulting <see cref="CommandDispatcher.ParsedCommand"/> case differs; used
    /// directly by <see cref="ParseSectionCreate"/> and <see cref="ParseDecisionCreate"/>, whose
    /// argv shape is otherwise identical. <see cref="ParseRuleCreate"/>, <see cref="ParseHazardCreate"/>
    /// and <see cref="ParseObligationCreate"/> do not use this — each has an extra required flag
    /// (<c>--scope</c>; <c>--condition</c>/<c>--cadence</c>; <c>--owed-by</c>, §7 block C) this
    /// shape has no room for.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseCardCreate(
        CommandDispatcher.CommandContext context,
        string commandLabel,
        bool requireChange,
        Func<string, string, CardOwner, string, string?, string, DateTimeOffset, CommandDispatcher.ParsedCommand> build)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? changeName = null;

        // --change is registered as a known flag only when this verb's scope actually needs one
        // (requireChange) — a decision (Capability scope, no changeName) is never offered a flag it
        // has nowhere to use; the funnel's own unrecognised-argument refusal catches a caller who
        // supplies one anyway, rather than this method silently accepting and discarding it.
        var setters = new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
        };
        if (requireChange)
        {
            setters["--change"] = value => changeName = value;
        }

        var flagRefusal = ConsumeKnownFlags(context, setters);
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (requireChange && changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires '--change <name>'."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(build(filePath, title, role, body, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// The shape every §7 block A discharge verb shares — one positional token (card file path),
    /// <c>--role</c> (required) and the optional <c>--change</c> flag, the same
    /// <see cref="ParseRoleAndChangeFlags"/> pair <see cref="ParseBlockGate"/> and
    /// <see cref="ParseSectionClose"/> already use. <paramref name="kind"/> is fixed per caller (<see cref="ParseRule"/>/<see cref="ParseHazard"/>/
    /// <see cref="ParseObligation"/>/<see cref="ParseDecision"/>'s own <c>discharge</c> arm), never
    /// read from argv — there is no <c>--kind</c> flag, because which of the four kinds is being
    /// discharged is exactly what the top-level command word (<c>rule</c>/<c>hazard</c>/…) already
    /// said.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRegisterDischarge(
        CommandDispatcher.CommandContext context, CardKind kind, string commandLabel)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires a card file path."));
        }

        var flags = ParseRoleAndChangeFlags(context, commandLabel);
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RegisterDischarge(
            kind, filePath, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>change</c>'s only job is routing to a subcommand — currently just <c>archive</c>. Same
    /// peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseChange(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'change' requires a subcommand. Known subcommands: archive."));
            case "archive":
                context.Arguments.TryTake();
                return ParseChangeArchive(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'change' subcommand: '{subcommand}'. Known subcommands: archive."));
        }
    }

    /// <summary>
    /// Builds <c>change archive</c>'s <see cref="CommandDispatcher.ParsedCommand.ChangeArchive"/>
    /// (§7 block D). One positional token — the change's own name, not a file path: archive acts
    /// on the whole change directory <see cref="CardLayout.ChangesDirectory"/> names, never on one
    /// card, so there is no single file path to take the way every other verb's positional token
    /// is one. <c>--role</c> is required, the same as every other card-model write.
    ///
    /// <para>
    /// <b><c>--compact-family</c>/<c>--absorbs</c> are §7 block F's hook</b> (register: "Compaction
    /// of change-scoped rules SHALL be performed by the architect at archive") — the same card-id,
    /// comma-separated-list shapes <c>rule compact</c>'s own <c>--id</c>/<c>--absorbs</c> use, not
    /// a second flag vocabulary. Both are optional, but only together: naming one without the other
    /// is a self-contradictory request (compact what into what?), refused here rather than left for
    /// <see cref="Cards.CardStore.CompactRules"/> to discover with half its arguments missing.
    /// Whether <c>--role</c> actually names the architect is <see cref="CommandDispatcher.
    /// RunChangeArchive"/>'s check (it needs the parsed <see cref="CardOwner"/> value, not just its
    /// text), not this method's.
    /// </para>
    /// </summary>
    private static CommandDispatcher.ParseResult ParseChangeArchive(CommandDispatcher.CommandContext context)
    {
        var changeName = context.Arguments.TryTake();
        if (changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'change archive' requires a change name."));
        }

        string? roleText = null;
        string? compactFamilyId = null;
        string? compactAbsorbsRaw = null;
        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--compact-family"] = value => compactFamilyId = value,
            ["--absorbs"] = value => compactAbsorbsRaw = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'change archive' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if ((compactFamilyId is null) != (compactAbsorbsRaw is null))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'change archive' requires '--compact-family' and '--absorbs' together, or neither."));
        }

        IReadOnlyList<string>? compactAbsorbedIds = null;
        if (compactAbsorbsRaw is not null)
        {
            if (compactAbsorbsRaw.Length == 0)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "empty-absorb-set",
                    "'--absorbs' requires at least one rule id — a family with no members is not a family."));
            }

            var absorbedIds = CardFileFormat.SplitFrontmatterList(compactAbsorbsRaw);
            foreach (var id in absorbedIds)
            {
                if (!BlockCardFields.IsValidListItem(id))
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "invalid-absorbs", $"'--absorbs' cannot contain an empty item: '{compactAbsorbsRaw}'."));
                }
            }

            compactAbsorbedIds = absorbedIds;
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.ChangeArchive(
            changeName, role, compactFamilyId, compactAbsorbedIds, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>section</c>'s only job is routing to a subcommand: <c>verdict</c>, <c>close</c> and
    /// <c>status</c> (§5 block E). Same peek-don't-take shape as <see cref="ParseBlock"/>, same
    /// reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSection(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'section' requires a subcommand. Known subcommands: create, verdict, authorise, close, status."));
            case "create":
                context.Arguments.TryTake();
                return ParseSectionCreate(context);
            case "verdict":
                context.Arguments.TryTake();
                return ParseSectionVerdict(context);
            case "authorise":
                context.Arguments.TryTake();
                return ParseSectionAuthorise(context);
            case "close":
                context.Arguments.TryTake();
                return ParseSectionClose(context);
            case "status":
                context.Arguments.TryTake();
                return ParseSectionStatus(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'section' subcommand: '{subcommand}'. Known subcommands: create, verdict, authorise, close, status."));
        }
    }

    /// <summary>
    /// Builds <c>section create</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionCreate"/>
    /// (§7 block A, Product Owner ruling: "<c>section create</c> is in §7's scope"). One positional
    /// token (card file path); <c>--role</c>, <c>--title</c> and <c>--change</c> are required. The
    /// body is read from stdin during this parse, the same read-only-extraction discipline
    /// <see cref="ParseFindingRecord"/> already applies.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionCreate(CommandDispatcher.CommandContext context) =>
        ParseCardCreate(context, "'section create'", requireChange: true, build:
            (filePath, title, role, body, changeName, workingDirectory, timestamp) =>
                new CommandDispatcher.ParsedCommand.SectionCreate(filePath, title, role, body, changeName!, workingDirectory, timestamp));

    /// <summary>
    /// Builds <c>section verdict</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionVerdict"/>:
    /// one positional token (card file path) followed by <c>--verdict</c>, <c>--range-from</c> and
    /// <c>--range-to</c> (all required), <c>--role</c> (required) and the optional <c>--change</c>
    /// flag. <c>--verdict</c>'s wire-format validity is argv-decidable, the same O-3 discipline
    /// <see cref="ParseBlockTransition"/> already applies to <c>--role</c>.
    ///
    /// <para>
    /// <b>§8a block B's additions: <c>--finding-recurred</c> and <c>--finding-new</c>, both
    /// repeatable, both file/argv-decidable at parse.</b> <c>--finding-recurred &lt;card-id&gt;</c>
    /// follows the same repeatable, one-flag-occurrence-per-item shape <c>--claims</c>/
    /// <c>--limits</c>/<c>nit raise --site</c> already established (§8 remediation blocker 3's
    /// ruling: free text routed through a single comma-joined value silently splits on a comma
    /// inside the text itself) — each occurrence is one card id, resolved through <see cref="Cards.
    /// CardIdentityResolver"/> at execute time. <c>--finding-new &lt;manifest-file&gt;</c> is also
    /// repeatable — any number of first-time findings in one verdict (Architect ruling, DEVLOG "§8a
    /// block B — architect: accept the design, reject the one-new-finding cap": a section with
    /// several new findings on its first pass is the ordinary case, not a corner one) — but each
    /// occurrence is <b>one self-contained manifest file</b>, parsed by <see cref="Cards.
    /// NewFindingCardManifest"/>, not a slot in a positionally-zipped set of flags: see that type's
    /// own doc comment for why a quartet of repeatable flags (key/file/title/body-file, occurrence
    /// <em>n</em> across all four naming one finding) was rejected — it can silently attach one
    /// finding's body to another finding's key, a failure no count-mismatch refusal can see. A
    /// manifest's own body is read from the manifest file, never as a quoted argument (ADR-0001) and
    /// never from stdin (unlike <c>section create</c>'s single body) — the same "read a value from a
    /// file, not an argument" discipline <c>--blind-spot-body-file</c> established, reused inside
    /// <see cref="Cards.NewFindingCardManifest.Parse"/> rather than re-invented, including its
    /// argv-decidable-first/environmental-second split between "no readable file" and "exists but
    /// unreadable" for the manifest file itself.
    /// </para>
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionVerdict(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section verdict' requires a card file path."));
        }

        string? verdictText = null;
        string? rangeFrom = null;
        string? rangeTo = null;
        string? roleText = null;
        string? changeName = null;
        var recurringFindingIds = new List<string>();
        var newFindingManifestPaths = new List<string>();

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--verdict"] = value => verdictText = value,
            ["--range-from"] = value => rangeFrom = value,
            ["--range-to"] = value => rangeTo = value,
            ["--role"] = value => roleText = value,
            ["--change"] = value => changeName = value,
            ["--finding-recurred"] = value => recurringFindingIds.Add(value),
            ["--finding-new"] = value => newFindingManifestPaths.Add(value),
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (verdictText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--verdict <verdict>'."));
        }

        if (!SectionVerdictWireFormat.TryParse(verdictText, out var verdict))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-verdict", $"unrecognised verdict: '{verdictText}'. Recognised verdicts: {SectionVerdictWireFormat.RecognisedValues}."));
        }

        if (rangeFrom is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--range-from <commit>'."));
        }

        if (!Callboard.Cards.SectionVerdictEntry.IsValidRangeValue(rangeFrom))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-range", "'--range-from' cannot be empty or whitespace-only — a range endpoint that cannot be read back is not a range."));
        }

        if (rangeTo is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--range-to <commit>'."));
        }

        if (!Callboard.Cards.SectionVerdictEntry.IsValidRangeValue(rangeTo))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-range", "'--range-to' cannot be empty or whitespace-only — a range endpoint that cannot be read back is not a range."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        foreach (var recurringId in recurringFindingIds)
        {
            if (string.IsNullOrWhiteSpace(recurringId))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "invalid-finding-recurred", "'--finding-recurred' cannot name an empty or whitespace-only card id."));
            }
        }

        var newFindings = new List<Callboard.Cards.NewFindingCardRequest>(newFindingManifestPaths.Count);
        foreach (var manifestPath in newFindingManifestPaths)
        {
            // §6 remediation's own precedent (--blind-spot-body-file): argv-decidable first (no
            // readable file at all is the caller's own mistake to fix here), environmental second
            // (a file that exists but cannot be read is not something the caller typo'd).
            if (!File.Exists(manifestPath))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "finding-new-manifest-not-found",
                    $"'--finding-new' names a path with no readable file: '{manifestPath}'."));
            }

            string manifestContent;
            try
            {
                manifestContent = File.ReadAllText(manifestPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "finding-new-manifest-unreadable",
                    $"'--finding-new' names a file that exists but could not be read: '{manifestPath}' ({ex.Message})"));
            }

            var (request, manifestFailure) = Callboard.Cards.NewFindingCardManifest.Parse(manifestPath, manifestContent);
            if (manifestFailure is not null)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "finding-new-manifest-malformed", manifestFailure));
            }

            newFindings.Add(request!);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionVerdict(
            filePath, verdict, rangeFrom, rangeTo, role, changeName, recurringFindingIds, newFindings, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>section authorise</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionAuthorise"/>:
    /// one positional token (card file path), <c>--reason &lt;text&gt;</c> (required, non-empty/
    /// whitespace-only checked here — argv-decidable, the same discipline <c>--state</c> follows
    /// for <c>block approve</c>), plus the <c>--role</c>/<c>--change</c> pair
    /// <see cref="ParseRoleAndChangeFlags"/> already factors out. Whether <c>--role</c> actually
    /// names <see cref="CardOwner.ProductOwner"/> is not checked here — that is <see cref="Cards.
    /// CardStore.RecordSectionAuthorisationUnderExistingLock"/>'s own first decision, the same
    /// "recorded, not authorised at parse" split every other role-checked write already follows.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionAuthorise(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section authorise' requires a card file path."));
        }

        string? reason = null;
        string? roleText = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--reason"] = value => reason = value,
            ["--role"] = value => roleText = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "reason-required",
                "'section authorise' requires '--reason <text>' naming why the bound is being pushed further."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section authorise' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionAuthorise(
            filePath, reason, role, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>section close</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionClose"/>:
    /// one positional token (card file path), <c>--role</c> (required) and the optional
    /// <c>--change</c> flag — the same <c>--role</c>/<c>--change</c> pair
    /// <see cref="ParseRoleAndChangeFlags"/> already factors out. §8a block A briefly gave this verb
    /// a third flag, <c>--state</c>, to compare against each block's <c>reviewed_state</c>; §8a
    /// block A's revision (Product Owner ruling: closing SHALL NOT compare `reviewed_state` against
    /// the repository) removed the check that flag fed, so it is gone too.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionClose(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section close' requires a card file path."));
        }

        var flags = ParseRoleAndChangeFlags(context, "'section close'");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionClose(
            filePath, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>section status</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionStatus"/>:
    /// one positional token (card file path), nothing else — read-only, so no role, no
    /// <c>--change</c>, no timestamp (work-lifecycle: "the system answers from the section entity
    /// without requiring its cards to be read").
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionStatus(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section status' requires a card file path."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionStatus(filePath, context.WorkingDirectory));
    }

    /// <summary>
    /// <c>finding</c>'s only job is routing to a subcommand: <c>record</c> and <c>status</c> (§6
    /// block C). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseFinding(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'finding' requires a subcommand. Known subcommands: record, status."));
            case "record":
                context.Arguments.TryTake();
                return ParseFindingRecord(context);
            case "status":
                context.Arguments.TryTake();
                return ParseFindingStatus(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'finding' subcommand: '{subcommand}'. Known subcommands: record, status."));
        }
    }

    /// <summary>
    /// Builds <c>finding status</c>'s <see cref="CommandDispatcher.ParsedCommand.FindingStatus"/>:
    /// one positional token (card file path), nothing else — read-only, the same
    /// <see cref="ParseSectionStatus"/> shape. Unlike <c>section status</c>, this also carries
    /// <see cref="CommandDispatcher.CommandContext.WorkingDirectory"/>: staleness computation needs
    /// the repository root to resolve an <see cref="Callboard.Cards.FindingExtent.Explicit"/>
    /// extent's paths against (§6 block C ruling — the fingerprint is content, resolved relative to
    /// the repo, never git).
    /// </summary>
    private static CommandDispatcher.ParseResult ParseFindingStatus(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'finding status' requires a card file path."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.FindingStatus(filePath, context.WorkingDirectory));
    }

    /// <summary>
    /// Builds <c>finding record</c>'s <see cref="CommandDispatcher.ParsedCommand.FindingRecord"/>
    /// (§6 block B). One positional token (card file path); <c>--role</c>, <c>--title</c>,
    /// <c>--section</c>, <c>--change</c> and <c>--blind-spot</c> are required, the rest optional.
    /// The body is read from stdin during this parse (a read-only extraction, not the card-writing
    /// side effect O-3 guards — see <see cref="CommandDispatcher.ParsedCommand.FindingRecord"/>'s
    /// own doc comment), so a missing or non-redirected stdin refuses here rather than at execute
    /// time.
    ///
    /// <para>
    /// <b>The blind-spot declaration is checked as input, here (findings: "A clean finding requires
    /// a blind-spot declaration", §6 block B Architect ruling).</b> Block A already made "not
    /// declared" unrepresentable on a constructed <see cref="Callboard.Cards.FindingCardFields"/> —
    /// there is no nullable field left to discover empty later — so the refusal belongs at this
    /// boundary: the caller either supplied <c>--blind-spot none</c> (an explicit assertion there is
    /// no blind spot) or <c>--blind-spot obligation</c>/<c>--blind-spot hazard</c> (a declaration
    /// that is raised as that kind of card). Anything else — the flag missing entirely, or any other
    /// value — refuses with <c>unrecognised-blind-spot</c>, naming both declarations the system will
    /// accept, exactly as the spec's "the system refuses and names the declaration it requires"
    /// scenario asks for.
    /// </para>
    /// </summary>
    private static CommandDispatcher.ParseResult ParseFindingRecord(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires a card file path."));
        }

        string? roleText = null;
        string? title = null;
        string? section = null;
        string? changeName = null;
        string? blindSpotText = null;
        string? instrument = null;
        string? verifiedAt = null;
        string? extentInstrument = null;
        string? extentExplicitRaw = null;
        string? blindSpotFile = null;
        string? blindSpotTitle = null;
        string? blindSpotBodyFile = null;
        string? dispositionText = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--title"] = value => title = value,
            ["--section"] = value => section = value,
            ["--change"] = value => changeName = value,
            ["--blind-spot"] = value => blindSpotText = value,
            ["--instrument"] = value => instrument = value,
            ["--verified-at"] = value => verifiedAt = value,
            ["--extent-instrument"] = value => extentInstrument = value,
            ["--extent-explicit"] = value => extentExplicitRaw = value,
            ["--blind-spot-file"] = value => blindSpotFile = value,
            ["--blind-spot-title"] = value => blindSpotTitle = value,
            ["--blind-spot-body-file"] = value => blindSpotBodyFile = value,
            ["--disposition"] = value => dispositionText = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--title <text>'."));
        }

        if (section is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--section <name>'."));
        }

        if (changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--change <name>'."));
        }

        FindingBlindSpotRaiseRequest? raiseRequest;
        switch (blindSpotText)
        {
            case "none":
                raiseRequest = null;
                break;
            case "obligation":
            case "hazard":
                if (blindSpotFile is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-argument",
                        $"'finding record' requires '--blind-spot-file <path>' when --blind-spot is '{blindSpotText}'."));
                }

                if (blindSpotTitle is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-argument",
                        $"'finding record' requires '--blind-spot-title <text>' when --blind-spot is '{blindSpotText}'."));
                }

                if (blindSpotBodyFile is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-argument",
                        $"'finding record' requires '--blind-spot-body-file <path>' when --blind-spot is '{blindSpotText}'."));
                }

                // §6 remediation (reviewer nit) — split along the same "File.Exists first" line
                // RunFindingStatus/RunSectionStatus already draw for card-not-found: a path the
                // caller named that resolves to no readable file (missing, or naming a directory —
                // File.Exists is false for both) is the caller's own mistake to fix, argv-decidable
                // here at parse time; a path that does exist as a file but cannot be read for
                // environmental reasons (permission denied, a disk error) is not something the
                // caller typo'd, and gets a different code even though both still refuse at parse
                // time — there is no tool-failure outcome available before ParseResult.Ready.
                if (!File.Exists(blindSpotBodyFile))
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "blind-spot-body-file-not-found",
                        $"'--blind-spot-body-file' names a path with no readable file: '{blindSpotBodyFile}'."));
                }

                string blindSpotBody;
                try
                {
                    blindSpotBody = File.ReadAllText(blindSpotBodyFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "blind-spot-body-file-unreadable",
                        $"'--blind-spot-body-file' names a file that exists but could not be read: '{blindSpotBodyFile}' ({ex.Message})"));
                }

                var raisedKind = blindSpotText == "obligation" ? CardKind.Obligation : CardKind.Hazard;
                raiseRequest = new FindingBlindSpotRaiseRequest(raisedKind, blindSpotFile, blindSpotTitle, blindSpotBody);
                break;
            default:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unrecognised-blind-spot",
                    "'finding record' requires '--blind-spot <none|obligation|hazard>' — 'none' explicitly asserts " +
                    "there is no blind spot; 'obligation' or 'hazard' declares one and raises it as that kind of " +
                    (blindSpotText is null ? "card." : $"card. Unrecognised value: '{blindSpotText}'.")));
        }

        if (extentInstrument is not null && extentExplicitRaw is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-extent",
                "'--extent-instrument' and '--extent-explicit' are mutually exclusive; declare at most one."));
        }

        var extent = FindingExtent.BlockScope;
        if (extentInstrument is not null)
        {
            try
            {
                extent = FindingExtent.Instrument(extentInstrument);
            }
            catch (ArgumentException ex)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal("invalid-extent", ex.Message));
            }
        }
        else if (extentExplicitRaw is not null)
        {
            try
            {
                extent = FindingExtent.Explicit(extentExplicitRaw.Split(','));
            }
            catch (ArgumentException ex)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal("invalid-extent", ex.Message));
            }
        }

        // §6 block C: absent-or-"measured" declares the default (FindingDisposition.Measured,
        // itself never re-verified by staleness) — same "argv-decidable, checked here" O-3
        // discipline --blind-spot's own switch above already applies.
        FindingDisposition disposition;
        switch (dispositionText)
        {
            case null:
            case "measured":
                disposition = FindingDisposition.Measured;
                break;
            case "argued-clean":
                disposition = FindingDisposition.ArguedClean;
                break;
            default:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unrecognised-disposition",
                    $"'finding record' requires '--disposition <measured|argued-clean>' when supplied. Unrecognised value: '{dispositionText}'."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.FindingRecord(
            filePath, title, section, changeName, role, body, instrument, extent, verifiedAt, raiseRequest, disposition,
            context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>rule</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c> (§7
    /// block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRule(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'rule' requires a subcommand. Known subcommands: create, discharge, promote, author, compact, " +
                    "propose-compact, promote-constitution."));
            case "create":
                context.Arguments.TryTake();
                return ParseRuleCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Rule, "'rule discharge'");
            case "promote":
                context.Arguments.TryTake();
                return ParseRulePromote(context);
            case "author":
                context.Arguments.TryTake();
                return ParseRuleAuthor(context);
            case "compact":
                context.Arguments.TryTake();
                return ParseRuleCompact(context);
            case "propose-compact":
                context.Arguments.TryTake();
                return ParseRuleProposeCompact(context);
            case "promote-constitution":
                context.Arguments.TryTake();
                return ParseRulePromoteConstitution(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'rule' subcommand: '{subcommand}'. Known subcommands: create, discharge, promote, " +
                    "author, compact, propose-compact, promote-constitution."));
        }
    }

    /// <summary>
    /// Builds <c>rule promote</c>'s <see cref="CommandDispatcher.ParsedCommand.RulePromote"/> (§7
    /// block E). <c>--id</c> is a card id resolved at execute time through
    /// <see cref="CardIdentityResolver"/>, the same identity-addressing convention
    /// <c>decision supersede</c> already established — there is no positional file-path argument
    /// here, unlike every §7 block A creation verb.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRulePromote(CommandDispatcher.CommandContext context)
    {
        string? id = null;
        string? roleText = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--id"] = value => id = value,
            ["--role"] = value => roleText = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (id is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule promote' requires '--id <card-id>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule promote' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        // §9 block A2 remediation round two, Architect ruling: required unconditionally, not only
        // when the target happens to be change-scoped. The verb exists to promote a change-scoped
        // rule to repository scope, so the caller always knows the change; making the flag optional
        // left the ordinary invocation ('rule promote --id X --role Y', no '--change') anchoring
        // with changeName: null and silently failing to record for exactly the common case this
        // fix was for — "worse than one that records nowhere: it reads as complete." A refusal on
        // an already-repository-scoped rule ignores changeName entirely (CardLayout.DirectoryFor),
        // so requiring the flag costs that path nothing — the same reasoning ParseRoleAndChangeFlags'
        // own requireChange:true callers already rely on.
        if (changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule promote' requires '--change <name>'."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RulePromote(
            id, role, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>rule author</c>'s <see cref="CommandDispatcher.ParsedCommand.RuleAuthor"/> (§7
    /// block E, register: "Authoring a rule from findings SHALL create a new card and SHALL record
    /// which findings it was earned from"). Same shape as <see cref="ParseRuleCreate"/> (a card file
    /// path, a caller-chosen <c>--scope</c>) plus <c>--earned-from</c>: a comma-separated list of
    /// finding card ids, reusing the same <see cref="CardFileFormat.SplitFrontmatterList"/>-style
    /// comma convention <c>finding record</c>'s own <c>--extent-value</c> already uses for a list
    /// flag, required and non-empty (checked here, argv-decidable — whether each id actually
    /// resolves to a <c>finding</c> card is <c>CommandDispatcher.RunRuleAuthor</c>'s job, since that
    /// needs the record, not just argv).
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRuleAuthor(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule author' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? scopeText = null;
        string? changeName = null;
        string? earnedFromRaw = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--scope"] = value => scopeText = value,
            ["--change"] = value => changeName = value,
            ["--earned-from"] = value => earnedFromRaw = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule author' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule author' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (scopeText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule author' requires '--scope <change|repository>'."));
        }

        if (!CardScopeWireFormat.TryParse(scopeText, out var scope))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-scope", $"unrecognised scope: '{scopeText}'. Recognised scopes: {CardScopeWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrEmpty(earnedFromRaw))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'rule author' requires '--earned-from <finding-id>[,<finding-id>...]' — a rule authored from " +
                "findings SHALL record which findings it was earned from."));
        }

        var earnedFrom = CardFileFormat.SplitFrontmatterList(earnedFromRaw);
        foreach (var id in earnedFrom)
        {
            if (!BlockCardFields.IsValidListItem(id))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "invalid-earned-from",
                    $"'--earned-from' cannot contain an empty item: '{earnedFromRaw}'."));
            }
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RuleAuthor(
            filePath, title, role, scope, body, changeName, earnedFrom, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>rule compact</c>'s <see cref="CommandDispatcher.ParsedCommand.RuleCompact"/> (§7
    /// block F, register: "The system SHALL support compacting several rules into a family rule
    /// stating what they share"). <c>--id</c> (the family) and <c>--absorbs</c> (a comma-separated
    /// list of member rule ids, the same convention <c>--earned-from</c> already established) are
    /// both card ids resolved at execute time through <see cref="CardIdentityResolver"/> — no
    /// positional file-path argument, the same identity-addressing shape <c>rule promote</c> and
    /// <c>decision supersede</c> already use. <c>--change</c> is required — this block restricts
    /// compaction to one named change's own change-scoped rules (see <see cref="Cards.CardStore.
    /// CompactRules"/>'s own doc comment for why).
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRuleCompact(CommandDispatcher.CommandContext context)
    {
        string? familyId = null;
        string? absorbsRaw = null;
        string? changeName = null;
        string? roleText = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--id"] = value => familyId = value,
            ["--absorbs"] = value => absorbsRaw = value,
            ["--change"] = value => changeName = value,
            ["--role"] = value => roleText = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (familyId is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule compact' requires '--id <family-rule-id>'."));
        }

        if (string.IsNullOrEmpty(absorbsRaw))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "empty-absorb-set",
                "'rule compact' requires '--absorbs <rule-id>[,<rule-id>...]' — a family with no members is not a family."));
        }

        var absorbedIds = CardFileFormat.SplitFrontmatterList(absorbsRaw);
        foreach (var id in absorbedIds)
        {
            if (!BlockCardFields.IsValidListItem(id))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "invalid-absorbs", $"'--absorbs' cannot contain an empty item: '{absorbsRaw}'."));
            }
        }

        if (changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule compact' requires '--change <name>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule compact' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RuleCompact(
            familyId, absorbedIds, changeName, role, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>rule propose-compact</c>'s <see cref="CommandDispatcher.ParsedCommand.
    /// RuleProposeCompact"/> (§7 block G, 7.9). <c>--absorbs</c> reuses <see cref="ParseRuleCompact"/>'s
    /// comma-list convention and its two argv-decidable checks (non-empty, no empty item) — the
    /// backing set is still a list of rule ids even though nothing here will ever compact them.
    /// Unlike <see cref="ParseRuleCompact"/> there is no <c>--id</c> (no family card exists yet) and
    /// no <c>--change</c> (this proposes over the repository-scoped register, not one named change's
    /// own rules). The candidate text is read from stdin the same way <see cref="ParseRuleAuthor"/>'s
    /// body is — new proposed wording, not a path to something already on disk. <c>--proposal-file</c>
    /// (§7 remediation, blocker 1) is required for the same reason every card-creation verb requires
    /// a path: this call now creates one <c>question</c> card recording the proposal, and the caller
    /// names where, the same convention <see cref="ParseQuestionCreate"/> itself follows.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRuleProposeCompact(CommandDispatcher.CommandContext context)
    {
        string? absorbsRaw = null;
        string? roleText = null;
        string? proposalFilePath = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--absorbs"] = value => absorbsRaw = value,
            ["--role"] = value => roleText = value,
            ["--proposal-file"] = value => proposalFilePath = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (string.IsNullOrEmpty(absorbsRaw))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "empty-absorb-set",
                "'rule propose-compact' requires '--absorbs <rule-id>[,<rule-id>...]' — a family with no members is not a family."));
        }

        var backingIds = CardFileFormat.SplitFrontmatterList(absorbsRaw);
        foreach (var id in backingIds)
        {
            if (!BlockCardFields.IsValidListItem(id))
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "invalid-absorbs", $"'--absorbs' cannot contain an empty item: '{absorbsRaw}'."));
            }
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule propose-compact' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrEmpty(proposalFilePath))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'rule propose-compact' requires '--proposal-file <path>' — the file the recorded " +
                "proposal (a 'question' card owned by the Product Owner) is written to."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var candidateText = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RuleProposeCompact(
            candidateText, backingIds, proposalFilePath, role, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>rule promote-constitution</c>'s <see cref="CommandDispatcher.ParsedCommand.
    /// RulePromoteConstitution"/> (§7 block G, 7.12; extended in the reviewer-round-1 remediation).
    /// <c>--id</c> is left as raw text here — resolving it against the record is <see cref="
    /// CommandDispatcher.RunRulePromoteConstitution"/>'s job, since it needs the resolved card to
    /// append the durable refusal comment to, not merely to quote the id in a message. No stdin:
    /// there is no card body this verb ever writes to a new card — the comment it appends to the
    /// existing rule is built entirely from <c>--id</c>/<c>--role</c>/the clock, in the handler.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRulePromoteConstitution(CommandDispatcher.CommandContext context)
    {
        string? id = null;
        string? roleText = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--id"] = value => id = value,
            ["--role"] = value => roleText = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (id is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule promote-constitution' requires '--id <card-id>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule promote-constitution' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RulePromoteConstitution(
            id, role, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>rule create</c>'s <see cref="CommandDispatcher.ParsedCommand.RuleCreate"/> (§7
    /// block A). <c>--scope</c> is the one flag no other creation verb here needs — <c>rule</c> is
    /// the only kind <see cref="CardScopeRules"/> gives more than one legal scope, and its wire-
    /// format validity is checked here (argv-decidable, O-3); whether the specific pairing is
    /// actually legal is still <see cref="CardScopeRules.Validate"/>'s call at execute time, not
    /// this method's.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRuleCreate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? scopeText = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--scope"] = value => scopeText = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (scopeText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires '--scope <change|repository>'."));
        }

        if (!CardScopeWireFormat.TryParse(scopeText, out var scope))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-scope", $"unrecognised scope: '{scopeText}'. Recognised scopes: {CardScopeWireFormat.RecognisedValues}."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RuleCreate(
            filePath, title, role, scope, body, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>hazard</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c> (§7
    /// block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseHazard(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'hazard' requires a subcommand. Known subcommands: create, discharge."));
            case "create":
                context.Arguments.TryTake();
                return ParseHazardCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Hazard, "'hazard discharge'");
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'hazard' subcommand: '{subcommand}'. Known subcommands: create, discharge."));
        }
    }

    /// <summary>
    /// Builds <c>hazard create</c>'s <see cref="CommandDispatcher.ParsedCommand.HazardCreate"/> (§7
    /// block A, register: "Hazards carry a verification condition"). <b>The load-bearing refusal
    /// site for register's "the system refuses and states the condition it requires" scenario</b> —
    /// <c>--condition</c> and <c>--cadence</c> are both required, and a missing one is refused here,
    /// naming exactly what is missing, rather than deferred to a later layer that would have to
    /// reconstruct the same check. <b>Two distinct codes, not one (reviewer finding, block A
    /// review round 1):</b> a missing <c>--condition</c> is <c>hazard-missing-condition</c>, a
    /// missing <c>--cadence</c> is <c>hazard-missing-cadence</c> — an earlier version minted
    /// <c>hazard-missing-condition</c> for both, which let a machine caller correct the wrong flag
    /// on the first refusal and then be surprised by a second one. Two independently-triggerable
    /// conditions get two codes; this is not the near-synonymous-code collapse <see cref="
    /// CommandDispatcher.WrongCardKind"/>'s own doc comment describes, because that shape is one
    /// code covering the same fact stated two ways, not two different facts sharing one code.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseHazardCreate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'hazard create' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? condition = null;
        string? cadence = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--condition"] = value => condition = value,
            ["--cadence"] = value => cadence = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'hazard create' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'hazard create' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrWhiteSpace(condition))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "hazard-missing-condition",
                "'hazard create' requires '--condition <text>' — a hazard cannot be raised without a condition " +
                "under which it can be verified still to hold."));
        }

        if (string.IsNullOrWhiteSpace(cadence))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "hazard-missing-cadence",
                "'hazard create' requires '--cadence <text>' — a hazard cannot be raised without a cadence at " +
                "which its condition is re-checked."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.HazardCreate(
            filePath, title, role, body, condition, cadence, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>obligation</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c>
    /// (§7 block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseObligation(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'obligation' requires a subcommand. Known subcommands: create, discharge."));
            case "create":
                context.Arguments.TryTake();
                return ParseObligationCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Obligation, "'obligation discharge'");
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'obligation' subcommand: '{subcommand}'. Known subcommands: create, discharge."));
        }
    }

    /// <summary>
    /// Builds <c>obligation create</c>'s <see cref="CommandDispatcher.ParsedCommand.ObligationCreate"/>
    /// (§7 block A/C). Scope is always <see cref="CardScope.Change"/>, so <c>--change</c> is
    /// required. Unlike block A's shipped shape, this no longer goes through the shared
    /// <see cref="ParseCardCreate"/> — <c>--section</c> (register: "An obligation SHALL name the
    /// section expected to discharge it") is a required flag <see cref="ParseCardCreate"/>'s shape
    /// has no room for, the same reason <see cref="ParseHazardCreate"/> does not use it either.
    /// <b>Argv-decidable here, resolved against the record in <c>CommandDispatcher.
    /// RunObligationCreate</c>:</b> a missing or blank <c>--section</c> is refused at parse time
    /// (O-3), naming exactly what is missing; whether the id actually resolves to a real
    /// <c>section</c> card cannot be decided from argv alone and is checked afterward, through
    /// <c>CommandDispatcher.ResolveCardReference</c>.
    ///
    /// <para>
    /// <b>Renamed from <c>--owed-by</c> (§9 block D, carried item F).</b> <c>question create</c>'s
    /// own <c>--owed-by &lt;role&gt;</c> (the role that owes the answer) and this flag used to share
    /// one name over two different-typed values — a section id here, a role there — on a CLI an
    /// agent reads cold with no way to tell which shape a given <c>--owed-by</c> expects without
    /// already knowing the verb. This flag moves, not <c>question create</c>'s: <c>--section</c> is
    /// already this exact CLI's own name for "a section id" (<c>finding record --section</c>), so
    /// this reuses established vocabulary rather than the two-word domain phrase; <c>question
    /// create</c>'s <c>--owed-by &lt;role&gt;</c> is register's own phrase ("continues to surface to
    /// the role that owes its answer") and has no shorter established name to fall back to instead.
    /// The underlying model field (<see cref="RegisterCardFields.OwedBy"/>, the wire key
    /// <c>owed_by</c>, <see cref="Cli.CardCreateResult.OwedBy"/>) is unchanged — only the CLI flag
    /// spelling moves.
    /// </para>
    /// </summary>
    private static CommandDispatcher.ParseResult ParseObligationCreate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'obligation create' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? changeName = null;
        string? owedBy = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--change"] = value => changeName = value,
            ["--section"] = value => owedBy = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'obligation create' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'obligation create' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'obligation create' requires '--change <name>'."));
        }

        if (string.IsNullOrWhiteSpace(owedBy))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "obligation-missing-section",
                "'obligation create' requires '--section <section-id>' — an obligation cannot be raised " +
                "without naming the section expected to discharge it."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.ObligationCreate(
            filePath, title, role, body, changeName, owedBy, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>decision</c>'s only job is routing to a subcommand: <c>create</c>, <c>discharge</c> and
    /// <c>supersede</c> (§7 blocks A/C). Same peek-don't-take shape as <see cref="ParseIndex"/>,
    /// same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseDecision(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'decision' requires a subcommand. Known subcommands: create, discharge, supersede."));
            case "create":
                context.Arguments.TryTake();
                return ParseDecisionCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Decision, "'decision discharge'");
            case "supersede":
                context.Arguments.TryTake();
                return ParseDecisionSupersede(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'decision' subcommand: '{subcommand}'. Known subcommands: create, discharge, supersede."));
        }
    }

    /// <summary>
    /// Builds <c>decision create</c>'s <see cref="CommandDispatcher.ParsedCommand.DecisionCreate"/>
    /// (§7 block A). Scope is always <see cref="CardScope.Capability"/>, which
    /// <see cref="Cards.CardLayout.DirectoryFor"/> resolves without a change name — so, unlike
    /// <see cref="ParseObligationCreate"/>/<see cref="ParseSectionCreate"/>, this does not accept
    /// <c>--change</c> at all.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseDecisionCreate(CommandDispatcher.CommandContext context) =>
        ParseCardCreate(context, "'decision create'", requireChange: false, build:
            (filePath, title, role, body, _, workingDirectory, timestamp) =>
                new CommandDispatcher.ParsedCommand.DecisionCreate(filePath, title, role, body, workingDirectory, timestamp));

    /// <summary>
    /// <c>question</c> (§7 remediation, blocker 1, creation; §9 block D, <c>answer</c>/<c>defer</c>
    /// — the vocabulary a question's lifecycle needed once card-model's plain <c>"open"</c> literal
    /// stopped being the whole story).
    /// </summary>
    private static CommandDispatcher.ParseResult ParseQuestion(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'question' requires a subcommand. Known subcommands: create, answer, defer."));
            case "create":
                context.Arguments.TryTake();
                return ParseQuestionCreate(context);
            case "answer":
                context.Arguments.TryTake();
                return ParseQuestionAnswer(context);
            case "defer":
                context.Arguments.TryTake();
                return ParseQuestionDefer(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'question' subcommand: '{subcommand}'. Known subcommands: create, answer, defer."));
        }
    }

    /// <summary>
    /// Builds <c>question create</c>'s <see cref="CommandDispatcher.ParsedCommand.QuestionCreate"/>.
    /// Scope is always <see cref="CardScope.Repository"/>, the same reason <see cref="
    /// ParseDecisionCreate"/> does not accept <c>--change</c> either. <b>No longer routed through
    /// the shared <see cref="ParseCardCreate"/></b> (§7 second remediation) — that helper's shape
    /// has room for exactly one role, <c>--role</c>, and assumes it is also the card's owner, which
    /// is correct for the four register kinds and <c>section</c> but wrong for a question: card-model
    /// makes <c>owner</c> the routing mechanism ("the single role whose turn it is to act"), and for
    /// a question that is the role who <em>owes the answer</em>, not the role who raised it.
    /// <c>--owed-by &lt;role&gt;</c> (required, own parsing here — the same shape <see cref="
    /// ParseObligationCreate"/> already gives its own differently-typed <c>--owed-by</c>) names the
    /// owner explicitly; <c>--role</c> keeps meaning the acting role everywhere, unchanged.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseQuestionCreate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question create' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? owedByText = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--owed-by"] = value => owedByText = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question create' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question create' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var actingRole))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrEmpty(owedByText))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'question create' requires '--owed-by <role>' — the role that owes the answer, which " +
                "becomes the card's owner. It is not guessed from '--role', because the raiser and the " +
                "answerer are usually different roles."));
        }

        if (!CardOwnerWireFormat.TryParse(owedByText, out var owedByRole))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised '--owed-by' role: '{owedByText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.QuestionCreate(
            filePath, title, actingRole, owedByRole, body, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>question answer</c>'s <see cref="CommandDispatcher.ParsedCommand.QuestionAnswer"/>
    /// (§9 block D, process-enforcement: "An answer must be written down"). One positional token —
    /// the question card's own file path, the same addressing <see cref="ParseBlockTransition"/>
    /// already uses for an existing card, since a question (unlike a block or a nit) has no
    /// identity-resolved id surface of its own yet. <c>--decision &lt;id&gt;</c> names the
    /// <c>decision</c> card recording the answer; the stdin body is the inline answer for the
    /// trivial case. <b>Naming neither is refused here, at parse (Architect ruling, §9 block D) —
    /// see <see cref="Cards.CardQuestionAnswerOutcome"/>'s own doc comment for why that refusal is
    /// argv-decidable and therefore never card-addressed</b>, the same "argv-decidable is decided
    /// here" discipline <see cref="ParseObligationCreate"/>'s missing-<c>--section</c> check already
    /// follows. Stdin is still required redirected even when <c>--decision</c> is given (the same
    /// "always read, sometimes empty" shape <see cref="ParseNitDisposition"/> already has for
    /// <c>decline</c>) — an empty inline body alongside a named decision is not a contradiction, it
    /// is simply an answer with nothing extra to say inline.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseQuestionAnswer(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question answer' requires a card file path."));
        }

        string? roleText = null;
        string? decisionId = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--decision"] = value => decisionId = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question answer' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var inlineAnswer = StdinBodyReader.ReadBody(stdin!);

        if (string.IsNullOrEmpty(decisionId) && string.IsNullOrWhiteSpace(inlineAnswer))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "question-answer-missing-answer",
                "'question answer' requires either '--decision <decision-id>' naming the decision card that " +
                "records the answer, or a non-empty inline answer on stdin — a question cannot be marked " +
                "answered with neither."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.QuestionAnswer(
            filePath, role, string.IsNullOrEmpty(decisionId) ? null : decisionId, string.IsNullOrWhiteSpace(inlineAnswer) ? null : inlineAnswer,
            changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>question defer</c>'s <see cref="CommandDispatcher.ParsedCommand.QuestionDefer"/>
    /// (§9 block D — the question status vocabulary entire, including <c>deferred</c>). Same
    /// file-path addressing as <see cref="ParseQuestionAnswer"/>. <c>--target &lt;text&gt;</c>
    /// (required, argv-decidable — a missing one is refused here) names the later section or change
    /// this question is deferred to, as free text — see <see cref="Cards.QuestionCardFields.
    /// DeferredTarget"/>'s own doc comment for why it is never resolved through <see cref="Cards.
    /// CardIdentityResolver"/>. No stdin: deferring links to a target, it does not write new prose,
    /// the same reason <see cref="ParseDecisionSupersede"/> never reads stdin either.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseQuestionDefer(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question defer' requires a card file path."));
        }

        string? roleText = null;
        string? target = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--target"] = value => target = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'question defer' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'question defer' requires '--target <section-or-change>' — the later section or change this " +
                "question is deferred to."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.QuestionDefer(
            filePath, role, target, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>decision supersede</c>'s <see cref="CommandDispatcher.ParsedCommand.DecisionSupersede"/>
    /// (§7 block C, register: "A decision MAY name the decision it supersedes and the decision that
    /// supersedes it"). One positional token — the superseding decision's own card <b>id</b>, not a
    /// file path (block B's resolver is what makes this addressable by identity at all) — and
    /// <c>--supersedes &lt;id&gt;</c> (required) plus <c>--role</c> (required). No stdin body: this
    /// verb links two already-existing decisions, it does not write new prose, the same reason
    /// <see cref="ParseRegisterDischarge"/> never reads stdin either.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseDecisionSupersede(CommandDispatcher.CommandContext context)
    {
        var supersedingId = context.Arguments.TryTake();
        if (supersedingId is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'decision supersede' requires the superseding decision's card id."));
        }

        string? roleText = null;
        string? supersededId = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--supersedes"] = value => supersededId = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'decision supersede' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrWhiteSpace(supersededId))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'decision supersede' requires '--supersedes <decision-id>'."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.DecisionSupersede(
            supersedingId, supersededId, role, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>nit</c>'s only job is routing to a subcommand: <c>raise</c> and <c>disposition</c> (§8
    /// block B). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseNit(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'nit' requires a subcommand. Known subcommands: raise, disposition."));
            case "raise":
                context.Arguments.TryTake();
                return ParseNitRaise(context);
            case "disposition":
                context.Arguments.TryTake();
                return ParseNitDisposition(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'nit' subcommand: '{subcommand}'. Known subcommands: raise, disposition."));
        }
    }

    /// <summary>
    /// Builds <c>nit raise</c>'s <see cref="CommandDispatcher.ParsedCommand.NitRaise"/> (§8 block B,
    /// review-certification: "A nit SHALL be raised as an addressed comment, not as a card"). Named
    /// by <c>--id</c> — the block card it is raised against — the same identity-addressing
    /// convention <c>block approve</c> already established (§7's settled ruling). <c>--site</c> is
    /// repeatable (Architect ruling, §8 block B brief item 2: "record sites now … even though
    /// nothing in this block reads them back"); <see cref="ConsumeKnownFlags"/>'s own loop already
    /// supports a flag appearing more than once, so no second flag-parsing shape is needed for it.
    /// Body on redirected stdin per ADR-0001/D1 — the nit's own text, addressed to the architect.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseNitRaise(CommandDispatcher.CommandContext context)
    {
        string? id = null;
        string? roleText = null;
        string? changeName = null;
        var required = false;
        var sites = new List<string>();

        // One flag loop, not two (an earlier shape split '--change' into a second
        // ConsumeKnownFlags call — wrong, because that call's own loop stops at the first token
        // its own map does not recognise, so a '--change' placed before '--required'/'--site' in
        // argv would strand them unconsumed for the funnel's unrecognised-argument refusal to
        // trip on, even though they are perfectly legal flags this verb accepts).
        var flagRefusal = ConsumeKnownFlags(
            context,
            new Dictionary<string, Action<string>>(StringComparer.Ordinal)
            {
                ["--id"] = value => id = value,
                ["--role"] = value => roleText = value,
                ["--site"] = value => sites.Add(value),
                ["--change"] = value => changeName = value,
            },
            new Dictionary<string, Action>(StringComparer.Ordinal)
            {
                ["--required"] = () => required = true,
            });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (id is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'nit raise' requires '--id <block-card-id>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'nit raise' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.NitRaise(
            id, role, required, sites, body, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>nit disposition</c>'s <see cref="CommandDispatcher.ParsedCommand.NitDisposition"/>
    /// (§8 block B, review-certification: "Nits carry a disposition"). Named by <c>--id</c> — the
    /// nit's own id, resolved through <see cref="Cards.NitResolver"/> at execute time, not
    /// <see cref="Cards.CardIdentityResolver"/> (a nit is a comment, not a card — see that type's
    /// own doc comment). <c>--raise &lt;path&gt;</c>/<c>--title &lt;text&gt;</c> are required only
    /// for <c>defer</c>/<c>decline</c> (argv-decidable: <c>--disposition</c>'s own value settles
    /// which, checked here rather than left to execute) — the raised card's own body is the same
    /// stdin body every disposition reads (review-certification: "load-bearing for <c>decline</c>").
    /// Role <em>permission</em> (architect-only) is left to the execute phase, the same split
    /// <c>block approve</c>'s own role check uses.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseNitDisposition(CommandDispatcher.CommandContext context)
    {
        string? nitId = null;
        string? roleText = null;
        string? dispositionText = null;
        string? raiseFilePath = null;
        string? raiseTitle = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--id"] = value => nitId = value,
            ["--role"] = value => roleText = value,
            ["--disposition"] = value => dispositionText = value,
            ["--raise"] = value => raiseFilePath = value,
            ["--title"] = value => raiseTitle = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (nitId is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'nit disposition' requires '--id <nit-id>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'nit disposition' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (dispositionText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'nit disposition' requires '--disposition fix-before-land|defer|decline'."));
        }

        if (!NitDispositionWireFormat.TryParse(dispositionText, out var disposition))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-disposition",
                $"unrecognised disposition: '{dispositionText}'. Recognised dispositions: {NitDispositionWireFormat.RecognisedValues}."));
        }

        var raiseKind = disposition == NitDisposition.Defer ? CardKind.Obligation
            : disposition == NitDisposition.Decline ? CardKind.Decision
            : (CardKind?)null;

        NitDispositionRaiseRequest? raiseRequest = null;
        if (raiseKind is not null)
        {
            if (raiseFilePath is null)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-argument",
                    $"'nit disposition --disposition {dispositionText}' requires '--raise <card-file-path>'."));
            }

            if (raiseTitle is null)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-argument",
                    $"'nit disposition --disposition {dispositionText}' requires '--title <text>'."));
            }
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        // The raise request's own construction happens here, once (never repeated per disposition
        // case), even though raiseKind/raiseFilePath/raiseTitle are only ever non-null together
        // (checked above) — the constructor's own kind restriction is a second, independent
        // statement of the same invariant, not the only one, the same "verify rather than merely
        // rely on the one call site" discipline FindingBlindSpotRaiseRequest's own doc comment
        // describes.
        if (raiseKind is not null)
        {
            raiseRequest = new NitDispositionRaiseRequest(raiseKind, raiseFilePath!, raiseTitle!, body);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.NitDisposition(
            nitId, role, disposition, body, raiseRequest, changeName, context.WorkingDirectory, context.Clock()));
    }
}
