using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.LiveTv.Recordings;

/// <summary>
/// A recording that <see cref="RecordingsManager"/> is running: the public <see cref="ActiveRecordingInfo"/>
/// together with the state used to end it on schedule, and to stop it early when a recording with a higher
/// priority needs its tuner.
/// </summary>
internal sealed class ActiveRecording
{
    private readonly Lock _sync = new();
    private readonly TaskCompletionSource _tunerReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _startedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _timerChanged = CreateTimerChangedSignal();
    private DateTime _endDate;
    private int _postPaddingSeconds;
    private bool _started;
    private bool _cancelledBeforeStart;
    private DateTime? _stoppedForPriorityAt;

    public ActiveRecording(ActiveRecordingInfo info, TimerInfo timer)
    {
        Info = info;
        SetTimer(timer);
    }

    public ActiveRecordingInfo Info { get; }

    /// <summary>
    /// Gets or sets the output file chosen for this recording. It is set before the recorder starts,
    /// while <see cref="ActiveRecordingInfo.Path"/> stays empty until the file exists.
    /// </summary>
    public string? ReservedPath { get; set; }

    /// <summary>
    /// Gets or sets the tuner host that the recording's live stream was opened on. It stays <c>null</c> until
    /// the live stream is open, and for sources that are not opened on a tuner host.
    /// </summary>
    public string? TunerHostId { get; set; }

    /// <summary>
    /// Gets a task that completes once the recording has released its tuner.
    /// </summary>
    public Task TunerReleased => _tunerReleased.Task;

    /// <summary>
    /// Gets a task that completes once the recording has started writing its file.
    /// </summary>
    public Task Started => _startedSignal.Task;

    /// <summary>
    /// Gets when the recording was claimed to be stopped for a recording with a higher priority, or
    /// <c>null</c> if it wasn't.
    /// </summary>
    public DateTime? StoppedForPriorityAt
    {
        get
        {
            lock (_sync)
            {
                return _stoppedForPriorityAt;
            }
        }
    }

    public bool IsInProgress
    {
        get
        {
            lock (_sync)
            {
                return _started;
            }
        }
    }

    private DateTime ScheduledEnd => _endDate.AddSeconds(_postPaddingSeconds);

    /// <summary>
    /// Applies a changed timer and wakes anything waiting on the scheduled end.
    /// </summary>
    /// <param name="timer">The changed timer.</param>
    /// <returns>The scheduled end, including post-padding, before and after the change.</returns>
    public (DateTime PreviousEnd, DateTime NewEnd) UpdateTimer(TimerInfo timer)
    {
        lock (_sync)
        {
            var previousEnd = ScheduledEnd;
            SetTimer(timer);

            if (!_started && timer.Status == RecordingStatus.Cancelled)
            {
                _cancelledBeforeStart = true;
            }

            SignalTimerChanged();
            return (previousEnd, ScheduledEnd);
        }
    }

    /// <summary>
    /// Marks the recording as started, unless it was cancelled first or has already started. The timer is
    /// applied, so anything waiting on the scheduled end is woken.
    /// </summary>
    /// <param name="timer">The current timer, which may have changed while the recording was starting.</param>
    /// <returns><c>true</c> if the caller should treat the recording as started.</returns>
    public bool TryMarkStarted(TimerInfo timer)
    {
        lock (_sync)
        {
            if (_started
                || _cancelledBeforeStart
                || timer.Status == RecordingStatus.Cancelled
                || Info.CancellationTokenSource.IsCancellationRequested)
            {
                return false;
            }

            SetTimer(timer);
            SignalTimerChanged();
            _started = true;
            _startedSignal.TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// Gets the time left until the scheduled end.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <param name="timerChanged">A task that completes the next time the timer changes.</param>
    /// <returns>The time left, which is zero or negative once the end has passed.</returns>
    public TimeSpan GetTimeRemaining(DateTime utcNow, out Task timerChanged)
    {
        lock (_sync)
        {
            timerChanged = _timerChanged.Task;
            return ScheduledEnd - utcNow;
        }
    }

    /// <summary>
    /// Gets how much of the program is left to record.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <returns>The time left until the program ends, which is zero once it has ended.</returns>
    public TimeSpan GetProgramTimeRemaining(DateTime utcNow)
    {
        lock (_sync)
        {
            var remaining = _endDate - utcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Claims this recording to be stopped for a recording with a higher priority, so that only one recording
    /// stops it. The caller compares the priorities.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <returns><c>true</c> if the caller should stop this recording; otherwise, <c>false</c>.</returns>
    public bool TryClaimForHigherPriority(DateTime utcNow)
    {
        lock (_sync)
        {
            if (!_started || _stoppedForPriorityAt.HasValue || Info.CancellationTokenSource.IsCancellationRequested)
            {
                return false;
            }

            _stoppedForPriorityAt = utcNow;
            return true;
        }
    }

    /// <summary>
    /// Signals that the recording has released its tuner, or that it never opened one.
    /// </summary>
    public void MarkTunerReleased() => _tunerReleased.TrySetResult();

    private static TaskCompletionSource CreateTimerChangedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SetTimer(TimerInfo timer)
    {
        Info.Timer = timer;
        _endDate = timer.EndDate;

        _postPaddingSeconds = Math.Max(0, timer.PostPaddingSeconds);
    }

    private void SignalTimerChanged()
    {
        var signal = _timerChanged;
        _timerChanged = CreateTimerChangedSignal();
        signal.TrySetResult();
    }
}
