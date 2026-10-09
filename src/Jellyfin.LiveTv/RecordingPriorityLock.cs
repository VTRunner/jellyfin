#pragma warning disable CS1591

using AsyncKeyedLock;

namespace Jellyfin.LiveTv;

internal static class RecordingPriorityLock
{
    public static readonly AsyncNonKeyedLocker Instance = new(1);
}
