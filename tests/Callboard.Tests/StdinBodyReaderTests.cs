using System.Reflection;
using Callboard.Cli;

namespace Callboard.Tests;

public sealed class StdinBodyReaderTests
{
    [Fact]
    public void ReadBody_ReturnsStdinContentVerbatim()
    {
        const string body = "line one\nline two with \"quotes\" and 'apostrophes'\n\nline four after a blank line\n";

        var result = ReadRedirectedBody(body);

        Assert.Equal(body, result);
    }

    [Fact]
    public void ReadBody_PreservesShellMetacharactersUnescaped()
    {
        const string body = "$(rm -rf /) `backticks` && echo pwned; > redirect | pipe";

        var result = ReadRedirectedBody(body);

        Assert.Equal(body, result);
    }

    [Fact]
    public void ReadBody_EmptyStdinReturnsEmptyString()
    {
        var result = ReadRedirectedBody(string.Empty);

        Assert.Equal(string.Empty, result);
    }

    // §3 obligation 4: the guard is a precondition of obtaining a reader at all, not a call a
    // body-reading handler is merely supposed to remember to make first.
    [Fact]
    public void TryCreate_WhenStdinIsNotRedirected_RefusesAndProducesNoUsableReader()
    {
        var refusal = StdinBodyReader.RedirectedStdin.TryCreate(TextReader.Null, isInputRedirected: false, out _);

        Assert.NotNull(refusal);
        Assert.Equal("stdin-not-redirected", refusal!.Code);
    }

    [Fact]
    public void TryCreate_WhenStdinIsRedirected_YieldsAReaderThatReadsTheBody()
    {
        using var input = new StringReader("hello");

        var refusal = StdinBodyReader.RedirectedStdin.TryCreate(input, isInputRedirected: true, out var stdin);

        Assert.Null(refusal);
        Assert.NotNull(stdin);
        Assert.Equal("hello", StdinBodyReader.ReadBody(stdin));
    }

    // Proves the guard "cannot be skipped" structurally, not merely that it works when called:
    // there is no overload of ReadBody that takes a raw TextReader, so a caller has nothing to
    // pass without having gone through TryCreate first.
    [Fact]
    public void ReadBody_HasNoOverloadAcceptingARawTextReader()
    {
        var overloads = typeof(StdinBodyReader)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(static method => method.Name == nameof(StdinBodyReader.ReadBody))
            .ToList();

        var overload = Assert.Single(overloads);
        var parameter = Assert.Single(overload.GetParameters());
        Assert.Equal(typeof(StdinBodyReader.RedirectedStdin), parameter.ParameterType);
    }

    // And the other half: RedirectedStdin itself cannot be constructed except through TryCreate —
    // every constructor Reflection can find is non-public.
    [Fact]
    public void RedirectedStdin_HasNoPublicConstructor()
    {
        var constructors = typeof(StdinBodyReader.RedirectedStdin)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.All(constructors, static ctor => Assert.False(ctor.IsPublic));
    }

    private static string ReadRedirectedBody(string body)
    {
        using var input = new StringReader(body);
        var refusal = StdinBodyReader.RedirectedStdin.TryCreate(input, isInputRedirected: true, out var stdin);

        Assert.Null(refusal);
        Assert.NotNull(stdin);
        return StdinBodyReader.ReadBody(stdin);
    }
}
