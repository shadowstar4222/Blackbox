using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class InstantClipDurationParserTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("2m", 120)]
    [InlineData("1.5m", 90)]
    [InlineData("00:05:00", 300)]
    public void Parse_accepts_presets_and_custom_durations(string value, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), InstantClipDurationParser.Parse(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("0s")]
    [InlineData("31m")]
    public void Parse_rejects_invalid_or_unsafe_durations(string value)
    {
        Assert.ThrowsAny<Exception>(() => InstantClipDurationParser.Parse(value));
    }
}
