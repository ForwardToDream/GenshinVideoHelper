using GenshinVideoHelper.Core.Progress;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Progress;

public sealed class TimeTextTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(75.9, "01:15")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-4, "00:00")]
    [InlineData(double.NaN, "00:00")]
    public void Format_UsesMinutesUntilAnHour(double seconds, string expected) => Assert.Equal(expected, TimeText.Format(seconds));

    [Theory]
    [InlineData("95", 95)]
    [InlineData("1:35", 95)]
    [InlineData(" 1：35 ", 95)]
    [InlineData("1:02:03", 3723)]
    [InlineData("90:00", 5400)]
    [InlineData("0:07.5", 7.5)]
    public void TryParse_AcceptsSecondsAndClockForms(string text, double expected)
    {
        Assert.True(TimeText.TryParse(text, out var seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("1::5")]
    public void TryParse_RejectsAnythingElse(string? text) => Assert.False(TimeText.TryParse(text, out _));
}
