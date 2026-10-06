using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.LiveTv;

/// <summary>
/// Maintains the synchronization state required by the active recording implementation.
/// </summary>
internal static class ActiveRecordingInfoState
{
    private static readonly ConditionalWeakTable<ActiveRecordingInfo, State> States = new();

    internal static void UpdateTimer(ActiveRecordingInfo info, TimerInfo timer)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(timer);
        States.GetOrCreateValue(info).UpdateTimer(info, timer);
    }

    internal static bool IsInProgress(ActiveRecordingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.TryGetValue(info, out var state) && state.IsInProgress();
    }

    internal static bool TryMarkStarted(ActiveRecordingInfo info, TimerInfo timer)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(timer);
        return States.GetOrCreateValue(info).TryMarkStarted(info, timer);
    }

    internal static bool TryGetOptionalPostPaddingState(ActiveRecordingInfo info, DateTime utcNow, out TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.GetOrCreateValue(info).TryGetOptionalPostPaddingState(info, utcNow, out remaining);
    }

    internal static bool TryClaimOptionalPostPadding(ActiveRecordingInfo info, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.GetOrCreateValue(info).TryClaimOptionalPostPadding(info, utcNow);
    }

    internal static bool TryClaimEndCancellation(ActiveRecordingInfo info, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.GetOrCreateValue(info).TryClaimEndCancellation(utcNow);
    }

    internal static (DateTime EndDate, int PostPaddingSeconds, Task TimerChangedTask) GetTimerState(ActiveRecordingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.GetOrCreateValue(info).GetTimerState();
    }

    internal static void ReservePath(ActiveRecordingInfo info, string path)
    {
        ArgumentNullException.ThrowIfNull(info);
        States.GetOrCreateValue(info).ReservedPath = path;
    }

    internal static string? GetReservedPath(ActiveRecordingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.TryGetValue(info, out var state) ? state.ReservedPath : null;
    }

    internal static Task GetTunerReleasedTask(ActiveRecordingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return States.GetOrCreateValue(info).TunerReleasedTask;
    }

    internal static void TunerReleased(ActiveRecordingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        States.GetOrCreateValue(info).TunerReleased();
    }

    private static TaskCompletionSource<bool> CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class State
    {
        private readonly Lock _sync = new();
        private readonly TaskCompletionSource<bool> _tunerReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<bool> _timerChanged = CreateSignal();
        private DateTime _endDate;
        private int _postPaddingSeconds;
        private bool _isPostPaddingRequired;
        private RecordingStatus _status;
        private bool _postPaddingPreemptionClaimed;
        private bool _endCancellationClaimed;

        internal Task TunerReleasedTask => _tunerReleased.Task;

        internal string? ReservedPath { get; set; }

        private DateTime EffectiveEndDate => _endDate.AddSeconds(_postPaddingSeconds);

        internal void UpdateTimer(ActiveRecordingInfo info, TimerInfo timer)
        {
            lock (_sync)
            {
                SetTimerState(info, timer);

                if (_status != RecordingStatus.InProgress
                    && timer.Status == RecordingStatus.Cancelled)
                {
                    _status = RecordingStatus.Cancelled;
                }

                var signal = _timerChanged;
                _timerChanged = CreateSignal();
                signal.TrySetResult(true);
            }
        }

        internal bool IsInProgress()
        {
            lock (_sync)
            {
                return _status == RecordingStatus.InProgress;
            }
        }

        internal bool TryMarkStarted(ActiveRecordingInfo info, TimerInfo timer)
        {
            lock (_sync)
            {
                if (_status == RecordingStatus.InProgress || !CanStartRecording(info, timer))
                {
                    return false;
                }

                SetTimerState(info, timer);
                _status = RecordingStatus.InProgress;
                return true;
            }
        }

        internal bool TryGetOptionalPostPaddingState(ActiveRecordingInfo info, DateTime utcNow, out TimeSpan remaining)
        {
            lock (_sync)
            {
                if (info.CancellationTokenSource.IsCancellationRequested)
                {
                    remaining = default;
                    return false;
                }

                if (!IsInOptionalPostPaddingWindow(utcNow, out var effectiveEnd))
                {
                    remaining = default;
                    return false;
                }

                remaining = effectiveEnd - utcNow;
                return true;
            }
        }

        internal bool TryClaimOptionalPostPadding(ActiveRecordingInfo info, DateTime utcNow)
        {
            lock (_sync)
            {
                if (info.CancellationTokenSource.IsCancellationRequested)
                {
                    return false;
                }

                if (_postPaddingPreemptionClaimed)
                {
                    return false;
                }

                if (!IsInOptionalPostPaddingWindow(utcNow, out _))
                {
                    return false;
                }

                _postPaddingPreemptionClaimed = true;
                return true;
            }
        }

        internal bool TryClaimEndCancellation(DateTime utcNow)
        {
            lock (_sync)
            {
                if (_endCancellationClaimed)
                {
                    return false;
                }

                if (_status != RecordingStatus.InProgress)
                {
                    return false;
                }

                if (utcNow < EffectiveEndDate)
                {
                    return false;
                }

                _endCancellationClaimed = true;
                return true;
            }
        }

        internal (DateTime EndDate, int PostPaddingSeconds, Task TimerChangedTask) GetTimerState()
        {
            lock (_sync)
            {
                return (_endDate, _postPaddingSeconds, _timerChanged.Task);
            }
        }

        internal void TunerReleased()
        {
            _tunerReleased.TrySetResult(true);
        }

        private void SetTimerState(ActiveRecordingInfo info, TimerInfo timer)
        {
            info.Timer = timer;
            _endDate = timer.EndDate;
            _postPaddingSeconds = timer.PostPaddingSeconds;
            _isPostPaddingRequired = timer.IsPostPaddingRequired;
        }

        private bool CanStartRecording(ActiveRecordingInfo info, TimerInfo timer)
        {
            return !info.CancellationTokenSource.IsCancellationRequested
                && _status != RecordingStatus.Cancelled
                && timer.Status != RecordingStatus.Cancelled;
        }

        private bool IsInOptionalPostPaddingWindow(DateTime utcNow, out DateTime effectiveEnd)
        {
            effectiveEnd = default;

            if (_status != RecordingStatus.InProgress
                || _isPostPaddingRequired
                || _postPaddingSeconds <= 0)
            {
                return false;
            }

            effectiveEnd = EffectiveEndDate;
            return utcNow >= _endDate && utcNow < effectiveEnd;
        }
    }
}
