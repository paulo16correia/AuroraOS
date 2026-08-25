using System.Diagnostics;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Resources;

/// <summary>
/// Reads host load using only what means the same thing on macOS, Linux and Windows.
/// </summary>
/// <remarks>
/// Anything that does not travel is reported as null rather than approximated. RFC 033's own
/// instruction for a missing metric is to assume UNKNOWN and admit conservatively, which is the
/// opposite of the tempting default where an unread number is treated as a healthy one.
/// </remarks>
public sealed class SystemResourceProbe : IResourceProbe
{
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSampleAt;

    public SystemResourceProbe(IClock clock)
    {
        _clock = clock;
        _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
        _lastSampleAt = clock.UtcNow;
    }

    public ResourceReading Read() => new(SampleCpu(), SampleMemory(), SampleDisk());

    /// <summary>
    /// Processor use since the last reading, as a fraction of one machine.
    /// </summary>
    /// <remarks>
    /// Sampled from this process rather than the host: there is no portable way to read
    /// whole-machine CPU, and Aurora's own consumption is both measurable everywhere and the part
    /// it can actually do something about.
    /// </remarks>
    private double? SampleCpu()
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.UtcNow;
            TimeSpan elapsed = now - _lastSampleAt;

            // Two readings closer together than a millisecond say nothing about load.
            if (elapsed < TimeSpan.FromMilliseconds(1))
            {
                return null;
            }

            try
            {
                TimeSpan used = Process.GetCurrentProcess().TotalProcessorTime;
                var fraction = (used - _lastCpuTime).TotalMilliseconds
                               / (elapsed.TotalMilliseconds * Environment.ProcessorCount);

                _lastCpuTime = used;
                _lastSampleAt = now;

                return Math.Clamp(fraction, 0, 1);
            }
            catch (Exception denied) when (denied is InvalidOperationException or PlatformNotSupportedException)
            {
                return null;
            }
        }
    }

    private static double? SampleMemory()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();

        return info.TotalAvailableMemoryBytes <= 0
            ? null
            : Math.Clamp((double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes, 0, 1);
    }

    private static double? SampleDisk()
    {
        try
        {
            DriveInfo? root = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.IsReady && d.TotalSize > 0
                    && AppContext.BaseDirectory.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal));

            return root is null
                ? null
                : Math.Clamp(1.0 - ((double)root.AvailableFreeSpace / root.TotalSize), 0, 1);
        }
        catch (Exception unavailable) when (unavailable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
