using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class FfprobeOutputParserTests
{
    [Fact]
    public void Parse_reads_duration_video_properties_and_named_audio_tracks()
    {
        const string json = """
            {
              "streams": [
                {
                  "codec_type": "video",
                  "codec_name": "h264",
                  "pix_fmt": "nv12",
                  "width": 1920,
                  "height": 1080,
                  "avg_frame_rate": "60000/1001",
                  "color_transfer": "smpte2084"
                },
                { "codec_type": "audio", "tags": { "title": "Full listening mix" } },
                { "codec_type": "audio" }
              ],
              "format": { "duration": "123.5" }
            }
            """;

        var result = FfprobeOutputParser.Parse(json);

        Assert.Equal(TimeSpan.FromSeconds(123.5), result.Duration);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.InRange(result.FrameRate, 59.94m, 59.95m);
        Assert.True(result.IsHdr);
        Assert.Equal(["Full listening mix", "Game audio"], result.AudioTrackTitles);
    }

    [Fact]
    public void Parse_replaces_generic_obs_track_tags_with_readable_names()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720, "avg_frame_rate": "30/1" },
                { "codec_type": "audio", "tags": { "title": "Track1" } },
                { "codec_type": "audio", "tags": { "title": "Track2" } },
                { "codec_type": "audio", "tags": { "title": "Track3" } },
                { "codec_type": "audio", "tags": { "title": "Track4" } },
                { "codec_type": "audio", "tags": { "title": "Track5" } }
              ],
              "format": { "duration": "10" }
            }
            """;

        var result = FfprobeOutputParser.Parse(json);

        Assert.Equal(
            ["Full listening mix", "Game audio", "Voice chat", "Raw microphone", "Processed microphone"],
            result.AudioTrackTitles);
    }
}
