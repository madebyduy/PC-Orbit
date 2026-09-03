using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PcOrbit.Adapters.Windows.Interop;
using PcOrbit.Core.Abstractions;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The live gauges, read from documented Windows APIs and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// No vendor SDK, no sensor library, no WMI polling: this runs once a second, so it uses
/// <c>GetSystemTimes</c>, <c>GlobalMemoryStatusEx</c>, <see cref="DriveInfo"/> and
/// <see cref="NetworkInterface"/> — all cheap, all available to a standard user (spec 21.10).
/// </para>
/// <para>
/// What is deliberately <em>not</em> here: CPU and GPU temperature, fan RPM and acoustic level.
/// Windows exposes no vendor-independent way to read those (spec 8.3.1 lists them as unreadable),
/// and a plausible-looking number in a UI is worse than an honest blank. They stay null, and the
/// UI says "we could not read this" instead of inventing 42 °C.
/// </para>
/// </remarks>
public sealed partial class WindowsLiveMetrics : ILiveMetrics
{
    private readonly object _gate = new();

    private ulong _lastIdle;
    private ulong _lastBusy;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTimeOffset _lastNetworkAt;

    public LiveMetrics Sample()
    {
        lock (_gate)
        {
            // Each reader is called exactly once per sample. The network one carries the previous
            // byte counters, so calling it twice would measure a zero-length interval.
            (double? ramUsed, double? ramTotal) = ReadMemory();
            (double? diskUsed, double? diskTotal) = ReadDisk();
            (string? adapter, double? link, double? down, double? up) = ReadNetwork();
            PowerState power = SystemInterop.ReadPowerState();

            return new LiveMetrics(
                Uptime: ReadUptime(),
                CpuPercent: ReadCpuPercent(),
                RamUsedGb: ramUsed,
                RamTotalGb: ramTotal,
                DiskUsedGb: diskUsed,
                DiskTotalGb: diskTotal,
                NetworkAdapter: adapter,
                NetworkLinkMbps: link,
                NetworkDownMbps: down,
                NetworkUpMbps: up,
                BatteryPercent: power.BatteryPercent,
                OnBattery: power.OnBattery);
        }
    }

    private static TimeSpan? ReadUptime() =>
        Environment.TickCount64 > 0 ? TimeSpan.FromMilliseconds(Environment.TickCount64) : null;

    /// <summary>
    /// Total CPU utilisation between this call and the previous one.
    /// </summary>
    /// <remarks>
    /// Kernel time already includes idle time, which is the classic mistake here: busy is
    /// (kernel - idle) + user. The first call has no previous sample to compare against and
    /// therefore returns null rather than a made-up 0%.
    /// </remarks>
    private double? ReadCpuPercent()
    {
        if (!GetSystemTimes(out FileTime idleRaw, out FileTime kernelRaw, out FileTime userRaw))
        {
            return null;
        }

        ulong idle = idleRaw.Value;
        ulong kernel = kernelRaw.Value;
        ulong user = userRaw.Value;
        ulong busy = kernel - idle + user;

        ulong previousIdle = _lastIdle;
        ulong previousBusy = _lastBusy;

        _lastIdle = idle;
        _lastBusy = busy;

        if (previousBusy == 0 || busy < previousBusy || idle < previousIdle)
        {
            return null;
        }

        ulong busyDelta = busy - previousBusy;
        ulong idleDelta = idle - previousIdle;
        ulong total = busyDelta + idleDelta;

        return total == 0 ? null : Math.Clamp(busyDelta / (double)total * 100d, 0d, 100d);
    }

    private static (double? Used, double? Total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

        if (!GlobalMemoryStatusEx(ref status))
        {
            return (null, null);
        }

        const double gb = 1024d * 1024d * 1024d;
        double total = status.TotalPhys / gb;
        double available = status.AvailPhys / gb;

        return (total - available, total);
    }

    private static (double? Used, double? Total) ReadDisk()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);

            if (!drive.IsReady)
            {
                return (null, null);
            }

            const double gb = 1024d * 1024d * 1024d;
            double total = drive.TotalSize / gb;
            double free = drive.AvailableFreeSpace / gb;

            return (total - free, total);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The adapter actually carrying traffic, its link speed, and the throughput since the last
    /// sample. Loopback and tunnel adapters are skipped: they are not "your network".
    /// </summary>
    private (string? Name, double? LinkMbps, double? DownMbps, double? UpMbps) ReadNetwork()
    {
        try
        {
            NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderByDescending(n => n.GetIPv4Statistics().BytesReceived)
                .FirstOrDefault();

            if (adapter is null)
            {
                return (null, null, null, null);
            }

            IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            long rx = statistics.BytesReceived;
            long tx = statistics.BytesSent;

            double? down = null;
            double? up = null;

            if (_lastNetworkAt != default && rx >= _lastRxBytes && tx >= _lastTxBytes)
            {
                double seconds = (now - _lastNetworkAt).TotalSeconds;

                if (seconds > 0.2)
                {
                    const double bitsPerMegabit = 1_000_000d;
                    down = (rx - _lastRxBytes) * 8d / seconds / bitsPerMegabit;
                    up = (tx - _lastTxBytes) * 8d / seconds / bitsPerMegabit;
                }
            }

            _lastRxBytes = rx;
            _lastTxBytes = tx;
            _lastNetworkAt = now;

            double? link = adapter.Speed > 0 ? adapter.Speed / 1_000_000d : null;

            return (adapter.Name, link, down, up);
        }
        catch (NetworkInformationException)
        {
            return (null, null, null, null);
        }
        catch (PlatformNotSupportedException)
        {
            return (null, null, null, null);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemTimes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        private uint _low;
        private uint _high;

        internal ulong Value => ((ulong)_high << 32) | _low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhys;
        internal ulong AvailPhys;
        internal ulong TotalPageFile;
        internal ulong AvailPageFile;
        internal ulong TotalVirtual;
        internal ulong AvailVirtual;
        internal ulong AvailExtendedVirtual;
    }
}
