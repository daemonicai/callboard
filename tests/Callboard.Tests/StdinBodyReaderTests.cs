using Callboard.Cli;

namespace Callboard.Tests;

public sealed class StdinBodyReaderTests
{
    [Fact]
    public void ReadBody_ReturnsStdinContentVerbatim()
    {
        const string body = "line one\nline two with \"quotes\" and 'apostrophes'\n\nline four after a blank line\n";
        using var reader = new StringReader(body);

        var result = StdinBodyReader.ReadBody(reader);

        Assert.Equal(body, result);
    }

    [Fact]
    public void ReadBody_PreservesShellMetacharactersUnescaped()
    {
        const string body = "$(rm -rf /) `backticks` && echo pwned; > redirect | pipe";
        using var reader = new StringReader(body);

        var result = StdinBodyReader.ReadBody(reader);

        Assert.Equal(body, result);
    }

    [Fact]
    public void ReadBody_EmptyStdinReturnsEmptyString()
    {
        using var reader = new StringReader(string.Empty);

        var result = StdinBodyReader.ReadBody(reader);

        Assert.Equal(string.Empty, result);
    }
}
