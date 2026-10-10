using System.Collections.Generic;

namespace Jellyfin.LiveTv.Recordings;

/// <summary>
/// Reports the tuner hosts that running recordings are using.
/// </summary>
public interface IActiveRecordingTunerProvider
{
    /// <summary>
    /// Gets the tuner host of each running recording that has opened its live stream on a tuner host.
    /// </summary>
    /// <returns>The tuner host ids, keyed by the id of the recording's timer.</returns>
    IReadOnlyDictionary<string, string> GetActiveRecordingTunerHostIds();
}
