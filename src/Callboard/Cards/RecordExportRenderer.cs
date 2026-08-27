using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Renders a set of cards, already in <see cref="RecordExportAssembler.ReadingOrderDescription"/>'s
/// order, as one readable Markdown document approximating the shape of the DEVLOG this change
/// itself was built through (§11 block C, record-retrieval: "The system SHALL render a section, or
/// a whole change, as a single readable document approximating the shape of the log it replaces").
///
/// <para>
/// <b>Every content class §11 block C's own enumeration named has a rendering here</b> — see that
/// block's DEVLOG post for the full list and its reasoning; in this type's terms: frontmatter and
/// body (the architect's brief and every narrative post alike live in <see cref="CardFile.Body"/>
/// or <see cref="CardFile.Comments"/>), the five kind-specific field groups (register rulings,
/// section verdicts, question answers, finding extents), the four append-only sequences
/// (handovers, transitions, claims/limits, refusals — including gate results, on
/// <see cref="BlockCardFields.GateResults"/>), and the complete comment thread (worker reports,
/// reviewer findings, nits and their dispositions, handoffs, in-thread questions).
/// </para>
///
/// <para>
/// <b>Never stamps the current wall-clock time, and never reads git.</b> Every value emitted here
/// comes from a card's own recorded fields — the same "reconstructed from the record, never from
/// git" discipline the block C brief states — which is what makes exporting the same record twice
/// byte-identical: nothing on this path varies with when the export command happened to run.
/// </para>
/// </summary>
internal static class RecordExportRenderer
{
    internal static string Render(string title, IReadOnlyList<(string FilePath, CardFile Card)> cardsInReadingOrder)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append("\n\n");

        foreach (var (_, card) in cardsInReadingOrder)
        {
            AppendCard(builder, card);
        }

        return builder.ToString();
    }

    private static void AppendCard(StringBuilder builder, CardFile card)
    {
        var frontmatter = card.Frontmatter;
        builder.Append("## ").Append(frontmatter.Kind.ToWireString()).Append(' ').Append(frontmatter.Id)
            .Append(" — ").Append(frontmatter.Title).Append("\n\n");
        builder.Append("- status: ").Append(frontmatter.Status).Append('\n');
        builder.Append("- owner: ").Append(frontmatter.Owner.ToWireString()).Append('\n');
        builder.Append("- scope: ").Append(frontmatter.Scope.ToWireString()).Append('\n');
        if (frontmatter.Section.Length > 0)
        {
            builder.Append("- section: ").Append(frontmatter.Section).Append('\n');
        }

        builder.Append("- created: ").Append(frontmatter.Created.ToString("O")).Append('\n');
        builder.Append("- updated: ").Append(frontmatter.Updated.ToString("O")).Append('\n');
        builder.Append('\n');

        if (card.Body.Length > 0)
        {
            builder.Append(card.Body).Append("\n\n");
        }

        AppendKindFields(builder, card);
        AppendSequences(builder, card);
        AppendThread(builder, card);
    }

    private static void AppendKindFields(StringBuilder builder, CardFile card) =>
        card.Frontmatter.Kind.Match<object?>(
            onBlock: () =>
            {
                AppendBlockFields(builder, card.BlockFields);
                return null;
            },
            onQuestion: () =>
            {
                AppendQuestionFields(builder, card.QuestionFields);
                return null;
            },
            onFinding: () =>
            {
                AppendFindingFields(builder, card.FindingFields);
                return null;
            },
            onObligation: () =>
            {
                AppendRegisterFields(builder, card.RegisterFields);
                return null;
            },
            onRule: () =>
            {
                AppendRegisterFields(builder, card.RegisterFields);
                return null;
            },
            onHazard: () =>
            {
                AppendRegisterFields(builder, card.RegisterFields);
                return null;
            },
            onDecision: () =>
            {
                AppendRegisterFields(builder, card.RegisterFields);
                return null;
            },
            onSection: () =>
            {
                AppendSectionFields(builder, card.SectionFields);
                return null;
            });

    private static void AppendBlockFields(StringBuilder builder, BlockCardFields fields)
    {
        var hasContent = fields.Base is not null || fields.ReviewedState is not null || fields.Tasks.Length > 0
            || fields.Round is not null || fields.BlockedBy.Length > 0 || fields.GateResults.Length > 0 || fields.FindingKey is not null;
        if (!hasContent)
        {
            return;
        }

        builder.Append("### block fields\n\n");
        if (fields.Base is not null)
        {
            builder.Append("- base: ").Append(fields.Base).Append('\n');
        }

        if (fields.ReviewedState is not null)
        {
            builder.Append("- reviewed state: ").Append(fields.ReviewedState).Append('\n');
        }

        if (fields.Tasks.Length > 0)
        {
            builder.Append("- tasks: ").Append(string.Join(", ", fields.Tasks)).Append('\n');
        }

        if (fields.Round is not null)
        {
            builder.Append("- round: ").Append(fields.Round.Value).Append('\n');
        }

        if (fields.BlockedBy.Length > 0)
        {
            builder.Append("- blocked by: ").Append(string.Join(", ", fields.BlockedBy)).Append('\n');
        }

        if (fields.FindingKey is not null)
        {
            builder.Append("- finding key: ").Append(fields.FindingKey).Append('\n');
        }

        foreach (var gate in fields.GateResults)
        {
            builder.Append("- gate ").Append(gate.Label).Append(": exit ").Append(gate.ExitCode)
                .Append(" (round ").Append(gate.Round).Append(")\n");
        }

        builder.Append('\n');
    }

    private static void AppendSectionFields(StringBuilder builder, SectionCardFields fields)
    {
        var hasContent = fields.Base is not null || fields.ClosedBy is not null || fields.ClosedAt is not null
            || fields.Verdicts.Length > 0 || fields.Authorisations.Length > 0;
        if (!hasContent)
        {
            return;
        }

        builder.Append("### section fields\n\n");
        if (fields.Base is not null)
        {
            builder.Append("- base: ").Append(fields.Base).Append('\n');
        }

        if (fields.ClosedBy is not null)
        {
            builder.Append("- closed by: ").Append(fields.ClosedBy.ToWireString())
                .Append(" at ").Append(fields.ClosedAt!.Value.ToString("O")).Append('\n');
        }

        builder.Append('\n');

        foreach (var verdict in fields.Verdicts)
        {
            builder.Append("- verdict [").Append(verdict.Verdict.ToWireString()).Append("] by ").Append(verdict.By.ToWireString())
                .Append(" over ").Append(verdict.RangeFrom).Append("..").Append(verdict.RangeTo)
                .Append(" at ").Append(verdict.Timestamp.ToString("O")).Append('\n');
        }

        foreach (var authorisation in fields.Authorisations)
        {
            builder.Append("- authorisation by ").Append(authorisation.By.ToWireString()).Append(": ")
                .Append(authorisation.Reason).Append(" at ").Append(authorisation.Timestamp.ToString("O")).Append('\n');
        }

        if (fields.Verdicts.Length > 0 || fields.Authorisations.Length > 0)
        {
            builder.Append('\n');
        }
    }

    private static void AppendFindingFields(StringBuilder builder, FindingCardFields fields)
    {
        builder.Append("### finding fields\n\n");
        if (fields.Instrument is not null)
        {
            builder.Append("- instrument: ").Append(fields.Instrument).Append('\n');
        }

        var extentText = fields.Extent.Match(
            onInstrument: static command => $"instrument: {command}",
            onExplicit: static items => $"explicit: {string.Join(", ", items)}",
            onBlockScope: static () => "block scope");
        builder.Append("- extent: ").Append(extentText).Append('\n');

        if (fields.VerifiedAt is not null)
        {
            builder.Append("- verified at: ").Append(fields.VerifiedAt).Append('\n');
        }

        var blindSpotText = fields.BlindSpot.Match(
            onNone: static () => "none",
            onRaisedAs: static cardId => $"raised as {cardId}");
        builder.Append("- blind spot: ").Append(blindSpotText).Append('\n');

        builder.Append("- disposition: ")
            .Append(fields.Disposition.Match(onMeasured: static () => "measured", onArguedClean: static () => "arguedClean"))
            .Append('\n');

        if (fields.ExtentFingerprint is not null)
        {
            builder.Append("- extent fingerprint:\n");
            foreach (var file in fields.ExtentFingerprint.Files)
            {
                builder.Append("  - ").Append(file.RelativePath).Append(": ").Append(file.ContentHash ?? "(absent)").Append('\n');
            }
        }

        builder.Append('\n');
    }

    private static void AppendRegisterFields(StringBuilder builder, RegisterCardFields fields)
    {
        var hasContent = fields.Condition is not null || fields.Cadence is not null || fields.DischargedBy is not null
            || fields.DischargedAt is not null || fields.OwedBy is not null || fields.DeclinedReason is not null
            || fields.Supersedes is not null || fields.SupersededBy is not null
            || fields.EarnedFrom.Length > 0 || fields.Absorbs.Length > 0;
        if (!hasContent)
        {
            return;
        }

        builder.Append("### register fields\n\n");
        if (fields.Condition is not null)
        {
            builder.Append("- condition: ").Append(fields.Condition).Append('\n');
        }

        if (fields.Cadence is not null)
        {
            builder.Append("- cadence: ").Append(fields.Cadence).Append('\n');
        }

        if (fields.OwedBy is not null)
        {
            builder.Append("- owed by: ").Append(fields.OwedBy).Append('\n');
        }

        if (fields.Supersedes is not null)
        {
            builder.Append("- supersedes: ").Append(fields.Supersedes).Append('\n');
        }

        if (fields.SupersededBy is not null)
        {
            builder.Append("- superseded by: ").Append(fields.SupersededBy).Append('\n');
        }

        if (fields.EarnedFrom.Length > 0)
        {
            builder.Append("- earned from: ").Append(string.Join(", ", fields.EarnedFrom)).Append('\n');
        }

        if (fields.Absorbs.Length > 0)
        {
            builder.Append("- absorbs: ").Append(string.Join(", ", fields.Absorbs)).Append('\n');
        }

        if (fields.DischargedBy is not null)
        {
            builder.Append("- discharged by: ").Append(fields.DischargedBy.ToWireString())
                .Append(" at ").Append(fields.DischargedAt!.Value.ToString("O")).Append('\n');
        }

        if (fields.DeclinedReason is not null)
        {
            builder.Append("- declined reason: ").Append(fields.DeclinedReason).Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendQuestionFields(StringBuilder builder, QuestionCardFields fields)
    {
        var hasContent = fields.AnsweredBy is not null || fields.DeferredBy is not null;
        if (!hasContent)
        {
            return;
        }

        builder.Append("### question fields\n\n");
        if (fields.AnsweredBy is not null)
        {
            builder.Append("- answered by: ").Append(fields.AnsweredBy.ToWireString())
                .Append(" at ").Append(fields.AnsweredAt!.Value.ToString("O")).Append('\n');
            if (fields.AnswerDecisionId is not null)
            {
                builder.Append("- answer (decision): ").Append(fields.AnswerDecisionId).Append('\n');
            }

            if (fields.AnswerInline is not null)
            {
                builder.Append("- answer (inline): ").Append(fields.AnswerInline).Append('\n');
            }
        }

        if (fields.DeferredBy is not null)
        {
            builder.Append("- deferred by: ").Append(fields.DeferredBy.ToWireString())
                .Append(" at ").Append(fields.DeferredAt!.Value.ToString("O")).Append('\n');
            if (fields.DeferredTarget is not null)
            {
                builder.Append("- deferred to: ").Append(fields.DeferredTarget).Append('\n');
            }
        }

        builder.Append('\n');
    }

    private static void AppendSequences(StringBuilder builder, CardFile card)
    {
        if (card.Handovers.Count > 0)
        {
            builder.Append("### handovers\n\n");
            foreach (var handover in card.Handovers)
            {
                builder.Append("- ").Append(handover.By.ToWireString()).Append(" → ").Append(handover.To.ToWireString())
                    .Append(" at ").Append(handover.Timestamp.ToString("O")).Append('\n');
            }

            builder.Append('\n');
        }

        if (card.Transitions.Count > 0)
        {
            builder.Append("### transitions\n\n");
            foreach (var transition in card.Transitions)
            {
                builder.Append("- ").Append(transition.By.ToWireString()).Append(' ').Append(transition.Name)
                    .Append(" (").Append(transition.From.ToWireString()).Append(" → ").Append(transition.To.ToWireString()).Append(')')
                    .Append(" at ").Append(transition.Timestamp.ToString("O")).Append('\n');
            }

            builder.Append('\n');
        }

        if (card.Claims.Count > 0 || card.Limits.Count > 0)
        {
            builder.Append("### certification\n\n");
            foreach (var claim in card.Claims)
            {
                builder.Append("- claim [round ").Append(claim.Round).Append("]: ").Append(claim.Text).Append('\n');
            }

            foreach (var limit in card.Limits)
            {
                builder.Append("- limit [round ").Append(limit.Round).Append("]: ").Append(limit.Text).Append('\n');
            }

            builder.Append('\n');
        }

        if (card.Refusals.Count > 0)
        {
            builder.Append("### refusals\n\n");
            foreach (var refusal in card.Refusals)
            {
                builder.Append("- ").Append(refusal.By.ToWireString()).Append(" refused: ").Append(refusal.Rule)
                    .Append(" — remedy: ").Append(refusal.Remedy).Append(" at ").Append(refusal.Timestamp.ToString("O")).Append('\n');
            }

            builder.Append('\n');
        }
    }

    private static void AppendThread(StringBuilder builder, CardFile card)
    {
        if (card.Comments.Count == 0)
        {
            return;
        }

        builder.Append("### thread\n\n");
        foreach (var comment in card.Comments)
        {
            builder.Append("**[").Append(comment.Author.ToWireString()).Append("]** ").Append(comment.Timestamp.ToString("O"));
            if (comment.To is not null)
            {
                builder.Append(" → @").Append(comment.To.ToWireString());
            }

            if (comment.ReplyTo is not null)
            {
                builder.Append(" (replies to ").Append(comment.ReplyTo).Append(')');
            }

            if (comment.Resolves is not null)
            {
                builder.Append(" (resolves ").Append(comment.Resolves).Append(')');
            }

            if (comment.IsNit)
            {
                builder.Append(" [NIT");
                if (comment.Required)
                {
                    builder.Append(", required");
                }

                if (comment.Sites.Count > 0)
                {
                    builder.Append(", sites: ").Append(string.Join(", ", comment.Sites));
                }

                builder.Append(']');
            }

            if (comment.Disposition is not null)
            {
                builder.Append(" [disposition: ").Append(comment.Disposition.ToWireString()).Append(']');
            }

            builder.Append('\n').Append(comment.Body).Append("\n\n");
        }
    }
}
