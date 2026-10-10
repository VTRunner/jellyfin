#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.Recordings;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.LiveTv;

/// <summary>
/// Produces a deterministic, event-driven forecast of scheduled recording tuner allocation.
/// </summary>
internal sealed class RecordingScheduleForecastEngine
{
    private const string WillRecord = "WillRecord";
    private const string Conflict = "Conflict";
    private const string PaddingAtRisk = "PaddingAtRisk";
    private const string StartsLate = "StartsLate";
    private const string StopsEarly = "StopsEarly";
    private const string Unknown = "Unknown";

    // Events at the same time are handled in this order: tuners are released before recordings start.
    private const int FullEndEvent = 0;
    private const int StartEvent = 1;

    // A recording that gets a tuner within this time of its scheduled start isn't reported as starting late.
    private static readonly TimeSpan LateStartThreshold = TimeSpan.FromMinutes(1);

    // A recording that is stopped before its program starts tries again after this long, as the server retries it.
    private static readonly TimeSpan StoppedBeforeProgramRetryDelay = TimeSpan.FromSeconds(60);

    private readonly IServerConfigurationManager _config;

    public RecordingScheduleForecastEngine(IServerConfigurationManager config)
    {
        _config = config;
    }

    public async Task<IReadOnlyList<RecordingScheduleForecastDto>> ForecastAsync(
        IReadOnlyList<ForecastTimerInput> timers,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var hostInfos = _config.GetLiveTvConfiguration().TunerHosts
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .ToArray();
        var hostStates = hostInfos
            .Select((info, index) => new { info, index })
            .ToDictionary(
                i => i.info.Id,
                i => new TunerHostState(i.info, i.index),
                StringComparer.OrdinalIgnoreCase);

        var channelHostsByService = new Dictionary<ILiveTvService, Dictionary<string, string[]>>();
        var channelNumberHostsByService = new Dictionary<ILiveTvService, Dictionary<string, string[]>>();
        var seriesPrioritiesByService = new Dictionary<ILiveTvService, Dictionary<string, int>>();

        foreach (var service in timers.Select(i => i.Service).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<string, string[]> channelMap = null;
            Dictionary<string, string[]> channelNumberMap = null;
            try
            {
                var channels = service is DefaultLiveTvService defaultLiveTvService
                    ? await defaultLiveTvService.GetChannelsForRecordingForecastAsync(cancellationToken).ConfigureAwait(false)
                    : await service.GetChannelsAsync(cancellationToken).ConfigureAwait(false);

                channelMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                channelNumberMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var channel in channels)
                {
                    if (string.IsNullOrWhiteSpace(channel.TunerHostId))
                    {
                        continue;
                    }

                    if (!hostInfos.Any(host => string.Equals(host.Id, channel.TunerHostId, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Timers normally use ChannelInfo.Id. TunerChannelId is included as
                    // a fallback for providers whose scheduled timer uses the provider
                    // channel id instead of the tuner-specific channel id.
                    AddChannelHostMapping(channelMap, channel.Id, channel.TunerHostId);
                    AddChannelHostMapping(channelMap, channel.TunerChannelId, channel.TunerHostId);
                    AddChannelHostMapping(channelNumberMap, channel.Number, channel.TunerHostId);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Leave channelMap null so the forecast reports that tuner compatibility
                // could not be determined rather than failing the entire forecast request.
            }

            channelHostsByService[service] = channelMap;
            channelNumberHostsByService[service] = channelNumberMap;

            try
            {
                var seriesTimers = await service.GetSeriesTimersAsync(cancellationToken).ConfigureAwait(false);
                seriesPrioritiesByService[service] = seriesTimers
                    .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                    .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(i => i.Key, i => i.First().Priority, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                seriesPrioritiesByService[service] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        var forecastTimers = new List<ForecastTimerState>();

        foreach (var input in timers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullEnd = input.EndDate.AddSeconds(Math.Max(input.PostPaddingSeconds, 0));
            if (fullEnd <= utcNow)
            {
                continue;
            }

            var priority = input.Priority;
            if (!string.IsNullOrWhiteSpace(input.SeriesTimerId)
                && seriesPrioritiesByService.TryGetValue(input.Service, out var seriesPriorities)
                && seriesPriorities.TryGetValue(input.SeriesTimerId, out var seriesPriority))
            {
                priority = seriesPriority;
            }

            if (!channelHostsByService.TryGetValue(input.Service, out var channelMap) || channelMap is null)
            {
                forecastTimers.Add(new ForecastTimerState(input, priority, Array.Empty<TunerHostState>(), Array.Empty<TunerHostState>(), utcNow));
                continue;
            }

            channelNumberHostsByService.TryGetValue(input.Service, out var channelNumberMap);

            string[] channelHostIds;
            if (string.IsNullOrWhiteSpace(input.ChannelId))
            {
                // A series timer configured to record on any channel can use any configured
                // tuner host. The scheduler will choose the host when the recording starts.
                channelHostIds = hostStates.Keys.ToArray();
            }
            else if (!channelMap.TryGetValue(input.ChannelId, out channelHostIds) || channelHostIds.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(input.ChannelNumber)
                    || channelNumberMap is null
                    || !channelNumberMap.TryGetValue(input.ChannelNumber, out channelHostIds)
                    || channelHostIds.Length == 0)
                {
                    forecastTimers.Add(new ForecastTimerState(input, priority, Array.Empty<TunerHostState>(), Array.Empty<TunerHostState>(), utcNow));
                    continue;
                }
            }

            // A recording in progress stays on the tuner host it records on, unless it is stopped before its
            // program starts. Then it can try any tuner host that carries its channel again.
            var channelHosts = GetHostStates(hostStates, channelHostIds);
            var candidates = input.Status == RecordingStatus.InProgress && !string.IsNullOrWhiteSpace(input.TunerHostId)
                ? GetHostStates(hostStates, [input.TunerHostId])
                : channelHosts;

            forecastTimers.Add(new ForecastTimerState(
                input,
                priority,
                candidates,
                channelHosts,
                input.Status == RecordingStatus.InProgress
                    ? utcNow
                    : Max(input.StartDate.AddSeconds(-Math.Max(input.PrePaddingSeconds, 0)), utcNow)));
        }

        var hosts = hostStates.Values.OrderBy(i => i.Order).ToArray();
        var eventQueue = new PriorityQueue<ForecastEvent, ForecastEventPriority>();
        var waiting = new List<ForecastTimerState>();
        long sequence = 0;

        foreach (var state in forecastTimers
            .Where(i => i.Input.Status == RecordingStatus.InProgress)
            .OrderBy(i => i.Input.StartDate)
            .ThenBy(i => i.Priority)
            .ThenBy(i => i.Input.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (state.Candidates.Length == 0)
            {
                continue;
            }

            var slot = state.Candidates
                .OrderBy(i => i.Order)
                .Select(i => i.GetAvailableSlot())
                .FirstOrDefault(i => i is not null);

            if (slot is null)
            {
                // The live system should never report more active recordings than the
                // configured fixed tuner capacity. Retain a deterministic overflow
                // reservation so an inconsistent state cannot make the forecast invent free capacity.
                state.Candidates[0].AddOverflowAssignment(state);
            }
            else
            {
                Assign(state, slot);
                state.CurrentSlot = slot;
            }

            // It keeps recording, unless a recording with a higher priority stops it.
            state.Forecast = state.CreateDto(WillRecord);
            EnqueueLifecycleEvents(state, eventQueue, ref sequence, utcNow);
        }

        foreach (var state in forecastTimers
            .Where(i => i.Input.Status == RecordingStatus.New)
            .OrderBy(i => i.EffectiveStart)
            .ThenBy(i => i.Priority)
            .ThenBy(i => i.Input.Id, StringComparer.OrdinalIgnoreCase))
        {
            EnqueueEvent(eventQueue, new ForecastEvent(state, StartEvent, state.EffectiveStart), ref sequence);
        }

        while (eventQueue.TryDequeue(out var forecastEvent, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (forecastEvent.Type)
            {
                case FullEndEvent:
                    HandleFullEnd(forecastEvent.State, forecastEvent.Timestamp, eventQueue, ref sequence, waiting);
                    break;

                case StartEvent:
                    HandleStart(forecastEvent.State, forecastEvent.Timestamp, forecastEvent.Trigger, eventQueue, ref sequence, waiting, forecastTimers, hosts);
                    break;
            }
        }

        return forecastTimers
            .Where(i => i.Input.Status is RecordingStatus.New or RecordingStatus.InProgress)
            .Select(i => i.ToDto())
            .ToArray();
    }

    /// <summary>
    /// Tries to start a recording: at its scheduled start, and again whenever a tuner may have become
    /// available while it waits for one.
    /// </summary>
    private static void HandleStart(
        ForecastTimerState state,
        DateTime utcNow,
        ForecastTimerState trigger,
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ref long sequence,
        List<ForecastTimerState> waiting,
        IReadOnlyList<ForecastTimerState> timers,
        IReadOnlyList<TunerHostState> hosts)
    {
        // Ignore a recording that has started, or that has stopped waiting for a tuner.
        if (state.HasStarted || (state.Forecast is not null && !state.IsWaiting))
        {
            return;
        }

        if (state.Candidates.Length == 0)
        {
            state.Forecast = state.CreateDto(Unknown);
            return;
        }

        // Like the recording itself, a recording that is waiting for a tuner gives up when its program ends.
        if (state.IsWaiting && utcNow >= state.Input.EndDate)
        {
            StopWaiting(state, waiting);
            return;
        }

        if (!state.HasAttempted)
        {
            // Tuner availability is reported for the scheduled start, even if the recording starts later.
            state.HasAttempted = true;
            state.ScheduledCompatibleTunerCount = GetCapacity(state.Candidates);
            state.ScheduledAvailableTunerCount = GetAvailableCapacity(state.Candidates);
        }

        var slot = state.Candidates
            .OrderBy(i => i.Order)
            .Select(i => i.GetAvailableSlot())
            .FirstOrDefault(i => i is not null);

        ForecastTimerState deferredFor = null;
        if (slot is null)
        {
            // A recording with a higher priority gets a tuner from one with a lower priority, as the server does: it
            // stops the recording with the lowest priority on a tuner that can record its channel, and of those, the
            // one that loses the least of its program. It skips a tuner that a recording with a higher priority
            // would take back from it before its program starts.
            var preemptableSlots = state.Candidates
                .SelectMany(i => i.GetPreemptableSlots(state.Priority))
                .OrderByDescending(i => i.Assignment.Priority)
                .ThenBy(i => Max(i.Assignment.MainEnd, utcNow))
                .ThenBy(i => i.Assignment.FullEnd)
                .ThenBy(i => i.Assignment.TimerId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Host.Order)
                .ThenBy(i => i.Index)
                .ToList();

            foreach (var preemptable in preemptableSlots)
            {
                var stoppedBy = FindStopBeforeProgram(state, preemptable, utcNow, timers, hosts);
                if (stoppedBy is null)
                {
                    Preempt(preemptable, state, utcNow, eventQueue, ref sequence, waiting);
                    slot = preemptable;
                    break;
                }

                deferredFor ??= stoppedBy;
            }
        }

        if (slot is not null)
        {
            StopWaiting(state, waiting);
            Assign(state, slot);

            if (utcNow > state.ScheduledStart + LateStartThreshold)
            {
                // It waited for a tuner, so it starts late: possibly only its pre-padding is lost.
                var waitedFor = trigger ?? state.WaitingFor;
                state.Forecast = state.CreateDto(
                    StartsLate,
                    state.ScheduledCompatibleTunerCount,
                    state.ScheduledAvailableTunerCount,
                    waitedFor?.Input.Id,
                    waitedFor?.Input.Name,
                    waitedFor?.Priority,
                    utcNow);
            }
            else
            {
                state.Forecast = state.CreateDto(
                    WillRecord,
                    state.ScheduledCompatibleTunerCount,
                    state.ScheduledAvailableTunerCount);
            }

            EnqueueLifecycleEvents(state, eventQueue, ref sequence, utcNow);
            return;
        }

        if (deferredFor is not null)
        {
            // It tries again once the recording with the higher priority has started, as the server does.
            EnqueueEvent(eventQueue, new ForecastEvent(state, StartEvent, deferredFor.EffectiveStart), ref sequence);
        }

        if (state.IsWaiting)
        {
            return;
        }

        // No tuner is free, and every recording on one has the same or a higher priority, or would be stopped for
        // nothing, so the recording waits for a tuner, as it does when it runs. It doesn't record unless one
        // becomes available before its program ends.
        var blockedBy = deferredFor ?? state.Candidates
            .SelectMany(i => i.GetOccupiedAssignments())
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.FullEnd)
            .ThenBy(i => i.TimerId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Owner;

        state.IsWaiting = true;
        state.WaitingFor = blockedBy;
        waiting.Add(state);

        state.Forecast = state.CreateDto(
            Conflict,
            state.ScheduledCompatibleTunerCount,
            state.ScheduledAvailableTunerCount,
            blockedBy?.Input.Id,
            blockedBy?.Input.Name,
            blockedBy?.Priority);
    }

    /// <summary>
    /// Finds the recording with a higher priority that would stop a recording before its program starts, if the
    /// recording took the tuner on a slot now, as the server checks before it stops a recording.
    /// </summary>
    private static ForecastTimerState FindStopBeforeProgram(
        ForecastTimerState state,
        TunerSlotState slot,
        DateTime utcNow,
        IReadOnlyList<ForecastTimerState> timers,
        IReadOnlyList<TunerHostState> hosts)
    {
        if (utcNow >= state.Input.StartDate)
        {
            return null;
        }

        // Recordings with a higher priority that are due to start before the program, and haven't tried for a tuner.
        var upcoming = timers
            .Where(i => i.Input.Status == RecordingStatus.New
                && i.Forecast is null
                && i.Candidates.Length > 0
                && i.Priority < state.Priority
                && i.EffectiveStart > utcNow
                && i.EffectiveStart <= state.Input.StartDate)
            .OrderBy(i => i.EffectiveStart)
            .ThenBy(i => i.Priority)
            .ThenBy(i => i.Input.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (upcoming.Count == 0)
        {
            return null;
        }

        var stoppedBy = PreemptionLookahead.FindStopBeforeProgram(
            new PreemptionLookahead.TunerUse(state.Input.Id, slot.Host.Info.Id, state.Priority, state.Input.EndDate, state.ScheduledEnd),
            state.Input.StartDate,
            hosts
                .SelectMany(i => i.GetOccupiedAssignments())
                .Where(i => i != slot.Assignment)
                .Select(i => new PreemptionLookahead.TunerUse(i.TimerId, i.Slot.Host.Info.Id, i.Priority, i.MainEnd, i.FullEnd)),
            upcoming.Select(i => new PreemptionLookahead.UpcomingRecording(
                i.Input.Id,
                i.Priority,
                i.EffectiveStart,
                i.Input.EndDate,
                i.ScheduledEnd,
                i.Candidates.Select(host => host.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase))),
            hosts
                .Select(i => new PreemptionLookahead.TunerHostCapacity(i.Info.Id, i.IsUnlimited ? null : i.Capacity - i.OverflowAssignmentCount))
                .ToList());

        return stoppedBy is null
            ? null
            : upcoming.Find(i => string.Equals(i.Input.Id, stoppedBy.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static void HandleFullEnd(
        ForecastTimerState state,
        DateTime utcNow,
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ref long sequence,
        List<ForecastTimerState> waiting)
    {
        var assignment = state.Assignment;
        if (assignment is null)
        {
            return;
        }

        if (assignment.FullEnd > utcNow)
        {
            return;
        }

        if (assignment.Slot is not null)
        {
            assignment.Slot.Assignment = null;
        }
        else
        {
            state.Candidates[0].ReleaseOverflowAssignment();
        }

        state.Assignment = null;

        // The tuner is free, so a waiting recording can start now.
        RetryWaiting(state, utcNow, eventQueue, ref sequence, waiting);
    }

    /// <summary>
    /// Lets the recordings that are waiting for a tuner try again at <paramref name="utcNow"/>, when a tuner is
    /// released. They try in priority order, together with any recording that is scheduled to start then.
    /// </summary>
    private static void RetryWaiting(
        ForecastTimerState trigger,
        DateTime utcNow,
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ref long sequence,
        List<ForecastTimerState> waiting)
    {
        foreach (var state in waiting)
        {
            if (state.PendingRetry != utcNow)
            {
                state.PendingRetry = utcNow;
                EnqueueEvent(eventQueue, new ForecastEvent(state, StartEvent, utcNow, trigger), ref sequence);
            }
        }
    }

    private static void StopWaiting(ForecastTimerState state, List<ForecastTimerState> waiting)
    {
        if (state.IsWaiting)
        {
            state.IsWaiting = false;
            waiting.Remove(state);
        }
    }

    private static void Assign(ForecastTimerState state, TunerSlotState slot)
    {
        var assignment = new TunerSlotAssignment(
            state,
            slot,
            state.Input.Id,
            state.Input.Name,
            state.Priority,
            state.Input.EndDate,
            state.Input.EndDate.AddSeconds(Math.Max(state.Input.PostPaddingSeconds, 0)));

        slot.Assignment = assignment;
        state.Assignment = assignment;
        state.HasStarted = true;
    }

    /// <summary>
    /// Stops the recording on a slot for a recording with a higher priority. A recording that has not reached
    /// its program yet loses what it recorded and tries again for a tuner, as it does when it runs; otherwise it
    /// stops early, or loses its post-padding.
    /// </summary>
    private static void Preempt(
        TunerSlotState slot,
        ForecastTimerState requestingState,
        DateTime utcNow,
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ref long sequence,
        List<ForecastTimerState> waiting)
    {
        var assignment = slot.Assignment;
        if (assignment is null)
        {
            return;
        }

        var owner = assignment.Owner;
        var stopsBeforeProgram = utcNow <= owner.Input.StartDate;

        // A recording that keeps what it recorded is reported while it still has its tuner, so that the forecast
        // shows the tuner it records on.
        if (!stopsBeforeProgram)
        {
            if (utcNow < assignment.MainEnd)
            {
                owner.MarkStopsEarly(requestingState, utcNow);
            }
            else
            {
                owner.MarkPaddingAtRisk(requestingState.Input.Id, requestingState.Input.Name, requestingState.Priority);
            }
        }

        assignment.FullEnd = utcNow;
        slot.Assignment = null;
        owner.Assignment = null;

        if (stopsBeforeProgram)
        {
            // It takes a tuner that is released while it waits, and when it tries again, it can stop a
            // recording with a lower priority than its own.
            owner.WaitForTunerAgain(requestingState);
            waiting.Add(owner);
            EnqueueEvent(eventQueue, new ForecastEvent(owner, StartEvent, utcNow + StoppedBeforeProgramRetryDelay), ref sequence);
        }
    }

    private static void EnqueueLifecycleEvents(
        ForecastTimerState state,
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ref long sequence,
        DateTime utcNow)
    {
        if (state.Assignment is null)
        {
            return;
        }

        var fullEnd = state.Input.EndDate.AddSeconds(Math.Max(state.Input.PostPaddingSeconds, 0));
        if (fullEnd >= utcNow)
        {
            EnqueueEvent(eventQueue, new ForecastEvent(state, FullEndEvent, fullEnd), ref sequence);
        }
    }

    private static void EnqueueEvent(
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ForecastEvent forecastEvent,
        ref long sequence)
    {
        eventQueue.Enqueue(
            forecastEvent,
            new ForecastEventPriority(
                forecastEvent.Timestamp,
                forecastEvent.Type,
                forecastEvent.State.Priority,
                forecastEvent.State.Input.Id,
                sequence++));
    }

    private static void AddChannelHostMapping(
        Dictionary<string, string[]> channelMap,
        string channelId,
        string tunerHostId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        if (!channelMap.TryGetValue(channelId, out var hostIds))
        {
            channelMap[channelId] = [tunerHostId];
            return;
        }

        if (!hostIds.Contains(tunerHostId, StringComparer.OrdinalIgnoreCase))
        {
            channelMap[channelId] = [.. hostIds, tunerHostId];
        }
    }

    private static TunerHostState[] GetHostStates(Dictionary<string, TunerHostState> hostStates, IEnumerable<string> hostIds)
    {
        return hostIds
            .Select(hostId => hostStates.TryGetValue(hostId, out var host) ? host : null)
            .Where(host => host is not null)
            .ToArray();
    }

    private static int? GetCapacity(IEnumerable<TunerHostState> hosts)
    {
        var hostArray = hosts.ToArray();
        if (hostArray.Any(i => i.IsUnlimited))
        {
            return null;
        }

        return hostArray.Sum(i => i.Capacity);
    }

    private static int? GetAvailableCapacity(IEnumerable<TunerHostState> hosts)
    {
        var hostArray = hosts.ToArray();
        if (hostArray.Any(i => i.IsUnlimited))
        {
            return null;
        }

        return hostArray.Sum(i => i.AvailableSlotCount);
    }

    private static DateTime Max(DateTime first, DateTime second) => first > second ? first : second;

    private readonly record struct ForecastEventPriority(
        DateTime Timestamp,
        int Type,
        int RecordingPriority,
        string TimerId,
        long Sequence) : IComparable<ForecastEventPriority>
    {
        public int CompareTo(ForecastEventPriority other)
        {
            var result = Timestamp.CompareTo(other.Timestamp);
            if (result != 0)
            {
                return result;
            }

            result = Type.CompareTo(other.Type);
            if (result != 0)
            {
                return result;
            }

            result = RecordingPriority.CompareTo(other.RecordingPriority);
            if (result != 0)
            {
                return result;
            }

            result = string.Compare(TimerId, other.TimerId, StringComparison.OrdinalIgnoreCase);
            return result != 0 ? result : Sequence.CompareTo(other.Sequence);
        }
    }

    internal sealed class ForecastTimerInput
    {
        public string Id { get; init; }

        public string Name { get; init; }

        public string ChannelId { get; init; }

        public string ChannelNumber { get; init; }

        public string SeriesTimerId { get; init; }

        public string TunerHostId { get; init; }

        public DateTime StartDate { get; init; }

        public DateTime EndDate { get; init; }

        public int Priority { get; init; }

        public int PrePaddingSeconds { get; init; }

        public int PostPaddingSeconds { get; init; }

        public RecordingStatus Status { get; init; }

        public ILiveTvService Service { get; init; }
    }

    private sealed class ForecastTimerState
    {
        public ForecastTimerState(
            ForecastTimerInput input,
            int priority,
            TunerHostState[] candidates,
            TunerHostState[] channelCandidates,
            DateTime effectiveStart)
        {
            Input = input;
            Priority = priority;
            Candidates = candidates;
            ChannelCandidates = channelCandidates;
            EffectiveStart = effectiveStart;
        }

        public ForecastTimerInput Input { get; }

        public int Priority { get; }

        /// <summary>
        /// Gets the tuner hosts the recording can use.
        /// </summary>
        public TunerHostState[] Candidates { get; private set; }

        /// <summary>
        /// Gets the tuner hosts that carry the recording's channel.
        /// </summary>
        public TunerHostState[] ChannelCandidates { get; }

        public DateTime EffectiveStart { get; }

        public RecordingScheduleForecastDto Forecast { get; set; }

        public TunerSlotAssignment Assignment { get; set; }

        /// <summary>
        /// Gets or sets the tuner slot that a recording in progress records on now. The forecast shows it until the
        /// recording gets another one, also when a recording with a higher priority stops it before its program
        /// starts.
        /// </summary>
        public TunerSlotState CurrentSlot { get; set; }

        /// <summary>
        /// Gets the scheduled start, including pre-padding.
        /// </summary>
        public DateTime ScheduledStart => Input.StartDate.AddSeconds(-Math.Max(Input.PrePaddingSeconds, 0));

        /// <summary>
        /// Gets the scheduled end, including post-padding.
        /// </summary>
        public DateTime ScheduledEnd => Input.EndDate.AddSeconds(Math.Max(Input.PostPaddingSeconds, 0));

        public bool HasAttempted { get; set; }

        public bool HasStarted { get; set; }

        public bool IsWaiting { get; set; }

        public DateTime? PendingRetry { get; set; }

        /// <summary>
        /// Gets or sets the recording that held the tuner when this one started waiting for it.
        /// </summary>
        public ForecastTimerState WaitingFor { get; set; }

        public int? ScheduledCompatibleTunerCount { get; set; }

        public int? ScheduledAvailableTunerCount { get; set; }

        /// <summary>
        /// Records that a recording with a higher priority stops this recording before its program ends.
        /// </summary>
        public void MarkStopsEarly(ForecastTimerState requestingState, DateTime utcNow)
        {
            Forecast = CreateDto(
                StopsEarly,
                Forecast?.CompatibleTunerCount,
                Forecast?.AvailableTunerCount,
                requestingState.Input.Id,
                requestingState.Input.Name,
                requestingState.Priority,
                Forecast?.ExpectedStart,
                utcNow);
        }

        /// <summary>
        /// Puts a recording that was stopped before its program started back to waiting for a tuner.
        /// </summary>
        public void WaitForTunerAgain(ForecastTimerState requestingState)
        {
            Candidates = ChannelCandidates;
            HasStarted = false;
            IsWaiting = true;
            WaitingFor = requestingState;
            Forecast = CreateDto(
                Conflict,
                ScheduledCompatibleTunerCount,
                ScheduledAvailableTunerCount,
                requestingState.Input.Id,
                requestingState.Input.Name,
                requestingState.Priority);
        }

        public void MarkPaddingAtRisk(string blockedByTimerId, string blockedByName, int blockedByPriority)
        {
            // Starting late is the bigger loss, so it stays the reported outcome.
            if (Forecast?.ForecastStatus == StartsLate)
            {
                return;
            }

            Forecast = CreateDto(
                PaddingAtRisk,
                null,
                null,
                blockedByTimerId,
                blockedByName,
                blockedByPriority);
        }

        public RecordingScheduleForecastDto ToDto()
        {
            if (Forecast is null)
            {
                return CreateDto(Unknown);
            }

            return Forecast;
        }

        public RecordingScheduleForecastDto CreateDto(
            string status,
            int? compatibleTunerCount = null,
            int? availableTunerCount = null,
            string blockedByTimerId = null,
            string blockedByName = null,
            int? blockedByPriority = null,
            DateTime? expectedStart = null,
            DateTime? expectedEnd = null)
        {
            var slot = Assignment?.Slot ?? CurrentSlot;
            return new RecordingScheduleForecastDto
            {
                TimerId = Input.Id,
                ForecastStatus = status,
                Priority = Priority,
                EffectiveStart = ScheduledStart,
                EffectiveEnd = Input.EndDate.AddSeconds(Math.Max(Input.PostPaddingSeconds, 0)),
                ExpectedStart = expectedStart,
                ExpectedEnd = expectedEnd,
                CompatibleTunerCount = compatibleTunerCount,
                AvailableTunerCount = availableTunerCount,
                TunerHostId = slot?.Host.Info.Id,
                TunerHostName = slot?.Host.Info.FriendlyName,
                TunerHostType = slot?.Host.Info.Type,
                TunerHostIndex = slot?.Host.Order + 1,
                TunerSlotIndex = slot?.Index,
                BlockedByTimerId = blockedByTimerId,
                BlockedByName = blockedByName,
                BlockedByPriority = blockedByPriority
            };
        }
    }

    private sealed class TunerHostState
    {
        private readonly List<TunerSlotState> _slots = new();
        private int _overflowAssignmentCount;

        public TunerHostState(TunerHostInfo info, int order)
        {
            Info = info;
            Order = order;

            if (!IsUnlimited)
            {
                for (var index = 0; index < Capacity; index++)
                {
                    _slots.Add(new TunerSlotState(this, index));
                }
            }
        }

        public TunerHostInfo Info { get; }

        public int Order { get; }

        public int Capacity => Info.TunerCount <= 0 ? int.MaxValue : Info.TunerCount;

        public bool IsUnlimited => Info.TunerCount <= 0;

        public int AvailableSlotCount => IsUnlimited ? int.MaxValue : Math.Max(0, Capacity - _slots.Count(i => i.Assignment is not null) - _overflowAssignmentCount);

        /// <summary>
        /// Gets the number of recordings in progress that hold a tuner beyond the configured capacity.
        /// </summary>
        public int OverflowAssignmentCount => _overflowAssignmentCount;

        public TunerSlotState GetAvailableSlot()
        {
            var freeSlot = _slots.FirstOrDefault(i => i.Assignment is null);
            if (freeSlot is not null)
            {
                return freeSlot;
            }

            if (IsUnlimited)
            {
                var slot = new TunerSlotState(this, _slots.Count);
                _slots.Add(slot);
                return slot;
            }

            return null;
        }

        public IEnumerable<TunerSlotAssignment> GetOccupiedAssignments()
        {
            foreach (var slot in _slots)
            {
                if (slot.Assignment is not null)
                {
                    yield return slot.Assignment;
                }
            }
        }

        public IEnumerable<TunerSlotState> GetPreemptableSlots(int requestingPriority)
        {
            foreach (var slot in _slots)
            {
                if (slot.Assignment is { } assignment && assignment.Priority > requestingPriority)
                {
                    yield return slot;
                }
            }
        }

        public void AddOverflowAssignment(ForecastTimerState state)
        {
            _overflowAssignmentCount++;
            state.Assignment = new TunerSlotAssignment(
                state,
                null,
                state.Input.Id,
                state.Input.Name,
                state.Priority,
                state.Input.EndDate,
                state.Input.EndDate.AddSeconds(Math.Max(state.Input.PostPaddingSeconds, 0)));
        }

        public void ReleaseOverflowAssignment()
        {
            if (_overflowAssignmentCount > 0)
            {
                _overflowAssignmentCount--;
            }
        }
    }

    private sealed class TunerSlotState
    {
        public TunerSlotState(TunerHostState host, int index)
        {
            Host = host;
            Index = index;
        }

        public TunerHostState Host { get; }

        public int Index { get; }

        public TunerSlotAssignment Assignment { get; set; }
    }

    private sealed class TunerSlotAssignment
    {
        public TunerSlotAssignment(
            ForecastTimerState owner,
            TunerSlotState slot,
            string timerId,
            string name,
            int priority,
            DateTime mainEnd,
            DateTime fullEnd)
        {
            Owner = owner;
            Slot = slot;
            TimerId = timerId;
            Name = name;
            Priority = priority;
            MainEnd = mainEnd;
            FullEnd = fullEnd;
        }

        public ForecastTimerState Owner { get; }

        public TunerSlotState Slot { get; }

        public string TimerId { get; }

        public string Name { get; }

        public int Priority { get; }

        public DateTime MainEnd { get; }

        public DateTime FullEnd { get; set; }
    }

    /// <summary>
    /// An event in the forecast. For a waiting recording's new attempt to start, <c>Trigger</c> is the
    /// recording whose end prompted it.
    /// </summary>
    private sealed record ForecastEvent(ForecastTimerState State, int Type, DateTime Timestamp, ForecastTimerState Trigger = null);
}
