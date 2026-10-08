using GenshinVideoHelper.Core.Models;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Models;

public sealed class VideoIdentityTests
{
    [Theory]
    [InlineData(" BV1hjgG6jEa6 ", 1)]
    [InlineData("https://www.bilibili.com/video/BV1hjgG6jEa6/?spm_id_from=episodes&p=3", 3)]
    [InlineData("https://www.bilibili.com/video/BV1hjgG6jEa6/?p=4&spm_id_from=ignored", 4)]
    public void Parse_ValidInput_NormalizesVideoAndPart(string input, int part)
    {
        var identity = VideoIdentity.Parse(input);
        Assert.Equal(new("BV1hjgG6jEa6", part), identity);
        Assert.Equal($"https://www.bilibili.com/video/BV1hjgG6jEa6/?p={part}", identity.Url);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("https://bilibili.com.evil.test/video/BV1hjgG6jEa6")]
    [InlineData("javascript:alert(1)")] [InlineData("http://www.bilibili.com/video/BV1hjgG6jEa6")]
    [InlineData("https://www.bilibili.com/video/BV1hjgG6jEa6/?p=0")]
    [InlineData("https://www.bilibili.com/video/BV1hjgG6jEa6/?p=no")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input) => Assert.False(VideoIdentity.TryParse(input, out _));
}
