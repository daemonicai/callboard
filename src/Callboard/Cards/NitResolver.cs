using System.Linq;

namespace Callboard.Cards;

/// <summary>
/// Answers "which block card, and which of its comments, carries this nit id?" by reading the
/// record — never the derived index (ADR-0004) — across every directory
/// <see cref="CardLayout.ResolveRecordDirectories"/> names, the same directories
/// <see cref="CardIdentityResolver"/> walks. A nit is a comment (review-certification: "raised as
/// an addressed comment, not as a card"), so <c>nit disposition --id</c> cannot resolve through
/// <see cref="CardIdentityResolver"/> — that type only ever matches a card's own
/// <see cref="CardFrontmatter.Id"/>, and a nit's id lives one level down, on the
/// <see cref="CardComment"/> itself. Restricted to <c>block</c> cards, since <c>nit raise</c>
/// (§8 block B) only ever appends a nit to one — see <see cref="CardStore.IsBlockCard"/>.
/// </summary>
internal static class NitResolver
{
    internal static NitResolution Resolve(string cardsRoot, string nitId)
    {
        var matches = new List<(string FilePath, CardFile Card, CardComment Comment)>();
        var unreadable = new List<string>();

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
                    onFailure: static _ => null);

                if (card is null)
                {
                    unreadable.Add(filePath);
                    continue;
                }

                if (!CardStore.IsBlockCard(card))
                {
                    continue;
                }

                foreach (var comment in card.Comments)
                {
                    if (comment.IsNit && string.Equals(comment.Id, nitId, StringComparison.Ordinal))
                    {
                        matches.Add((filePath, card, comment));
                    }
                }
            }
        }

        if (matches.Count > 1)
        {
            var filePaths = matches.Select(static match => match.FilePath).OrderBy(static path => path, StringComparer.Ordinal).ToList();
            return NitResolution.Duplicate(nitId, filePaths);
        }

        if (matches.Count == 1)
        {
            return NitResolution.Found(matches[0].FilePath, matches[0].Card, matches[0].Comment);
        }

        if (unreadable.Count > 0)
        {
            unreadable.Sort(StringComparer.Ordinal);
            return NitResolution.Unreadable(nitId, unreadable);
        }

        return NitResolution.NotFound(nitId);
    }
}
