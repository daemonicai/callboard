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
        var comment = new CardComment("C-0001", CardOwner.Reviewer, Created, "Narrative body.", null, CardOwner.Worker, false, []);
        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "A title", "open", CardOwner.Worker, CardScope.Change, "3", Created, Updated);
        WriteCard(CardScope.Change, "b-0001", new CardFile(frontmatter, "Body.", [comment], []));

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
        Assert.Equal("open", reader.GetString(3));
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
        var first = new CardComment("C-0001", CardOwner.Worker, Created, "First.", null, CardOwner.Architect, false, []);
        var second = new CardComment("C-0002", CardOwner.Architect, Updated, "Second.", "C-0001", null, true, []);
        var frontmatter = new CardFrontmatter(
            "B-0002", CardKind.Question, "Q", "open", CardOwner.Architect, CardScope.Change, "3", Created, Created);
        WriteCard(CardScope.Change, "b-0002", new CardFile(frontmatter, "Body.", [first, second], []));

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
        Assert.Equal(0, reader.GetInt32(5));

        Assert.True(reader.Read());
        Assert.Equal("C-0002", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal("architect", reader.GetString(2));
        Assert.Equal("C-0001", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.Equal(1, reader.GetInt32(5));

        Assert.False(reader.Read());
    }

    [Fact]
    public void Populate_NeverWritesCardOrCommentBodyTextIntoTheDatabaseFile()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        const string cardBodySecret = "UNMISTAKABLE_CARD_BODY_MARKER_7f3a";
        const string commentBodySecret = "UNMISTAKABLE_COMMENT_BODY_MARKER_9c1e";
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, commentBodySecret, null, null, false, []);
        var frontmatter = new CardFrontmatter(
            "B-0003", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "3", Created, Created);
        WriteCard(CardScope.Change, "b-0003", new CardFile(frontmatter, cardBodySecret, [comment], []));

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

    [Fact]
    public void Populate_ReadsRepositoryAndCapabilityScopedCardsToo()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var repositoryFrontmatter = new CardFrontmatter(
            "R-0001", CardKind.Rule, "Rule", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        var capabilityFrontmatter = new CardFrontmatter(
            "D-0001", CardKind.Decision, "Decision", "open", CardOwner.Architect, CardScope.Capability, string.Empty, Created, Created);
        WriteCard(CardScope.Repository, "r-0001", new CardFile(repositoryFrontmatter, "Body.", [], []));
        WriteCard(CardScope.Capability, "d-0001", new CardFile(capabilityFrontmatter, "Body.", [], []));

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(2, result.IndexedCardCount);
    }

    private static CardFile GoodCard(string id) =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title " + id, "open", CardOwner.Worker, CardScope.Change, "3", Created, Created),
            "Body.",
            [],
            []);

    private string CardPath(CardScope scope, string fileStem)
    {
        var directory = scope.Match(
            onSection: () => CardLayout.ChangesDirectory(ChangeName),
            onChange: () => CardLayout.ChangesDirectory(ChangeName),
            onCapability: () => CardLayout.DecisionsDirectory,
            onRepository: () => CardLayout.RegisterDirectory);

        return Path.Combine(_root, directory.Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
    }

    private void WriteCard(CardScope scope, string fileStem, CardFile card)
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
            onFailure: failure => throw new Xunit.Sdk.XunitException($"setup write failed: {failure.Reason}"));
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }
}
