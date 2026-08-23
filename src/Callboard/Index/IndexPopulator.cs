using Callboard.Cards;
using Microsoft.Data.Sqlite;

namespace Callboard.Index;

/// <summary>
/// Rebuilds the derived index from the primary record alone (record-retrieval: "reconstruct all
/// derived state from the primary record"). <see cref="Populate"/> reads only <c>*.md</c> card
/// files under <paramref name="cardsRoot"/>'s <c>callboard/register/</c>, <c>callboard/decisions/</c>,
/// <c>callboard/changes/&lt;name&gt;/</c> and <c>callboard/changes/archive/&lt;name&gt;/</c> — the
/// same layout <see cref="CardLayout"/> and <see cref="CardStore"/> use — via
/// <see cref="CardStore.ReadAllCards"/>. No other input is consulted, so a rebuild starting from a
/// fresh, empty <paramref name="databasePath"/> is reconstructible from the record alone, exactly
/// what the requirement this block serves asks for.
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
    /// of the rebuild and never throws. <b>Neither does a duplicated identity</b> (§4 remediation
    /// R2): two files sharing one <c>id</c>, or one card whose own thread repeats a
    /// <c>comment id</c>, would each violate <see cref="IndexSchema"/>'s primary keys — rather than
    /// let that constraint violation escape as an unhandled exception (which it did before this
    /// fix, surfacing as a <c>tool-failure</c> and aborting the whole rebuild), both are detected in
    /// <see cref="ExcludeDuplicateIdentities"/> before a single row is written, so the affected
    /// card(s) never reach the database at all and are reported in <see cref="IndexPopulationResult.Failures"/>
    /// naming the offending file(s) instead — the same "reported failure inside a successful
    /// rebuild" category a corrupt card already gets.
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

        // Computed over every card actually read, before duplicates are excluded below — an
        // identity that only exists because of a now-excluded duplicate file was still genuinely
        // observed on disk, and hiding it from this check would let a counter reset past it
        // unnoticed (exactly the recycling CardIdentityAllocator's doc comment names).
        var identityCounterViolations = CardIdentityAllocator.VerifyCounters(cardsRoot, ObservedMaxIdByKind(successes));

        var indexable = ExcludeDuplicateIdentities(successes, failures);

        WriteDatabase(databasePath, indexable);

        var indexedCommentCount = indexable.Sum(static success => success.Card.Comments.Count);

        return new IndexPopulationResult(indexable.Count, indexedCommentCount, failures, identityCounterViolations);
    }

    /// <summary>
    /// Removes every card that would collide with <see cref="IndexSchema"/>'s primary keys before
    /// <see cref="WriteDatabase"/> ever sees it, appending one <see cref="IndexPopulationResult.Failures"/>
    /// entry per excluded file (§4 remediation R2). Two routes, both reachable from the record
    /// alone:
    ///
    /// <list type="bullet">
    /// <item>Two files whose frontmatter <c>id</c> matches — what the spec's own "Rule promoted
    /// from change to repository scope" scenario produces when a card's file is written at its new
    /// scope's path without removing the old one (<see cref="AnchoredCardPath"/> requires the new
    /// path; nothing today moves or deletes the old one). Every file sharing the id is excluded and
    /// named in the others' failure reasons — not last-writer-wins, since neither file is more
    /// authoritative than the other.</item>
    /// <item>One card whose own <see cref="CardFile.Comments"/> repeats a <c>comment id</c> —
    /// nothing upstream of this rejects a duplicate on append or on parse. Only that file is
    /// excluded; every other card, including ones sharing no relationship to it, is indexed
    /// normally.</item>
    /// </list>
    /// </summary>
    private static List<(string FilePath, CardFile Card)> ExcludeDuplicateIdentities(
        List<(string FilePath, CardFile Card)> successes,
        List<(string FilePath, string Reason)> failures)
    {
        var excludedFilePaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in successes.GroupBy(static success => success.Card.Frontmatter.Id, StringComparer.Ordinal))
        {
            var filePaths = group.Select(static success => success.FilePath).OrderBy(static path => path, StringComparer.Ordinal).ToList();
            if (filePaths.Count <= 1)
            {
                continue;
            }

            var namedFiles = string.Join(", ", filePaths.Select(static path => $"'{path}'"));
            foreach (var filePath in filePaths)
            {
                failures.Add((filePath, $"card id '{group.Key}' is claimed by more than one file: {namedFiles}; none of them has been indexed."));
                excludedFilePaths.Add(filePath);
            }
        }

        var indexable = new List<(string FilePath, CardFile Card)>();
        foreach (var (filePath, card) in successes)
        {
            if (excludedFilePaths.Contains(filePath))
            {
                continue;
            }

            var duplicateCommentId = card.Comments
                .GroupBy(static comment => comment.Id, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Count() > 1);

            if (duplicateCommentId is not null)
            {
                failures.Add((filePath, $"comment id '{duplicateCommentId.Key}' appears more than once in the thread of card " +
                    $"'{card.Frontmatter.Id}'; the card has not been indexed."));
                continue;
            }

            indexable.Add((filePath, card));
        }

        return indexable;
    }

    /// <summary>
    /// The highest identity number actually seen on disk, per kind — what 4.2's
    /// <see cref="CardIdentityAllocator.VerifyCounters"/> compares each kind's committed counter
    /// against. A card whose <c>id</c> does not match its own <c>kind</c>'s prefix is simply
    /// omitted from that kind's maximum rather than misread — see
    /// <see cref="CardIdentityAllocator.TryParseIdentityNumber"/>.
    /// </summary>
    private static IReadOnlyDictionary<CardKind, int> ObservedMaxIdByKind(IReadOnlyList<(string FilePath, CardFile Card)> successes)
    {
        var observedMaxByKind = new Dictionary<CardKind, int>();

        foreach (var (_, card) in successes)
        {
            if (!CardIdentityAllocator.TryParseIdentityNumber(card.Frontmatter.Kind, card.Frontmatter.Id, out var number))
            {
                continue;
            }

            if (!observedMaxByKind.TryGetValue(card.Frontmatter.Kind, out var existing) || number > existing)
            {
                observedMaxByKind[card.Frontmatter.Kind] = number;
            }
        }

        return observedMaxByKind;
    }

    /// <summary>
    /// Every directory holding <c>*.md</c> card files — delegated entirely to
    /// <see cref="CardLayout.ResolveRecordDirectories"/> (§7 block B) so this enumeration and the id
    /// resolver's own walk cannot silently drift apart; see that method's own doc comment for what
    /// it covers and why (§4 remediation R1's archive reasoning still applies, unchanged).
    /// </summary>
    private static IReadOnlyList<string> ResolveCardSources(string cardsRoot) => CardLayout.ResolveRecordDirectories(cardsRoot);

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
            InsertComment(connection, transaction, frontmatter.Id, ordinal, card.Comments);
        }
    }

    /// <summary>
    /// Indexes the comment at <paramref name="ordinal"/> in <paramref name="comments"/>. The
    /// <c>resolved</c> column is not read off <see cref="CardComment"/> — that field no longer
    /// exists (card-model 4.6: resolution is an appended comment naming what it resolves, not a
    /// stored flag) — it is derived here via <see cref="CardCommentRouting.IsResolved"/>, over the
    /// whole thread, exactly as it would be recomputed from the record on a rebuild (ADR-0004).
    /// </summary>
    private static void InsertComment(SqliteConnection connection, SqliteTransaction transaction, string cardId, int ordinal, IReadOnlyList<CardComment> comments)
    {
        var comment = comments[ordinal];

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
        command.Parameters.AddWithValue("$resolved", CardCommentRouting.IsResolved(comments, ordinal) ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
