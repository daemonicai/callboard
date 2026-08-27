using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Renders a <see cref="BoardView"/> as one self-contained HTML document (§12 block B,
/// record-retrieval: "a local, read-only, human-readable view of the board ... SHALL require no
/// server, no authentication and no hosting"; binding ADR D5: one file, inline CSS, no build
/// step). Every byte the reader's browser needs — markup and styling alike — is in the single
/// string this returns; nothing here references an external stylesheet, font, script or image.
///
/// <para>
/// <b>Lanes by flow vocabulary, columns by that vocabulary's states</b> (Product Owner ruling,
/// §12 block B rework): <see cref="BoardView.Lanes"/> renders as a "Board" section (block,
/// section, question, finding, each a row of flow-state columns) and <see cref="BoardView.
/// RegisterLanes"/> renders as a visually distinct "Register" area below it (one row per register
/// kind, open against discharged) — register cards SHALL NOT occupy flow states, so they never
/// share a row with the flow lanes above.
/// </para>
///
/// <para>
/// <b>Blocked-on and owed-by render inline, on the card, inside its lane</b> — not in a separate
/// summary section a reader might never scroll to (Product Owner ruling: "must not be exiled to a
/// footer the eye never reaches while it is looking at the lane").
/// </para>
///
/// <para>
/// <b>Read-only by construction, not by convention</b> (record-retrieval's second scenario: "no
/// state changes"). The output contains no <c>&lt;form&gt;</c>, <c>&lt;script&gt;</c>,
/// <c>&lt;input&gt;</c> or <c>&lt;button&gt;</c> element, and issues no external request of any
/// kind — a <c>file://</c> page with no script and no form has no mechanism by which opening or
/// reading it could alter the record. The test suite asserts this on the rendered string itself,
/// not merely on this type's intent.
/// </para>
/// </summary>
internal static class BoardViewRenderer
{
    internal static string Render(BoardView view)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<title>callboard</title>\n<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");
        html.Append("<h1>callboard</h1>\n");

        html.Append("<section class=\"board\">\n<h2>Board</h2>\n");
        foreach (var lane in view.Lanes)
        {
            AppendLane(html, lane, view);
        }

        html.Append("</section>\n");

        html.Append("<section class=\"register\">\n<h2>Register</h2>\n");
        foreach (var lane in view.RegisterLanes)
        {
            AppendLane(html, lane, view);
        }

        html.Append("</section>\n");

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendLane(StringBuilder html, BoardViewLane lane, BoardView view)
    {
        html.Append("<div class=\"lane\">\n<h3>").Append(Escape(lane.Name)).Append("</h3>\n<div class=\"columns\">\n");

        foreach (var column in lane.Columns)
        {
            html.Append("<div class=\"column\">\n<h4>").Append(Escape(column.Name)).Append("</h4>\n");

            if (column.OwnerGroups.Count == 0)
            {
                html.Append("<p class=\"empty\">No cards.</p>\n");
            }

            foreach (var ownerGroup in column.OwnerGroups)
            {
                html.Append("<div class=\"owner-group\">\n<h5>").Append(Escape(ownerGroup.Owner.ToWireString())).Append("</h5>\n<ul>\n");
                foreach (var card in ownerGroup.Cards)
                {
                    AppendCard(html, card, view);
                }

                html.Append("</ul>\n</div>\n");
            }

            html.Append("</div>\n");
        }

        html.Append("</div>\n</div>\n");
    }

    private static void AppendCard(StringBuilder html, BoardViewCard card, BoardView view)
    {
        html.Append("<li class=\"card\">");
        html.Append("<span class=\"card-id\">").Append(Escape(card.Card.Frontmatter.Id)).Append("</span> ");
        html.Append(Escape(card.Card.Frontmatter.Title));

        if (view.BlockedById.TryGetValue(card.Card.Frontmatter.Id, out var blocked))
        {
            html.Append(blocked.Halted ? " <span class=\"badge halted\">halted</span>" : " <span class=\"badge\">blocked</span>");
            html.Append("<div class=\"blocked-on\">blocked on: ").Append(FormatBlockedByIds(view, blocked.BlockedByIds)).Append("</div>\n");

            if (blocked.Halted)
            {
                html.Append("<div class=\"halted-by\">halted by open question ");
                html.Append("<span class=\"card-id\">").Append(Escape(blocked.HaltedByQuestionId ?? string.Empty)).Append("</span> — ");
                html.Append(Escape(blocked.HaltedByQuestionTitle ?? string.Empty));
                html.Append("</div>\n");
            }
        }

        if (view.OpenQuestionOwesById.TryGetValue(card.Card.Frontmatter.Id, out var owesAnswer))
        {
            html.Append("<div class=\"owed-by\">owed by <span class=\"badge\">").Append(Escape(owesAnswer.ToWireString())).Append("</span></div>\n");
        }

        html.Append("</li>\n");
    }

    private static string FormatBlockedByIds(BoardView view, IReadOnlyList<string> blockedByIds)
    {
        if (blockedByIds.Count == 0)
        {
            return "(none recorded)";
        }

        var parts = blockedByIds.Select(id =>
        {
            var idText = Escape(id);
            if (view.SummaryById.TryGetValue(id, out var summary))
            {
                var closedSuffix = summary.Closed ? " (closed)" : string.Empty;
                return $"<span class=\"card-id\">{idText}</span> {Escape(summary.Title)}{closedSuffix}";
            }

            return $"<span class=\"card-id\">{idText}</span>";
        });

        return string.Join(", ", parts);
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private const string Css = """
        :root { color-scheme: light dark; }
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; margin: 2rem; line-height: 1.4; }
        h1 { margin-bottom: 0.25rem; }
        h2 { margin-top: 2.5rem; border-bottom: 1px solid #8888; padding-bottom: 0.25rem; }
        h3 { margin: 1.5rem 0 0.5rem; }
        h4 { margin-bottom: 0.25rem; }
        h5 { margin: 0.75rem 0 0.25rem; font-size: 0.85rem; opacity: 0.8; }
        .lane { margin-bottom: 1.5rem; }
        .columns { display: flex; flex-wrap: wrap; gap: 1.5rem; }
        .column { flex: 1 1 200px; min-width: 200px; border: 1px solid #8886; border-radius: 8px; padding: 0.75rem 1rem; }
        .register .column { background: #8888881a; }
        .owner-group ul { margin: 0; padding-left: 1.2rem; }
        .card { margin-bottom: 0.5rem; }
        .card-id { font-family: ui-monospace, Menlo, Consolas, monospace; font-weight: 600; }
        .badge { display: inline-block; font-size: 0.75rem; padding: 0.1rem 0.4rem; border-radius: 4px; background: #8883; margin-left: 0.25rem; }
        .badge.halted { background: #d9534f55; }
        .blocked-on, .halted-by, .owed-by { font-size: 0.85rem; opacity: 0.85; margin-left: 1rem; }
        .empty { opacity: 0.7; font-style: italic; }
        """;
}
