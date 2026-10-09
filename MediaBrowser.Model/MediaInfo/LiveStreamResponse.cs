#pragma warning disable CS1591

using System.Text.Json.Serialization;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Model.MediaInfo
{
    public class LiveStreamResponse
    {
        public LiveStreamResponse(MediaSourceInfo mediaSource, string tunerHostId = "")
        {
            MediaSource = mediaSource;
            TunerHostId = tunerHostId;
        }

        public MediaSourceInfo MediaSource { get; }

        [JsonIgnore]
        public string TunerHostId { get; }
    }
}
