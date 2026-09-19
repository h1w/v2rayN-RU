using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class RegexGuardTests
{
    [Fact]
    public void IsRegexMatch_ValidMatch_ReturnsTrue()
    {
        var result = Utils.IsRegexMatch("hello world", "world");
        Assert.True(result);
    }

    [Fact]
    public void IsRegexMatch_ValidNonMatch_ReturnsFalse()
    {
        var result = Utils.IsRegexMatch("hello world", "earth");
        Assert.False(result);
    }

    [Fact]
    public void IsRegexMatch_EmptyPattern_ReturnsTrue()
    {
        var result = Utils.IsRegexMatch("hello world", "");
        Assert.True(result);
    }

    [Fact]
    public void IsRegexMatch_NullPattern_ReturnsTrue()
    {
        var result = Utils.IsRegexMatch("hello world", null);
        Assert.True(result);
    }

    [Fact]
    public void IsRegexMatch_EmptyInput_ReturnsFalse()
    {
        var result = Utils.IsRegexMatch("", "pattern");
        Assert.False(result);
    }

    [Fact]
    public void IsRegexMatch_InvalidPattern_FailsOpenReturnsTrue()
    {
        // Unclosed parenthesis is invalid regex
        var result = Utils.IsRegexMatch("text", "(abc[");
        Assert.True(result);
    }

    [Fact]
    public void IsRegexMatch_ReDoSPattern_TimesOutAndReturnsTrue()
    {
        // Pathological regex: (a+)+$ against 'aaaaaaaaaaaaaaaaaaaaaaaaaaaa!'
        var evilPattern = @"^(a+)+$";
        var evilInput = new string('a', 30) + "!";
        
        var sw = Stopwatch.StartNew();
        var result = Utils.IsRegexMatch(evilInput, evilPattern, timeoutSeconds: 1);
        sw.Stop();

        Assert.True(result);
        Assert.True(sw.Elapsed.TotalSeconds < 5);
    }
}
