using System;

namespace MediaBrowser.Model.LiveTv;

/// <summary>
/// Describes a session that is watching live TV, and the tuner host its live stream uses. It is shown with the
/// recording schedule so that tuner use can be seen, and is not part of the recording forecast.
/// </summary>
public class LiveTvTunerSessionDto
{
    /// <summary>
    /// Gets or sets the session identifier.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the user watching.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the name of the device watching.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the name of the client watching.
    /// </summary>
    public string? Client { get; set; }

    /// <summary>
    /// Gets or sets the item identifier of the channel being watched.
    /// </summary>
    public Guid ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the name of the channel being watched.
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>
    /// Gets or sets the number of the channel being watched.
    /// </summary>
    public string? ChannelNumber { get; set; }

    /// <summary>
    /// Gets or sets the name of the program on the channel now.
    /// </summary>
    public string? ProgramName { get; set; }

    /// <summary>
    /// Gets or sets when the program on the channel now started.
    /// </summary>
    public DateTime? ProgramStartDate { get; set; }

    /// <summary>
    /// Gets or sets when the program on the channel now ends.
    /// </summary>
    public DateTime? ProgramEndDate { get; set; }

    /// <summary>
    /// Gets or sets the tuner host that the live stream was opened on, or <c>null</c> when it isn't known.
    /// </summary>
    public string? TunerHostId { get; set; }

    /// <summary>
    /// Gets or sets the friendly name of the tuner host.
    /// </summary>
    public string? TunerHostName { get; set; }

    /// <summary>
    /// Gets or sets the type of the tuner host.
    /// </summary>
    public string? TunerHostType { get; set; }

    /// <summary>
    /// Gets or sets the one-based position of the tuner host in the tuner host list, as the recording forecast
    /// reports it.
    /// </summary>
    public int? TunerHostIndex { get; set; }
}
