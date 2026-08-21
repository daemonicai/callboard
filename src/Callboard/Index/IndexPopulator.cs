using Callboard.Cards;
using Microsoft.Data.Sqlite;

namespace Callboard.Index;

/// <summary>
/// Rebuilds the derived index from the primary record alone (record-retrieval: "reconstruct all
/// derived state from the primary record"). <see cref="Populate"/> reads only <c>*.md</c> card
/// files under <paramref name="cardsRoot"/>'s <c>callboard/register/</c>, <c>callboard/decisions/</c>
/// and <c>callboard/changes/&lt;name&gt;/</c> — the same layout <see cref="CardLayout"/> and
/// <see cref="CardStore"/> use — via <see cref="CardStore.ReadAllCards"/>. No other input is
/// consulted, so a rebuild starting from a fresh, empty <paramref name="databasePath"/> is
/// reconstructible from the record alone, exactly what the requirement this block serves asks for.
///
/// <para>
/// This block has no production caller: block B wires <see cref="Populate"/> to the
/// <c>index rebuild</c> verb, in the same section, not this one — see the block A brief in
/// <c>DEVLOG.md</c> for why that split is deliberate rather than an oversight.
/// </para>
/// </summary>
internal static class IndexPopulator
{
    /// <summary>
    /// Reads every card under <paramref name="cardsRoot"/>, builds a fresh index, and swaps it
    /// into <paramref name="databasePath"/> atomically — a full replace, never an incremental
    /// merge (3.3 is a <em>rebuild</em>). A card that fails to parse is recorded in the returned
    /// <see cref="IndexPopulationResult.Failures"/> and otherwise skipped; it never stops the rest
    /// of the rebuild and never throws.
    /// </summary>
    internal static IndexPopulationResult Populate(string cardsRoot, string databasePath)
    {
        var successes = new List<(string FilePath, CardFile Card)>();
        var failures = new List<(string FilePath, string Reason)>();

        foreach (var directory in ResolveCardSources(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                result.Match<object?>(
                    onSuccess: success =>
                    {
                        successes.Add((filePath, success.Card));
                        return null;
                    },
                    onFailure: failure =>
                    {
                        failures.Add((filePath, failure.Reason));
                        return null;
                    });
            }
        }

        WriteDatabase(databasePath, successes);

        var indexedCommentCount = successes.Sum(static success => success.Card.Comments.Count);

        return new IndexPopulationResult(successes.Count, indexedCommentCount, failures);
    }

    private static IReadOnlyList<string> ResolveCardSources(string cardsRoot)
    {
        var directories = new List<string>
        {
            CombineWithLayout(cardsRoot, CardLayout.RegisterDirectory),
            CombineWithLayout(cardsRoot, CardLayout.DecisionsDirectory),
        };

        // Population does not know change names ahead of time, so it enumerates CardLayout's
        // changes root directly rather than asking CardLayout to resolve one card's directory.
        var changesRoot = CombineWithLayout(cardsRoot, CardLayout.ChangesRootDirectory);
        if (Directory.Exists(changesRoot))
        {
            directories.AddRange(
                Directory.EnumerateDirectories(changesRoot)
                    .OrderBy(static path => path, StringComparer.Ordinal));
        }

        return directories;
    }

    private static string CombineWithLayout(string cardsRoot, string layoutDirectory) =>
        Path.Combine(cardsRoot, layoutDirectory.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteDatabase(string databasePath, IReadOnlyList<(string FilePath, CardFile Card)> cards)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException($"'{databasePath}' has no containing directory.", nameof(databasePath));
        }

        Directory.CreateDirectory(directory);

        // Built entirely in a temp file beside the target, then swapped in with the same
        // create-temp-then-rename(overwrite:true) technique CardStore.AtomicWrite uses — §2
        // established File.Move(overwrite:true) as the atomic primitive on this platform
        // (overwrite:false is not; see DEVLOG "Platform facts"). A mid-run failure below leaves
        // only the orphaned temp file; the previous index (or its absence) is untouched, never a
        // half-populated database in place — this is what makes a rebuild that dies partway
        // through safe to simply retry.
        var tempPath = Path.Combine(directory, $"callboard.db.tmp-{Guid.NewGuid():N}");

        try
        {
            using (var connection = new SqliteConnection($"Data Source={tempPath}"))
            {
                connection.Open();
                IndexSchema.Create(connection);

                using var transaction = connection.BeginTransaction();
                foreach (var (filePath, card) in cards)
                {
                    InsertCard(connection, transaction, filePath, card);
                }

                transaction.Commit();
            }

            // SqliteConnection.Dispose (the using block above) closes the native handle
            // synchronously, so the file is not still open when the rename below runs.
            File.Move(tempPath, databasePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void InsertCard(SqliteConnection connection, SqliteTransaction transaction, string filePath, CardFile card)
    {
        var frontmatter = card.Frontmatter;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO cards (id, kind, title, status, owner, scope, section, created, updated, file_path)
                VALUES ($id, $kind, $title, $status, $owner, $scope, $section, $created, $updated, $filePath);
                """;
            command.Parameters.AddWithValue("$id", frontmatter.Id);
            command.Parameters.AddWithValue("$kind", frontmatter.Kind.ToWireString());
            command.Parameters.AddWithValue("$title", frontmatter.Title);
            command.Parameters.AddWithValue("$status", frontmatter.Status);
            command.Parameters.AddWithValue("$owner", frontmatter.Owner.ToWireString());
            command.Parameters.AddWithValue("$scope", frontmatter.Scope.ToWireString());
            command.Parameters.AddWithValue("$section", frontmatter.Section);
            command.Parameters.AddWithValue("$created", frontmatter.Created.ToString("O"));
            command.Parameters.AddWithValue("$updated", frontmatter.Updated.ToString("O"));
            command.Parameters.AddWithValue("$filePath", filePath);
            command.ExecuteNonQuery();
        }

        for (var ordinal = 0; ordinal < card.Comments.Count; ordinal++)
        {
            InsertComment(connection, transaction, frontmatter.Id, ordinal, card.Comments[ordinal]);
        }
    }

    private static void InsertComment(SqliteConnection connection, SqliteTransaction transaction, string cardId, int ordinal, CardComment comment)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO comments (card_id, comment_id, ordinal, author, timestamp, reply_to, addressed_to, resolved)
            VALUES ($cardId, $commentId, $ordinal, $author, $timestamp, $replyTo, $addressedTo, $resolved);
            """;
        command.Parameters.AddWithValue("$cardId", cardId);
        command.Parameters.AddWithValue("$commentId", comment.Id);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$author", comment.Author.ToWireString());
        command.Parameters.AddWithValue("$timestamp", comment.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$replyTo", (object?)comment.ReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$addressedTo", (object?)comment.To?.ToWireString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved", comment.Resolved ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
