using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §9 block A3 — the card-addressed refusal cases <see cref="CardNitRaiseOutcome"/> and
/// <see cref="CardNitDispositionOutcome"/> gained onto the refusal reporting format, exercised
/// directly against <see cref="CardStore.RaiseNit"/>/<see cref="CardStore.DispositionNit"/> rather
/// than through the CLI, so a case reachable only by racing the CLI's own pre-lock resolution
/// (<see cref="CardNitDispositionOutcome.NitNotFound"/>) is still provable.
/// </summary>
public sealed class CardNitStoreTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-nit-store-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardNitStoreTests()
    {
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void RaiseNit_TargetIsNotABlockCard_Refuses_AndRecordsAgainstTheCard()
    {
        var path = WriteQuestionCard("q-0001", "Q-0001");
        var comment = new CardComment(
            Id: "nit-0001", Author: CardOwner.Reviewer, Timestamp: Created, Body: "An observation.",
            ReplyTo: null, To: CardOwner.Architect, Resolves: null, UnknownHeaderFields: [], IsNit: true);

        var outcome = CardStore.RaiseNit(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var notABlock = Assert.IsType<CardNitRaiseOutcome.NotABlockCard>(outcome);
        Assert.Equal(CardKind.Question, notABlock.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, refusal.By);
        Assert.Equal(Created, refusal.Timestamp);
        Assert.Equal(notABlock.RefusingRule, refusal.Rule);
        Assert.Equal(notABlock.Remedy, refusal.Remedy);
    }

    [Fact]
    public void DispositionNit_TargetIsNotABlockCard_Refuses_AndRecordsAgainstTheCard()
    {
        var path = WriteQuestionCard("q-0002", "Q-0002");

        var outcome = CardStore.DispositionNit(
            _root, path, "nit-does-not-matter", NitDisposition.FixBeforeLand, "reason", CardOwner.Architect, Created,
            TimeSpan.FromSeconds(5), ChangeName, raiseRequest: null);

        var notABlock = Assert.IsType<CardNitDispositionOutcome.NotABlockCard>(outcome);
        Assert.Equal(CardKind.Question, notABlock.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(notABlock.RefusingRule, refusal.Rule);
        Assert.Equal(notABlock.Remedy, refusal.Remedy);
    }

    // NitResolver already refuses an unknown id at the CLI boundary, before CardStore.DispositionNit
    // is ever called — CardNitDispositionOutcome.NitNotFound is the under-lock recheck for the
    // race NitResolver itself cannot close (found before the lock was acquired), so it is only
    // provable by calling CardStore.DispositionNit directly with an id no comment on the card
    // carries.
    [Fact]
    public void DispositionNit_NoLiveNitCarriesTheId_Refuses_AndRecordsAgainstTheCard()
    {
        var path = WriteBlockCard("b-0001", "B-0001", BlockFlowState.InReview);

        var outcome = CardStore.DispositionNit(
            _root, path, "nit-does-not-exist", NitDisposition.FixBeforeLand, "reason", CardOwner.Architect, Created,
            TimeSpan.FromSeconds(5), ChangeName, raiseRequest: null);

        var nitNotFound = Assert.IsType<CardNitDispositionOutcome.NitNotFound>(outcome);
        Assert.Equal("nit-does-not-exist", nitNotFound.NitId);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(nitNotFound.RefusingRule, refusal.Rule);
        Assert.Equal(nitNotFound.Remedy, refusal.Remedy);
    }

    [Fact]
    public void DispositionNit_AlreadyDispositioned_Refuses_AndRecordsAgainstTheCard()
    {
        var nit = new CardComment(
            Id: "nit-0002", Author: CardOwner.Reviewer, Timestamp: Created, Body: "Fix this.",
            ReplyTo: null, To: CardOwner.Architect, Resolves: null, UnknownHeaderFields: [], IsNit: true);
        var disposition = new CardComment(
            Id: "disposition-0001", Author: CardOwner.Architect, Timestamp: Created.AddMinutes(1), Body: "Fixed.",
            ReplyTo: "nit-0002", To: null, Resolves: "nit-0002", UnknownHeaderFields: [], Disposition: NitDisposition.FixBeforeLand);
        var path = WriteBlockCard("b-0002", "B-0002", BlockFlowState.Briefed, round: 1, comments: [nit, disposition]);

        var outcome = CardStore.DispositionNit(
            _root, path, "nit-0002", NitDisposition.Decline, "second attempt", CardOwner.Architect, Created.AddHours(1),
            TimeSpan.FromSeconds(5), ChangeName, raiseRequest: null);

        var alreadyDispositioned = Assert.IsType<CardNitDispositionOutcome.AlreadyDispositioned>(outcome);
        Assert.Equal("nit-0002", alreadyDispositioned.NitId);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.Comments.Count);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(Created.AddHours(1), refusal.Timestamp);
        Assert.Equal(alreadyDispositioned.RefusingRule, refusal.Rule);
        Assert.Equal(alreadyDispositioned.Remedy, refusal.Remedy);
    }

    // review-certification: raising the obligation/decision defer/decline promotes is a two-write,
    // all-or-nothing operation — a collision on the raised card's own target path refuses the
    // whole disposition, recorded against the block card the disposition targets (the raised
    // card's own path is never read or parsed, only File.Exists-checked, so there is nothing
    // resolved there to record against — the same reasoning CardRulePromoteOutcome.
    // TargetAlreadyExists already applies).
    [Fact]
    public void DispositionNit_RaisedCardTargetAlreadyExists_Refuses_AndRecordsAgainstTheBlockCard()
    {
        var nit = new CardComment(
            Id: "nit-0003", Author: CardOwner.Reviewer, Timestamp: Created, Body: "Fix this.",
            ReplyTo: null, To: CardOwner.Architect, Resolves: null, UnknownHeaderFields: [], IsNit: true);
        var path = WriteBlockCard("b-0003", "B-0003", BlockFlowState.InReview, comments: [nit]);

        var raisedPath = Path.Combine(_directory, "o-0001.md");
        File.WriteAllText(raisedPath, "an unrelated file already occupies this path");
        var raiseRequest = new NitDispositionRaiseRequest(CardKind.Obligation, raisedPath, "Address later", "Discharge this.");

        var outcome = CardStore.DispositionNit(
            _root, path, "nit-0003", NitDisposition.Defer, "Deferring this.", CardOwner.Architect, Created.AddHours(1),
            TimeSpan.FromSeconds(5), ChangeName, raiseRequest);

        var raisedAlreadyExists = Assert.IsType<CardNitDispositionOutcome.RaisedCardAlreadyExists>(outcome);
        Assert.Equal(raisedPath, raisedAlreadyExists.FilePath);

        // The unrelated file is untouched, and the disposition never lands on the block card either
        // — all-or-nothing.
        Assert.Equal("an unrelated file already occupies this path", File.ReadAllText(raisedPath));
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Comments);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(Created.AddHours(1), refusal.Timestamp);
        Assert.Equal(raisedAlreadyExists.RefusingRule, refusal.Rule);
        Assert.Equal(raisedAlreadyExists.Remedy, refusal.Remedy);
    }

    private string WriteQuestionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "9", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteBlockCard(
        string fileStem, string id, BlockFlowState status, int round = 1, IReadOnlyList<CardComment>? comments = null)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "9", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: null, Tasks: [], Round: round, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", comments ?? [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
