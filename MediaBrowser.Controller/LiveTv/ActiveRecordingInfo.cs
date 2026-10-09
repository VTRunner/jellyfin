#nullable disable

#pragma warning disable CS1591

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.LiveTv;

namespace MediaBrowser.Controller.LiveTv
{
    public class ActiveRecordingInfo
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool> _timerChanged = CreateSignal();
        private readonly TaskCompletionSource<bool> _tunerReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TimerInfo _timer;
        private DateTime _endDate;
        private int _postPaddingSeconds;
        private bool _isPostPaddingRequired;
        private int _priority;
        private RecordingStatus _status;
        private bool _postPaddingPreemptionClaimed;
        private bool _endCancellationClaimed;

        public string Id { get; set; }

        public string Path { get; set; }

        public TimerInfo Timer
        {
            get
            {
                lock (_sync)
                {
                    return _timer;
                }
            }
        }

        public CancellationTokenSource CancellationTokenSource { get; set; }

        public string TunerHostId { get; set; }

        public Task TunerReleasedTask => _tunerReleased.Task;

        public bool IsInProgress
        {
            get
            {
                lock (_sync)
                {
                    return _status == RecordingStatus.InProgress;
                }
            }
        }

        public void UpdateTimer(TimerInfo timer)
        {
            ArgumentNullException.ThrowIfNull(timer);

            lock (_sync)
            {
                _timer = timer;
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

        public bool TryMarkStarted(TimerInfo timer)
        {
            ArgumentNullException.ThrowIfNull(timer);

            lock (_sync)
            {
                if (CancellationTokenSource.IsCancellationRequested
                    || _status == RecordingStatus.Cancelled
                    || timer.Status == RecordingStatus.Cancelled)
                {
                    return false;
                }

                _timer = timer;
                _status = RecordingStatus.InProgress;
                _endDate = timer.EndDate;
                _postPaddingSeconds = timer.PostPaddingSeconds;
                _isPostPaddingRequired = timer.IsPostPaddingRequired;
                _priority = timer.Priority;
                return true;
            }
        }

        public bool TryGetOptionalPostPaddingState(DateTime utcNow, out int priority, out TimeSpan remaining)
        {
            lock (_sync)
            {
                var postPaddingSeconds = _postPaddingSeconds;
                var isPostPaddingRequired = _isPostPaddingRequired;
                var effectiveEnd = _endDate.AddSeconds(postPaddingSeconds);
                if (_status != RecordingStatus.InProgress
                    || isPostPaddingRequired
                    || postPaddingSeconds <= 0
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

        public bool TryClaimOptionalPostPadding(DateTime utcNow, int requestingPriority)
        {
            lock (_sync)
            {
                var postPaddingSeconds = _postPaddingSeconds;
                var isPostPaddingRequired = _isPostPaddingRequired;
                if (_postPaddingPreemptionClaimed
                    || _status != RecordingStatus.InProgress
                    || isPostPaddingRequired
                    || postPaddingSeconds <= 0
                    || _priority <= requestingPriority)
                {
                    return false;
                }

                var effectiveEnd = _endDate.AddSeconds(postPaddingSeconds);
                if (utcNow < _endDate || utcNow >= effectiveEnd)
                {
                    return false;
                }

                _postPaddingPreemptionClaimed = true;
                return true;
            }
        }


        public bool TryClaimEndCancellation(DateTime utcNow)
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

        public (DateTime EndDate, int PostPaddingSeconds, bool IsPostPaddingRequired, Task TimerChangedTask) GetTimerState()
        {
            lock (_sync)
            {
                return (_endDate, _postPaddingSeconds, _isPostPaddingRequired, _timerChanged.Task);
            }
        }

        public void TunerReleased()
            => _tunerReleased.TrySetResult(true);

        private static TaskCompletionSource<bool> CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
