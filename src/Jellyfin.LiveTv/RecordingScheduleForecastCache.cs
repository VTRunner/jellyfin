using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.LiveTv;

/// <summary>
/// Persists small pieces of tuner discovery data used by the recording schedule forecast.
/// </summary>
internal static class RecordingScheduleForecastCache
{
    private static readonly TimeSpan TunerCountCacheLifetime = TimeSpan.FromDays(7);

    public static async Task<int> GetTunerCountAsync(
        string cachePath,
        string tunerHostId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cachePath) || string.IsNullOrWhiteSpace(tunerHostId))
        {
            return 0;
        }

        var filePath = Path.Combine(cachePath, tunerHostId + "_tunercount");
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TunerCountCacheLifetime)
            {
                return 0;
            }

            var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tunerCount)
                ? tunerCount
                : 0;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static async Task SaveTunerCountAsync(
        string cachePath,
        string tunerHostId,
        int tunerCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cachePath)
            || string.IsNullOrWhiteSpace(tunerHostId)
            || tunerCount <= 0)
        {
            return;
        }

        var filePath = Path.Combine(cachePath, tunerHostId + "_tunercount");
        try
        {
            Directory.CreateDirectory(cachePath);
            await File.WriteAllTextAsync(
                filePath,
                tunerCount.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            // Failure to persist the optimization must not affect the forecast.
        }
        catch (UnauthorizedAccessException)
        {
            // Failure to persist the optimization must not affect the forecast.
        }
    }
}
