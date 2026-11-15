using Maksov.VideoService.Videos.Domain;

namespace Maksov.VideoService.Videos.Tests;

public sealed class RenditionLadderTests
{
    [Fact]
    public void Landscape_1080p_gets_full_ladder()
    {
        var ladder = RenditionLadder.Plan(1920, 1080, 30);

        Assert.Equal(["240p", "360p", "480p", "720p", "1080p"], ladder.Select(r => r.Name));
        Assert.Equal((1920, 1080), (ladder[^1].Width, ladder[^1].Height));
        Assert.Equal((426, 240), (ladder[0].Width, ladder[0].Height));
    }

    [Fact]
    public void Portrait_video_is_scaled_by_short_side()
    {
        var ladder = RenditionLadder.Plan(1080, 1920, 30);

        Assert.Equal((1080, 1920), (ladder[^1].Width, ladder[^1].Height));
        Assert.Equal((720, 1280), (ladder[3].Width, ladder[3].Height));
    }

    [Fact]
    public void Never_upscales()
    {
        var ladder = RenditionLadder.Plan(854, 480, 25);

        Assert.Equal("480p", ladder[^1].Name);
        Assert.DoesNotContain(ladder, r => r.Height > 480);
    }

    [Fact]
    public void Tiny_source_gets_single_rung_with_even_dimensions()
    {
        var ladder = RenditionLadder.Plan(177, 101, 30);

        var only = Assert.Single(ladder);
        Assert.Equal(0, only.Width % 2);
        Assert.Equal(0, only.Height % 2);
    }

    [Fact]
    public void All_dimensions_are_even()
    {
        foreach (var r in RenditionLadder.Plan(1278, 717, 30))
        {
            Assert.Equal(0, r.Width % 2);
            Assert.Equal(0, r.Height % 2);
        }
    }

    [Fact]
    public void High_frame_rate_gets_more_bitrate()
    {
        var normal = RenditionLadder.Plan(1280, 720, 30)[^1];
        var high = RenditionLadder.Plan(1280, 720, 60)[^1];

        Assert.True(high.VideoKbps > normal.VideoKbps);
    }

    [Fact]
    public void Source_above_1080p_is_capped()
    {
        var ladder = RenditionLadder.Plan(3840, 2160, 30);

        Assert.Equal("1080p", ladder[^1].Name);
    }
}

public sealed class UploadPolicyTests
{
    private readonly UploadPolicy _policy = new(100 * 1024 * 1024, UploadPolicy.DefaultContentTypes);

    [Fact]
    public void Valid_request_has_no_errors() =>
        Assert.Empty(_policy.Validate("Sunset", "video/mp4", 1024));

    [Theory]
    [InlineData(null, "video/mp4", 10, "title")]
    [InlineData("  ", "video/mp4", 10, "title")]
    [InlineData("ok", "image/png", 10, "contentType")]
    [InlineData("ok", "video/mp4", 0, "sizeBytes")]
    [InlineData("ok", "video/mp4", 200L * 1024 * 1024, "sizeBytes")]
    public void Invalid_request_is_rejected(string? title, string contentType, long size, string field) =>
        Assert.Contains(_policy.Validate(title, contentType, size), e => e.Code == field);
}

public sealed class VideoStatusTests
{
    [Theory]
    [InlineData(VideoStatus.AwaitingUpload, VideoStatus.Uploaded, true)]
    [InlineData(VideoStatus.Uploaded, VideoStatus.Processing, true)]
    [InlineData(VideoStatus.Processing, VideoStatus.Ready, true)]
    [InlineData(VideoStatus.Ready, VideoStatus.Processing, false)]
    [InlineData(VideoStatus.Failed, VideoStatus.Uploaded, false)]
    [InlineData(VideoStatus.AwaitingUpload, VideoStatus.Ready, false)]
    public void Transitions_only_forward(VideoStatus from, VideoStatus to, bool allowed) =>
        Assert.Equal(allowed, from.CanTransitionTo(to));

    [Fact]
    public void Wire_format_roundtrips()
    {
        foreach (var status in Enum.GetValues<VideoStatus>())
        {
            Assert.Equal(status, VideoStatusExtensions.ParseWire(status.ToWire()));
        }
    }
}

