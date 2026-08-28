using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §13.6 — <see cref="CardFileParser.TryRecoverDeclaredId"/> directly, the primitive
/// <see cref="Cards.CardIdentityResolver"/>'s best-effort id recovery is built on. Proves the
/// boundary in isolation: recovers only from the leading frontmatter fence, never from a body line
/// that merely looks like one, and never when the fence itself is not intact.
/// </summary>
public sealed class CardFileParserRecoverIdTests
{
    [Fact]
    public void IntactFence_BadStatusAfterIt_RecoversTheDeclaredId()
    {
        var rawText =
            "---\n" +
            "id: B-0001\n" +
            "kind: block\n" +
            "title: A block\n" +
            "status: not-a-real-status\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: S-0001\n" +
            "created: 2026-08-20T09:00:00+00:00\n" +
            "updated: 2026-08-20T09:00:00+00:00\n" +
            "---\n" +
            "Body.\n";

        // The file as a whole still fails to parse (an unrecognised status) — recovery is a
        // separate, narrower operation than Parse itself.
        Assert.IsType<CardFileParseResult.Failure>(CardFileParser.Parse(rawText));

        Assert.Equal("B-0001", CardFileParser.TryRecoverDeclaredId(rawText));
    }

    // The exact case the brief named: a body line that happens to read like a declared id must
    // never be consulted — only the span between the opening and closing frontmatter fence is.
    [Fact]
    public void BodyLineThatLooksLikeAnId_NeverConsulted()
    {
        var rawText =
            "---\n" +
            "id: B-0002\n" +
            "kind: block\n" +
            "title: A block\n" +
            "status: not-a-real-status\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: S-0001\n" +
            "created: 2026-08-20T09:00:00+00:00\n" +
            "updated: 2026-08-20T09:00:00+00:00\n" +
            "---\n" +
            "id: B-9999\n" +
            "This body line is not frontmatter, even though it starts with 'id: '.\n";

        Assert.Equal("B-0002", CardFileParser.TryRecoverDeclaredId(rawText));
    }

    [Fact]
    public void NoOpeningFence_RecoversNothing()
    {
        var rawText = "id: B-0001\nnot a card file at all\n";

        Assert.Null(CardFileParser.TryRecoverDeclaredId(rawText));
    }

    // The fence opens but never closes — nothing inside it is trustworthy, including a well-formed
    // 'id:' line that appears before the file runs out.
    [Fact]
    public void OpeningFenceNeverCloses_RecoversNothing()
    {
        var rawText = "---\nid: B-0001\nkind: block\nno closing fence anywhere in this file\n";

        Assert.Null(CardFileParser.TryRecoverDeclaredId(rawText));
    }

    [Fact]
    public void FenceIntactButNoIdLine_RecoversNothing()
    {
        var rawText = "---\nkind: block\ntitle: A block\n---\nBody.\n";

        Assert.Null(CardFileParser.TryRecoverDeclaredId(rawText));
    }

    // §13.6 review, nit 1 — an indented fence line is not the literal "---" the ordinal equality
    // check requires, so it is simply never recognised as a fence at all. Fails closed: nothing is
    // recovered, rather than a false attribution built from a line that merely looks like a fence.
    [Fact]
    public void IndentedOpeningFence_NeverRecognised_RecoversNothing()
    {
        var rawText = "  ---\nid: B-0001\nkind: block\n---\nBody.\n";

        Assert.Null(CardFileParser.TryRecoverDeclaredId(rawText));
    }

    // The fence itself is intact and unindented, but the 'id:' line inside it is indented — its key
    // is " id", not "id", so the ordinal key check simply does not match it either. Fails closed the
    // same way: nothing is recovered rather than a false positive built from whitespace.
    [Fact]
    public void IndentedIdLineInsideAnIntactFence_NeverRecognised_RecoversNothing()
    {
        var rawText = "---\n  id: B-0001\nkind: block\n---\nBody.\n";

        Assert.Null(CardFileParser.TryRecoverDeclaredId(rawText));
    }

    // §13.6 review, nit 2 — CRLF line endings. LineSplitSeparators splits on "\n" only (the same
    // choice CardFileParser.Parse itself makes), so every line — including the opening fence line
    // itself — carries a trailing "\r" that a bare "---" never equals under Ordinal comparison.
    // Recovery therefore fails closed at the very first check: the opening line is never recognised
    // as the fence at all, so nothing is recovered — proven here as the value TryRecoverDeclaredId
    // actually returns, not argued from reading the code.
    [Fact]
    public void CrlfLineEndings_OpeningFenceLineNeverMatches_RecoversNothing()
    {
        var rawText = "---\r\nid: B-0001\r\nkind: block\r\n---\r\nBody.\r\n";

        Assert.Null(CardFileParser.TryRecoverDeclaredId(rawText));
    }

    // Dictionary-assignment semantics (the same as BuildFrontmatter's own fields dictionary): the
    // last 'id:' line inside the fence wins, not the first.
    [Fact]
    public void TwoIdLinesInsideTheFence_LastOneWins()
    {
        var rawText = "---\nid: B-0001\nid: B-0002\nkind: block\n---\nBody.\n";

        Assert.Equal("B-0002", CardFileParser.TryRecoverDeclaredId(rawText));
    }
}
