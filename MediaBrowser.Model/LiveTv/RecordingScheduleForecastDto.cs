#nullable disable
#pragma warning disable CS1591

using System;

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
    /// Gets or sets the forecast status: <c>WillRecord</c>, <c>StartsLate</c>, <c>StopsEarly</c>,
    /// <c>PaddingAtRisk</c>, <c>Conflict</c> or <c>Unknown</c>.
    /// </summary>
    public string ForecastStatus { get; set; }

    /// <summary>
    /// Gets or sets the recording priority. Lower values are higher priority.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the effective recording start time, including pre-padding.
    /// </summary>
    public DateTime EffectiveStart { get; set; }

    /// <summary>
    /// Gets or sets the effective recording end time, including post-padding.
    /// </summary>
    public DateTime EffectiveEnd { get; set; }

    /// <summary>
    /// Gets or sets when a recording that starts late is expected to start, after waiting for a tuner.
    /// It is only set when <see cref="ForecastStatus"/> is <c>StartsLate</c>.
    /// </summary>
    public DateTime? ExpectedStart { get; set; }

    /// <summary>
    /// Gets or sets when a recording is expected to stop before its program ends, because a recording
    /// with a higher priority needs its tuner.
    /// It is only set when <see cref="ForecastStatus"/> is <c>StopsEarly</c>.
    /// </summary>
    public DateTime? ExpectedEnd { get; set; }

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
    /// Gets or sets the timer identifier that blocked this recording, or that stops it or its post-padding early.
    /// </summary>
    public string BlockedByTimerId { get; set; }

    /// <summary>
    /// Gets or sets the name of the timer that blocked this recording, or that stops it or its post-padding early.
    /// </summary>
    public string BlockedByName { get; set; }

    /// <summary>
    /// Gets or sets the priority of the timer that blocked this recording, or that stops it or its post-padding early.
    /// </summary>
    public int? BlockedByPriority { get; set; }
}
