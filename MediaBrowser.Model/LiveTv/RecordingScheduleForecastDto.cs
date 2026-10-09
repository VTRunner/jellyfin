#nullable disable
#pragma warning disable CS1591

namespace MediaBrowser.Model.LiveTv;

/// <summary>
/// Describes the server-side forecast for a scheduled recording.
/// </summary>
public class RecordingScheduleForecastDto
{
    /// <summary>
    /// Gets or sets the internal timer identifier.
    /// </summary>
    public string TimerId { get; set; }

    /// <summary>
    /// Gets or sets the forecast status.
    /// </summary>
    public string ForecastStatus { get; set; }

    /// <summary>
    /// Gets or sets the recording priority. Lower values are higher priority.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the effective recording start time, including pre-padding.
    /// </summary>
    public global::System.DateTime EffectiveStart { get; set; }

    /// <summary>
    /// Gets or sets the effective recording end time, including post-padding.
    /// </summary>
    public global::System.DateTime EffectiveEnd { get; set; }

    /// <summary>
    /// Gets or sets the number of compatible tuner slots configured for this recording.
    /// Null means at least one compatible tuner host is configured as unlimited.
    /// </summary>
    public int? CompatibleTunerCount { get; set; }

    /// <summary>
    /// Gets or sets the number of compatible tuner slots that are free at recording start.
    /// Null means at least one compatible tuner host is configured as unlimited.
    /// </summary>
    public int? AvailableTunerCount { get; set; }

    /// <summary>
    /// Gets or sets the tuner host selected by the forecast simulator.
    /// </summary>
    public string TunerHostId { get; set; }

    /// <summary>
    /// Gets or sets the friendly name of the tuner host selected by the forecast simulator.
    /// </summary>
    public string TunerHostName { get; set; }

    /// <summary>
    /// Gets or sets the tuner host type selected by the forecast simulator.
    /// </summary>
    public string TunerHostType { get; set; }

    /// <summary>
    /// Gets or sets the one-based tuner host index used for display when no friendly name is available.
    /// </summary>
    public int? TunerHostIndex { get; set; }

    /// <summary>
    /// Gets or sets the zero-based tuner slot selected by the forecast simulator.
    /// </summary>
    public int? TunerSlotIndex { get; set; }

    /// <summary>
    /// Gets or sets the timer identifier that blocked this recording or caused optional padding to be preempted.
    /// </summary>
    public string BlockedByTimerId { get; set; }

    /// <summary>
    /// Gets or sets the name of the timer that blocked this recording or caused optional padding to be preempted.
    /// </summary>
    public string BlockedByName { get; set; }

    /// <summary>
    /// Gets or sets the priority of the timer that blocked this recording or caused optional padding to be preempted.
    /// </summary>
    public int? BlockedByPriority { get; set; }
}
