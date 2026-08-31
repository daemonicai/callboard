using System.Globalization;
using System.Linq;
using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Serialises a <see cref="CardFile"/> back to the ADR-0003 text format
/// <see cref="CardFileParser"/> reads — hand-rolled for the same reason (see that type's doc
/// comment for the AOT verdict). Frontmatter fields are written in a fixed order so the format
/// is diffable per card: a change to one field is one line's diff, not a shuffle.
/// </summary>
internal static class CardFileWriter
{
    internal static string Serialize(CardFile card)
    {
        var builder = new StringBuilder();
        var frontmatter = card.Frontmatter;

        builder.Append(CardFileFormat.FrontmatterFence).Append('\n');
        builder.Append("id: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Id)).Append('\n');
        builder.Append("kind: ").Append(frontmatter.Kind.ToWireString()).Append('\n');
        builder.Append("title: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Title)).Append('\n');
        builder.Append("status: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Status)).Append('\n');
        builder.Append("owner: ").Append(frontmatter.Owner.ToWireString()).Append('\n');
        builder.Append("scope: ").Append(frontmatter.Scope.ToWireString()).Append('\n');
        builder.Append("section: ").Append(CardFileFormat.EscapeFrontmatterValue(frontmatter.Section)).Append('\n');
        builder.Append("created: ").Append(FormatTimestamp(frontmatter.Created)).Append('\n');
        builder.Append("updated: ").Append(FormatTimestamp(frontmatter.Updated)).Append('\n');

        // §5's five block-only fields: emitted, in this fixed order, only for a block card, and
        // only the ones actually recorded — the same "present only when set" convention
        // BuildHeaderFields below already applies to a comment's optional reply-to/to/resolves, not
        // the "always present, empty when unset" convention section above uses. A freshly created
        // block card with none of the five set round-trips to exactly the same nine-field shape as
        // before this field existed, rather than gaining five blank lines. A card of any other kind
        // never reaches here with non-empty BlockFields (CardFileParser only ever populates it for
        // kind block), so this block is silently a no-op for every other kind rather than needing
        // its own guard.
        var isBlockCard = frontmatter.Kind.Match(
            onBlock: static () => true,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        if (isBlockCard)
        {
            var blockFields = card.BlockFields;

            if (blockFields.Base is { } baseCommit)
            {
                builder.Append("base: ").Append(CardFileFormat.EscapeFrontmatterValue(baseCommit)).Append('\n');
            }

            if (blockFields.ReviewedState is { } reviewedState)
            {
                builder.Append("reviewed_state: ").Append(CardFileFormat.EscapeFrontmatterValue(reviewedState)).Append('\n');
            }

            if (blockFields.Tasks.Length > 0)
            {
                builder.Append("tasks: ").Append(CardFileFormat.JoinFrontmatterList(blockFields.Tasks)).Append('\n');
            }

            if (blockFields.GateResults.Length > 0)
            {
                var gateItems = blockFields.GateResults
                    .Select(static result =>
                        $"{result.Label}={result.ExitCode.ToString(CultureInfo.InvariantCulture)}={result.Round.ToString(CultureInfo.InvariantCulture)}")
                    .ToList();
                builder.Append("gate_results: ").Append(CardFileFormat.JoinFrontmatterList(gateItems)).Append('\n');
            }

            if (blockFields.Round is { } round)
            {
                builder.Append("round: ").Append(round.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            if (blockFields.BlockedBy.Length > 0)
            {
                builder.Append("blocked_by: ").Append(CardFileFormat.JoinFrontmatterList(blockFields.BlockedBy)).Append('\n');
            }

            if (blockFields.FindingKey is { } findingKey)
            {
                builder.Append("finding_key: ").Append(CardFileFormat.EscapeFrontmatterValue(findingKey)).Append('\n');
            }
        }

        // §5 block E's three section-only scalar fields — same "present only when set" convention
        // as the block fields above, and the same guarantee that a card of any other kind never
        // reaches here with non-empty SectionFields (CardFileParser only ever populates it for kind
        // section).
        var isSectionCard = frontmatter.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => true);

        if (isSectionCard)
        {
            var sectionFields = card.SectionFields;

            if (sectionFields.Base is { } baseCommit)
            {
                builder.Append("base: ").Append(CardFileFormat.EscapeFrontmatterValue(baseCommit)).Append('\n');
            }

            if (sectionFields.ClosedBy is { } closedBy)
            {
                builder.Append("closed_by: ").Append(closedBy.ToWireString()).Append('\n');
            }

            if (sectionFields.ClosedAt is { } closedAt)
            {
                builder.Append("closed_at: ").Append(FormatTimestamp(closedAt)).Append('\n');
            }
        }

        // §9 block D's seven question-only fields — same "present only when set" convention as the
        // block/section fields above, and the same guarantee that a card of any other kind never
        // reaches here with non-default QuestionFields (CardFileParser only ever populates it for
        // kind question).
        var isQuestionCard = frontmatter.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => true,
            onFinding: static () => false,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        if (isQuestionCard)
        {
            var questionFields = card.QuestionFields;

            if (questionFields.AnsweredBy is { } answeredBy)
            {
                builder.Append("answered_by: ").Append(answeredBy.ToWireString()).Append('\n');
            }

            if (questionFields.AnsweredAt is { } answeredAt)
            {
                builder.Append("answered_at: ").Append(FormatTimestamp(answeredAt)).Append('\n');
            }

            if (questionFields.AnswerDecisionId is { } answerDecisionId)
            {
                builder.Append("answer_decision: ").Append(CardFileFormat.EscapeFrontmatterValue(answerDecisionId)).Append('\n');
            }

            if (questionFields.AnswerInline is { } answerInline)
            {
                builder.Append("answer_inline: ").Append(CardFileFormat.EscapeFrontmatterValue(answerInline)).Append('\n');
            }

            if (questionFields.DeferredBy is { } deferredBy)
            {
                builder.Append("deferred_by: ").Append(deferredBy.ToWireString()).Append('\n');
            }

            if (questionFields.DeferredAt is { } deferredAt)
            {
                builder.Append("deferred_at: ").Append(FormatTimestamp(deferredAt)).Append('\n');
            }

            if (questionFields.DeferredTarget is { } deferredTarget)
            {
                builder.Append("deferred_target: ").Append(CardFileFormat.EscapeFrontmatterValue(deferredTarget)).Append('\n');
            }
        }

        // §6 block A's four finding-only fields — same "present only when set" convention as the
        // block/section fields above, and the same guarantee that a card of any other kind never
        // reaches here with non-default FindingFields (CardFileParser only ever populates it for
        // kind finding). Extent's own default (FindingExtent.BlockScope) writes nothing at all —
        // an undeclared extent and a wire-absent extent are the same state, by design (see
        // FindingCardFields' own doc comment). BlindSpot is always emitted: it can never be the
        // "not yet recorded" state the other optional fields represent by omission, because that
        // state is not representable on FindingCardFields.BlindSpot in the first place.
        var isFindingCard = frontmatter.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => true,
            onObligation: static () => false,
            onRule: static () => false,
            onHazard: static () => false,
            onDecision: static () => false,
            onSection: static () => false);

        if (isFindingCard)
        {
            var findingFields = card.FindingFields;

            if (findingFields.Instrument is { } instrument)
            {
                builder.Append("instrument: ").Append(CardFileFormat.EscapeFrontmatterValue(instrument)).Append('\n');
            }

            var (extentForm, extentValue) = findingFields.Extent.Match(
                onInstrument: static command => ("instrument", CardFileFormat.EscapeFrontmatterValue(command)),
                onExplicit: static items => ("explicit", CardFileFormat.JoinFrontmatterList(items)),
                onBlockScope: static () => ((string?)null, (string?)null));

            if (extentForm is { } form)
            {
                builder.Append("extent: ").Append(form).Append('\n');
                builder.Append("extent_value: ").Append(extentValue).Append('\n');
            }

            if (findingFields.VerifiedAt is { } verifiedAt)
            {
                builder.Append("verified_at: ").Append(CardFileFormat.EscapeFrontmatterValue(verifiedAt)).Append('\n');
            }

            var (blindSpotForm, blindSpotCardId) = findingFields.BlindSpot.Match(
                onNone: static () => ("none", (string?)null),
                onRaisedAs: static cardId => ("raised-as", (string?)cardId));

            builder.Append("blind_spot: ").Append(blindSpotForm).Append('\n');
            if (blindSpotCardId is { } cardId)
            {
                builder.Append("blind_spot_card: ").Append(CardFileFormat.EscapeFrontmatterValue(cardId)).Append('\n');
            }

            // §6 block C's two additions, same "present only when set" convention. ExtentFingerprint
            // is null whenever there is nothing to fingerprint (Instrument/BlockScope extent) or
            // nothing was ever recorded (a card written before this field existed) — either way,
            // omitting the line here is exactly the "no fingerprint recorded" state
            // FindingStalenessEvaluator reads back as NotMeasurable, not Current.
            if (findingFields.ExtentFingerprint is { } fingerprint)
            {
                var fingerprintItems = fingerprint.Files
                    .Select(static file => $"{file.RelativePath}={file.ContentHash ?? "absent"}")
                    .ToList();
                builder.Append("extent_fingerprint: ").Append(CardFileFormat.JoinFrontmatterList(fingerprintItems)).Append('\n');
            }

            // Disposition's default (Measured) writes nothing at all — the same "undeclared and
            // default are the same wire state" convention Extent's own BlockScope case uses.
            var dispositionForm = findingFields.Disposition.Match(
                onMeasured: static () => (string?)null,
                onArguedClean: static () => "argued-clean");
            if (dispositionForm is { } dispositionText)
            {
                builder.Append("disposition: ").Append(dispositionText).Append('\n');
            }
        }

        // §7 block A's four register-only fields — same "present only when set" convention as the
        // block/section/finding fields above, and the same guarantee that a card of any other kind
        // never reaches here with non-default RegisterFields (CardFileParser only ever populates it
        // for the four register kinds).
        var isRegisterCard = frontmatter.Kind.Match(
            onBlock: static () => false,
            onQuestion: static () => false,
            onFinding: static () => false,
            onObligation: static () => true,
            onRule: static () => true,
            onHazard: static () => true,
            onDecision: static () => true,
            onSection: static () => false);

        if (isRegisterCard)
        {
            var registerFields = card.RegisterFields;

            // §7 block C remediation: every key below is a RegisterCardFieldKeys constant, the
            // one declaration CardFileParser's known-key set is also built from — see that type's
            // own doc comment for the defect this closes (a writer-known, parser-unknown key was
            // filed as unknown and re-emitted alongside the known line on every parse-then-write
            // cycle, duplicating it without bound).
            if (registerFields.Condition is { } condition)
            {
                builder.Append(RegisterCardFieldKeys.Condition).Append(": ").Append(CardFileFormat.EscapeFrontmatterValue(condition)).Append('\n');
            }

            if (registerFields.Cadence is { } cadence)
            {
                builder.Append(RegisterCardFieldKeys.Cadence).Append(": ").Append(CardFileFormat.EscapeFrontmatterValue(cadence)).Append('\n');
            }

            if (registerFields.OwedBy is { } owedBy)
            {
                builder.Append(RegisterCardFieldKeys.OwedBy).Append(": ").Append(CardFileFormat.EscapeFrontmatterValue(owedBy)).Append('\n');
            }

            if (registerFields.Supersedes is { } supersedes)
            {
                builder.Append(RegisterCardFieldKeys.Supersedes).Append(": ").Append(CardFileFormat.EscapeFrontmatterValue(supersedes)).Append('\n');
            }

            if (registerFields.SupersededBy is { } supersededBy)
            {
                builder.Append(RegisterCardFieldKeys.SupersededBy).Append(": ").Append(CardFileFormat.EscapeFrontmatterValue(supersededBy)).Append('\n');
            }

            if (registerFields.DischargedBy is { } dischargedBy)
            {
                builder.Append(RegisterCardFieldKeys.DischargedBy).Append(": ").Append(dischargedBy.ToWireString()).Append('\n');
            }

            if (registerFields.DischargedAt is { } dischargedAt)
            {
                builder.Append(RegisterCardFieldKeys.DischargedAt).Append(": ").Append(FormatTimestamp(dischargedAt)).Append('\n');
            }

            if (registerFields.EarnedFrom.Length > 0)
            {
                builder.Append(RegisterCardFieldKeys.EarnedFrom).Append(": ").Append(CardFileFormat.JoinFrontmatterList(registerFields.EarnedFrom)).Append('\n');
            }

            if (registerFields.Absorbs.Length > 0)
            {
                builder.Append(RegisterCardFieldKeys.Absorbs).Append(": ").Append(CardFileFormat.JoinFrontmatterList(registerFields.Absorbs)).Append('\n');
            }

            if (registerFields.DeclinedReason is { } declinedReason)
            {
                builder.Append(RegisterCardFieldKeys.DeclinedReason).Append(": ").Append(CardFileFormat.EscapeFrontmatterValue(declinedReason)).Append('\n');
            }
        }

        // Unknown fields (a §5/§6 field this build does not model, or a hand-added line) are
        // re-emitted after the known ones rather than interleaved back into their original
        // position — the parser records only the value at each known key, not a full original
        // line ordering, so exact interleaving cannot be reconstructed. What matters is that
        // nothing is lost: the raw key and the raw (already-escaped) value survive verbatim.
        foreach (var (key, rawValue) in card.UnknownFrontmatterFields)
        {
            builder.Append(key).Append(": ").Append(rawValue).Append('\n');
        }

        builder.Append(CardFileFormat.FrontmatterFence).Append('\n');

        AppendContent(builder, card.Body);

        // Handovers before comments — a fixed, deterministic layout (like the unknown-frontmatter-
        // fields convention above), not a claim about the physical order handovers and comments
        // actually happened in relative to each other. Each sequence's own internal order (oldest
        // first) is what the append-only guarantee is actually about, and that survives exactly:
        // CardStore only ever appends to one list or the other under the card's lock, never
        // reorders either.
        foreach (var handover in card.Handovers)
        {
            AppendBlock(builder, CardFileFormat.HandoverOpenLine, BuildHandoverFields(handover));
        }

        // Transitions after handovers, before comments — the same fixed, deterministic layout
        // convention as handovers-before-comments above; each sequence's own internal order
        // (oldest first) is what the append-only guarantee is actually about.
        foreach (var transition in card.Transitions)
        {
            AppendBlock(builder, CardFileFormat.TransitionOpenLine, BuildTransitionFields(transition));
        }

        // Verdicts after transitions, before comments — the same fixed, deterministic layout
        // convention as handovers-before-transitions above; each sequence's own internal order
        // (oldest first) is what the append-only guarantee is actually about. A section may
        // accumulate more than one verdict across supervisor rounds (work-lifecycle §3c: request
        // changes, remediate, re-review), so this is its own append-only sequence for the same
        // reason Transitions is not folded into a scalar.
        foreach (var verdict in card.SectionFields.Verdicts)
        {
            AppendBlock(builder, CardFileFormat.VerdictOpenLine, BuildVerdictFields(verdict));
        }

        // Authorisations after verdicts, before claims — same fixed, deterministic layout
        // convention (§8a block C, work-lifecycle: "Remediation beyond the second round requires
        // recorded authorisation"). A section may accumulate more than one, for the same reason
        // Verdicts is not folded into a scalar.
        foreach (var authorisation in card.SectionFields.Authorisations)
        {
            AppendBlock(builder, CardFileFormat.AuthorisationOpenLine, BuildAuthorisationFields(authorisation));
        }

        // Claims after verdicts/authorisations, then limits, before comments — the same fixed,
        // deterministic layout convention as every other append-only sequence above (§8 block A,
        // review-certification: "Certification enumerates its claims"). Mutually exclusive in
        // practice with Verdicts/Authorisations (a card is either a block or a section, never
        // both), so the ordering between the two never actually interleaves on one card — this is
        // simply where the fixed convention places them.
        foreach (var claim in card.Claims)
        {
            AppendBlock(builder, CardFileFormat.ClaimOpenLine, BuildClaimFields(claim));
        }

        foreach (var limit in card.Limits)
        {
            AppendBlock(builder, CardFileFormat.LimitOpenLine, BuildLimitFields(limit));
        }

        // Refusals after claims/limits, before comments — the same fixed, deterministic layout
        // convention as every other append-only sequence above (process-enforcement: "A refusal
        // SHALL be recorded against the card with the acting role and the time", §9 block A). Not
        // limited to a block or section card the way transitions/verdicts/authorisations are — any
        // kind of card can be the target of a refused attempt.
        foreach (var refusal in card.Refusals)
        {
            AppendBlock(builder, CardFileFormat.RefusalOpenLine, BuildRefusalFields(refusal));
        }

        // §14.4: the comment header moved onto the §14.1 delimited-block shape — the same
        // AppendBlock every other append-only family uses. The body and CommentFooter below are
        // unchanged: only the header carrying id/author/timestamp/etc. is now a block.
        foreach (var comment in card.Comments)
        {
            AppendBlock(builder, CardFileFormat.CommentOpenLine, BuildCommentFields(comment));

            AppendContent(builder, comment.Body);

            builder.Append(CardFileFormat.CommentFooter).Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendContent(StringBuilder builder, string content)
    {
        if (content.Length == 0)
        {
            return;
        }

        foreach (var line in content.Split('\n'))
        {
            builder.Append(CardFileFormat.EscapeContentLine(line)).Append('\n');
        }
    }

    /// <summary>
    /// §14.1: writes one delimited block for any of the eight append-only families — the open
    /// line, one already-escaped <c>key: value</c> line per field, and the close line
    /// (<see cref="CardFileFormat.BlockCloseLine"/>) — so every family shares exactly this shape
    /// and none can drift from it independently.
    /// </summary>
    private static void AppendBlock(StringBuilder builder, string openLine, IEnumerable<(string Key, string Value)> fields)
    {
        builder.Append(openLine).Append('\n');
        foreach (var (key, value) in fields)
        {
            builder.Append(key).Append(": ").Append(value).Append('\n');
        }

        builder.Append(CardFileFormat.BlockCloseLine).Append('\n');
    }

    /// <summary>
    /// §14.4: the comment header's fields, in the same order the pre-§14.4 single-line header
    /// emitted them, now yielded as <c>(Key, Value)</c> pairs for <see cref="AppendBlock"/> instead
    /// of built into one space-joined token string. <c>id</c>/<c>reply-to</c>/<c>resolves</c> — the
    /// header's only free-text fields — move onto <see cref="CardFileFormat.EscapeCardBlockValue"/>,
    /// the same escaper every other family's free-text field already uses (§14.2/14.3), superseding
    /// the header's former dedicated space-escaping pair.
    /// </summary>
    private static IEnumerable<(string Key, string Value)> BuildCommentFields(CardComment comment)
    {
        yield return ("id", CardFileFormat.EscapeCardBlockValue(comment.Id));
        yield return ("author", comment.Author.ToWireString());

        if (comment.ReplyTo is { } replyTo)
        {
            yield return ("reply-to", CardFileFormat.EscapeCardBlockValue(replyTo));
        }

        if (comment.To is { } to)
        {
            yield return ("to", to.ToWireString());
        }

        if (comment.Resolves is { } resolves)
        {
            yield return ("resolves", CardFileFormat.EscapeCardBlockValue(resolves));
        }

        yield return ("timestamp", FormatTimestamp(comment.Timestamp));

        if (comment.IsNit)
        {
            yield return (CardCommentNitFieldKeys.IsNit, "true");
        }

        if (comment.Required)
        {
            yield return (CardCommentNitFieldKeys.Required, "true");
        }

        if (comment.Sites.Count > 0)
        {
            yield return (CardCommentNitFieldKeys.Sites, CardFileFormat.JoinSiteList(comment.Sites));
        }

        if (comment.Disposition is { } disposition)
        {
            yield return (CardCommentNitFieldKeys.Disposition, disposition.ToWireString());
        }

        foreach (var (key, rawValue) in comment.UnknownHeaderFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildHandoverFields(CardHandover handover)
    {
        yield return ("by", handover.By.ToWireString());
        yield return ("to", handover.To.ToWireString());
        yield return ("timestamp", FormatTimestamp(handover.Timestamp));

        foreach (var (key, rawValue) in handover.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildTransitionFields(CardBlockTransitionEntry transition)
    {
        yield return ("by", transition.By.ToWireString());
        yield return ("name", transition.Name);
        yield return ("from", transition.From.ToWireString());
        yield return ("to", transition.To.ToWireString());
        yield return ("timestamp", FormatTimestamp(transition.Timestamp));

        foreach (var (key, rawValue) in transition.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildVerdictFields(SectionVerdictEntry verdict)
    {
        yield return ("by", verdict.By.ToWireString());
        yield return ("verdict", verdict.Verdict.ToWireString());
        yield return ("range-from", CardFileFormat.EscapeCardBlockValue(verdict.RangeFrom));
        yield return ("range-to", CardFileFormat.EscapeCardBlockValue(verdict.RangeTo));
        yield return ("timestamp", FormatTimestamp(verdict.Timestamp));

        foreach (var (key, rawValue) in verdict.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildAuthorisationFields(SectionAuthorisationEntry authorisation)
    {
        yield return ("by", authorisation.By.ToWireString());
        yield return ("reason", CardFileFormat.EscapeCardBlockValue(authorisation.Reason));
        yield return ("timestamp", FormatTimestamp(authorisation.Timestamp));

        foreach (var (key, rawValue) in authorisation.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildClaimFields(CardApprovalClaim claim)
    {
        yield return (CardApprovalFieldKeys.Id, CardFileFormat.EscapeCardBlockValue(claim.Id));
        yield return (CardApprovalFieldKeys.Round, claim.Round.ToString(CultureInfo.InvariantCulture));
        yield return (CardApprovalFieldKeys.Text, CardFileFormat.EscapeCardBlockValue(claim.Text));

        foreach (var (key, rawValue) in claim.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildLimitFields(CardApprovalLimit limit)
    {
        yield return (CardApprovalFieldKeys.Round, limit.Round.ToString(CultureInfo.InvariantCulture));
        yield return (CardApprovalFieldKeys.Text, CardFileFormat.EscapeCardBlockValue(limit.Text));

        foreach (var (key, rawValue) in limit.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildRefusalFields(CardRefusalEntry refusal)
    {
        yield return ("by", refusal.By.ToWireString());
        yield return ("rule", CardFileFormat.EscapeCardBlockValue(refusal.Rule));
        yield return ("remedy", CardFileFormat.EscapeCardBlockValue(refusal.Remedy));
        yield return ("timestamp", FormatTimestamp(refusal.Timestamp));

        foreach (var (key, rawValue) in refusal.UnknownFields)
        {
            yield return (key, rawValue);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
