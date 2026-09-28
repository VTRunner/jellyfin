using System;
using System.Runtime.CompilerServices;
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
        return States.GetOrCreateValue(info).IsInProgress();
    }

    internal static bool TryMarkStarted(ActiveRecordingInfo info, TimerInfo timer)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(timer);
        return States.GetOrCreateValue(info).TryMarkStarted(info, timer);
    }

    internal static bool TryGetOptionalPostPaddingState(ActiveRecordingInfo info, DateTime utcNow, out int priority, out TimeSpan remaining)
    {
        return States.GetOrCreateValue(info).TryGetOptionalPostPaddingState(utcNow, out priority, out remaining);
    }

    internal static bool TryClaimOptionalPostPadding(ActiveRecordingInfo info, DateTime utcNow)
    {
        return States.GetOrCreateValue(info).TryClaimOptionalPostPadding(utcNow);
    }

    internal static bool TryClaimEndCancellation(ActiveRecordingInfo info, DateTime utcNow)
    {
        return States.GetOrCreateValue(info).TryClaimEndCancellation(utcNow);
    }

    internal static (DateTime EndDate, int PostPaddingSeconds, Task TimerChangedTask) GetTimerState(ActiveRecordingInfo info)
    {
        return States.GetOrCreateValue(info).GetTimerState();
    }

    internal static Task GetTunerReleasedTask(ActiveRecordingInfo info)
    {
        return States.GetOrCreateValue(info).TunerReleasedTask;
    }

    internal static void TunerReleased(ActiveRecordingInfo info)
    {
        States.GetOrCreateValue(info).TunerReleased();
    }

    private static TaskCompletionSource<bool> CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class State
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _tunerReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _timerChanged = CreateSignal();
        private DateTime _endDate;
        private int _postPaddingSeconds;
        private bool _isPostPaddingRequired;
        private int _priority;
        private RecordingStatus _status;
        private bool _postPaddingPreemptionClaimed;
        private bool _endCancellationClaimed;

        internal Task TunerReleasedTask => _tunerReleased.Task;

        internal void UpdateTimer(ActiveRecordingInfo info, TimerInfo timer)
        {
            lock (_sync)
            {
                info.Timer = timer;
                _endDate = timer.EndDate;
                _postPaddingSeconds = timer.PostPaddingSeconds;
                _isPostPaddingRequired = timer.IsPostPaddingRequired;
                _priority = timer.Priority;

                // Preserve the active-recording state when updating an active timer.
                // TimerInfo.Status can lag the internal recording state during startup.
                if (_status != RecordingStatus.InProgress)
                {
                    _status = timer.Status;
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
                if (info.CancellationTokenSource.IsCancellationRequested
                    || _status == RecordingStatus.Cancelled
                    || timer.Status == RecordingStatus.Cancelled)
                {
                    return false;
                }

                info.Timer = timer;
                _status = RecordingStatus.InProgress;
                _endDate = timer.EndDate;
                _postPaddingSeconds = timer.PostPaddingSeconds;
                _isPostPaddingRequired = timer.IsPostPaddingRequired;
                _priority = timer.Priority;
                return true;
            }
        }

        internal bool TryGetOptionalPostPaddingState(DateTime utcNow, out int priority, out TimeSpan remaining)
        {
            lock (_sync)
            {
                var effectiveEnd = _endDate.AddSeconds(_postPaddingSeconds);
                if (_status != RecordingStatus.InProgress
                    || _isPostPaddingRequired
                    || _postPaddingSeconds <= 0
                    || utcNow < _endDate
                    || utcNow >= effectiveEnd)
                {
                    priority = 0;
                    remaining = default;
                    return false;
                }

                priority = _priority;
                remaining = effectiveEnd - utcNow;
                return true;
            }
        }

        internal bool TryClaimOptionalPostPadding(DateTime utcNow)
        {
            lock (_sync)
            {
                if (_postPaddingPreemptionClaimed
                    || _status != RecordingStatus.InProgress
                    || _isPostPaddingRequired
                    || _postPaddingSeconds <= 0)
                {
                    return false;
                }

                var effectiveEnd = _endDate.AddSeconds(_postPaddingSeconds);
                if (utcNow < _endDate || utcNow >= effectiveEnd)
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
                if (_endCancellationClaimed || _status != RecordingStatus.InProgress)
                {
                    return false;
                }

                var effectiveEnd = _endDate.AddSeconds(_postPaddingSeconds);
                if (utcNow < effectiveEnd)
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
    }
}
