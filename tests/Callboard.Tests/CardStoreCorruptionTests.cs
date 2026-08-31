using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 2.8 — damage to any single card file leaves every other card in the same directory readable
/// (record-retrieval: "damage to any single card SHALL NOT compromise any other card"). Each test
/// here writes several well-formed cards, replaces one file's bytes with a genuine byte-level mess
/// — not merely a value the parser dislikes — and asserts every other card still parses via
/// <see cref="CardStore.ReadAllCards"/>, the read path that isolates one file's outcome from the
/// rest.
/// </summary>
public sealed class CardStoreCorruptionTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-corruption-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardStoreCorruptionTests()
    {
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Truncation_MidComment_LeavesEveryOtherCardReadable()
    {
        var goodPathA = WriteGoodCard("b-0001");
        var wreckedPath = WriteGoodCard("b-0002", withComment: true);
        var goodPathB = WriteGoodCard("b-0003");

        var fullBytes = File.ReadAllBytes(wreckedPath);
        File.WriteAllBytes(wreckedPath, fullBytes[..(fullBytes.Length / 2)]);

        AssertOneCorruptTwoReadable(wreckedPath, goodPathA, goodPathB);
    }

    [Fact]
    public void InvalidUtf8Bytes_LeavesEveryOtherCardReadable()
    {
        var goodPathA = WriteGoodCard("b-0004");
        var wreckedPath = WriteGoodCard("b-0005");
        var goodPathB = WriteGoodCard("b-0006");

        // 0xFF and 0xFE are never valid anywhere in a UTF-8 byte stream.
        File.WriteAllBytes(wreckedPath, [0xFF, 0xFE, 0x00, 0xFF, 0x10, 0x20]);

        AssertOneCorruptTwoReadable(wreckedPath, goodPathA, goodPathB);
    }

    [Fact]
    public void UnterminatedCommentDelimiter_LeavesEveryOtherCardReadable()
    {
        var goodPathA = WriteGoodCard("b-0007");
        var wreckedPath = WriteGoodCard("b-0008");
        var goodPathB = WriteGoodCard("b-0009");

        var content = File.ReadAllText(wreckedPath)
            + "<!-- callboard:comment\n"
            + "id: C-0001\n"
            + "author: worker\n"
            + "timestamp: 2026-08-20T09:00:00+00:00\n"
            + "-->\n"
            + "this comment is never closed\n";
        File.WriteAllText(wreckedPath, content, new UTF8Encoding(false));

        AssertOneCorruptTwoReadable(wreckedPath, goodPathA, goodPathB);
    }

    [Fact]
    public void EmptyFile_LeavesEveryOtherCardReadable()
    {
        var goodPathA = WriteGoodCard("b-0010");
        var wreckedPath = WriteGoodCard("b-0011");
        var goodPathB = WriteGoodCard("b-0012");

        File.WriteAllBytes(wreckedPath, []);

        AssertOneCorruptTwoReadable(wreckedPath, goodPathA, goodPathB);
    }

    private void AssertOneCorruptTwoReadable(string wreckedPath, params string[] untouchedPaths)
    {
        var expectedTextByPath = untouchedPaths.ToDictionary(p => p, ReadWrittenText, StringComparer.Ordinal);

        var results = CardStore.ReadAllCards(_directory);

        Assert.Equal(1 + untouchedPaths.Length, results.Count);

        var forWreckedPath = results.Single(r => string.Equals(r.FilePath, wreckedPath, StringComparison.Ordinal));
        AssertParseFailure(forWreckedPath.Result);

        foreach (var untouchedPath in untouchedPaths)
        {
            var forThisPath = results.Single(r => string.Equals(r.FilePath, untouchedPath, StringComparison.Ordinal));
            var card = AssertParseSuccess(forThisPath.Result);
            Assert.Equal(expectedTextByPath[untouchedPath], CardFileWriter.Serialize(card));
        }
    }

    private string WriteGoodCard(string id, bool withComment = false)
    {
        var path = Path.Combine(_directory, id + ".md");
        var frontmatter = new CardFrontmatter(
            id.ToUpperInvariant(), CardKind.Block, "Title " + id, "drafting", CardOwner.Worker, CardScope.Change, "2", Created, Created);

        var write = CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName);
        write.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"setup write failed: no card at '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"setup write failed: already exists at '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"setup write failed: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"setup write failed: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"setup write failed: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"setup write failed: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"setup write failed: hand-entered derived-state field '{handEntered.Key}'"));

        if (withComment)
        {
            var comment = new CardComment("C-0001", CardOwner.Worker, Created, "A comment.", null, null, null, []);
            var appended = CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);
            appended.Match<object?>(
                onSuccess: static _ => null,
                onNotFound: notFound => throw new Xunit.Sdk.XunitException($"setup append failed: no card at '{notFound.FilePath}'"),
                onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"setup append failed: already exists at '{alreadyExists.FilePath}'"),
                onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"setup append failed: {layoutMismatch.Reason}"),
                onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"setup append failed: {corrupt.Reason}"),
                onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"setup append failed: {toolFailure.Reason}"),
                onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"setup append failed: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
                onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"setup append failed: hand-entered derived-state field '{handEntered.Key}'"));
        }

        return path;
    }

    private static string ReadWrittenText(string path) => File.ReadAllText(path, new UTF8Encoding(false));

    private static void AssertParseFailure(CardFileParseResult result) =>
        result.Match<object?>(
            onSuccess: success => throw new Xunit.Sdk.XunitException($"expected the wrecked card to fail to parse, got: {success.Card}"),
            onFailure: static _ => null);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
