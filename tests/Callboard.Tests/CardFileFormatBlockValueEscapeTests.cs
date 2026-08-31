using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §14.2/14.3 — <see cref="CardFileFormat.EscapeCardBlockValue"/>/<see cref="CardFileFormat.UnescapeCardBlockValue"/>,
/// the escaper every one of the eight §14.1 block families' free-text fields now shares (§14.4
/// brought the comment header's own id/reply-to/resolves on too): reuse of
/// <see cref="CardFileFormat.EscapeFrontmatterValue"/>'s own edge-space/backslash/newline handling,
/// plus one more composed step escaping a literal <c>--&gt;</c> so a rule, remedy, reason, or
/// claim/limit text carrying that run can never end the block's enclosing HTML comment early in a
/// rendered view. These tests are the ones that would have caught the "obvious implementation that
/// would undo §14" the brief names — a char-keyed table mapping <c>&gt;</c> to <c>\&gt;</c> would
/// also mangle every unrelated <c>=&gt;</c>.
/// </summary>
public sealed class CardFileFormatBlockValueEscapeTests
{
    [Fact]
    public void OrdinaryProse_WithHyphensCommasAndArrows_IsWrittenVerbatim()
    {
        // The exact case the brief warns against: a char-keyed '>' table would turn this into
        // "static _ =\> null", retiring \s only to mint =\>. The composed step fires on the
        // literal three characters "-->" and nothing shorter, so an ordinary "=>" is untouched.
        const string prose = "onFailure: static _ => null — a well-known, comma-separated case";

        var escaped = CardFileFormat.EscapeCardBlockValue(prose);

        Assert.Equal(prose, escaped);
        Assert.Equal(prose, CardFileFormat.UnescapeCardBlockValue(escaped));
    }

    [Fact]
    public void LiteralArrowTerminator_IsEscapedAndReversedExactly()
    {
        const string withArrow = "the record moves --> forward, not sideways";

        var escaped = CardFileFormat.EscapeCardBlockValue(withArrow);

        Assert.DoesNotContain("-->", escaped, StringComparison.Ordinal);
        Assert.Contains("the record moves \\-> forward", escaped, StringComparison.Ordinal);
        Assert.Equal(withArrow, CardFileFormat.UnescapeCardBlockValue(escaped));
    }

    [Fact]
    public void ArrowAtTheVeryStartOrEnd_RoundTrips()
    {
        const string leading = "--> starts with it";
        const string trailing = "ends with it -->";

        Assert.Equal(leading, CardFileFormat.UnescapeCardBlockValue(CardFileFormat.EscapeCardBlockValue(leading)));
        Assert.Equal(trailing, CardFileFormat.UnescapeCardBlockValue(CardFileFormat.EscapeCardBlockValue(trailing)));
    }

    [Fact]
    public void RepeatedArrowTerminator_EachOccurrenceRoundTrips()
    {
        const string repeated = "first --> second --> third";

        var escaped = CardFileFormat.EscapeCardBlockValue(repeated);

        Assert.DoesNotContain("-->", escaped, StringComparison.Ordinal);
        Assert.Equal(repeated, CardFileFormat.UnescapeCardBlockValue(escaped));
    }

    // §14.3: "Order matters; assert it." Backslash-doubling runs first, so a value already
    // containing the literal text \-> is serialised as \\-> and must never be misread by the
    // reverse pass as an escaped arrow terminator — the failure mode the brief calls out by name.
    [Fact]
    public void LiteralBackslashArrowText_IsNeverMisreadAsAnEscapedTerminator()
    {
        const string literalBackslashArrow = "a path like a\\->b, not a terminator";

        var escaped = CardFileFormat.EscapeCardBlockValue(literalBackslashArrow);

        // The genuinely doubled backslash from escaping, immediately followed by the original
        // "->" text — never collapsed into a bare arrow escape.
        Assert.Contains("a\\\\->b", escaped, StringComparison.Ordinal);
        Assert.Equal(literalBackslashArrow, CardFileFormat.UnescapeCardBlockValue(escaped));
    }

    [Fact]
    public void ArrowTerminatorImmediatelyAfterALiteralBackslash_RoundTrips()
    {
        // A harder case than the one above: the literal arrow terminator sits directly next to a
        // value that already ends in a genuine backslash, so the doubled-backslash pair and the
        // arrow escape are adjacent on the wire and must not be conflated in either direction.
        const string value = "trailing backslash\\ --> then more text";

        var escaped = CardFileFormat.EscapeCardBlockValue(value);

        Assert.Equal(value, CardFileFormat.UnescapeCardBlockValue(escaped));
    }

    [Fact]
    public void EdgeSpacesAndArrowTerminator_ComposeCorrectly()
    {
        const string value = " leads with a space and --> an arrow, trails with one ";

        var escaped = CardFileFormat.EscapeCardBlockValue(value);

        Assert.StartsWith("\\s", escaped, StringComparison.Ordinal);
        Assert.EndsWith("\\s", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("-->", escaped, StringComparison.Ordinal);
        Assert.Equal(value, CardFileFormat.UnescapeCardBlockValue(escaped));
    }

    [Fact]
    public void InteriorSpaces_AreNeverEscaped()
    {
        // The whole point of §14: this is what 13.9's "\sblock\scards\smove" finding fixes.
        const string sentence = "work-lifecycle: block cards move through a defined flow";

        var escaped = CardFileFormat.EscapeCardBlockValue(sentence);

        Assert.Equal(sentence, escaped);
    }
}
