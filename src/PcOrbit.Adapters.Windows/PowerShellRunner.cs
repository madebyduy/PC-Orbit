using System.Diagnostics;
using System.Text;

namespace PcOrbit.Adapters.Windows;

/// <param name="Succeeded">True when the process exited 0 <em>and</em> produced output.</param>
public sealed record PowerShellResult(bool Succeeded, string StandardOutput, string StandardError, int ExitCode);

/// <summary>
/// Runs a fixed PowerShell script and returns its output.
/// </summary>
/// <remarks>
/// <para>
/// Why PowerShell rather than the <c>System.Management</c> COM wrapper: several of the facts we
/// need have no clean WMI path at all (<c>Confirm-SecureBootUEFI</c>, BitLocker state, the WSL
/// default version), and running the query as text means the exact query can be stored as evidence
/// (spec 6.1 "how do you know?"). One process per scan, not one per fact.
/// </para>
/// <para>
/// Every script this class runs is a compile-time constant in this assembly. Nothing accepts user
/// input, and nothing builds a command line from a plan parameter — an action manifest names an
/// executor and passes schema-checked values, never a command (spec 17.1, 19.1). Scripts go in
/// through <c>-EncodedCommand</c>, which sidesteps both quoting bugs and the machine's script
/// execution policy without weakening it for anything else.
/// </para>
/// </remarks>
public sealed class PowerShellRunner
{
    private static readonly string PowerShellPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private readonly TimeSpan _timeout;

    public PowerShellRunner(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(90);

    public static PowerShellRunner Default { get; } = new();

    public async Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return new PowerShellResult(false, string.Empty, "Could not start powershell.exe.", -1);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Spec 28 lists command timeout as a fault we must handle rather than hang on. A probe
            // that never returns has to become an Unknown reading, not a frozen scan.
            TryKill(process);
            return new PowerShellResult(false, string.Empty, $"Timed out after {_timeout.TotalSeconds:0}s.", -2);
        }

        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);

        return new PowerShellResult(
            process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output),
            output.Trim(),
            error.Trim(),
            process.ExitCode);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone between the timeout and here. Nothing to do.
        }
    }
}
