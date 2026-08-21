using Microsoft.Data.Sqlite;

namespace Callboard.Index;

/// <summary>
/// The derived index's schema (design.md D4 / ADR-0004): queryable metadata only, never the
/// narrative. Two tables — <c>cards</c> mirrors <see cref="Cards.CardFrontmatter"/> plus the file
/// path the card was read from; <c>comments</c> mirrors <see cref="Cards.CardComment"/> minus its
/// <c>Body</c>, plus the ordinal within the card's thread that population itself assigns.
/// <b>No <c>body</c> column exists on either table, and none is added by any later block in this
/// section</b> — narrative retrieval is a file read by identity, never a database read.
///
/// <para>
/// Nothing else exists. D4 also names blocked-on edges and citation counts as fields the index may
/// eventually hold, but those fields do not exist in the primary record yet (§5 and §6 own them);
/// speculating a column ahead of the section that owns the field is exactly what this repo's
/// precedent rejects (<c>CommandDispatcher.CommandContext</c>'s doc comment: "Only members an
/// already-briefed need has asked for belong here").
/// </para>
///
/// <para>
/// Enum columns (<c>kind</c>, <c>owner</c>, <c>scope</c>, and the comment table's <c>author</c> /
/// <c>addressed_to</c>) store the wire strings the record itself uses
/// (<see cref="Cards.CardKindWireFormat.ToWireString(Cards.CardKind)"/> and its
/// <c>CardOwner</c>/<c>CardScope</c> equivalents) rather than a C#-internal ordinal, so the index
/// stays readable by a human running <c>sqlite3</c> directly, matching the plain-text record it was
/// built from.
/// </para>
/// </summary>
internal static class IndexSchema
{
    internal static void Create(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE cards (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                owner TEXT NOT NULL,
                scope TEXT NOT NULL,
                section TEXT NOT NULL,
                created TEXT NOT NULL,
                updated TEXT NOT NULL,
                file_path TEXT NOT NULL
            );

            CREATE TABLE comments (
                card_id TEXT NOT NULL REFERENCES cards (id),
                comment_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                author TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                reply_to TEXT NULL,
                addressed_to TEXT NULL,
                resolved INTEGER NOT NULL,
                PRIMARY KEY (card_id, comment_id)
            );
            """;
        command.ExecuteNonQuery();
    }
}
