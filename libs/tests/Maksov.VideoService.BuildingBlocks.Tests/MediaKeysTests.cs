using Maksov.VideoService.BuildingBlocks.Storage;

namespace Maksov.VideoService.BuildingBlocks.Tests;

public sealed class MediaKeysTests
{
    [Theory]
    [InlineData("manifest.mpd")]
    [InlineData("master.m3u8")]
    [InlineData("media_0.m3u8")]
    [InlineData("init-stream0.m4s")]
    [InlineData("chunk-stream3-00012.m4s")]
    public void Accepts_stream_files(string file) => Assert.True(MediaKeys.IsValidStreamFile(file));

    [Theory]
    [InlineData("")]
    [InlineData("../source")]
    [InlineData("..%2Fsource")]
    [InlineData("a/b.m4s")]
    [InlineData(".hidden.m4s")]
    [InlineData("source")]
    [InlineData("poster.exe")]
    [InlineData("x..m4s")]
    public void Rejects_unsafe_or_unknown_files(string file) => Assert.False(MediaKeys.IsValidStreamFile(file));

    [Fact]
    public void Keys_are_scoped_by_video()
    {
        var id = Guid.Parse("0190f5b4-0000-7000-8000-000000000001");

        Assert.Equal("uploads/0190f5b4000070008000000000000001/source", MediaKeys.Source(id));
        Assert.Equal("videos/0190f5b4000070008000000000000001/stream/manifest.mpd", MediaKeys.StreamFile(id, "manifest.mpd"));
    }
}
