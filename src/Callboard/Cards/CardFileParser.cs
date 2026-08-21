using System.Globalization;

namespace Callboard.Cards;

/// <summary>
/// Parses the ADR-0003 card file format: YAML-style frontmatter fences, a Markdown body, and
/// zero or more appended comment blocks — hand-rolled against the deliberately narrow schema
/// card-model defines, not a general YAML parser. See the 2.2 DEVLOG post for the AOT
/// verdict that led here: a reflection-based YAML library (YamlDotNet's default
/// <c>SerializerBuilder</c>/<c>DeserializerBuilder</c>) produced real IL3050/IL2104/IL3053
/// trim/AOT warnings under a real <c>dotnet publish -r osx-arm64</c>, which
/// <c>TreatWarningsAsErrors</c> would turn into build failures — so this format is read and
/// written by hand instead (ADR-0003 Consequences anticipates exactly this outcome).
///
/// Frontmatter keys are matched with explicit <see cref="StringComparer.Ordinal"/> — see
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class CardFileParser
{
    private static readonly string[] LineSplitSeparators = ["\n"];

    // The nine frontmatter keys 2.1's schema defines. Anything else is an unknown field — carried
    // verbatim on CardFile.UnknownFrontmatterFields rather than dropped; see that member's doc
    // comment for the extensibility rule this implements.
    private static readonly HashSet<string> KnownFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "id", "kind", "title", "status", "owner", "scope", "section", "created", "updated",
    };

    // The six comment-header fields this build recognises. Same rule, same reason, applied to the
    // per-comment header instead of the frontmatter block.
    private static readonly HashSet<string> KnownCommentHeaderKeys = new(StringComparer.Ordinal)
    {
        "id", "author", "reply-to", "to", "resolved", "timestamp",
    };

    internal static CardFileParseResult Parse(string rawText)
    {
        // CardFileWriter.Serialize always terminates its output with exactly one trailing '\n'
        // (from the closing frontmatter fence, the last body line, or the last comment footer —
        // whichever comes last). Stripping that one guaranteed trailing newline before splitting
        // keeps every remaining element a genuine line, rather than leaving a sentinel empty
        // element that a naive split would otherwise misread as one more (empty) line of content.
        var normalized = rawText.EndsWith('\n') ? rawText[..^1] : rawText;
        var lines = normalized.Split(LineSplitSeparators, StringSplitOptions.None);
        var cursor = 0;

        if (cursor >= lines.Length || !string.Equals(lines[cursor], CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
        {
            return Failure("missing opening frontmatter delimiter");
        }

        cursor++;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownFrontmatterFields = new List<(string Key, string RawValue)>();

        while (true)
        {
            if (cursor >= lines.Length)
            {
                return Failure("missing closing frontmatter delimiter");
            }

            var line = lines[cursor];

            if (string.Equals(line, CardFileFormat.FrontmatterFence, StringComparison.Ordinal))
            {
                cursor++;
                break;
            }

            var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return Failure($"malformed frontmatter line: '{line}'");
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 2)..];
            fields[key] = value;

            if (!KnownFrontmatterKeys.Contains(key))
            {
                unknownFrontmatterFields.Add((key, value));
            }

            cursor++;
        }

        var frontmatterResult = BuildFrontmatter(fields);
        if (frontmatterResult.Failure is { } frontmatterFailure)
        {
            return Failure(frontmatterFailure);
        }

        var bodyLines = new List<string>();
        while (cursor < lines.Length && !CardFileFormat.IsCommentHeader(lines[cursor]))
        {
            bodyLines.Add(CardFileFormat.UnescapeContentLine(lines[cursor]));
            cursor++;
        }

        // The body never carries a trailing blank line introduced purely by the join/split
        // round trip — an appended comment (or EOF) always follows the last real body line
        // directly.
        var body = string.Join('\n', bodyLines);

        var comments = new List<CardComment>();
        while (cursor < lines.Length)
        {
            var headerLine = lines[cursor];
            if (!CardFileFormat.IsCommentHeader(headerLine))
            {
                return Failure($"expected a comment header or end of file, found: '{headerLine}'");
            }

            if (!headerLine.EndsWith(CardFileFormat.CommentHeaderSuffix, StringComparison.Ordinal))
            {
                return Failure($"malformed comment header: '{headerLine}'");
            }

            var headerFieldsText = headerLine[CardFileFormat.CommentHeaderPrefix.Length..^CardFileFormat.CommentHeaderSuffix.Length];
            var headerFieldsResult = ParseCommentHeaderFields(headerFieldsText);
            if (headerFieldsResult.Failure is { } headerFailure)
            {
                return Failure(headerFailure);
            }

            cursor++;

            var commentBodyLines = new List<string>();
            while (cursor < lines.Length && !CardFileFormat.IsCommentFooter(lines[cursor]))
            {
                if (CardFileFormat.IsCommentHeader(lines[cursor]))
                {
                    return Failure($"missing comment footer before next comment header: '{lines[cursor]}'");
                }

                commentBodyLines.Add(CardFileFormat.UnescapeContentLine(lines[cursor]));
                cursor++;
            }

            if (cursor >= lines.Length)
            {
                return Failure("missing comment footer at end of file");
            }

            cursor++; // consume the footer line

            // headerFieldsResult.Failure is null here, so Fields/UnknownFields are guaranteed
            // non-null by ParseCommentHeaderFields's own contract.
            var commentResult = BuildComment(
                headerFieldsResult.Fields!, headerFieldsResult.UnknownFields!, string.Join('\n', commentBodyLines));
            if (commentResult.Failure is { } commentFailure)
            {
                return Failure(commentFailure);
            }

            // commentResult.Failure is null here, so Comment is guaranteed non-null by BuildComment's own contract.
            comments.Add(commentResult.Comment!);
        }

        // frontmatterResult.Failure is null here, so Frontmatter is guaranteed non-null by BuildFrontmatter's own contract.
        return new CardFileParseResult.Success(
            new CardFile(frontmatterResult.Frontmatter!, body, comments, unknownFrontmatterFields));
    }

    private static (CardFrontmatter? Frontmatter, string? Failure) BuildFrontmatter(
        IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("id", out var rawId))
        {
            return (null, "missing required frontmatter field: id");
        }

        var id = CardFileFormat.UnescapeFrontmatterValue(rawId);

        if (!fields.TryGetValue("kind", out var kindText))
        {
            return (null, "missing required frontmatter field: kind");
        }

        if (!CardKindWireFormat.TryParse(kindText, out var kind))
        {
            return (null, $"unrecognised kind: '{kindText}'. Recognised kinds: {CardKindWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("title", out var rawTitle))
        {
            return (null, "missing required frontmatter field: title");
        }

        var title = CardFileFormat.UnescapeFrontmatterValue(rawTitle);

        if (!fields.TryGetValue("status", out var rawStatus))
        {
            return (null, "missing required frontmatter field: status");
        }

        var status = CardFileFormat.UnescapeFrontmatterValue(rawStatus);

        if (!fields.TryGetValue("owner", out var ownerText))
        {
            return (null, "missing required frontmatter field: owner");
        }

        if (!CardOwnerWireFormat.TryParse(ownerText, out var owner))
        {
            return (null, $"unrecognised owner: '{ownerText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("scope", out var scopeText))
        {
            return (null, "missing required frontmatter field: scope");
        }

        if (!CardScopeWireFormat.TryParse(scopeText, out var scope))
        {
            return (null, $"unrecognised scope: '{scopeText}'. Recognised scopes: {CardScopeWireFormat.RecognisedValues}.");
        }

        var section = fields.TryGetValue("section", out var rawSection)
            ? CardFileFormat.UnescapeFrontmatterValue(rawSection)
            : string.Empty;

        if (!fields.TryGetValue("created", out var createdText))
        {
            return (null, "missing required frontmatter field: created");
        }

        if (!TryParseTimestamp(createdText, out var created))
        {
            return (null, $"invalid created timestamp: '{createdText}'");
        }

        if (!fields.TryGetValue("updated", out var updatedText))
        {
            return (null, "missing required frontmatter field: updated");
        }

        if (!TryParseTimestamp(updatedText, out var updated))
        {
            return (null, $"invalid updated timestamp: '{updatedText}'");
        }

        return (new CardFrontmatter(id, kind, title, status, owner, scope, section, created, updated), null);
    }

    private static (IReadOnlyDictionary<string, string>? Fields, IReadOnlyList<(string Key, string RawValue)>? UnknownFields, string? Failure)
        ParseCommentHeaderFields(string headerFieldsText)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownFields = new List<(string Key, string RawValue)>();

        if (headerFieldsText.Length > 0)
        {
            foreach (var token in headerFieldsText.Split(' '))
            {
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex < 0)
                {
                    return (null, null, $"malformed comment header field: '{token}'");
                }

                var key = token[..equalsIndex];
                var rawValue = token[(equalsIndex + 1)..];
                fields[key] = rawValue;

                if (!KnownCommentHeaderKeys.Contains(key))
                {
                    unknownFields.Add((key, rawValue));
                }
            }
        }

        return (fields, unknownFields, null);
    }

    private static (CardComment? Comment, string? Failure) BuildComment(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Key, string RawValue)> unknownFields,
        string body)
    {
        if (!fields.TryGetValue("id", out var rawId))
        {
            return (null, "comment missing required field: id");
        }

        var id = CardFileFormat.UnescapeCommentHeaderValue(rawId);

        if (!fields.TryGetValue("author", out var authorText))
        {
            return (null, $"comment '{id}' missing required field: author");
        }

        if (!CardOwnerWireFormat.TryParse(authorText, out var author))
        {
            return (null, $"comment '{id}' has unrecognised author: '{authorText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
        }

        if (!fields.TryGetValue("timestamp", out var timestampText))
        {
            return (null, $"comment '{id}' missing required field: timestamp");
        }

        if (!TryParseTimestamp(timestampText, out var timestamp))
        {
            return (null, $"comment '{id}' has invalid timestamp: '{timestampText}'");
        }

        string? replyTo = fields.TryGetValue("reply-to", out var replyToText)
            ? CardFileFormat.UnescapeCommentHeaderValue(replyToText)
            : null;

        CardOwner? to = null;
        if (fields.TryGetValue("to", out var toText))
        {
            if (!CardOwnerWireFormat.TryParse(toText, out var toOwner))
            {
                return (null, $"comment '{id}' has unrecognised 'to': '{toText}'. Recognised owners: {CardOwnerWireFormat.RecognisedValues}.");
            }

            to = toOwner;
        }

        var resolved = false;
        if (fields.TryGetValue("resolved", out var resolvedText))
        {
            if (string.Equals(resolvedText, "true", StringComparison.Ordinal))
            {
                resolved = true;
            }
            else if (string.Equals(resolvedText, "false", StringComparison.Ordinal))
            {
                resolved = false;
            }
            else
            {
                return (null, $"comment '{id}' has invalid 'resolved' value: '{resolvedText}'. Expected 'true' or 'false'.");
            }
        }

        return (new CardComment(id, author, timestamp, body, replyTo, to, resolved, unknownFields), null);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static CardFileParseResult.Failure Failure(string reason) => new(reason);
}
