using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §11 block B, task 11.2 — verifies, as a property rather than a set of spot assertions (§10
/// ruling 2: the coverage gate is the standard here), that no narrative outside the caller's queue
/// reaches <c>context</c>'s response (record-retrieval: "no narrative from cards outside its queue
/// appears"). Each fixture below carries a distinctive marker string nothing else in the response
/// could coincidentally reproduce; the property is that none of those markers ever appear anywhere
/// in the serialised response, checked against the response's raw JSON text rather than against
/// individual fields, so a leak through a field this file did not think to name is still caught.
///
/// <para>
/// <b>Deliberately not tested: an unresolved comment addressed to the caller.</b> Per part 2 of
/// working-context, that pulls the card it lives on into the caller's own queue — it is not an
/// out-of-queue exception, so there is no fixture here asserting such a card's narrative is
/// excluded. It is included by definition; only the top queue item is ever rendered in full, which
/// the non-top-queue-entry fixture below already covers regardless of why a card is queued.
/// </para>
/// </summary>
public sealed class CommandDispatcherContextNarrativeLeakTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Success_NoNarrativeOutsideTheCallersQueue_LeaksIntoTheResponse()
    {
        using var repo = new TempGitRepo();

        const string otherRoleMarker = "MARKER-OTHER-ROLE-7f3a9c";
        const string closedCardMarker = "MARKER-CLOSED-CARD-9c1b4e";
        const string priorSectionMarker = "MARKER-PRIOR-SECTION-2e6d81";
        const string nonTopQueueMarker = "MARKER-NON-TOP-QUEUE-4d8f52";

        // A card owned solely by another role, with no unresolved comment addressed to the caller —
        // never enters worker's queue and is never the top item.
        WriteMarkerBlock(repo, "b-other-role", "B-0001", CardOwner.Reviewer, "briefed", "S-0001", otherRoleMarker, FixedNow);

        // A closed card, owned by worker — excluded from the queue before ownership is even
        // considered (working-context: "SHALL NOT contain closed cards", enforced in
        // WorkingContextAssembler.Build ahead of the ownership check).
        WriteMarkerBlock(repo, "b-closed", "B-0002", CardOwner.Worker, "closed", "S-0001", closedCardMarker, FixedNow);

        // Narrative from a prior, already-closed section — same exclusion, a different section id,
        // so this does not merely repeat the closed-card case above under a different name.
        WriteMarkerBlock(repo, "b-prior-section", "B-0003", CardOwner.Worker, "closed", "S-0000", priorSectionMarker, FixedNow);

        // Two cards owned by worker put two entries in the queue. Queue order is "oldest 'updated'
        // first", so the later-updated one sorts second — its narrative must not reach the
        // response; only the top queue item is ever rendered in full.
        WriteMarkerBlock(repo, "b-top", "B-0004", CardOwner.Worker, "briefed", "S-0001", "the top item's own body — not a marker.", FixedNow);
        WriteMarkerBlock(repo, "b-non-top", "B-0005", CardOwner.Worker, "briefed", "S-0001", nonTopQueueMarker, FixedNow.AddMinutes(1));

        var result = Context(repo, "worker");

        Assert.Equal("B-0004", result.GetProperty("topItem").GetProperty("id").GetString());

        var queue = result.GetProperty("queue").EnumerateArray().ToArray();
        Assert.Equal(["B-0004", "B-0005"], queue.Select(entry => entry.GetProperty("id").GetString()).ToArray());

        // Every queue entry — the top item included — carries identity fields only; a queue entry
        // is never itself the channel a body or a comment reaches the response through.
        foreach (var entry in queue)
        {
            Assert.False(entry.TryGetProperty("body", out _));
            Assert.False(entry.TryGetProperty("comments", out _));
        }

        var raw = result.GetRawText();
        foreach (var marker in new[] { otherRoleMarker, closedCardMarker, priorSectionMarker, nonTopQueueMarker })
        {
            Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
        }
    }

    // §10 remediation S3, re-pinned at the response boundary rather than only on
    // WorkingContextAssembler's own output: the top item's "constraints" is a view of part 1
    // (register), naming the covering rule/hazard cards by id — never by repeating their body, even
    // though part 1 itself (liveRules/liveHazards) legitimately, and unconditionally, carries that
    // body in full. The marker below is therefore expected to appear exactly once in the whole
    // response — inside part 1 — never a second time via the top item's constraints view.
    [Fact]
    public void TopItem_Constraints_NameRegisterCardsByIdOnly_NeverByBody()
    {
        using var repo = new TempGitRepo();

        const string ruleMarker = "MARKER-RULE-BODY-6a2c17";
        WriteRule(repo, "r-0001", "R-0001", "open", ruleMarker);
        WriteMarkerBlock(repo, "b-top", "B-0001", CardOwner.Worker, "briefed", "S-0001", "the top item's own body.", FixedNow);

        var result = Context(repo, "worker");
        var topItem = result.GetProperty("topItem");

        var constraintIds = topItem.GetProperty("constraints").EnumerateArray().Select(entry => entry.GetString()).ToArray();
        Assert.Equal(["R-0001"], constraintIds);
        Assert.All(constraintIds, id => Assert.DoesNotContain(ruleMarker, id!, StringComparison.Ordinal));

        Assert.Equal(1, CountOccurrences(result.GetRawText(), ruleMarker));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void WriteMarkerBlock(
        TempGitRepo repo, string fileStem, string id, CardOwner owner, string status, string section, string marker, DateTimeOffset updated)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, section, FixedNow, updated);
        var comments = new[] { new CardComment("c-1", owner, updated, marker, null, null, null, []) };
        var card = new CardFile(frontmatter, marker, comments, []);
        WriteCard(path, card);
    }

    private static void WriteRule(TempGitRepo repo, string fileStem, string id, string status, string body)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Rule, "A rule", status, CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, body, [], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static JsonElement Context(TempGitRepo repo, string role)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["context", "--role", role], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-context-leak-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            ChangesDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory("establish-callboard").Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(ChangesDirectory);
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
