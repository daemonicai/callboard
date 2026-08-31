using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 14.5-remediation (§14 supervisor finding, both rounds): "the written file's basename equals the
/// minted id" — card-model's own scenario ("its file is named for the identity the system issued")
/// — had never been asserted for <em>any</em> card-minting CLI door, which is exactly why doors ten
/// through fourteen went unnoticed through three block reviews and a first section review. This
/// file is that assertion, run once across every door that mints a card, rather than folded
/// piecemeal into each door's own test file — one parameterised test over the derived door set, per
/// the supervisor's own suggested shape.
///
/// <para>
/// <b>The door set, re-derived from the allocation seam, not the naming seam</b> (the first round's
/// own derivation — grepping <see cref="CardLayout.FileNameFor"/>'s callers — is exactly what the
/// second-round supervisor finding named as the mistake: that seam cannot find a door that mints a
/// card without ever calling <c>FileNameFor</c>, which was true of every one of the four doors the
/// second round found. The reliable seam is every call site of <c>AllocateIdentity</c>/<see
/// cref="CardIdentityAllocator.Allocate"/> in <c>CardStore.cs</c> — allocating an identity is what a
/// card-minting write does that no other write does, independent of whether that call site happens
/// to route through <c>FileNameFor</c> itself or a distinct copy of the same three-step ordering).
/// Grepping that seam in <c>src/Callboard/Cards/CardStore.cs</c> finds exactly five call
/// sites: <see cref="CardStore.CreateCard"/> (nine <c>CommandDispatcher.cs</c> call sites — <c>rule
/// create</c>, <c>hazard create</c>, <c>obligation create</c>, <c>decision create</c>, <c>block
/// create</c>, <c>section create</c>, <c>question create</c>, <c>rule author</c>, <c>rule
/// propose-compact</c>), <see cref="CardStore.RecordFinding"/> (<c>finding record</c>, one or two
/// cards per call), <see cref="CardStore.DispositionNit"/> (<c>nit disposition --disposition
/// defer|decline</c>, one or two cards per call), <see cref="CardStore.PromoteComment"/>
/// (<c>comment promote</c>, always exactly two cards — the raised card and the resolved original,
/// though only the raised one is newly minted), and <see cref="CardStore.
/// RecordSectionVerdictUnderExistingLock"/> (<c>section verdict --finding-new</c>, zero or more
/// block cards, one per manifest). Fourteen doors total — nine from <c>CreateCard</c>, two from
/// <c>RecordFinding</c> (its finding and its optionally-raised card are minted <em>simultaneously</em>
/// by one call, so each is counted as its own door), and one each from <c>DispositionNit</c>,
/// <c>PromoteComment</c> and <c>RecordSectionVerdictUnderExistingLock</c> (each of these mints
/// <em>either/or</em> — one call produces exactly one card, whose kind depends on a caller-chosen
/// flag — so the method is one door regardless of how many kinds it can produce). Doors and theory
/// cases are not the same count precisely because of that either/or shape: this file needs a
/// separate case per kind to exercise both sides of it, so <c>DispositionNit</c>'s one door becomes
/// two cases (<c>defer</c>, <c>decline</c>) and <c>PromoteComment</c>'s one door becomes two more
/// (<c>question</c>, <c>decision</c>), while <c>RecordFinding</c>'s two simultaneous doors are
/// exercised together in a single case (one <c>finding record --blind-spot obligation</c> call
/// yields both tuples at once). Fifteen theory cases below: nine for <c>CreateCard</c>, one for
/// <c>RecordFinding</c>, two for <c>DispositionNit</c>, two for <c>PromoteComment</c>, one for
/// <c>RecordSectionVerdictUnderExistingLock</c>.
/// </para>
///
/// <para>
/// <b>Not self-extending — stated plainly rather than claimed otherwise.</b> Nothing in the product
/// assembly enumerates "every CLI verb that mints a card": <c>CommandDispatcher</c>'s own routing is
/// a closed-union visitor over <c>ParsedCommand</c>, and NativeAOT/ADR-0002 rules out driving this
/// theory from reflection over that assembly the way <c>RefusalCoverageGateTests</c> reflects over
/// <c>ICardRefusalReason</c> implementors in the test project's own build — there is no comparable
/// marker interface a card-minting outcome case carries, and adding one only to serve this one gate
/// would be instrumentation for the test, not a property the product needs. A fifteenth door added
/// without a new theory case here fails silently, the same way this gate itself was silently
/// incomplete for two supervisor rounds. The mitigation is procedural, not mechanical: re-derive the
/// <c>AllocateIdentity</c> seam (a single grep) whenever a new card-minting verb is added, and add
/// its case here — recorded as a standing instruction, not solved by this file.
/// </para>
/// </summary>
public sealed class CardMintedFileBasenameTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(MintingDoors))]
    public void MintedFile_BasenameEqualsTheMintedId(string label, Func<TempGitRepo, IReadOnlyList<(string Id, string FilePath)>> mint)
    {
        using var repo = new TempGitRepo();
        var minted = mint(repo);

        Assert.NotEmpty(minted);
        foreach (var (id, filePath) in minted)
        {
            Assert.True(
                File.Exists(filePath),
                $"[{label}] expected a card to exist at '{filePath}' for id '{id}', but it does not.");
            Assert.Equal(
                CardLayout.FileNameFor(id), Path.GetFileName(filePath));
        }
    }

    public static IEnumerable<object[]> MintingDoors()
    {
        yield return new object[]
        {
            "rule create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo => [MintOne(repo,
                ["rule", "create", "--title", "A rule", "--role", "architect", "--scope", "repository"])]),
        };

        yield return new object[]
        {
            "hazard create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo => [MintOne(repo,
                ["hazard", "create", "--title", "A hazard", "--role", "worker", "--condition", "The key rotates", "--cadence", "monthly"])]),
        };

        yield return new object[]
        {
            "obligation create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var sectionId = MintOne(repo, ["section", "create", "--title", "Section", "--role", "architect", "--change", ChangeName]).Id;
                return [MintOne(repo,
                    ["obligation", "create", "--title", "An obligation", "--role", "architect", "--change", ChangeName, "--section", sectionId])];
            }),
        };

        yield return new object[]
        {
            "decision create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo => [MintOne(repo,
                ["decision", "create", "--title", "A decision", "--role", "product-owner"])]),
        };

        yield return new object[]
        {
            "block create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo => [MintOne(repo,
                ["block", "create", "--title", "A block", "--role", "architect", "--change", ChangeName, "--task", "14.1"])]),
        };

        yield return new object[]
        {
            "section create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo => [MintOne(repo,
                ["section", "create", "--title", "A section", "--role", "architect", "--change", ChangeName])]),
        };

        yield return new object[]
        {
            "question create",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo => [MintOne(repo,
                ["question", "create", "--title", "A question", "--role", "worker", "--owed-by", "product-owner"])]),
        };

        yield return new object[]
        {
            "rule author",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var sectionId = MintOne(repo, ["section", "create", "--title", "Section", "--role", "architect", "--change", ChangeName]).Id;
                var findingId = MintOne(repo,
                    ["finding", "record", "--role", "worker", "--title", "An incident", "--section", sectionId, "--change", ChangeName, "--blind-spot", "none"]).Id;
                return [MintOne(repo,
                    ["rule", "author", "--title", "A rule earned from findings", "--role", "architect", "--scope", "repository", "--earned-from", findingId])];
            }),
        };

        // rule propose-compact's success result carries the minted proposal's id/path under
        // proposalId/proposalFilePath, not id/filePath — a different DTO shape (it never resolves
        // its own outcome through the same "created" case CreateCard's other eight callers share),
        // so this case reads those two fields directly rather than through the shared MintOne.
        yield return new object[]
        {
            "rule propose-compact",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var firstId = MintOne(repo, ["rule", "create", "--title", "First rule", "--role", "architect", "--scope", "repository"]).Id;
                var secondId = MintOne(repo, ["rule", "create", "--title", "Second rule", "--role", "architect", "--scope", "repository"]).Id;

                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    ["rule", "propose-compact", "--absorbs", $"{firstId},{secondId}", "--role", "worker"],
                    output, new StringReader("Candidate text."), TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"rule propose-compact failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var result = doc.RootElement.GetProperty("result");
                return [(result.GetProperty("proposalId").GetString()!, result.GetProperty("proposalFilePath").GetString()!)];
            }),
        };

        // finding record: the door this remediation closes, both cards it can mint in one call.
        yield return new object[]
        {
            "finding record (finding + raised obligation)",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var sectionId = MintOne(repo, ["section", "create", "--title", "Section", "--role", "architect", "--change", ChangeName]).Id;
                var bodyFile = Path.Combine(repo.Path, "blind-spot-body.txt");
                File.WriteAllText(bodyFile, "The instrument does not cover generated code.");

                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    [
                        "finding", "record", "--role", "worker", "--title", "Checked, with a gap",
                        "--section", sectionId, "--change", ChangeName, "--blind-spot", "obligation",
                        "--blind-spot-title", "A blind spot", "--blind-spot-body-file", bodyFile,
                    ],
                    output, new StringReader("Body of the finding."), TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"finding record failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var result = doc.RootElement.GetProperty("result");
                return
                [
                    (result.GetProperty("id").GetString()!, result.GetProperty("filePath").GetString()!),
                    (result.GetProperty("raisedCardId").GetString()!, result.GetProperty("raisedCardFilePath").GetString()!),
                ];
            }),
        };

        // Second round (§14 supervisor finding): nit disposition --disposition defer, raising an
        // obligation. The block card itself is hand-written, not minted through this call — only
        // the raised card is new.
        yield return new object[]
        {
            "nit disposition --disposition defer (raises obligation)",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var blockPath = WriteBlockCardWithNit(repo, "B-0001", "nit-1");
                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    [
                        "nit", "disposition", "--id", "nit-1", "--role", "architect", "--disposition", "defer",
                        "--title", "Address later", "--change", ChangeName,
                    ],
                    output, new StringReader("Discharge this."), TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"nit disposition (defer) failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var result = doc.RootElement.GetProperty("result");
                return [(result.GetProperty("raisedCardId").GetString()!, result.GetProperty("raisedCardFilePath").GetString()!)];
            }),
        };

        // Second round: nit disposition --disposition decline, raising a decision — the sibling
        // kind DispositionNit's own raise request can name.
        yield return new object[]
        {
            "nit disposition --disposition decline (raises decision)",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var blockPath = WriteBlockCardWithNit(repo, "B-0001", "nit-1");
                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    [
                        "nit", "disposition", "--id", "nit-1", "--role", "architect", "--disposition", "decline",
                        "--title", "Code is right as it stands", "--change", ChangeName,
                    ],
                    output, new StringReader("The pattern is deliberate."), TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"nit disposition (decline) failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var result = doc.RootElement.GetProperty("result");
                return [(result.GetProperty("raisedCardId").GetString()!, result.GetProperty("raisedCardFilePath").GetString()!)];
            }),
        };

        // Second round: comment promote --to question. The original card and its comment are
        // hand-written; only the raised question is new.
        yield return new object[]
        {
            "comment promote --to question",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var (cardId, commentId) = WriteBlockCardWithComment(repo, "B-0001", "thread-1", CardOwner.Architect);
                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    [
                        "comment", "promote", "--id", cardId, "--comment-id", commentId, "--role", "architect", "--to", "question",
                        "--title", "Should we ship X?", "--owed-by", "product-owner", "--change", ChangeName,
                    ],
                    output, new StringReader("Raised while resolving a thread."), TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"comment promote (question) failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var result = doc.RootElement.GetProperty("result");
                return [(result.GetProperty("raisedCardId").GetString()!, result.GetProperty("raisedCardFilePath").GetString()!)];
            }),
        };

        // Second round: comment promote --to decision — the sibling kind, no --owed-by needed.
        yield return new object[]
        {
            "comment promote --to decision",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var (cardId, commentId) = WriteBlockCardWithComment(repo, "B-0001", "thread-1", CardOwner.Architect);
                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    [
                        "comment", "promote", "--id", cardId, "--comment-id", commentId, "--role", "architect", "--to", "decision",
                        "--title", "Ship X now.", "--change", ChangeName,
                    ],
                    output, new StringReader("Raised while resolving a thread."), TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"comment promote (decision) failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var result = doc.RootElement.GetProperty("result");
                return [(result.GetProperty("raisedCardId").GetString()!, result.GetProperty("raisedCardFilePath").GetString()!)];
            }),
        };

        // Second round: section verdict --finding-new, minting a brand-new remediation block card
        // from a manifest. section verdict's own response reports only the id (newCardIds), never a
        // path — this case derives the expected path the same way the manifest itself no longer
        // can, via CardLayout directly, rather than reading one off the response.
        yield return new object[]
        {
            "section verdict --finding-new (mints a block)",
            (Func<TempGitRepo, IReadOnlyList<(string, string)>>)(repo =>
            {
                var sectionPath = WriteSectionCard(repo, "S-0001");
                var manifestPath = Path.Combine(repo.Path, "manifest.txt");
                File.WriteAllText(manifestPath, "---\nkey: finding-x001\ntitle: Fix the X defect\n---\nThe reviewer nit about X was not addressed.");

                var output = new StringWriter();
                var exitCode = CommandDispatcher.Run(
                    [
                        "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                        "--role", "supervisor", "--change", ChangeName, "--finding-new", manifestPath,
                    ],
                    output, TextReader.Null, TextWriter.Null,
                    isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
                Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"section verdict --finding-new failed: {output}");

                using var doc = JsonDocument.Parse(output.ToString());
                var newCardId = doc.RootElement.GetProperty("result").GetProperty("newCardIds").EnumerateArray().Single().GetString()!;
                var newCardPath = Path.Combine(
                    repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), CardLayout.FileNameFor(newCardId));
                return [(newCardId, newCardPath)];
            }),
        };
    }

    // Hand-writes a block card in 'in-review' carrying one live, unrequired, undispositioned nit —
    // the precondition 'nit disposition --disposition defer|decline' needs; the block itself is not
    // minted through the CLI door under test.
    private static string WriteBlockCardWithNit(TempGitRepo repo, string blockId, string nitId)
    {
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CardLayout.FileNameFor(blockId));
        var frontmatter = new CardFrontmatter(
            blockId, CardKind.Block, "A block", "in-review", CardOwner.Worker, CardScope.Change, "S-0001", FixedNow, FixedNow);
        var nit = new CardComment(nitId, CardOwner.Reviewer, FixedNow, "Fix this.", null, CardOwner.Architect, null, [], IsNit: true);
        var card = new CardFile(frontmatter, "Body.", [nit], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // Hand-writes a block card carrying one live, unresolved comment thread addressed to
    // addressedTo — the precondition 'comment promote' needs. Returns (cardId, commentId).
    private static (string CardId, string CommentId) WriteBlockCardWithComment(TempGitRepo repo, string blockId, string commentId, CardOwner addressedTo)
    {
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CardLayout.FileNameFor(blockId));
        var frontmatter = new CardFrontmatter(
            blockId, CardKind.Block, "A block", "in-review", CardOwner.Worker, CardScope.Change, "S-0001", FixedNow, FixedNow);
        var comment = new CardComment(commentId, CardOwner.Architect, FixedNow, "Original comment.", null, addressedTo, null, []);
        var card = new CardFile(frontmatter, "Body.", [comment], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (blockId, commentId);
    }

    // Hand-writes a section card — the precondition 'section verdict' needs.
    private static string WriteSectionCard(TempGitRepo repo, string sectionId)
    {
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CardLayout.FileNameFor(sectionId));
        var frontmatter = new CardFrontmatter(
            sectionId, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static (string Id, string FilePath) MintOne(TempGitRepo repo, string[] args)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            args, output, new StringReader("Body."), TextWriter.Null,
            isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"'{string.Join(' ', args)}' failed: {output}");

        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        return (result.GetProperty("id").GetString()!, result.GetProperty("filePath").GetString()!);
    }

    public sealed class TempGitRepo : IDisposable
    {
        public string Path { get; }

        public TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-minted-basename-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
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
