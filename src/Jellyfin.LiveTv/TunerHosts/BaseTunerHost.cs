#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public abstract class BaseTunerHost
    {
        private readonly ConcurrentDictionary<string, List<ChannelInfo>> _cache;

        protected BaseTunerHost(IServerConfigurationManager config, ILogger<BaseTunerHost> logger, IFileSystem fileSystem)
        {
            Config = config;
            Logger = logger;
            FileSystem = fileSystem;
            _cache = new ConcurrentDictionary<string, List<ChannelInfo>>();
        }

        protected IServerConfigurationManager Config { get; }

        protected ILogger<BaseTunerHost> Logger { get; }

        protected IFileSystem FileSystem { get; }

        public virtual bool IsSupported => true;

        public abstract string Type { get; }

        protected virtual string ChannelIdPrefix => Type + "_";

        protected abstract Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken);

        public async Task<List<ChannelInfo>> GetChannels(TunerHostInfo tuner, bool enableCache, CancellationToken cancellationToken)
        {
            var key = tuner.Id;

            if (enableCache && !string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out List<ChannelInfo> cache))
            {
                return cache;
            }

            var list = await GetChannelsInternal(tuner, cancellationToken).ConfigureAwait(false);
            // logger.LogInformation("Channels from {0}: {1}", tuner.Url, JsonSerializer.SerializeToString(list));

            if (!string.IsNullOrEmpty(key) && list.Count > 0)
            {
                _cache[key] = list;
            }

            return list;
        }

        protected virtual IList<TunerHostInfo> GetTunerHosts()
        {
            return Config.GetLiveTvConfiguration().TunerHosts
                .Where(i => string.Equals(i.Type, Type, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Gets channels for recording forecasting, falling back to the per-tuner disk cache
        /// when the live tuner channel query fails.
        /// </summary>
        /// <param name="tuner">The configured tuner host.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cached or live tuner channels.</returns>
        public async Task<List<ChannelInfo>> GetChannelsForRecordingForecast(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tuner.Id))
            {
                return [];
            }

            // Forecasting is a read-only calculation. Prefer the existing per-tuner
            // channel cache so a Schedule refresh does not force a network channel scan.
            // The normal Live TV channel refresh continues to update this cache.
            var channelCacheFile = Path.Combine(Config.ApplicationPaths.CachePath, tuner.Id + "_channels");
            try
            {
                var readStream = AsyncFile.OpenRead(channelCacheFile);
                await using (readStream.ConfigureAwait(false))
                {
                    var cachedChannels = await JsonSerializer.DeserializeAsync<List<ChannelInfo>>(readStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (cachedChannels is { Count: > 0 })
                    {
                        return cachedChannels;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // Fall through to a live query when no cache exists yet.
            }
            catch (IOException ex)
            {
                Logger.LogDebug(ex, "Error reading channel cache for recording forecast for tuner host {TunerHostId}; trying live data", tuner.Id);
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Error deserializing channel cache for recording forecast for tuner host {TunerHostId}; trying live data", tuner.Id);
            }

            try
            {
                var channels = await GetChannels(tuner, true, cancellationToken).ConfigureAwait(false);
                if (channels.Count > 0)
                {
                    return channels;
                }

                Logger.LogDebug("No live channels returned for recording forecast for tuner host {TunerHostId}", tuner.Id);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Error getting live channels for recording forecast for tuner host {TunerHostId}", tuner.Id);
            }

            return [];
        }

        public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
        {
            var list = new List<ChannelInfo>();

            var hosts = GetTunerHosts();

            foreach (var host in hosts)
            {
                var channelCacheFile = Path.Combine(Config.ApplicationPaths.CachePath, host.Id + "_channels");

                try
                {
                    var channels = await GetChannels(host, enableCache, cancellationToken).ConfigureAwait(false);
                    var newChannels = channels.Where(i => !list.Any(l => string.Equals(i.Id, l.Id, StringComparison.OrdinalIgnoreCase))).ToList();

                    list.AddRange(newChannels);

                    if (!enableCache)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(channelCacheFile));
                            var writeStream = AsyncFile.OpenWrite(channelCacheFile);
                            await using (writeStream.ConfigureAwait(false))
                            {
                                await JsonSerializer.SerializeAsync(writeStream, channels, cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error getting channel list");

                    if (enableCache)
                    {
                        try
                        {
                            var readStream = AsyncFile.OpenRead(channelCacheFile);
                            await using (readStream.ConfigureAwait(false))
                            {
                                var channels = await JsonSerializer
                                    .DeserializeAsync<List<ChannelInfo>>(readStream, cancellationToken: cancellationToken)
                                    .ConfigureAwait(false);
                                list.AddRange(channels);
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }

            return list;
        }

        protected abstract Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken);

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            if (IsValidChannelId(channelId))
            {
                var hosts = GetTunerHosts();

                foreach (var host in hosts)
                {
                    try
                    {
                        var channels = await GetChannels(host, true, cancellationToken).ConfigureAwait(false);
                        var channelInfo = channels.FirstOrDefault(i => string.Equals(i.Id, channelId, StringComparison.OrdinalIgnoreCase));

                        if (channelInfo is not null)
                        {
                            return await GetChannelStreamMediaSources(host, channelInfo, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error getting channels");
                    }
                }
            }

            return new List<MediaSourceInfo>();
        }

        protected abstract Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken);

        public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            if (!IsValidChannelId(channelId))
            {
                throw new FileNotFoundException();
            }

            var hosts = GetTunerHosts();

            var hostsWithChannel = new List<Tuple<TunerHostInfo, ChannelInfo>>();

            foreach (var host in hosts)
            {
                try
                {
                    var channels = await GetChannels(host, true, cancellationToken).ConfigureAwait(false);
                    var channelInfo = channels.FirstOrDefault(i => string.Equals(i.Id, channelId, StringComparison.OrdinalIgnoreCase));

                    if (channelInfo is not null)
                    {
                        hostsWithChannel.Add(new Tuple<TunerHostInfo, ChannelInfo>(host, channelInfo));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error getting channels");
                }
            }

            foreach (var hostTuple in hostsWithChannel)
            {
                var host = hostTuple.Item1;
                var channelInfo = hostTuple.Item2;

                try
                {
                    var liveStream = await GetChannelStream(host, channelInfo, streamId, currentLiveStreams, cancellationToken).ConfigureAwait(false);
                    var startTime = DateTime.UtcNow;
                    await liveStream.Open(cancellationToken).ConfigureAwait(false);
                    var endTime = DateTime.UtcNow;
                    Logger.LogInformation("Live stream opened after {0}ms", (endTime - startTime).TotalMilliseconds);
                    return liveStream;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error opening tuner");
                }
            }

            throw new LiveTvConflictException("Unable to find host to play channel");
        }

        protected virtual bool IsValidChannelId(string channelId)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            return channelId.StartsWith(ChannelIdPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
