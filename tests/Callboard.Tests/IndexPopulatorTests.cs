using System.Text;
using Callboard.Cards;
using Callboard.Index;
using Microsoft.Data.Sqlite;

namespace Callboard.Tests;

/// <summary>
/// 3.1–3.2 — the schema holds derived queryable state only, population reconstructs it from the
/// primary record alone, and a corrupt card degrades the rebuild rather than aborting or vanishing
/// from it (record-retrieval: "reconstruct all derived state from the primary record alone").
/// </summary>
public sealed class IndexPopulatorTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 21, 10, 30, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-index-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Populate_RoundTripsEveryFrontmatterField()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var comment = new CardComment("C-0001", CardOwner.Reviewer, Created, "Narrative body.", null, CardOwner.Worker, null, []);
        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "A title", "drafting", CardOwner.Worker, CardScope.Change, "3", Created, Updated);
        WriteCard(CardScope.Change, "b-0001", new NewCardFile(frontmatter, "Body."), [comment]);

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(1, result.IndexedCardCount);
        Assert.Empty(result.Failures);

        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, kind, title, status, owner, scope, section, created, updated, file_path FROM cards;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("B-0001", reader.GetString(0));
        Assert.Equal("block", reader.GetString(1));
        Assert.Equal("A title", reader.GetString(2));
        Assert.Equal("drafting", reader.GetString(3));
        Assert.Equal("worker", reader.GetString(4));
        Assert.Equal("change", reader.GetString(5));
        Assert.Equal("3", reader.GetString(6));
        Assert.Equal(Created, DateTimeOffset.Parse(reader.GetString(7)));
        Assert.Equal(Updated, DateTimeOffset.Parse(reader.GetString(8)));
        Assert.EndsWith("b-0001.md", reader.GetString(9), StringComparison.Ordinal);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Populate_RoutesEveryCommentWithCorrectOrdinals()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var first = new CardComment("C-0001", CardOwner.Worker, Created, "First.", null, CardOwner.Architect, null, []);
        var second = new CardComment("C-0002", CardOwner.Architect, Updated, "Second.", "C-0001", null, "C-0001", []);
        var frontmatter = new CardFrontmatter(
            "B-0002", CardKind.Question, "Q", "open", CardOwner.Architect, CardScope.Change, "3", Created, Created);
        WriteCard(CardScope.Change, "b-0002", new NewCardFile(frontmatter, "Body."), [first, second]);

        IndexPopulator.Populate(_root, databasePath);

        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT comment_id, ordinal, author, reply_to, addressed_to, resolved FROM comments ORDER BY ordinal;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("C-0001", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal("worker", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal("architect", reader.GetString(4));
        // Resolved here — not by anything on this row itself, but because C-0002 (below) names
        // it via `resolves`: the derived column IndexPopulator computes over the whole thread.
        Assert.Equal(1, reader.GetInt32(5));

        Assert.True(reader.Read());
        Assert.Equal("C-0002", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal("architect", reader.GetString(2));
        Assert.Equal("C-0001", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.Equal(0, reader.GetInt32(5));

        Assert.False(reader.Read());
    }

    [Fact]
    public void Populate_NeverWritesCardOrCommentBodyTextIntoTheDatabaseFile()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        const string cardBodySecret = "UNMISTAKABLE_CARD_BODY_MARKER_7f3a";
        const string commentBodySecret = "UNMISTAKABLE_COMMENT_BODY_MARKER_9c1e";
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, commentBodySecret, null, null, null, []);
        var frontmatter = new CardFrontmatter(
            "B-0003", CardKind.Block, "Title", "drafting", CardOwner.Worker, CardScope.Change, "3", Created, Created);
        WriteCard(CardScope.Change, "b-0003", new NewCardFile(frontmatter, cardBodySecret), [comment]);

        IndexPopulator.Populate(_root, databasePath);

        // Asserted against the database file's raw bytes, not against the writer above it — the
        // point is that D4 holds even if a later change adds a column that could carry it.
        var databaseBytes = File.ReadAllBytes(databasePath);
        var databaseText = Encoding.Latin1.GetString(databaseBytes);
        Assert.DoesNotContain(cardBodySecret, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(commentBodySecret, databaseText, StringComparison.Ordinal);
    }

    [Fact]
    public void Populate_IndexesGoodCardsAndReportsTheCorruptOne()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        WriteCard(CardScope.Change, "b-0004", GoodCard("B-0004"));
        var corruptPath = CardPath(CardScope.Change, "b-0005");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllBytes(corruptPath, [0xFF, 0xFE, 0x00, 0xFF]);
        WriteCard(CardScope.Change, "b-0006", GoodCard("B-0006"));

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(2, result.IndexedCardCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(corruptPath, failure.FilePath);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));

        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM cards ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        Assert.Equal(["B-0004", "B-0006"], ids);
    }

    [Fact]
    public void Populate_OnAnEmptyCardsRoot_ProducesAnEmptyValidIndex()
    {
        var databasePath = IndexPaths.DatabasePath(_root);

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(0, result.IndexedCardCount);
        Assert.Empty(result.Failures);
        Assert.True(File.Exists(databasePath));

        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM cards;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    // ---------------------------------------------------------------------------------------
    // §4 remediation R2 — a duplicated identity is a reported failure, not an aborted rebuild.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Populate_ReportsADuplicateCommentId_AndStillIndexesAHealthyCardElsewhere()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var duplicateCardPath = CardPath(CardScope.Change, "b-0010");

        var first = new CardComment("C-0001", CardOwner.Worker, Created, "First.", null, null, null, []);
        var repeated = new CardComment("C-0001", CardOwner.Reviewer, Updated, "Same id as the first.", null, null, null, []);
        var frontmatter = new CardFrontmatter(
            "B-0010", CardKind.Block, "Has a duplicate comment id", "drafting", CardOwner.Worker, CardScope.Change, "3", Created, Created);
        WriteCard(CardScope.Change, "b-0010", new NewCardFile(frontmatter, "Body."), [first, repeated]);
        WriteCard(CardScope.Change, "b-0011", GoodCard("B-0011"));

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(1, result.IndexedCardCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(duplicateCardPath, failure.FilePath);
        Assert.Contains("C-0001", failure.Reason, StringComparison.Ordinal);

        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM cards ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        Assert.Equal(["B-0011"], ids);
    }

    [Fact]
    public void Populate_ReportsTwoFilesClaimingOneCardId_NamesBothAndIndexesNeither_LeavingAHealthyCardElsewhereIndexed()
    {
        // What the spec's own "Rule promoted from change to repository scope" scenario produces
        // when performed the obvious way: the card's file is written at the new scope's path
        // without the old one being moved or removed.
        var databasePath = IndexPaths.DatabasePath(_root);
        var pathA = CardPath(CardScope.Change, "r-0020-change");
        var pathB = CardPath(CardScope.Repository, "r-0020-register");
        var frontmatterA = new CardFrontmatter(
            "R-0020", CardKind.Rule, "Original scope", "open", CardOwner.Architect, CardScope.Change, "3", Created, Created);
        var frontmatterB = new CardFrontmatter(
            "R-0020", CardKind.Rule, "Promoted scope", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        WriteCard(CardScope.Change, "r-0020-change", new NewCardFile(frontmatterA, "Body."));
        WriteCard(CardScope.Repository, "r-0020-register", new NewCardFile(frontmatterB, "Body."));
        WriteCard(CardScope.Change, "b-0021", GoodCard("B-0021"));

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(1, result.IndexedCardCount);
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, failure => failure.FilePath == pathA);
        Assert.Contains(result.Failures, failure => failure.FilePath == pathB);
        Assert.All(result.Failures, failure => Assert.Contains("R-0020", failure.Reason, StringComparison.Ordinal));

        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM cards ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        Assert.Equal(["B-0021"], ids);
    }

    [Fact]
    public void Populate_ReadsRepositoryAndCapabilityScopedCardsToo()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var repositoryFrontmatter = new CardFrontmatter(
            "R-0001", CardKind.Rule, "Rule", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        var capabilityFrontmatter = new CardFrontmatter(
            "D-0001", CardKind.Decision, "Decision", "open", CardOwner.Architect, CardScope.Capability, string.Empty, Created, Created);
        WriteCard(CardScope.Repository, "r-0001", new NewCardFile(repositoryFrontmatter, "Body."));
        WriteCard(CardScope.Capability, "d-0001", new NewCardFile(capabilityFrontmatter, "Body."));

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(2, result.IndexedCardCount);
    }

    private static NewCardFile GoodCard(string id) =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title " + id, "drafting", CardOwner.Worker, CardScope.Change, "3", Created, Created),
            "Body.");

    private string CardPath(CardScope scope, string fileStem)
    {
        var directory = scope.Match(
            onSection: () => CardLayout.ChangesDirectory(ChangeName),
            onChange: () => CardLayout.ChangesDirectory(ChangeName),
            onCapability: () => CardLayout.DecisionsDirectory,
            onRepository: () => CardLayout.RegisterDirectory);

        return Path.Combine(_root, directory.Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
    }

    private void WriteCard(CardScope scope, string fileStem, NewCardFile card, IReadOnlyList<CardComment>? comments = null)
    {
        var path = CardPath(scope, fileStem);
        var changeName = scope.Match(
            onSection: () => (string?)ChangeName,
            onChange: () => (string?)ChangeName,
            onCapability: () => null,
            onRepository: () => null);

        var result = CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5), changeName);
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"setup write failed: no card at '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"setup write failed: already exists at '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"setup write failed: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"setup write failed: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"setup write failed: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"setup write failed: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"setup write failed: hand-entered derived-state field '{handEntered.Key}'"));

        foreach (var comment in comments ?? [])
        {
            var appended = CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), changeName);
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
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }
}
