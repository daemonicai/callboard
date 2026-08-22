using System.Reflection;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.8 — proves the spec's "an appended comment cannot be edited or deleted" the strongest way
/// available: there is no operation to test.
///
/// <para>
/// <b>Round 1 was wrong, and the correction is the point.</b> The first version of this file
/// claimed that outright while <see cref="CardStore.WriteCard"/> still fully replaced a card —
/// the reviewer proved it false by writing a probe: a card holding one comment, then
/// <c>WriteCard</c> called again on the same path with an empty <c>Comments</c> list, and the
/// comment was gone on the next read. The reflection test below filtered
/// <see cref="CardStore"/>'s methods to those whose <em>name</em> contained <c>"Comment"</c> —
/// <c>WriteCard</c> does not, so the filter never saw it. <b>A test that enumerates a surface by
/// name proves only what its filter admits, not what the type exposes.</b> The fix applied is not
/// a narrower claim here — it is making <see cref="CardStore.WriteCard"/> itself create-only
/// (refuses under the lock when the target path already exists), so full replacement is no
/// longer reachable through this type at all. With that gap closed, "there is no operation to
/// test" is true again, for real this time, and is checked two ways below: a complete method
/// inventory (not a name filter) and a direct behavioural regression reproducing the reviewer's
/// exact probe.
/// </para>
///
/// <para>
/// <b>The mistake, written and confirmed not to compile (§3's standard):</b>
/// <code>
/// var comment = new CardComment(...);
/// comment.Body = "changed";                 // CS8852: Body is an init-only property
/// CardStore.EditComment(root, path, ...);   // CS0117: CardStore has no such member
/// </code>
/// Verified against this build by adding exactly this code to a scratch test file, running
/// <c>dotnet build</c>, and confirming both errors, then discarding the file — the same evidence
/// standard O-1 and O-2 closed on (block B: "Prove that by writing the mistake and showing it
/// does not compile"). Still true after the create-only fix; unaffected by it.
/// </para>
///
/// <para>
/// <b>The honest limit (named, not closed):</b> a card is a git-committed Markdown file humans
/// are expected to hand-edit (ADR-0003, "legible without the tool") — <c>callboard</c> cannot
/// refuse a text editor. What these tests guarantee is narrower: <c>callboard</c> itself never
/// rewrites or drops an existing comment. A human editing the file directly is guarded only by
/// git history, not by this tool, and nothing in this block (or any later one) should be read as
/// closing that gap.
/// </para>
///
/// <para>
/// <b>What is still owed:</b> the spec's refusal-shaped sentence — "the system refuses and
/// states that corrections are appended" — has no verb to refuse through in §4; there is no
/// operation that attempts an edit for a verb to refuse. §9 owns the closed refusal-code set, so
/// no code is minted here. Whichever later section wires a comment-editing verb (if one is ever
/// proposed) owes that scenario's refusal message; this block deliberately mints none.
/// </para>
/// </summary>
public sealed class CardCommentImmutabilityTests
{
    [Fact]
    public void CardComment_EveryProperty_IsInitOnlyOrGetOnly_NoneAreFreelySettable()
    {
        foreach (var property in typeof(CardComment).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.GetSetMethod(nonPublic: false);
            if (setter is null)
            {
                continue;
            }

            var isInitOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(modifier => modifier == typeof(System.Runtime.CompilerServices.IsExternalInit));

            Assert.True(
                isInitOnly,
                $"CardComment.{property.Name} has a plain settable setter — a comment must be constructible only, never mutable after the fact.");
        }
    }

    /// <summary>
    /// The fix for the round-1 finding: not a narrower filter, a complete inventory. Every static
    /// method <see cref="CardStore"/> declares is named here, with no <c>Where</c> clause standing
    /// between the reflected surface and the assertion — a future member fails this test simply by
    /// existing, which is what forces it to be read and accounted for (in the comment beside its
    /// name below) rather than silently passing because a pattern didn't match it, the way
    /// <c>"WriteCard".Contains("Comment")</c> silently didn't.
    /// </summary>
    [Fact]
    public void CardStore_EntireStaticMethodSurface_IsExplicitlyAccountedFor()
    {
        var actualMembers = typeof(CardStore)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var expectedMembers = new[]
        {
            "AppendComment",                     // read-modify-write: reads, appends one comment, writes — never drops one
            "AppendCommentUnderExistingLock",     // same, lock already held by the caller
            "AtomicWrite",                        // the shared byte-writer every path above funnels through; it writes whatever CardFileWriter.Serialize(card) produces for the CardFile each of those paths built — none of them builds one with a truncated Comments list
            "ReadAllCards",                       // read-only
            "ReadCard",                           // read-only
            "TransferOwnership",                  // read-modify-write: overwrites Owner/Handovers only; Comments passes through the `success.Card with { ... }` unchanged
            "TransferOwnershipUnderExistingLock", // same, lock already held
            "WithLock",                           // lock-acquisition plumbing; never touches a CardFile
            "WriteCard",                          // create-only (this fix) — see WriteCard_RefusesToOverwriteAnExistingCard_SoItCannotDropAComment below for the direct proof
        };

        Assert.Equal(expectedMembers.OrderBy(name => name, StringComparer.Ordinal), actualMembers);
    }

    /// <summary>
    /// The reviewer's exact probe, reproduced as a permanent regression test: a card is created and
    /// then has one comment appended; a second <see cref="CardStore.WriteCard"/> call on the same
    /// path must now be refused — and the original comment must still read back afterwards, proving
    /// the refusal actually prevented the drop rather than merely reporting a failure while the file
    /// underneath still changed. (§4 remediation R3 narrowed <see cref="CardStore.WriteCard"/>'s
    /// input further, to <see cref="NewCardFile"/>, which cannot carry a comment list at all — so
    /// the original probe's shape, a second <c>CardFile</c> with an emptied <c>Comments</c>, is no
    /// longer expressible as a call; create-only refusal is what is left to prove.)
    /// </summary>
    [Fact]
    public void WriteCard_RefusesToOverwriteAnExistingCard_SoItCannotDropAComment()
    {
        var root = Path.Combine(Path.GetTempPath(), "callboard-create-only-tests-" + Guid.NewGuid().ToString("N"));
        const string changeName = "establish-callboard";
        var directory = Path.Combine(root, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "b-0001.md");

        try
        {
            var timestamp = new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
            var frontmatter = new CardFrontmatter(
                "B-0001", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "4", timestamp, timestamp);
            var comment = new CardComment("C-0001", CardOwner.Worker, timestamp, "Do not drop me.", null, null, null, []);

            var created = CardStore.WriteCard(root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), changeName);
            AssertSuccess(created);
            AssertSuccess(CardStore.AppendComment(root, path, comment, TimeSpan.FromSeconds(5), changeName));

            // The reviewer's probe: the same production entry point, same path, a second create
            // attempt over a card that already carries a comment.
            var replacement = new NewCardFile(frontmatter, "Body.");
            var replaceResult = CardStore.WriteCard(root, path, replacement, TimeSpan.FromSeconds(5), changeName);

            AssertFailure(replaceResult);

            var readBack = CardStore.ReadCard(path);
            var card = AssertParseSuccess(readBack);
            Assert.Equal(comment, Assert.Single(card.Comments));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected write success, got failure: {failure.Reason}"));

    private static void AssertFailure(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => throw new Xunit.Sdk.XunitException("expected write failure (create-only refusal), got success"),
            onFailure: static _ => null);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
