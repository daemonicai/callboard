using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §9 remediation, round two (S4) — the CLI doors for <c>comment resolve</c>, <c>comment promote
/// --to question|decision</c> and <c>comment decline --reason</c>, so that every disposition
/// <c>9.3</c>/<c>9.6</c> name is a command a caller can actually run through the same
/// <see cref="CommandDispatcher.Run"/> surface every other verb uses.
/// </summary>
public sealed class CommandDispatcherCommentTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CommentResolve_LiveAddressedThread_Succeeds_AndResolvesIt()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0001", "B-0001", "thread-1", CardOwner.Reviewer);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "resolve", "--id", id, "--comment-id", "thread-1", "--role", "reviewer", "--change", "establish-callboard"],
            output, repo.Path, "Fixed.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(id, result.GetProperty("cardId").GetString());
        Assert.Equal("thread-1", result.GetProperty("commentId").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(CardCommentRouting.IsResolved(read.Comments, 0));
    }

    [Fact]
    public void CommentDecline_WithReason_Succeeds_AndResolvesIt()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0002", "B-0002", "thread-1", CardOwner.Architect);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "decline", "--id", id, "--comment-id", "thread-1", "--role", "architect", "--reason", "out of scope.", "--change", "establish-callboard"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(CardCommentRouting.IsResolved(read.Comments, 0));
        Assert.Equal("out of scope.", read.Comments[1].Body);
    }

    [Fact]
    public void CommentResolve_NoBody_Refuses_AtTheDoor_WithoutTouchingTheCard()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0008", "B-0008", "thread-1", CardOwner.Reviewer);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "resolve", "--id", id, "--comment-id", "thread-1", "--role", "reviewer", "--change", "establish-callboard"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CommentResolve_RoleNeitherAddresseeNorCardOwner_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0009", "B-0009", "thread-1", CardOwner.Reviewer);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "resolve", "--id", id, "--comment-id", "thread-1", "--role", "architect", "--change", "establish-callboard"],
            output, repo.Path, "Fixed.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.NotNull(refusal.GetProperty("rule").GetString());
        Assert.NotNull(refusal.GetProperty("remedy").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.False(CardCommentRouting.IsResolved(read.Comments, 0));
        Assert.Single(read.Refusals);
    }

    [Fact]
    public void CommentDecline_RoleNeitherAddresseeNorCardOwner_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0010", "B-0010", "thread-1", CardOwner.Architect);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "decline", "--id", id, "--comment-id", "thread-1", "--role", "reviewer", "--reason", "out of scope.", "--change", "establish-callboard"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("role-not-permitted", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.False(CardCommentRouting.IsResolved(read.Comments, 0));
    }

    [Fact]
    public void CommentPromote_RoleNeitherAddresseeNorCardOwner_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0011", "B-0011", "thread-1", CardOwner.Reviewer);
        var raisedPath = Path.Combine(repo.RegisterDirectory, "q-9997.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "promote", "--id", id, "--comment-id", "thread-1", "--role", "architect", "--to", "question",
                "--raise", raisedPath, "--title", "A question.", "--owed-by", "product-owner", "--change", "establish-callboard"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("role-not-permitted", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(File.Exists(raisedPath));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.False(CardCommentRouting.IsResolved(read.Comments, 0));
    }

    [Fact]
    public void CommentDecline_NoReason_Refuses_AtTheDoor_WithoutTouchingTheCard()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0003", "B-0003", "thread-1", CardOwner.Architect);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "decline", "--id", id, "--comment-id", "thread-1", "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CommentPromote_MissingTo_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var (_, id) = WriteCardWithComment(repo, "b-0004", "B-0004", "thread-1", CardOwner.Reviewer);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "promote", "--id", id, "--comment-id", "thread-1", "--role", "reviewer",
                "--raise", Path.Combine(repo.RegisterDirectory, "q-9999.md"), "--title", "A question."],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void CommentPromote_ToQuestion_MissingOwedBy_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var (_, id) = WriteCardWithComment(repo, "b-0005", "B-0005", "thread-1", CardOwner.Reviewer);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "promote", "--id", id, "--comment-id", "thread-1", "--role", "reviewer", "--to", "question",
                "--raise", Path.Combine(repo.RegisterDirectory, "q-9998.md"), "--title", "A question."],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void CommentPromote_ToQuestion_Succeeds_WritesTheQuestionCard_AndResolvesTheThread()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0006", "B-0006", "thread-1", CardOwner.Reviewer);
        var raisedPath = Path.Combine(repo.RegisterDirectory, "q-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "promote", "--id", id, "--comment-id", "thread-1", "--role", "reviewer", "--to", "question",
                "--raise", raisedPath, "--title", "Should we ship X?", "--owed-by", "product-owner", "--change", "establish-callboard"],
            output, repo.Path, "Raised while resolving a thread.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(raisedPath, result.GetProperty("raisedCardFilePath").GetString());
        Assert.Equal("question", result.GetProperty("raisedCardKind").GetString());

        Assert.True(File.Exists(raisedPath));
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(CardCommentRouting.IsResolved(read.Comments, 0));
    }

    [Fact]
    public void CommentResolve_CommentDoesNotExist_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0007", "B-0007", "thread-1", CardOwner.Reviewer);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "resolve", "--id", id, "--comment-id", "no-such-thread", "--role", "reviewer", "--change", "establish-callboard"],
            output, repo.Path, "Fixed.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("comment-not-found", refusal.GetProperty("code").GetString());
        Assert.NotNull(refusal.GetProperty("rule").GetString());
        Assert.NotNull(refusal.GetProperty("remedy").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Refusals);
    }

    [Fact]
    public void CommentAdd_Unaddressed_Succeeds_AndReturnsTheMintedCommentId()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteBlockCard(repo, "b-0020", "B-0020");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "worker", "--change", "establish-callboard"],
            output, repo.Path, "A note on the record.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(id, result.GetProperty("cardId").GetString());
        Assert.Equal("worker", result.GetProperty("actingRole").GetString());
        Assert.False(result.TryGetProperty("to", out _));
        var commentId = result.GetProperty("commentId").GetString();
        Assert.NotNull(commentId);
        Assert.StartsWith("comment-", commentId, StringComparison.Ordinal);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var added = Assert.Single(read.Comments);
        Assert.Equal(commentId, added.Id);
        Assert.Equal("A note on the record.", added.Body);
        Assert.Null(added.To);
        Assert.Null(added.ReplyTo);

        // Architect ruling item 2: comment add SHALL NOT be able to mint a nit.
        Assert.False(added.IsNit);
        Assert.False(added.Required);
        Assert.Empty(added.Sites);
        Assert.Null(added.Disposition);
    }

    [Fact]
    public void CommentAdd_Addressed_Succeeds_AndSetsTo()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteBlockCard(repo, "b-0021", "B-0021");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "worker", "--to", "architect", "--change", "establish-callboard"],
            output, repo.Path, "Please look at this.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("architect", doc.RootElement.GetProperty("result").GetProperty("to").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Architect, Assert.Single(read.Comments).To);
    }

    // Architect ruling item 6: addressing yourself is allowed — no refusal.
    [Fact]
    public void CommentAdd_AddressedToSelf_Succeeds()
    {
        using var repo = new TempGitRepo();
        var (_, id) = WriteBlockCard(repo, "b-0022", "B-0022");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "worker", "--to", "worker", "--change", "establish-callboard"],
            output, repo.Path, "Noting this for myself.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    [Fact]
    public void CommentAdd_ReplyToAnExistingComment_Succeeds()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteCardWithComment(repo, "b-0023", "B-0023", "thread-1", CardOwner.Architect);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "architect", "--reply-to", "thread-1", "--change", "establish-callboard"],
            output, repo.Path, "Following up on this.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("thread-1", doc.RootElement.GetProperty("result").GetProperty("replyTo").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.Comments.Count);
        Assert.Equal("thread-1", read.Comments[1].ReplyTo);
    }

    // Architect ruling item 4: '--reply-to' naming a comment not in that card's thread is a
    // refusal, not a silently-dropped field — and it records.
    [Fact]
    public void CommentAdd_ReplyToACommentNotOnThisCard_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteBlockCard(repo, "b-0024", "B-0024");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "worker", "--reply-to", "no-such-comment", "--change", "establish-callboard"],
            output, repo.Path, "Following up.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("reply-to-not-found", refusal.GetProperty("code").GetString());
        Assert.Contains("no-such-comment", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.NotNull(refusal.GetProperty("rule").GetString());
        Assert.NotNull(refusal.GetProperty("remedy").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, recorded.By);
        Assert.Equal(refusal.GetProperty("rule").GetString(), recorded.Rule);
        Assert.Equal(refusal.GetProperty("remedy").GetString(), recorded.Remedy);
    }

    [Fact]
    public void CommentAdd_NoBody_Refuses_AtTheDoor_WithoutTouchingTheCard()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteBlockCard(repo, "b-0025", "B-0025");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "worker", "--change", "establish-callboard"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CommentAdd_MissingId_Refuses_WithMissingArgument()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--role", "worker", "--change", "establish-callboard"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Architect ruling item 3: any card kind accepts a comment — proven against a question card,
    // not a block, to show the resolution is not kind-filtered.
    [Fact]
    public void CommentAdd_OnAQuestionCard_Succeeds()
    {
        using var repo = new TempGitRepo();
        var (path, id) = WriteQuestionCard(repo, "q-0001", "Q-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["comment", "add", "--id", id, "--role", "architect", "--change", "establish-callboard"],
            output, repo.Path, "A note on a question card.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Comments);
    }

    private static (string Path, string Id) WriteBlockCard(TempGitRepo repo, string fileStem, string id)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block card", "in-review", CardOwner.Worker, CardScope.Change, "S-0001", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (path, id);
    }

    private static (string Path, string Id) WriteQuestionCard(TempGitRepo repo, string fileStem, string id)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (path, id);
    }

    private static (string Path, string Id) WriteCardWithComment(TempGitRepo repo, string fileStem, string id, string commentId, CardOwner addressedTo)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block card", "in-review", CardOwner.Worker, CardScope.Change, "S-0001", FixedNow, FixedNow);
        var comment = new CardComment(
            Id: commentId, Author: CardOwner.Architect, Timestamp: FixedNow, Body: "Original comment.",
            ReplyTo: null, To: addressedTo, Resolves: null, UnknownHeaderFields: []);
        var card = new CardFile(frontmatter, "Body.", [comment], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (path, id);
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-comment-cli-tests-" + Guid.NewGuid().ToString("N"));
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
