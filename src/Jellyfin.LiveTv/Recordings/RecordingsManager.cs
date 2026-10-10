using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.IO;
using Jellyfin.LiveTv.Timers;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Recordings;

/// <inheritdoc cref="IRecordingsManager" />
public sealed class RecordingsManager : IRecordingsManager, IActiveRecordingUpdater, IActiveRecordingTunerProvider, IDisposable
{
    private static readonly TimeSpan MaxEndMonitorWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan TunerReleaseTimeout = TimeSpan.FromSeconds(30);

    // How often a recording that is waiting for a tuner tries again when nothing else wakes it, for
    // tuners released by something other than a recording, such as someone who stops watching live TV.
    private static readonly TimeSpan TunerWaitPollInterval = TimeSpan.FromMinutes(1);

    // The time between the attempts of waiting recordings that try for a tuner together, which puts
    // those with a higher priority first.
    private static readonly TimeSpan TunerTurnSpacing = TimeSpan.FromMilliseconds(500);

    // A tuner device can take a moment to accept a new stream after the recording on it stops, so for this long
    // after stopping a recording, a recording keeps trying for the tuner.
    private static readonly TimeSpan StoppedTunerSettleTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StoppedTunerRetryInterval = TimeSpan.FromSeconds(1);

    // A recording that was due to start this long ago, and hasn't started yet, is still expected to start.
    private static readonly TimeSpan UpcomingRecordingGrace = TimeSpan.FromMinutes(1);

    // How long after a recording with a higher priority is due to start, a recording that waits for it tries again.
    private static readonly TimeSpan UpcomingRecordingStartDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<RecordingsManager> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IStreamHelper _streamHelper;
    private readonly TimerManager _timerManager;
    private readonly SeriesTimerManager _seriesTimerManager;
    private readonly RecordingsMetadataManager _recordingsMetadataManager;
    private readonly ITunerHostManager _tunerHostManager;

    private readonly ConcurrentDictionary<string, ActiveRecording> _activeRecordings = new(StringComparer.OrdinalIgnoreCase);

    // The recordings that are trying to get a tuner, keyed by timer id, with when each started trying.
    private readonly ConcurrentDictionary<string, DateTime> _tunerRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncNonKeyedLocker _recordingDeleteSemaphore = new();
    private readonly Lock _recordingPathLock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingsManager"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/>.</param>
    /// <param name="config">The <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
    /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="libraryMonitor">The <see cref="ILibraryMonitor"/>.</param>
    /// <param name="providerManager">The <see cref="IProviderManager"/>.</param>
    /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
    /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
    /// <param name="streamHelper">The <see cref="IStreamHelper"/>.</param>
    /// <param name="timerManager">The <see cref="TimerManager"/>.</param>
    /// <param name="seriesTimerManager">The <see cref="SeriesTimerManager"/>.</param>
    /// <param name="recordingsMetadataManager">The <see cref="RecordingsMetadataManager"/>.</param>
    /// <param name="tunerHostManager">The <see cref="ITunerHostManager"/>.</param>
    public RecordingsManager(
        ILogger<RecordingsManager> logger,
        IServerConfigurationManager config,
        IHttpClientFactory httpClientFactory,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IProviderManager providerManager,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        IStreamHelper streamHelper,
        TimerManager timerManager,
        SeriesTimerManager seriesTimerManager,
        RecordingsMetadataManager recordingsMetadataManager,
        ITunerHostManager tunerHostManager)
    {
        _logger = logger;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _fileSystem = fileSystem;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _providerManager = providerManager;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _streamHelper = streamHelper;
        _timerManager = timerManager;
        _seriesTimerManager = seriesTimerManager;
        _recordingsMetadataManager = recordingsMetadataManager;
        _tunerHostManager = tunerHostManager;

        _config.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
    }

    private string DefaultRecordingPath
    {
        get
        {
            var path = _config.GetLiveTvConfiguration().RecordingPath;

            return string.IsNullOrWhiteSpace(path)
                ? Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "recordings")
                : path;
        }
    }

    /// <inheritdoc />
    public string? GetActiveRecordingPath(string id)
        => _activeRecordings.GetValueOrDefault(id)?.Info.Path;

    /// <inheritdoc />
    public ActiveRecordingInfo? GetActiveRecordingInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _activeRecordings.IsEmpty)
        {
            return null;
        }

        foreach (var (_, recording) in _activeRecordings)
        {
            var recordingInfo = recording.Info;
            if (string.Equals(recordingInfo.Path, path, StringComparison.Ordinal)
                && !recordingInfo.CancellationTokenSource.IsCancellationRequested
                && recording.IsInProgress)
            {
                return recordingInfo;
            }
        }

        return null;
    }

    /// <inheritdoc />
    IReadOnlyDictionary<string, string> IActiveRecordingTunerProvider.GetActiveRecordingTunerHostIds()
    {
        var tunerHostIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (timerId, recording) in _activeRecordings)
        {
            var tunerHostId = recording.TunerHostId;
            if (!string.IsNullOrWhiteSpace(tunerHostId))
            {
                tunerHostIds[timerId] = tunerHostId;
            }
        }

        return tunerHostIds;
    }

    /// <inheritdoc />
    public IEnumerable<VirtualFolderInfo> GetRecordingFolders()
    {
        if (Directory.Exists(DefaultRecordingPath))
        {
            yield return new VirtualFolderInfo
            {
                Locations = [DefaultRecordingPath],
                Name = "Recordings"
            };
        }

        var customPath = _config.GetLiveTvConfiguration().MovieRecordingPath;
        if (!string.IsNullOrWhiteSpace(customPath)
            && !string.Equals(customPath, DefaultRecordingPath, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(customPath))
        {
            yield return new VirtualFolderInfo
            {
                Locations = [customPath],
                Name = "Recorded Movies",
                CollectionType = CollectionTypeOptions.movies
            };
        }

        customPath = _config.GetLiveTvConfiguration().SeriesRecordingPath;
        if (!string.IsNullOrWhiteSpace(customPath)
            && !string.Equals(customPath, DefaultRecordingPath, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(customPath))
        {
            yield return new VirtualFolderInfo
            {
                Locations = [customPath],
                Name = "Recorded Shows",
                CollectionType = CollectionTypeOptions.tvshows
            };
        }
    }

    /// <inheritdoc />
    public async Task CreateRecordingFolders()
    {
        try
        {
            var recordingFolders = GetRecordingFolders().ToArray();
            var virtualFolders = _libraryManager.GetVirtualFolders();

            var allExistingPaths = virtualFolders.SelectMany(i => i.Locations).ToList();

            var pathsAdded = new List<string>();

            foreach (var recordingFolder in recordingFolders)
            {
                var pathsToCreate = recordingFolder.Locations
                    .Where(i => !allExistingPaths.Any(p => _fileSystem.AreEqual(p, i)))
                    .ToList();

                if (pathsToCreate.Count == 0)
                {
                    continue;
                }

                var mediaPathInfos = pathsToCreate.Select(i => new MediaPathInfo(i)).ToArray();
                var libraryOptions = new LibraryOptions
                {
                    PathInfos = mediaPathInfos
                };

                try
                {
                    await _libraryManager
                        .AddVirtualFolder(recordingFolder.Name, recordingFolder.CollectionType, libraryOptions, true)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating virtual folder");
                }

                pathsAdded.AddRange(pathsToCreate);
            }

            var config = _config.GetLiveTvConfiguration();

            var pathsToRemove = config.MediaLocationsCreated
                .Except(recordingFolders.SelectMany(i => i.Locations))
                .ToList();

            if (pathsAdded.Count > 0 || pathsToRemove.Count > 0)
            {
                pathsAdded.InsertRange(0, config.MediaLocationsCreated);
                config.MediaLocationsCreated = pathsAdded.Except(pathsToRemove).Distinct().ToArray();
                _config.SaveConfiguration("livetv", config);
            }

            foreach (var path in pathsToRemove)
            {
                await RemovePathFromLibraryAsync(path).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating recording folders");
        }
    }

    private async Task RemovePathFromLibraryAsync(string path)
    {
        _logger.LogDebug("Removing path from library: {0}", path);

        var requiresRefresh = false;
        var virtualFolders = _libraryManager.GetVirtualFolders();

        foreach (var virtualFolder in virtualFolders)
        {
            if (!virtualFolder.Locations.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (virtualFolder.Locations.Length == 1)
            {
                try
                {
                    await _libraryManager.RemoveVirtualFolder(virtualFolder.Name, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing virtual folder");
                }
            }
            else
            {
                try
                {
                    _libraryManager.RemoveMediaPath(virtualFolder.Name, path);
                    requiresRefresh = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing media path");
                }
            }
        }

        if (requiresRefresh)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    /// <inheritdoc />
    public void CancelRecording(string timerId, TimerInfo? timer)
    {
        if (_activeRecordings.TryGetValue(timerId, out var recording))
        {
            if (timer is not null)
            {
                recording.UpdateTimer(timer);
            }

            recording.Info.CancellationTokenSource.Cancel();
        }
    }

    /// <inheritdoc />
    bool IActiveRecordingUpdater.TryUpdateTimer(TimerInfo timer)
    {
        ArgumentNullException.ThrowIfNull(timer);

        if (!_activeRecordings.TryGetValue(timer.Id, out var recording))
        {
            return false;
        }

        if (recording.Info.CancellationTokenSource.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Recording {TimerId} is already stopping, so the timer change does not affect it.",
                timer.Id);
            return true;
        }

        var (previousEnd, newEnd) = recording.UpdateTimer(timer);
        if (previousEnd != newEnd)
        {
            _logger.LogInformation(
                "Recording {TimerId} will now end at {EndDate} instead of {PreviousEndDate}. Post-padding: {PostPadding}.",
                timer.Id,
                newEnd,
                previousEnd,
                TimeSpan.FromSeconds(timer.PostPaddingSeconds));
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="recordingEndDate"/> is not used. The end is taken from the timer, so that changes to its
    /// post-padding apply while the recording is running.
    /// </remarks>
    public async Task RecordStream(ActiveRecordingInfo recordingInfo, BaseItem channel, DateTime recordingEndDate)
    {
        ArgumentNullException.ThrowIfNull(recordingInfo);
        ArgumentNullException.ThrowIfNull(channel);

        var timer = _timerManager.GetTimer(recordingInfo.Id) ?? recordingInfo.Timer;
        recordingInfo.Path = string.Empty;
        var recording = new ActiveRecording(recordingInfo, timer);

        if (!_activeRecordings.TryAdd(timer.Id, recording))
        {
            _logger.LogInformation("Skipping RecordStream because timer {TimerId} is already being recorded.", timer.Id);
            return;
        }

        var endMonitorTask = MonitorRecordingEndAsync(recording);

        string recordingPath = string.Empty;
        string? seriesPath = null;
        string? liveStreamId = null;
        RecordingStatus recordingStatus = RecordingStatus.Error;

        try
        {
            if (recordingInfo.CancellationTokenSource.IsCancellationRequested || timer.Status == RecordingStatus.Cancelled)
            {
                recordingStatus = RecordingStatus.Completed;
                return;
            }

            var remoteMetadata = await FetchInternetMetadata(timer, recordingInfo.CancellationTokenSource.Token).ConfigureAwait(false);
            recordingPath = GetRecordingPath(timer, remoteMetadata, out seriesPath);

            var allMediaSources = await _mediaSourceManager
                .GetPlaybackMediaSources(channel, null, true, false, recordingInfo.CancellationTokenSource.Token).ConfigureAwait(false);

            var mediaStreamInfo = allMediaSources[0];
            IDirectStreamProvider? directStreamProvider = null;
            if (mediaStreamInfo.RequiresOpening)
            {
                var liveStreamResponse = await OpenLiveStreamWithPreemptionAsync(
                    mediaStreamInfo,
                    channel,
                    timer,
                    recordingInfo.CancellationTokenSource.Token).ConfigureAwait(false);

                mediaStreamInfo = liveStreamResponse.Item1.MediaSource;
                liveStreamId = mediaStreamInfo.LiveStreamId;
                directStreamProvider = liveStreamResponse.Item2;
                recording.TunerHostId = liveStreamResponse.Item1.TunerHostId;
            }

            using var recorder = GetRecorder(mediaStreamInfo);

            recordingPath = recorder.GetOutputPath(mediaStreamInfo, recordingPath);

            // Reserve the path so concurrent recordings don't pick the same file name, but don't
            // publish it through ActiveRecordingInfo.Path until the recorder has started and the
            // file exists. Until then Path stays empty, which callers still treat as "active".
            recordingPath = ReserveUniqueRecordingPath(recording, recordingPath, timer.Id);

            _libraryMonitor.ReportFileSystemChangeBeginning(recordingPath);

            var scheduledRecordingStart = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds);
            var scheduledRecordingEnd = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);

            _logger.LogInformation(
                "Beginning recording {TimerId}. Planned duration: {Duration}, ending at {EndDate}. Pre-padding: {PrePadding}. Post-padding: {PostPadding}.",
                timer.Id,
                scheduledRecordingEnd - scheduledRecordingStart,
                scheduledRecordingEnd,
                TimeSpan.FromSeconds(timer.PrePaddingSeconds),
                TimeSpan.FromSeconds(timer.PostPaddingSeconds));
            _logger.LogInformation("Writing file to: {Path}", recordingPath);

            void OnStarted()
            {
                var activeTimer = _timerManager.GetTimer(timer.Id) ?? timer;
                if (!recording.TryMarkStarted(activeTimer))
                {
                    if (!recording.IsInProgress && !recordingInfo.CancellationTokenSource.IsCancellationRequested)
                    {
                        _logger.LogInformation("Timer {TimerId} was cancelled while its recording was starting. Stopping the recording.", timer.Id);
                        _ = recordingInfo.CancellationTokenSource.CancelAsync();
                    }

                    return;
                }

                recordingInfo.Path = recordingPath;
                activeTimer.Status = RecordingStatus.InProgress;

                try
                {
                    _timerManager.AddOrUpdate(activeTimer, false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving the in-progress status of timer {TimerId}. The recording continues.", activeTimer.Id);
                }

                _ = OnRecordingStartedAsync(activeTimer, recordingPath, seriesPath);
            }

            await recorder.Record(
                directStreamProvider,
                mediaStreamInfo,
                recordingPath,
                OnStarted,
                recordingInfo.CancellationTokenSource.Token).ConfigureAwait(false);

            recordingStatus = RecordingStatus.Completed;
            _logger.LogInformation("Recording completed: {RecordPath}", recordingPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Recording stopped: {RecordPath}", recordingPath);
            recordingStatus = RecordingStatus.Completed;
        }
        catch (LiveTvConflictException ex)
        {
            // The recording waited for a tuner, and none became available before its program ended.
            _logger.LogWarning("No tuner became available for recording {TimerId} before its program ended. {Message}", timer.Id, ex.Message);
            recordingStatus = RecordingStatus.Error;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording to {RecordPath}", recordingPath);
            recordingStatus = RecordingStatus.Error;
        }
        finally
        {
            try
            {
                var tunerReleased = true;
                if (!string.IsNullOrWhiteSpace(liveStreamId))
                {
                    try
                    {
                        await _mediaSourceManager.CloseLiveStream(liveStreamId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        tunerReleased = false;
                        _logger.LogError(ex, "Error closing live stream");
                    }
                }

                if (tunerReleased)
                {
                    // Wakes a recording with a higher priority that stopped this recording, and recordings
                    // that are waiting for a tuner.
                    recording.MarkTunerReleased();
                }

                if (!recordingInfo.CancellationTokenSource.IsCancellationRequested)
                {
                    await recordingInfo.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                }

                await endMonitorTask.ConfigureAwait(false);

                // A recording that was stopped for a recording with a higher priority before its program
                // started holds only pre-padding. Remove it and try again, so that the recording records the
                // program if a tuner becomes available before the program ends.
                var stoppedForPriorityAt = recording.StoppedForPriorityAt;
                if (stoppedForPriorityAt.HasValue
                    && stoppedForPriorityAt.Value <= (_timerManager.GetTimer(timer.Id) ?? timer).StartDate)
                {
                    _logger.LogInformation(
                        "Recording {TimerId} was stopped for a recording with a higher priority before its program started. It will try again for a tuner.",
                        timer.Id);
                    recordingStatus = RecordingStatus.Error;
                    if (!string.IsNullOrWhiteSpace(recordingPath))
                    {
                        DeletePrePaddingRecording(recordingPath);
                    }
                }

                if (!string.IsNullOrWhiteSpace(recordingPath))
                {
                    DeleteFileIfEmpty(recordingPath);
                    TriggerRefresh(recordingPath);
                    _libraryMonitor.ReportFileSystemChangeComplete(recordingPath, false);
                }
            }
            finally
            {
                // Always unregister, or this timer could not be recorded again until a restart. This
                // happens before the retry/complete handling below because PostProcessRecording waits
                // for the user's post-processor, and the recording must not appear active meanwhile.
                _activeRecordings.TryRemove(timer.Id, out _);
            }

            var finalTimer = _timerManager.GetTimer(timer.Id) ?? recordingInfo.Timer;

            var isCancelled = finalTimer.Status == RecordingStatus.Cancelled;

            if (!isCancelled
                && recordingStatus != RecordingStatus.Completed
                && DateTime.UtcNow < finalTimer.EndDate
                && finalTimer.RetryCount < 10)
            {
                const int RetryIntervalSeconds = 60;
                _logger.LogInformation("Retrying recording in {0} seconds.", RetryIntervalSeconds);

                finalTimer.Status = RecordingStatus.New;
                var retryAt = DateTime.UtcNow.AddSeconds(RetryIntervalSeconds);
                if (retryAt < finalTimer.StartDate)
                {
                    // Before the program starts, retry within the pre-padding and keep the program's start.
                    finalTimer.PrePaddingSeconds = (int)(finalTimer.StartDate - retryAt).TotalSeconds;
                }
                else
                {
                    finalTimer.PrePaddingSeconds = 0;
                    finalTimer.StartDate = retryAt;
                }

                finalTimer.RetryCount++;
                _timerManager.AddOrUpdate(finalTimer);
            }
            else if (!string.IsNullOrWhiteSpace(recordingPath) && File.Exists(recordingPath))
            {
                finalTimer.RecordingPath = recordingPath;
                finalTimer.Status = RecordingStatus.Completed;
                _timerManager.AddOrUpdate(finalTimer, false);
                await PostProcessRecording(recordingPath).ConfigureAwait(false);
            }
            else if (!isCancelled && !_disposed && _timerManager.GetTimer(timer.Id) is not null)
            {
                // Nothing was recorded. A recording stopped by a shutdown keeps its timer, so a recording
                // that was waiting for a tuner tries again when the server starts.
                _timerManager.Delete(finalTimer);
            }
        }
    }

    /// <summary>
    /// Opens a live stream for a recording. When no tuner is free, it stops a recording with a lower priority
    /// that holds a tuner the channel can use, or else waits for a tuner, so that the recording starts late
    /// rather than not at all. It gives up when the program ends.
    /// </summary>
    /// <remarks>
    /// It stops at most one recording. A tuner host reports any failure to open a stream as a conflict, so if
    /// the stream still can't be opened, for example because the channel can't be tuned, stopping more
    /// recordings might not help.
    /// </remarks>
    private async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamWithPreemptionAsync(
        MediaSourceInfo mediaStreamInfo,
        BaseItem channel,
        TimerInfo timer,
        CancellationToken cancellationToken)
    {
        DateTime? waitingSince = null;
        DateTime? stoppedRecordingAt = null;
        HashSet<string>? channelTunerHostIds = null;
        _tunerRequests[timer.Id] = DateTime.UtcNow;

        try
        {
            while (true)
            {
                DateTime? retryAt = null;

                // Recordings with a higher priority that are trying for a tuner at the same time go first.
                await WaitForTunerTurnAsync(timer.Id, cancellationToken).ConfigureAwait(false);

                try
                {
                    // Do not pass the recording cancellation token here. OpenLiveStreamInternal
                    // registers the live stream before its media probing completes. If cancellation
                    // aborts that operation before this method returns, RecordStream would not yet
                    // have the LiveStreamId needed to close the tuner.
                    var liveStream = await _mediaSourceManager.OpenLiveStreamInternal(
                        new LiveStreamRequest
                        {
                            ItemId = channel.Id,
                            OpenToken = mediaStreamInfo.OpenToken
                        },
                        CancellationToken.None).ConfigureAwait(false);

                    if (waitingSince.HasValue)
                    {
                        _logger.LogInformation("Recording {TimerId} got a tuner after waiting {Wait}.", timer.Id, DateTime.UtcNow - waitingSince.Value);
                    }

                    return liveStream;
                }
                catch (LiveTvConflictException)
                {
                    if (stoppedRecordingAt.HasValue)
                    {
                        if (DateTime.UtcNow - stoppedRecordingAt.Value < StoppedTunerSettleTime)
                        {
                            // The tuner of the recording that was stopped may not accept a stream yet.
                            await Task.Delay(StoppedTunerRetryInterval, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                    else
                    {
                        if (channelTunerHostIds is null)
                        {
                            channelTunerHostIds = await GetTunerHostIdsForChannelAsync(channel.ExternalId, cancellationToken).ConfigureAwait(false);
                            if (channelTunerHostIds.Count == 0)
                            {
                                _logger.LogWarning(
                                    "No tuner host is known to carry channel {ChannelId}, so recording {TimerId} doesn't stop a recording with a lower priority to get a tuner.",
                                    channel.ExternalId,
                                    timer.Id);
                            }
                        }

                        var (stopped, upcomingStart) = await TryStopLowerPriorityRecordingAsync(timer.Id, channelTunerHostIds, cancellationToken).ConfigureAwait(false);
                        if (stopped)
                        {
                            // Don't open a tuner for a recording that was stopped while it waited for one.
                            cancellationToken.ThrowIfCancellationRequested();
                            stoppedRecordingAt = DateTime.UtcNow;
                            continue;
                        }

                        // It didn't stop a recording because a recording with a higher priority would stop it again
                        // before its program starts. Try again once that recording has started.
                        if (upcomingStart.HasValue && upcomingStart.Value + UpcomingRecordingStartDelay > DateTime.UtcNow)
                        {
                            retryAt = upcomingStart.Value + UpcomingRecordingStartDelay;
                        }
                    }

                    // Every tuner that can record the channel is in use by recordings with the same or a higher
                    // priority, or by something else, or by a recording that a recording with a higher priority will
                    // stop first, or the stream can't be opened even after stopping a recording. Wait for a tuner to
                    // be released, unless the program ends first.
                    var programEnd = GetProgramEnd(timer);
                    if (DateTime.UtcNow >= programEnd)
                    {
                        throw;
                    }

                    if (!waitingSince.HasValue)
                    {
                        waitingSince = DateTime.UtcNow;
                        _logger.LogInformation(
                            "No tuner is available for recording {TimerId}. It will start when a tuner is released, unless its program ends first at {EndDate}.",
                            timer.Id,
                            programEnd);
                    }
                }

                await WaitForReleasedTunerAsync(timer.Id, retryAt, cancellationToken).ConfigureAwait(false);

                // A recording that waited for a tuner doesn't start once its program has ended, when only its
                // post-padding would be left to record. The schedule forecast assumes the same.
                if (DateTime.UtcNow >= GetProgramEnd(timer))
                {
                    throw new LiveTvConflictException("The program ended before a tuner became available.");
                }
            }
        }
        finally
        {
            _tunerRequests.TryRemove(timer.Id, out _);
        }
    }

    // The end of the timer's program, without post-padding. The timer can change while its recording waits.
    private DateTime GetProgramEnd(TimerInfo timer) => (_timerManager.GetTimer(timer.Id) ?? timer).EndDate;

    /// <summary>
    /// Delays an attempt to open a tuner by the number of recordings trying for a tuner that go first:
    /// those with a higher priority, or the same priority and a longer wait. When a tuner is released,
    /// waiting recordings try together, and opening live streams is serialized, so the first to try gets it.
    /// </summary>
    private async Task WaitForTunerTurnAsync(string timerId, CancellationToken cancellationToken)
    {
        if (_tunerRequests.Count < 2)
        {
            return;
        }

        var timer = _timerManager.GetTimer(timerId);
        if (timer is null)
        {
            return;
        }

        var seriesPriorities = GetSeriesTimerPriorities();
        var priority = GetRecordingPriority(timer, seriesPriorities);
        var requestedAt = _tunerRequests.TryGetValue(timerId, out var since) ? since : DateTime.MaxValue;
        var aheadCount = 0;
        foreach (var (otherTimerId, otherRequestedAt) in _tunerRequests)
        {
            if (string.Equals(otherTimerId, timerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var otherTimer = _timerManager.GetTimer(otherTimerId);
            if (otherTimer is null)
            {
                continue;
            }

            var otherPriority = GetRecordingPriority(otherTimer, seriesPriorities);
            if (otherPriority < priority || (otherPriority == priority && otherRequestedAt < requestedAt))
            {
                aheadCount++;
            }
        }

        if (aheadCount > 0)
        {
            await Task.Delay(TunerTurnSpacing * aheadCount, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until a tuner may have become available: when another recording releases its tuner, when another
    /// recording starts, which lets a recording with a higher priority stop it, at <paramref name="retryAt"/>, or
    /// after <see cref="TunerWaitPollInterval"/>.
    /// </summary>
    private async Task WaitForReleasedTunerAsync(string timerId, DateTime? retryAt, CancellationToken cancellationToken)
    {
        var wait = TunerWaitPollInterval;
        if (retryAt.HasValue && retryAt.Value - DateTime.UtcNow < wait)
        {
            wait = retryAt.Value - DateTime.UtcNow;
        }

        var wakeUps = new List<Task>();
        foreach (var (otherTimerId, recording) in _activeRecordings)
        {
            if (string.Equals(otherTimerId, timerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!recording.TunerReleased.IsCompleted)
            {
                wakeUps.Add(recording.TunerReleased);
            }

            if (!recording.Started.IsCompleted)
            {
                wakeUps.Add(recording.Started);
            }
        }

        wakeUps.Add(Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellationToken));
        await Task.WhenAny(wakeUps).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Stops a running recording with a lower priority than the requesting recording, on a tuner host that can
    /// record the requesting recording's channel, and waits for its tuner to be released. The recording with the
    /// lowest priority is stopped first, and of those, the one that loses the least of its program.
    /// </summary>
    /// <remarks>
    /// A recording that hasn't reached its program yet doesn't take a tuner that a recording with a higher priority,
    /// due to start before its program, would take back from it: it would record only pre-padding, which is deleted,
    /// and cut the other recording short for nothing. See <see cref="PreemptionLookahead"/>.
    /// </remarks>
    /// <param name="requestingTimerId">The timer id of the recording that needs a tuner.</param>
    /// <param name="tunerHostIds">The tuner hosts that carry the channel of the recording that needs a tuner.</param>
    /// <param name="requestingCancellationToken">The cancellation token of the recording that needs a tuner.</param>
    /// <returns>
    /// Whether a recording was stopped. When none was, because a recording with a higher priority would stop the
    /// requesting recording again, also when that recording is due to start.
    /// </returns>
    private async Task<(bool Stopped, DateTime? UpcomingStart)> TryStopLowerPriorityRecordingAsync(
        string requestingTimerId,
        HashSet<string> tunerHostIds,
        CancellationToken requestingCancellationToken)
    {
        if (tunerHostIds.Count == 0)
        {
            return (false, null);
        }

        // Looking up the tuner hosts of their channels reads files, so it happens before the lock.
        var upcoming = await GetUpcomingRecordingsBeforeProgramAsync(requestingTimerId, requestingCancellationToken).ConfigureAwait(false);

        ActiveRecording? stopped = null;
        var stoppedPriority = 0;
        var stoppedProgramLeft = TimeSpan.Zero;
        var requestingPriority = 0;
        PreemptionLookahead.UpcomingRecording? stoppedBy = null;

        // Choose and claim the recording under the priority lock, so priorities can't be reordered
        // meanwhile. The lock isn't held while waiting for the tuner.
        using (await RecordingPriorityLock.Instance.LockAsync(requestingCancellationToken).ConfigureAwait(false))
        {
            requestingCancellationToken.ThrowIfCancellationRequested();

            var requestingTimer = _timerManager.GetTimer(requestingTimerId);
            if (requestingTimer is null)
            {
                return (false, null);
            }

            // Rank recordings as the recording priority list and the schedule forecast do. Episodes
            // take their series' priority: their own Priority isn't updated while they are cancelled,
            // so an episode that is re-enabled later can still carry an old value.
            var seriesPriorities = GetSeriesTimerPriorities();
            requestingPriority = GetRecordingPriority(requestingTimer, seriesPriorities);
            var now = DateTime.UtcNow;
            var candidates = _activeRecordings
                .Where(pair => !string.Equals(pair.Key, requestingTimerId, StringComparison.OrdinalIgnoreCase))
                .Where(pair => pair.Value.TunerHostId is not null && tunerHostIds.Contains(pair.Value.TunerHostId))
                .Select(pair => (
                    Recording: pair.Value,
                    Priority: GetRecordingPriority(_timerManager.GetTimer(pair.Key) ?? pair.Value.Info.Timer, seriesPriorities),
                    ProgramLeft: pair.Value.GetProgramTimeRemaining(now),
                    TimeLeft: pair.Value.GetTimeRemaining(now, out _)))
                // A recording with a higher priority always gets a tuner, even if a recording with a lower
                // priority (a higher number) has to stop before its program ends. Recordings with the same or
                // a higher priority keep their tuners, and the requesting recording starts late instead.
                .Where(candidate => candidate.Priority > requestingPriority)
                .OrderByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.ProgramLeft)
                .ThenBy(candidate => candidate.TimeLeft)
                .ThenBy(candidate => candidate.Recording.Info.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lookahead = upcoming.Where(recording => recording.Priority < requestingPriority).ToList();
            var tunerUses = GetTunerUses(now, seriesPriorities);
            var tunerHosts = GetTunerHostCapacities();
            var requestingEnd = requestingTimer.EndDate.AddSeconds(Math.Max(0, requestingTimer.PostPaddingSeconds));

            foreach (var candidate in candidates)
            {
                if (lookahead.Count > 0)
                {
                    var stopsBeforeProgram = PreemptionLookahead.FindStopBeforeProgram(
                        new PreemptionLookahead.TunerUse(requestingTimerId, candidate.Recording.TunerHostId ?? string.Empty, requestingPriority, requestingTimer.EndDate, requestingEnd),
                        requestingTimer.StartDate,
                        tunerUses.Where(use => !string.Equals(use.Id, candidate.Recording.Info.Id, StringComparison.OrdinalIgnoreCase)),
                        lookahead,
                        tunerHosts);
                    if (stopsBeforeProgram is not null)
                    {
                        stoppedBy ??= stopsBeforeProgram;
                        continue;
                    }
                }

                if (candidate.Recording.TryClaimForHigherPriority(now))
                {
                    stopped = candidate.Recording;
                    stoppedPriority = candidate.Priority;
                    stoppedProgramLeft = candidate.ProgramLeft;
                    break;
                }
            }
        }

        if (stopped is null)
        {
            if (stoppedBy is null)
            {
                return (false, null);
            }

            _logger.LogInformation(
                "Recording {TimerId} (priority {Priority}) doesn't stop a recording with a lower priority yet: recording {UpcomingTimerId} (priority {UpcomingPriority}) starts at {UpcomingStart}, before this recording's program, and would stop it again. Lower numbers are higher priority.",
                requestingTimerId,
                requestingPriority,
                stoppedBy.Id,
                stoppedBy.Priority,
                stoppedBy.Start);
            return (false, stoppedBy.Start);
        }

        _logger.LogInformation(
            "Stopping recording {TimerId} (priority {Priority}) with {ProgramLeft} of its program left, to release a tuner for recording {RequestingTimerId} (priority {RequestingPriority}). Lower numbers are higher priority.",
            stopped.Info.Id,
            stoppedPriority,
            stoppedProgramLeft,
            requestingTimerId,
            requestingPriority);

        await stopped.Info.CancellationTokenSource.CancelAsync().ConfigureAwait(false);

        try
        {
            await stopped.TunerReleased
                .WaitAsync(TunerReleaseTimeout, requestingCancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Timed out waiting for recording {TimerId} to release its tuner.",
                stopped.Info.Id);
        }

        return (true, null);
    }

    /// <summary>
    /// Gets the recordings with a higher priority than a recording that are due to start before its program does,
    /// and haven't started, for <see cref="PreemptionLookahead"/>.
    /// </summary>
    /// <param name="requestingTimerId">The timer id of the recording.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recordings, in the order they start.</returns>
    private async Task<List<PreemptionLookahead.UpcomingRecording>> GetUpcomingRecordingsBeforeProgramAsync(
        string requestingTimerId,
        CancellationToken cancellationToken)
    {
        var upcoming = new List<PreemptionLookahead.UpcomingRecording>();
        var requestingTimer = _timerManager.GetTimer(requestingTimerId);
        var now = DateTime.UtcNow;
        if (requestingTimer is null || now >= requestingTimer.StartDate)
        {
            return upcoming;
        }

        var seriesPriorities = GetSeriesTimerPriorities();
        var requestingPriority = GetRecordingPriority(requestingTimer, seriesPriorities);
        var tunerHostIdsByChannel = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var timer in _timerManager.GetAll())
        {
            var start = timer.StartDate.AddSeconds(-Math.Max(0, timer.PrePaddingSeconds));
            var priority = GetRecordingPriority(timer, seriesPriorities);
            if (timer.Status != RecordingStatus.New
                || priority >= requestingPriority
                || start < now - UpcomingRecordingGrace
                || start > requestingTimer.StartDate
                || string.Equals(timer.Id, requestingTimerId, StringComparison.OrdinalIgnoreCase)
                || _activeRecordings.ContainsKey(timer.Id))
            {
                continue;
            }

            // A timer without a channel can record on any channel.
            var channelId = timer.ChannelId ?? string.Empty;
            if (!tunerHostIdsByChannel.TryGetValue(channelId, out var tunerHostIds))
            {
                tunerHostIds = string.IsNullOrWhiteSpace(channelId)
                    ? GetTunerHostCapacities().Select(host => host.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : await GetTunerHostIdsForChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
                tunerHostIdsByChannel[channelId] = tunerHostIds;
            }

            upcoming.Add(new PreemptionLookahead.UpcomingRecording(
                timer.Id,
                priority,
                start,
                timer.EndDate,
                timer.EndDate.AddSeconds(Math.Max(0, timer.PostPaddingSeconds)),
                tunerHostIds));
        }

        return upcoming
            .OrderBy(recording => recording.Start)
            .ThenBy(recording => recording.Priority)
            .ThenBy(recording => recording.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gets the running recordings that hold tuners, for <see cref="PreemptionLookahead"/>. Recordings that are being
    /// stopped are left out.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="seriesPriorities">The priorities of the series timers.</param>
    /// <returns>The recordings that hold tuners.</returns>
    private List<PreemptionLookahead.TunerUse> GetTunerUses(DateTime now, IReadOnlyDictionary<string, int> seriesPriorities)
    {
        return _activeRecordings
            .Where(pair => pair.Value.TunerHostId is not null
                && pair.Value.StoppedForPriorityAt is null
                && !pair.Value.TunerReleased.IsCompleted
                && !pair.Value.Info.CancellationTokenSource.IsCancellationRequested)
            .Select(pair => new PreemptionLookahead.TunerUse(
                pair.Key,
                pair.Value.TunerHostId ?? string.Empty,
                GetRecordingPriority(_timerManager.GetTimer(pair.Key) ?? pair.Value.Info.Timer, seriesPriorities),
                now + pair.Value.GetProgramTimeRemaining(now),
                now + pair.Value.GetTimeRemaining(now, out _)))
            .ToList();
    }

    /// <summary>
    /// Gets the configured tuner hosts with their numbers of tuners, in the order recordings try them, for
    /// <see cref="PreemptionLookahead"/>.
    /// </summary>
    /// <returns>The tuner hosts.</returns>
    private List<PreemptionLookahead.TunerHostCapacity> GetTunerHostCapacities()
    {
        return _config.GetLiveTvConfiguration().TunerHosts
            .Where(host => !string.IsNullOrWhiteSpace(host.Id))
            .Select(host => new PreemptionLookahead.TunerHostCapacity(host.Id, host.TunerCount > 0 ? host.TunerCount : null))
            .ToList();
    }

    /// <summary>
    /// Gets the configured tuner hosts that carry a channel, from their channel lists, which are cached.
    /// </summary>
    /// <param name="channelId">The channel id of a timer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ids of the tuner hosts that can record the channel.</returns>
    private async Task<HashSet<string>> GetTunerHostIdsForChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var tunerHostIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return tunerHostIds;
        }

        foreach (var host in _config.GetLiveTvConfiguration().TunerHosts)
        {
            if (string.IsNullOrWhiteSpace(host.Id))
            {
                continue;
            }

            var tunerHost = _tunerHostManager.TunerHosts
                .FirstOrDefault(i => string.Equals(i.Type, host.Type, StringComparison.OrdinalIgnoreCase));
            if (tunerHost is not BaseTunerHost baseTunerHost)
            {
                continue;
            }

            var channels = await baseTunerHost.GetChannelsForRecordingForecast(host, cancellationToken).ConfigureAwait(false);
            if (channels.Exists(channel => string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase)))
            {
                tunerHostIds.Add(host.Id);
            }
        }

        return tunerHostIds;
    }

    /// <summary>
    /// Gets the priority that a timer records with: its series timer's priority when it belongs to one,
    /// otherwise its own. Lower values are higher priority.
    /// </summary>
    /// <param name="timer">The timer.</param>
    /// <param name="seriesPriorities">The priority of each series timer, keyed by series timer id.</param>
    /// <returns>The recording priority.</returns>
    private static int GetRecordingPriority(TimerInfo timer, IReadOnlyDictionary<string, int> seriesPriorities)
        => !string.IsNullOrWhiteSpace(timer.SeriesTimerId)
            && seriesPriorities.TryGetValue(timer.SeriesTimerId, out var seriesPriority)
                ? seriesPriority
                : timer.Priority;

    private Dictionary<string, int> GetSeriesTimerPriorities()
    {
        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var seriesTimer in _seriesTimerManager.GetAll())
        {
            if (!string.IsNullOrWhiteSpace(seriesTimer.Id))
            {
                priorities[seriesTimer.Id] = seriesTimer.Priority;
            }
        }

        return priorities;
    }

    private async Task MonitorRecordingEndAsync(ActiveRecording recording)
    {
        var cancellationTokenSource = recording.Info.CancellationTokenSource;

        try
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                var remaining = recording.GetTimeRemaining(DateTime.UtcNow, out var timerChanged);
                if (remaining <= TimeSpan.Zero)
                {
                    _logger.LogInformation("Recording {TimerId} has reached its scheduled end. Stopping the recording.", recording.Info.Id);
                    await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }

                // Cap each wait: Task.Delay rejects delays over ~49.7 days, and the loop
                // re-evaluates the end time after every wake-up anyway.
                var wait = remaining < MaxEndMonitorWait ? remaining : MaxEndMonitorWait;

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
                var delayTask = Task.Delay(wait, delayCancellation.Token);
                await Task.WhenAny(delayTask, timerChanged).ConfigureAwait(false);
                await delayCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The monitor is the only thing that ends a recording on schedule. If it fails,
            // stop the recording rather than letting it run indefinitely.
            _logger.LogError(ex, "Error monitoring recording end for {TimerId}. Stopping the recording.", recording.Info.Id);
            await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task OnRecordingStartedAsync(TimerInfo timer, string recordingPath, string? seriesPath)
    {
        try
        {
            await _recordingsMetadataManager.SaveRecordingMetadata(timer, recordingPath, seriesPath).ConfigureAwait(false);
            await CreateRecordingFolders().ConfigureAwait(false);

            TriggerRefresh(recordingPath);
            await EnforceKeepUpTo(timer, seriesPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing recording start processing for {TimerId}.", timer.Id);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Set first, so that recordings stopped by the shutdown can tell, and keep their timers.
        _disposed = true;
        _recordingDeleteSemaphore.Dispose();

        foreach (var pair in _activeRecordings.ToList())
        {
            pair.Value.Info.CancellationTokenSource.Cancel();
        }
    }

    private async void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (string.Equals(e.Key, "livetv", StringComparison.OrdinalIgnoreCase))
        {
            await CreateRecordingFolders().ConfigureAwait(false);
        }
    }

    private async Task<RemoteSearchResult?> FetchInternetMetadata(TimerInfo timer, CancellationToken cancellationToken)
    {
        if (!timer.IsSeries || timer.SeriesProviderIds.Count == 0)
        {
            return null;
        }

        var query = new RemoteSearchQuery<SeriesInfo>
        {
            SearchInfo = new SeriesInfo
            {
                ProviderIds = timer.SeriesProviderIds,
                Name = timer.Name,
                MetadataCountryCode = _config.Configuration.MetadataCountryCode,
                MetadataLanguage = _config.Configuration.PreferredMetadataLanguage
            }
        };

        var results = await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(query, cancellationToken).ConfigureAwait(false);

        return results.FirstOrDefault();
    }

    private string GetRecordingPath(TimerInfo timer, RemoteSearchResult? metadata, out string? seriesPath)
    {
        var recordingPath = DefaultRecordingPath;
        var config = _config.GetLiveTvConfiguration();
        seriesPath = null;

        if (timer.IsProgramSeries)
        {
            var customRecordingPath = config.SeriesRecordingPath;
            var allowSubfolder = true;
            if (!string.IsNullOrWhiteSpace(customRecordingPath))
            {
                allowSubfolder = string.Equals(customRecordingPath, recordingPath, StringComparison.OrdinalIgnoreCase);
                recordingPath = customRecordingPath;
            }

            if (allowSubfolder && config.EnableRecordingSubfolders)
            {
                recordingPath = Path.Combine(recordingPath, "Series");
            }

            // trim trailing period from the folder name
            var folderName = _fileSystem.GetValidFilename(timer.Name).Trim().TrimEnd('.').Trim();

            if (metadata is not null && metadata.ProductionYear is not null)
            {
                folderName += " (" + metadata.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")";
            }

            // Can't use the year here in the folder name because it is the year of the episode, not the series.
            recordingPath = Path.Combine(recordingPath, folderName);

            seriesPath = recordingPath;

            if (timer.SeasonNumber.HasValue)
            {
                folderName = string.Format(
                    CultureInfo.InvariantCulture,
                    "Season {0}",
                    timer.SeasonNumber.Value);
                recordingPath = Path.Combine(recordingPath, folderName);
            }
        }
        else if (timer.IsMovie)
        {
            var customRecordingPath = config.MovieRecordingPath;
            var allowSubfolder = true;
            if (!string.IsNullOrWhiteSpace(customRecordingPath))
            {
                allowSubfolder = string.Equals(customRecordingPath, recordingPath, StringComparison.OrdinalIgnoreCase);
                recordingPath = customRecordingPath;
            }

            if (allowSubfolder && config.EnableRecordingSubfolders)
            {
                recordingPath = Path.Combine(recordingPath, "Movies");
            }

            var folderName = _fileSystem.GetValidFilename(timer.Name).Trim();
            if (timer.ProductionYear is not null)
            {
                folderName += " (" + timer.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")";
            }

            // trim trailing period from the folder name
            folderName = folderName.TrimEnd('.').Trim();

            recordingPath = Path.Combine(recordingPath, folderName);
        }
        else if (timer.IsKids)
        {
            if (config.EnableRecordingSubfolders)
            {
                recordingPath = Path.Combine(recordingPath, "Kids");
            }

            var folderName = _fileSystem.GetValidFilename(timer.Name).Trim();
            if (timer.ProductionYear is not null)
            {
                folderName += " (" + timer.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")";
            }

            // trim trailing period from the folder name
            folderName = folderName.TrimEnd('.').Trim();

            recordingPath = Path.Combine(recordingPath, folderName);
        }
        else if (timer.IsSports)
        {
            if (config.EnableRecordingSubfolders)
            {
                recordingPath = Path.Combine(recordingPath, "Sports");
            }

            recordingPath = Path.Combine(recordingPath, _fileSystem.GetValidFilename(timer.Name).Trim());
        }
        else
        {
            if (config.EnableRecordingSubfolders)
            {
                recordingPath = Path.Combine(recordingPath, "Other");
            }

            recordingPath = Path.Combine(recordingPath, _fileSystem.GetValidFilename(timer.Name).Trim());
        }

        var recordingFileName = _fileSystem.GetValidFilename(RecordingHelper.GetRecordingName(timer)).Trim() + ".ts";

        return Path.Combine(recordingPath, recordingFileName);
    }

    /// <summary>
    /// Deletes a recording that holds only pre-padding, together with the metadata saved next to it.
    /// </summary>
    /// <param name="recordingPath">The path of the recording.</param>
    private void DeletePrePaddingRecording(string recordingPath)
    {
        var paths = new List<string> { recordingPath, Path.ChangeExtension(recordingPath, ".nfo") };
        var directory = Path.GetDirectoryName(recordingPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            var thumbPrefix = Path.GetFileNameWithoutExtension(recordingPath) + "-thumb.";
            paths.AddRange(Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).StartsWith(thumbPrefix, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    _fileSystem.DeleteFile(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Error deleting {Path}, which a recording stopped before its program started left behind", path);
            }
        }
    }

    private void DeleteFileIfEmpty(string path)
    {
        var file = _fileSystem.GetFileInfo(path);

        if (file.Exists && file.Length == 0)
        {
            try
            {
                _fileSystem.DeleteFile(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting 0-byte failed recording file {Path}", path);
            }
        }
    }

    private void TriggerRefresh(string path)
    {
        _logger.LogInformation("Triggering refresh on {Path}", path);

        var item = GetAffectedBaseItem(Path.GetDirectoryName(path));
        if (item is null)
        {
            return;
        }

        _logger.LogInformation("Refreshing recording parent {Path}", item.Path);
        _providerManager.QueueRefresh(
            item.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                RefreshPaths =
                [
                    path,
                    Path.GetDirectoryName(path),
                    Path.GetDirectoryName(Path.GetDirectoryName(path))
                ]
            },
            RefreshPriority.High);
    }

    private BaseItem? GetAffectedBaseItem(string? path)
    {
        BaseItem? item = null;
        var parentPath = Path.GetDirectoryName(path);
        while (item is null && !string.IsNullOrEmpty(path))
        {
            item = _libraryManager.FindByPath(path, null);
            path = Path.GetDirectoryName(path);
        }

        if (item is not null
            && item.GetType() == typeof(Folder)
            && string.Equals(item.Path, parentPath, StringComparison.OrdinalIgnoreCase))
        {
            var parentItem = item.GetParent();
            if (parentItem is not null && parentItem is not AggregateFolder)
            {
                item = parentItem;
            }
        }

        return item;
    }

    private async Task EnforceKeepUpTo(TimerInfo timer, string? seriesPath)
    {
        if (string.IsNullOrWhiteSpace(timer.SeriesTimerId)
            || string.IsNullOrWhiteSpace(seriesPath))
        {
            return;
        }

        var seriesTimerId = timer.SeriesTimerId;
        var seriesTimer = _seriesTimerManager.GetAll()
            .FirstOrDefault(i => string.Equals(i.Id, seriesTimerId, StringComparison.OrdinalIgnoreCase));

        if (seriesTimer is null || seriesTimer.KeepUpTo <= 0)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        using (await _recordingDeleteSemaphore.LockAsync().ConfigureAwait(false))
        {
            if (_disposed)
            {
                return;
            }

            var timersToDelete = _timerManager.GetAll()
                .Where(timerInfo => timerInfo.Status == RecordingStatus.Completed
                    && !string.IsNullOrWhiteSpace(timerInfo.RecordingPath)
                    && string.Equals(timerInfo.SeriesTimerId, seriesTimerId, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(timerInfo.RecordingPath))
                .OrderByDescending(i => i.EndDate)
                .Skip(seriesTimer.KeepUpTo - 1)
                .ToList();

            DeleteLibraryItemsForTimers(timersToDelete);

            if (_libraryManager.FindByPath(seriesPath, true) is not Folder librarySeries)
            {
                return;
            }

            var episodesToDelete = librarySeries.GetItemList(
                    new InternalItemsQuery
                    {
                        OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                        IsVirtualItem = false,
                        IsFolder = false,
                        Recursive = true,
                        DtoOptions = new DtoOptions(true)
                    })
                .Where(i => i.IsFileProtocol && File.Exists(i.Path))
                .Skip(seriesTimer.KeepUpTo - 1);

            foreach (var item in episodesToDelete)
            {
                try
                {
                    _libraryManager.DeleteItem(
                        item,
                        new DeleteOptions
                        {
                            DeleteFileLocation = true
                        },
                        true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting item");
                }
            }
        }
    }

    private void DeleteLibraryItemsForTimers(List<TimerInfo> timers)
    {
        foreach (var timer in timers)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                DeleteLibraryItemForTimer(timer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting recording");
            }
        }
    }

    private void DeleteLibraryItemForTimer(TimerInfo timer)
    {
        var libraryItem = _libraryManager.FindByPath(timer.RecordingPath, false);
        if (libraryItem is not null)
        {
            _libraryManager.DeleteItem(
                libraryItem,
                new DeleteOptions
                {
                    DeleteFileLocation = true
                },
                true);
        }
        else if (File.Exists(timer.RecordingPath))
        {
            _fileSystem.DeleteFile(timer.RecordingPath);
        }

        _timerManager.Delete(timer);
    }

    private string ReserveUniqueRecordingPath(ActiveRecording recording, string path, string timerId)
    {
        // Choosing and reserving the path in one locked step keeps two recordings that start
        // together from picking the same file.
        lock (_recordingPathLock)
        {
            var uniquePath = EnsureFileUnique(path, timerId);
            recording.ReservedPath = uniquePath;
            return uniquePath;
        }
    }

    private string EnsureFileUnique(string path, string timerId)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        var index = 1;
        while (File.Exists(path) || _activeRecordings.Any(i
                   => string.Equals(i.Value.ReservedPath ?? i.Value.Info.Path, path, StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(i.Key, timerId, StringComparison.OrdinalIgnoreCase)))
        {
            name += " - " + index.ToString(CultureInfo.InvariantCulture);

            path = Path.ChangeExtension(Path.Combine(parent, name), extension);
            index++;
        }

        return path;
    }

    private IRecorder GetRecorder(MediaSourceInfo mediaSource)
    {
        if (mediaSource.RequiresLooping
            || !(mediaSource.Container ?? string.Empty).EndsWith("ts", StringComparison.OrdinalIgnoreCase)
            || (mediaSource.Protocol != MediaProtocol.File && mediaSource.Protocol != MediaProtocol.Http))
        {
            return new EncodedRecorder(_logger, _mediaEncoder, _config.ApplicationPaths, _config);
        }

        return new DirectRecorder(_logger, _httpClientFactory, _streamHelper);
    }

    private async Task PostProcessRecording(string path)
    {
        var options = _config.GetLiveTvConfiguration();
        if (string.IsNullOrWhiteSpace(options.RecordingPostProcessor))
        {
            return;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                Arguments = options.RecordingPostProcessorArguments
                    .Replace("{path}", path, StringComparison.OrdinalIgnoreCase),
                CreateNoWindow = true,
                ErrorDialog = false,
                FileName = options.RecordingPostProcessor,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            };
            process.EnableRaisingEvents = true;

            _logger.LogInformation("Running recording post processor {0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            process.Start();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("Recording post-processing script completed with exit code {ExitCode}", process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running recording post processor");
        }
    }
}
