using System.Text.RegularExpressions;

namespace Callboard.Cards;

/// <summary>One live change's task completion, counted from its own <c>tasks.md</c> at request
/// time (working-context, §10 block C: "task completion counted from the task list itself" —
/// Architect ruling: <c>openspec/changes/&lt;change&gt;/tasks.md</c>, not the block cards'
/// referenced task ids, which are a different thing). <see cref="Ticked"/>/<see cref="Total"/> are
/// both <c>0</c> when <see cref="TasksFileFound"/> is <see langword="false"/> — a change with no
/// <c>tasks.md</c> says so explicitly rather than reporting zero, which would read as "no
/// tasks".</summary>
internal sealed record TasksMdCompletion(string ChangeName, bool TasksFileFound, int Ticked, int Total);

/// <summary>
/// Parses <c>tasks.md</c>'s own checkbox lines (<c>- [ ] N.M ...</c> / <c>- [x] N.M ...</c>) —
/// read, never written: <c>tasks.md</c> is the Architect's, and the hook boundary enforces that
/// (§10 block C brief). Counts every checkbox line in the file regardless of which <c>## N.</c>
/// section it falls under — the spec asks for the change's task completion as a whole, not a
/// per-section breakdown.
/// </summary>
internal static partial class TasksMdParser
{
    // Source-generated (not RegexOptions.Compiled, which emits IL at runtime — not NativeAOT-safe,
    // ADR-0002 / D2): the pattern is compiled ahead of time into ordinary generated C#.
    [GeneratedRegex(@"^\s*-\s\[([ xX])\]\s")]
    private static partial Regex CheckboxLine();

    /// <summary>
    /// <paramref name="repoRoot"/>-relative: reads <c>openspec/changes/&lt;changeName&gt;/tasks.md</c>
    /// — a tree <see cref="CardLayout"/> has no notion of, since <c>tasks.md</c> is not a card and
    /// never lives under <c>callboard/</c>. Returns <see cref="TasksMdCompletion.TasksFileFound"/>
    /// <see langword="false"/> when the file does not exist, rather than throwing or reporting an
    /// empty change: an absent <c>tasks.md</c> is a fact worth stating, not an error.
    /// </summary>
    internal static TasksMdCompletion CountCompletion(string repoRoot, string changeName)
    {
        var path = Path.Combine(repoRoot, "openspec", "changes", changeName, "tasks.md");
        if (!File.Exists(path))
        {
            return new TasksMdCompletion(changeName, TasksFileFound: false, Ticked: 0, Total: 0);
        }

        var ticked = 0;
        var total = 0;
        foreach (var line in File.ReadLines(path))
        {
            var match = CheckboxLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            total++;
            if (!string.Equals(match.Groups[1].Value, " ", StringComparison.Ordinal))
            {
                ticked++;
            }
        }

        return new TasksMdCompletion(changeName, TasksFileFound: true, Ticked: ticked, Total: total);
    }
}
