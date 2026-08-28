using System.Linq;
using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Answers "which card carries this id?" by reading the record — never the derived index
/// (ADR-0004: the index is derived and never authoritative, so resolution cannot be built on top
/// of it) — across every directory <see cref="CardLayout.ResolveRecordDirectories"/> names:
/// <c>register/</c>, <c>decisions/</c>, every live change and every archived one. This is the
/// resolver the Product Owner's identity-addressing ruling calls for (§7 block B): "reference
/// fields hold card ids; a resolver answers id → card across the live tree and
/// <c>changes/archive/</c>, refusing on a duplicate id."
///
/// <para>
/// <b>Archive stays a directory move (Product Owner ruling, restated).</b> Searching
/// <see cref="CardLayout.ArchiveDirectory"/> alongside every live change is what makes card-model's
/// "a card's identity SHALL remain valid and resolvable after the change that raised it is
/// archived" true <em>without</em> archive touching a single card file — this resolver is what
/// makes that move invisible to a caller resolving an id, not a data migration archive performs.
/// </para>
///
/// <para>
/// <b>A duplicate id refuses, never picks (the defect §6 fail-closed on twice).</b> More than one
/// file carrying the same <c>id</c> answers <see cref="CardIdentityResolution.Duplicate"/> rather
/// than "whichever <see cref="CardStore.ReadAllCards"/> happened to enumerate first" — the exact
/// shape the reviewer proved reachable, twice, against the label-matching mechanism this resolver
/// replaces.
/// </para>
///
/// <para>
/// <b>A parsed match decides first, and unconditionally (§13.6).</b> <see cref="
/// CardIdentityResolution.Found"/>/<see cref="CardIdentityResolution.Duplicate"/> are decided from
/// files that parsed cleanly before any unparseable file is even consulted for a recovered id — a
/// parsed file's frontmatter is the record, while an id scraped from a file <see cref="
/// CardFileParser.Parse"/> already refused is evidence about a claim, not a second record. Only
/// once no parsed file carries the id does an unparseable file's own declared id become
/// significant, and even then only to distinguish <see cref="CardIdentityResolution.Corrupt"/>
/// (the file claiming the id is sitting right there, unparseable — the remedy is "open it") from
/// <see cref="CardIdentityResolution.Unreadable"/> (nothing found or claimed the id, but the walk
/// could not rule it out — the remedy is "repair what could not be read"). Getting this backwards
/// — trusting a corrupt file's claim over a parsed file's — would let a stale or hand-duplicated id
/// in a broken file shadow the card that actually owns it.
/// </para>
///
/// <para>
/// <b>Zero matches is not the same as zero candidates (§6 remediation B3, re-applied here).</b> If
/// no file's frontmatter carries the requested id but at least one file under a searched directory
/// could not be read at all, that id might live in the unreadable file — this answers
/// <see cref="CardIdentityResolution.Corrupt"/> or <see cref="CardIdentityResolution.Unreadable"/>,
/// not <see cref="CardIdentityResolution.NotFound"/>. A caller must not equate "could not confirm"
/// with "confirmed absent". This check applies only when no match was actually found: once one
/// file's frontmatter is confirmed to carry the requested id, a read failure elsewhere in the
/// record does not retract that answer — ids are unique by construction (<see cref="
/// CardIdentityAllocator"/>), and manufacturing a second, unproven "maybe this is a duplicate too"
/// case out of an unrelated read failure would be paranoia this method has no evidence for, not
/// fail-closed discipline.
/// </para>
/// </summary>
internal static class CardIdentityResolver
{
    internal static CardIdentityResolution Resolve(string cardsRoot, string id)
    {
        var matches = new List<(string FilePath, CardFile Card)>();
        var unreadable = new List<UnreadableCard>();
        var claimants = new List<UnreadableCard>();

        foreach (var directory in CardLayout.ResolveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                var card = result.Match<CardFile?>(
                    onSuccess: static success => success.Card,
                    onFailure: failure =>
                    {
                        unreadable.Add(new UnreadableCard(filePath, failure.Reason));

                        var declaredId = TryRecoverDeclaredId(filePath);
                        if (declaredId is not null && string.Equals(declaredId, id, StringComparison.Ordinal))
                        {
                            claimants.Add(new UnreadableCard(filePath, failure.Reason));
                        }

                        return null;
                    });

                if (card is null)
                {
                    continue;
                }

                if (string.Equals(card.Frontmatter.Id, id, StringComparison.Ordinal))
                {
                    matches.Add((filePath, card));
                }
            }
        }

        if (matches.Count > 1)
        {
            var filePaths = matches.Select(static match => match.FilePath).OrderBy(static path => path, StringComparer.Ordinal).ToList();
            return CardIdentityResolution.Duplicate(id, filePaths);
        }

        if (matches.Count == 1)
        {
            return CardIdentityResolution.Found(matches[0].FilePath, matches[0].Card);
        }

        if (claimants.Count > 0)
        {
            return CardIdentityResolution.Corrupt(id, UnreadableCards.Ordered(claimants));
        }

        if (unreadable.Count > 0)
        {
            return CardIdentityResolution.Unreadable(id, UnreadableCards.Ordered(unreadable));
        }

        return CardIdentityResolution.NotFound(id);
    }

    /// <summary>
    /// Re-reads <paramref name="filePath"/>'s raw bytes for best-effort id recovery (§13.6) — a
    /// second read of a file <see cref="CardStore.ReadAllCards"/> already read and could not parse,
    /// because <see cref="CardFileParseResult.Failure"/> carries only <see cref="
    /// CardFileParseResult.Failure.Reason"/> and not the text itself. Same encoding as <see cref="
    /// CardStore.ReadCard"/>. A file that cannot even be re-read (deleted or permission-denied
    /// between the two reads) yields no recovered id rather than a second failure to report — the
    /// original <see cref="CardFileParseResult.Failure.Reason"/> already covers that file.
    /// </summary>
    private static string? TryRecoverDeclaredId(string filePath)
    {
        string rawText;
        try
        {
            rawText = File.ReadAllText(filePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return CardFileParser.TryRecoverDeclaredId(rawText);
    }
}
