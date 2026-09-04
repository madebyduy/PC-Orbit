using System.ComponentModel;
using System.Diagnostics;
using PcOrbit.Core.Abstractions;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The heaviest processes, from the process list Windows already keeps.
/// </summary>
/// <remarks>
/// <para>
/// CPU share is computed the honest way: the change in a process's total processor time divided by
/// the wall-clock time since the previous sample, divided by the core count. That makes 100% mean
/// "the whole machine", which is what a person reading a list expects — not the per-core number
/// Task Manager's Details tab shows.
/// </para>
/// <para>
/// A process whose times we cannot read (it exited, or it belongs to another user and this build
/// is not elevated) is skipped rather than reported as idle. Access is never demanded: spec 21.10
/// requires this to work for a standard user, so a partial list is the correct outcome.
/// </para>
/// </remarks>
public sealed class WindowsProcessMonitor : IProcessMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _previous = [];

    /// <summary>
    /// Each process's executable, asked for once.
    /// </summary>
    /// <remarks>
    /// <c>MainModule</c> opens the process and walks its module list, which is the expensive part
    /// of reading a process and the part most likely to be refused. A PID's executable cannot change
    /// while the PID lives, so it is asked for on first sight and remembered — including remembering
    /// that the answer was "not allowed", so a refused process is not re-asked every three seconds.
    /// Swept with <see cref="_previous"/>, so it cannot grow past the processes that exist.
    /// </remarks>
    private readonly Dictionary<int, string?> _paths = [];

    public IReadOnlyList<ProcessUsage> Top(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            int cores = Math.Max(1, Environment.ProcessorCount);
            List<ProcessUsage> usage = [];
            HashSet<int> seen = [];

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;

                        // The idle process is not a program anyone can act on, and it would always
                        // top a list sorted by CPU time.
                        if (process.Id == 0 || name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        TimeSpan cpu = process.TotalProcessorTime;
                        double memoryMb = process.WorkingSet64 / (1024d * 1024d);

                        seen.Add(process.Id);

                        double? percent = null;

                        if (_previous.TryGetValue(process.Id, out (TimeSpan Cpu, DateTimeOffset At) before))
                        {
                            double seconds = (now - before.At).TotalSeconds;

                            if (seconds > 0.2 && cpu >= before.Cpu)
                            {
                                percent = Math.Clamp(
                                    (cpu - before.Cpu).TotalSeconds / seconds / cores * 100d,
                                    0d,
                                    100d);
                            }
                        }

                        _previous[process.Id] = (cpu, now);

                        if (!_paths.TryGetValue(process.Id, out string? path))
                        {
                            path = PathOf(process);
                            _paths[process.Id] = path;
                        }

                        usage.Add(new ProcessUsage(name, process.Id, percent, memoryMb, path));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // Exited between enumeration and read, or readable only with more rights.
                    }
                }
            }

            // Drop processes that are gone, or the dictionary grows for the life of the app.
            foreach (int id in _previous.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _previous.Remove(id);
                _paths.Remove(id);
            }

            return
            [
                .. usage
                    .OrderByDescending(u => u.CpuPercent ?? -1)
                    .ThenByDescending(u => u.MemoryMb)
                    .Take(count),
            ];
        }
    }

    /// <summary>Where the process's executable is, or null when Windows will not say.</summary>
    private static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Another user's process, an elevated one, or one that exited mid-read. Not knowing is
            // the answer, and the caller shows a letter rather than a guessed icon.
            return null;
        }
    }
}
