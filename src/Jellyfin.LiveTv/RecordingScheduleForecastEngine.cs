#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
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
    private const string Unknown = "Unknown";

    private const int FullEndEvent = 0;
    private const int MainEndEvent = 1;
    private const int StartEvent = 2;

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
                forecastTimers.Add(new ForecastTimerState(input, priority, Array.Empty<TunerHostState>(), utcNow));
                continue;
            }

            channelNumberHostsByService.TryGetValue(input.Service, out var channelNumberMap);

            string[] candidateHostIds;
            if (string.IsNullOrWhiteSpace(input.ChannelId))
            {
                // A series timer configured to record on any channel can use any configured
                // tuner host. The scheduler will choose the host when the recording starts.
                candidateHostIds = hostStates.Keys.ToArray();
            }
            else if (!channelMap.TryGetValue(input.ChannelId, out var hostIds) || hostIds.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(input.ChannelNumber)
                    || channelNumberMap is null
                    || !channelNumberMap.TryGetValue(input.ChannelNumber, out hostIds)
                    || hostIds.Length == 0)
                {
                    forecastTimers.Add(new ForecastTimerState(input, priority, Array.Empty<TunerHostState>(), utcNow));
                    continue;
                }

                candidateHostIds = input.Status == RecordingStatus.InProgress
                    && !string.IsNullOrWhiteSpace(input.TunerHostId)
                    ? new[] { input.TunerHostId }
                    : hostIds;
            }
            else
            {
                candidateHostIds = input.Status == RecordingStatus.InProgress
                    && !string.IsNullOrWhiteSpace(input.TunerHostId)
                    ? new[] { input.TunerHostId }
                    : hostIds;
            }

            var candidates = candidateHostIds
                .Select(hostId => hostStates.TryGetValue(hostId, out var host) ? host : null)
                .Where(host => host is not null)
                .Cast<TunerHostState>()
                .ToArray();

            forecastTimers.Add(new ForecastTimerState(
                input,
                priority,
                candidates,
                input.Status == RecordingStatus.InProgress
                    ? utcNow
                    : Max(input.StartDate.AddSeconds(-Math.Max(input.PrePaddingSeconds, 0)), utcNow)));
        }

        var eventQueue = new PriorityQueue<ForecastEvent, ForecastEventPriority>();
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
            }

            EnqueueLifecycleEvents(state, eventQueue, ref sequence, utcNow);
        }

        foreach (var state in forecastTimers
            .Where(i => i.Input.Status == RecordingStatus.New)
            .OrderBy(i => i.EffectiveStart)
            .ThenBy(i => i.Priority)
            .ThenBy(i => i.Input.Id, StringComparer.OrdinalIgnoreCase))
        {
            EnqueueEvent(eventQueue, new ForecastEvent(state, StartEvent), ref sequence);
        }

        while (eventQueue.TryDequeue(out var forecastEvent, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (forecastEvent.Type)
            {
                case FullEndEvent:
                    HandleFullEnd(forecastEvent.State, forecastEvent.Timestamp);
                    break;

                case MainEndEvent:
                    HandleMainEnd(forecastEvent.State);
                    break;

                case StartEvent:
                    HandleStart(forecastEvent.State, forecastEvent.Timestamp, eventQueue, ref sequence);
                    break;
            }
        }

        return forecastTimers
            .Where(i => i.Input.Status is RecordingStatus.New or RecordingStatus.InProgress)
            .Select(i => i.ToDto())
            .ToArray();
    }

    private static void HandleStart(
        ForecastTimerState state,
        DateTime utcNow,
        PriorityQueue<ForecastEvent, ForecastEventPriority> eventQueue,
        ref long sequence)
    {
        if (state.Candidates.Length == 0)
        {
            state.Forecast = state.CreateDto(Unknown);
            return;
        }

        var compatibleCount = GetCapacity(state.Candidates);
        var availableCount = GetAvailableCapacity(state.Candidates);

        var freeSlot = state.Candidates
            .OrderBy(i => i.Order)
            .Select(i => i.GetAvailableSlot())
            .FirstOrDefault(i => i is not null);

        if (freeSlot is not null)
        {
            Assign(state, freeSlot);
            state.Forecast = state.CreateDto(WillRecord, compatibleCount, availableCount);
            EnqueueLifecycleEvents(state, eventQueue, ref sequence, utcNow);
            return;
        }

        var preemptable = state.Candidates
            .SelectMany(i => i.GetPreemptableSlots(utcNow, state.Priority))
            .OrderByDescending(i => i.Assignment.Priority)
            .ThenBy(i => i.Assignment.FullEnd)
            .ThenBy(i => i.Assignment.TimerId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Host.Order)
            .ThenBy(i => i.Index)
            .FirstOrDefault();

        if (preemptable is not null)
        {
            Preempt(preemptable, state, utcNow);
            Assign(state, preemptable);
            state.Forecast = state.CreateDto(WillRecord, compatibleCount, availableCount);
            EnqueueLifecycleEvents(state, eventQueue, ref sequence, utcNow);
            return;
        }

        var blockedBy = state.Candidates
            .SelectMany(i => i.GetOccupiedAssignments())
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.FullEnd)
            .ThenBy(i => i.TimerId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        state.Forecast = state.CreateDto(
            Conflict,
            compatibleCount,
            availableCount,
            blockedBy?.TimerId,
            blockedBy?.Name,
            blockedBy?.Priority);
    }

    private static void HandleMainEnd(ForecastTimerState state)
    {
        if (state.Assignment is null || state.Assignment.Phase != TunerSlotPhase.MainRecording)
        {
            return;
        }

        state.Assignment.Phase = state.Input.IsPostPaddingRequired || state.Input.PostPaddingSeconds <= 0
            ? TunerSlotPhase.RequiredPadding
            : TunerSlotPhase.OptionalPadding;

        if (state.Forecast?.ForecastStatus == PaddingAtRisk)
        {
            return;
        }

        if (state.Assignment.Phase == TunerSlotPhase.OptionalPadding)
        {
            state.Forecast ??= state.CreateDto(WillRecord);
        }
    }

    private static void HandleFullEnd(ForecastTimerState state, DateTime utcNow)
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
            state.Input.EndDate.AddSeconds(Math.Max(state.Input.PostPaddingSeconds, 0)),
            state.Input.IsPostPaddingRequired || state.Input.PostPaddingSeconds <= 0
                ? TunerSlotPhase.MainRecording
                : TunerSlotPhase.MainRecording);

        slot.Assignment = assignment;
        state.Assignment = assignment;
    }

    private static void Preempt(TunerSlotState slot, ForecastTimerState requestingState, DateTime utcNow)
    {
        var assignment = slot.Assignment;
        if (assignment is null || assignment.Phase != TunerSlotPhase.OptionalPadding)
        {
            return;
        }

        assignment.Owner.MarkPaddingAtRisk(
            requestingState.Input.Id,
            requestingState.Input.Name,
            requestingState.Priority);
        assignment.FullEnd = utcNow;
        slot.Assignment = null;
        assignment.Owner.Assignment = null;
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

        var mainEnd = state.Input.EndDate;
        var fullEnd = state.Input.EndDate.AddSeconds(Math.Max(state.Input.PostPaddingSeconds, 0));
        if (mainEnd >= utcNow)
        {
            EnqueueEvent(eventQueue, new ForecastEvent(state, MainEndEvent), ref sequence);
        }

        if (fullEnd >= utcNow)
        {
            EnqueueEvent(eventQueue, new ForecastEvent(state, FullEndEvent), ref sequence);
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
        public bool IsPostPaddingRequired { get; init; }
        public RecordingStatus Status { get; init; }
        public ILiveTvService Service { get; init; }
    }

    private sealed class ForecastTimerState
    {
        public ForecastTimerState(
            ForecastTimerInput input,
            int priority,
            TunerHostState[] candidates,
            DateTime effectiveStart)
        {
            Input = input;
            Priority = priority;
            Candidates = candidates;
            EffectiveStart = effectiveStart;
        }

        public ForecastTimerInput Input { get; }
        public int Priority { get; }
        public TunerHostState[] Candidates { get; }
        public DateTime EffectiveStart { get; }
        public RecordingScheduleForecastDto Forecast { get; set; }
        public TunerSlotAssignment Assignment { get; set; }

        public void MarkPaddingAtRisk(string blockedByTimerId, string blockedByName, int blockedByPriority)
        {
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
            int? blockedByPriority = null)
        {
            return new RecordingScheduleForecastDto
            {
                TimerId = Input.Id,
                ForecastStatus = status,
                Priority = Priority,
                EffectiveStart = Input.StartDate.AddSeconds(-Math.Max(Input.PrePaddingSeconds, 0)),
                EffectiveEnd = Input.EndDate.AddSeconds(Math.Max(Input.PostPaddingSeconds, 0)),
                CompatibleTunerCount = compatibleTunerCount,
                AvailableTunerCount = availableTunerCount,
                TunerHostId = Assignment?.Slot?.Host.Info.Id,
                TunerHostName = Assignment?.Slot?.Host.Info.FriendlyName,
                TunerHostType = Assignment?.Slot?.Host.Info.Type,
                TunerHostIndex = Assignment?.Slot?.Host.Order + 1,
                TunerSlotIndex = Assignment?.Slot?.Index,
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

        public IEnumerable<TunerSlotState> GetPreemptableSlots(DateTime utcNow, int requestingPriority)
        {
            foreach (var slot in _slots)
            {
                if (slot.Assignment is { Phase: TunerSlotPhase.OptionalPadding } assignment
                    && assignment.IsOptionalPadding(utcNow)
                    && assignment.Priority > requestingPriority)
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
                state.Input.EndDate.AddSeconds(Math.Max(state.Input.PostPaddingSeconds, 0)),
                TunerSlotPhase.MainRecording);
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
            DateTime fullEnd,
            TunerSlotPhase phase)
        {
            Owner = owner;
            Slot = slot;
            TimerId = timerId;
            Name = name;
            Priority = priority;
            MainEnd = mainEnd;
            FullEnd = fullEnd;
            Phase = phase;
        }

        public ForecastTimerState Owner { get; }
        public TunerSlotState Slot { get; }
        public string TimerId { get; }
        public string Name { get; }
        public int Priority { get; }
        public DateTime MainEnd { get; }
        public DateTime FullEnd { get; set; }
        public TunerSlotPhase Phase { get; set; }

        public bool IsOptionalPadding(DateTime utcNow)
            => Phase == TunerSlotPhase.OptionalPadding && utcNow >= MainEnd && utcNow < FullEnd;
    }

    private enum TunerSlotPhase
    {
        MainRecording,
        OptionalPadding,
        RequiredPadding
    }

    private sealed record ForecastEvent(ForecastTimerState State, int Type)
    {
        public DateTime Timestamp => Type switch
        {
            FullEndEvent => State.Input.EndDate.AddSeconds(Math.Max(State.Input.PostPaddingSeconds, 0)),
            MainEndEvent => State.Input.EndDate,
            _ => State.EffectiveStart
        };
    }

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
}
