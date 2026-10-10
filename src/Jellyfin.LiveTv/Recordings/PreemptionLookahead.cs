using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.LiveTv.Recordings;

/// <summary>
/// Decides whether a recording that has not reached its program yet should take the tuner of a recording with a
/// lower priority. It should not when a recording with a higher priority that starts before its program would stop it
/// again: it would record only pre-padding, which is deleted, and cut the other recording short for nothing.
/// The server and the schedule forecast both use it, so that they agree.
/// </summary>
/// <remarks>
/// It looks ahead only to the start of the requesting recording's program, and only at recordings with a higher
/// priority. Those take tuners as the server does: a free tuner first, otherwise the tuner of the recording with the
/// lowest priority below their own, and of those, the one that loses the least of its program. Live TV is not
/// included, as in the forecast.
/// </remarks>
internal static class PreemptionLookahead
{
    /// <summary>
    /// Finds the recording with a higher priority that would stop the requesting recording before its program starts,
    /// if the requesting recording took a tuner now.
    /// </summary>
    /// <param name="requesting">The requesting recording, on the tuner host of the tuner it would take.</param>
    /// <param name="programStart">When the requesting recording's program starts.</param>
    /// <param name="tunerUses">The other recordings that hold tuners, without the recording it would stop.</param>
    /// <param name="upcoming">
    /// The recordings with a higher priority than the requesting recording that start before its program, in the order
    /// they start.
    /// </param>
    /// <param name="tunerHosts">The tuner hosts, in the order recordings try them.</param>
    /// <returns>The recording that would stop it, or <c>null</c> if none would.</returns>
    public static UpcomingRecording? FindStopBeforeProgram(
        TunerUse requesting,
        DateTime programStart,
        IEnumerable<TunerUse> tunerUses,
        IEnumerable<UpcomingRecording> upcoming,
        IReadOnlyList<TunerHostCapacity> tunerHosts)
    {
        var uses = new List<TunerUse>(tunerUses) { requesting };
        foreach (var recording in upcoming)
        {
            if (recording.Start > programStart || recording.Priority >= requesting.Priority)
            {
                continue;
            }

            // Recordings that have ended by then have released their tuners.
            uses.RemoveAll(use => use.ScheduledEnd <= recording.Start);

            var freeHost = tunerHosts.FirstOrDefault(host => recording.TunerHostIds.Contains(host.Id)
                && (host.TunerCount is null || uses.Count(use => IsOnHost(use, host.Id)) < host.TunerCount));
            if (freeHost is not null)
            {
                uses.Add(new TunerUse(recording.Id, freeHost.Id, recording.Priority, recording.ProgramEnd, recording.ScheduledEnd));
                continue;
            }

            var stopped = uses
                .Where(use => recording.TunerHostIds.Contains(use.TunerHostId) && use.Priority > recording.Priority)
                .OrderByDescending(use => use.Priority)
                .ThenBy(use => use.ProgramEnd > recording.Start ? use.ProgramEnd : recording.Start)
                .ThenBy(use => use.ScheduledEnd)
                .ThenBy(use => use.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (stopped is null)
            {
                // It waits for a tuner.
                continue;
            }

            if (ReferenceEquals(stopped, requesting))
            {
                return recording;
            }

            uses.Remove(stopped);
            uses.Add(new TunerUse(recording.Id, stopped.TunerHostId, recording.Priority, recording.ProgramEnd, recording.ScheduledEnd));
        }

        return null;
    }

    private static bool IsOnHost(TunerUse use, string tunerHostId)
        => string.Equals(use.TunerHostId, tunerHostId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A recording that holds a tuner.
    /// </summary>
    /// <param name="Id">The timer identifier.</param>
    /// <param name="TunerHostId">The tuner host of the tuner.</param>
    /// <param name="Priority">The recording priority. Lower values are higher priority.</param>
    /// <param name="ProgramEnd">When the program ends.</param>
    /// <param name="ScheduledEnd">When the recording ends, including post-padding.</param>
    internal sealed record TunerUse(string Id, string TunerHostId, int Priority, DateTime ProgramEnd, DateTime ScheduledEnd);

    /// <summary>
    /// A recording with a higher priority than the requesting recording that starts before its program does.
    /// </summary>
    /// <param name="Id">The timer identifier.</param>
    /// <param name="Priority">The recording priority. Lower values are higher priority.</param>
    /// <param name="Start">When the recording starts, including pre-padding.</param>
    /// <param name="ProgramEnd">When the program ends.</param>
    /// <param name="ScheduledEnd">When the recording ends, including post-padding.</param>
    /// <param name="TunerHostIds">The tuner hosts that carry the recording's channel.</param>
    internal sealed record UpcomingRecording(
        string Id,
        int Priority,
        DateTime Start,
        DateTime ProgramEnd,
        DateTime ScheduledEnd,
        IReadOnlySet<string> TunerHostIds);

    /// <summary>
    /// A tuner host and its number of tuners.
    /// </summary>
    /// <param name="Id">The tuner host identifier.</param>
    /// <param name="TunerCount">The number of tuners, or <c>null</c> when the tuner host has no limit.</param>
    internal sealed record TunerHostCapacity(string Id, int? TunerCount);
}
