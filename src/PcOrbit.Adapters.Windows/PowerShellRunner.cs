using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PcOrbit.Adapters.Windows;

/// <param name="Succeeded">True when the process exited 0 <em>and</em> produced output.</param>
public sealed partial record PowerShellResult(bool Succeeded, string StandardOutput, string StandardError, int ExitCode)
{
    /// <summary>
    /// The error, as a person would write it.
    /// </summary>
    /// <remarks>
    /// Windows PowerShell serialises its error stream as CLIXML the moment stderr is redirected, so
    /// <see cref="StandardError"/> arrives as a wall of XML with the one useful sentence buried in
    /// it. That sentence ends up in front of users — an unreadable capability says why it is
    /// unreadable (spec 6.1) — so it gets unwrapped here rather than in every caller.
    /// </remarks>
    public string ErrorSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StandardError))
            {
                return string.Empty;
            }

            if (!StandardError.StartsWith("#< CLIXML", StringComparison.Ordinal))
            {
                return Collapse(StandardError);
            }

            IEnumerable<string> messages = ClixmlError()
                .Matches(StandardError)
                .Select(m => Unescape(m.Groups[1].Value))
                .Where(s => !string.IsNullOrWhiteSpace(s));

            string joined = Collapse(string.Join(" ", messages));

            // The first line of a PowerShell error is the message; everything after it is the
            // "At line:4 char:13 +" echo of our own script, which tells the reader nothing.
            int echo = joined.IndexOf("At line:", StringComparison.Ordinal);

            if (echo > 0)
            {
                joined = joined[..echo].TrimEnd();
            }

            return joined.Length == 0 ? "the command reported an error with no message" : joined;
        }
    }

    private static string Unescape(string value) => value
        .Replace("_x000D_", string.Empty, StringComparison.Ordinal)
        .Replace("_x000A_", " ", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal);

    private static string Collapse(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex("<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline)]
    private static partial Regex ClixmlError();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

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

    public Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken = default) =>
        RunWithArgumentsAsync(script, [], cancellationToken);

    /// <summary>
    /// Runs a fixed script with values supplied separately, reachable as
    /// <c>$env:PCORBIT_ARG0</c>, <c>PCORBIT_ARG1</c> and so on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only way a value that did not come from this assembly may reach PowerShell. Values
    /// travel in the child process's environment, so a name containing a quote, a semicolon or a
    /// newline is a string to the script and never a second command — the script itself stays a
    /// compile-time constant, which is the property spec 19.1 is actually asking for.
    /// </para>
    /// <para>
    /// Not positional arguments: <c>powershell.exe -EncodedCommand &lt;b64&gt; value</c> is refused
    /// outright with "a command is already specified", because anything after the encoded command
    /// is read as a second command rather than as <c>$args</c>. That refusal reached a user as an
    /// error dialog when they tried to switch off a startup entry.
    /// </para>
    /// <para>
    /// Callers still validate the value against an allowlist before getting here. The environment
    /// makes injection impossible; the allowlist makes the operation <em>intended</em>. Both.
    /// </para>
    /// </remarks>
    public async Task<PowerShellResult> RunWithArgumentsAsync(
        string script,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(arguments);

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

        for (int i = 0; i < arguments.Count; i++)
        {
            startInfo.Environment[$"PCORBIT_ARG{i}"] = arguments[i];
        }

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
