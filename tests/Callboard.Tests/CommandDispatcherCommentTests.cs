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
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("comment-not-found", refusal.GetProperty("code").GetString());
        Assert.NotNull(refusal.GetProperty("rule").GetString());
        Assert.NotNull(refusal.GetProperty("remedy").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Refusals);
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
