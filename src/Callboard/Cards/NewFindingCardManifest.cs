namespace Callboard.Cards;

/// <summary>
/// Parses one <c>--finding-new &lt;manifest-file&gt;</c> occurrence (§8a block B revision — see the
/// DEVLOG, "architect: accept the design, reject the one-new-finding cap" and the worker's reply
/// under the same heading) into a <see cref="NewFindingCardRequest"/>. A manifest is one small,
/// self-contained file — the same fenced-header-then-body shape a card file itself uses
/// (<see cref="CardFileFormat.FrontmatterFence"/>, ADR-0003), reused deliberately rather than a
/// new format invented for this one caller:
///
/// <code>
/// ---
/// key: finding-x007
/// new-card-file: /repo-relative/or/absolute/path/to/b-0012.md
/// title: Fix the X defect
/// ---
/// The finding's body, verbatim — free text, any number of lines, any characters at all.
/// </code>
///
/// <para>
/// <b>Why one file per finding, not a quartet of repeatable flags zipped by occurrence
/// (rejected shape).</b> Four repeatable flags (<c>--finding-new-key</c>/<c>-file</c>/<c>-title</c>/
/// <c>-body-file</c>), each occurrence <em>n</em> across all four naming one finding, would let a
/// caller supply the right <em>count</em> in every one of the four but the wrong <em>order</em> in
/// one of them — that composes, parses, and silently attaches finding A's body to finding B's key, a
/// failure a count-mismatch refusal cannot see at all. A manifest file has nothing to mis-zip: one
/// <c>--finding-new</c> occurrence is one file, and that file is the complete, self-describing
/// record of exactly one finding, read from one set of bytes in one parse.
/// </para>
///
/// <para>
/// <b>No delimiter-in-freetext risk</b> — the same defect §8's <c>--claims</c>/<c>--limits</c>
/// blocker closed for comma-joined values, closed the same way here: header values are terminated by
/// a newline, not by a character free text might contain, and the body is separated from the header
/// by the closing fence line, never by scanning the body's own text for anything.
/// </para>
///
/// <para>
/// The header key is <see cref="NewCardFileKey"/> (<c>new-card-file</c>), not <c>file</c> — kept
/// textually distinct from the manifest file itself, the flag's own argument, so a reader (or a
/// grep) is never left asking "which file".
/// </para>
/// </summary>
internal static class NewFindingCardManifest
{
    internal const string KeyKey = "key";
    internal const string NewCardFileKey = "new-card-file";
    internal const string TitleKey = "title";

    private static readonly string[] RequiredHeaderKeysInOrder = [KeyKey, NewCardFileKey, TitleKey];

    /// <summary>
    /// Parses <paramref name="manifestPath"/>'s content (already read — file existence and
    /// readability are checked by the caller, the same argv-decidable/environmental split
    /// <c>--blind-spot-body-file</c> established) into a <see cref="NewFindingCardRequest"/>, or a
    /// human-readable reason it could not be, naming <paramref name="manifestPath"/>.
    /// </summary>
    internal static (NewFindingCardRequest? Request, string? Failure) Parse(string manifestPath, string content)
    {
        // Split on the platform-independent line terminator this codebase's own card format uses
        // elsewhere (CardFileParser.LineSplitSeparators) — "\n" alone, so a "\r\n" file still parses
        // (the trailing "\r" lands on the last header value or the fence line; both are trimmed
        // below where it matters, and the body is taken from the raw content, not the split lines,
        // so a "\r\n" body round-trips exactly as read).
        var lines = content.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != CardFileFormat.FrontmatterFence)
        {
            return (null, $"manifest '{manifestPath}' does not open with the required '{CardFileFormat.FrontmatterFence}' fence on its first line.");
        }

        var headerValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var closingFenceLineIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line == CardFileFormat.FrontmatterFence)
            {
                closingFenceLineIndex = i;
                break;
            }

            var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return (null, $"manifest '{manifestPath}' has a header line with no ': ' separator: '{line}'.");
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (!RequiredHeaderKeysInOrder.Contains(key, StringComparer.Ordinal))
            {
                return (null, $"manifest '{manifestPath}' has an unrecognised header key: '{key}'. Recognised keys: {string.Join(", ", RequiredHeaderKeysInOrder)}.");
            }

            if (!headerValues.TryAdd(key, value))
            {
                return (null, $"manifest '{manifestPath}' has header key '{key}' more than once.");
            }
        }

        if (closingFenceLineIndex < 0)
        {
            return (null, $"manifest '{manifestPath}' has no closing '{CardFileFormat.FrontmatterFence}' fence.");
        }

        foreach (var requiredKey in RequiredHeaderKeysInOrder)
        {
            if (!headerValues.ContainsKey(requiredKey))
            {
                return (null, $"manifest '{manifestPath}' is missing required header key '{requiredKey}'.");
            }
        }

        var key0 = headerValues[KeyKey];
        if (!NewFindingCardRequest.IsValidKey(key0))
        {
            return (null, $"manifest '{manifestPath}' has an empty or whitespace-only '{KeyKey}'.");
        }

        var newCardFile = headerValues[NewCardFileKey];
        if (string.IsNullOrWhiteSpace(newCardFile))
        {
            return (null, $"manifest '{manifestPath}' has an empty or whitespace-only '{NewCardFileKey}'.");
        }

        var title = headerValues[TitleKey];
        if (string.IsNullOrWhiteSpace(title))
        {
            return (null, $"manifest '{manifestPath}' has an empty or whitespace-only '{TitleKey}'.");
        }

        // Everything after the closing fence line's own terminator, verbatim — taken from the
        // rejoined tail of `lines`, not re-sliced out of `content` by character offset, so the
        // "\n" split above and this reconstruction can never disagree on where the fence line ends.
        var body = string.Join('\n', lines[(closingFenceLineIndex + 1)..]);

        return (new NewFindingCardRequest(key0, newCardFile, title, body), null);
    }
}
