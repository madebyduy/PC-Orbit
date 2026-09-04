using System.Globalization;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Serialization;
using PcOrbit.Core.Transactions;

namespace PcOrbit.Cli;

/// <summary>
/// <c>pco</c> — the command-line face of the v0.1 engine.
/// </summary>
/// <remarks>
/// <para>
/// Spec step 1 says the first thing to build is one vertical slice — detect, compile a plan, apply,
/// survive a restart, verify — and explicitly not a dashboard of hundreds of settings. A CLI is the
/// honest shape for that: it exercises the whole engine on real hardware while the UI decisions
/// from spec 21.4 through 21.9 are still being tested on paper with real people (step 6b).
/// </para>
/// <para>
/// Nothing here contains product logic. Every command reads the machine, asks the compiler, prints,
/// and hands work to the transaction engine — so the desktop UI can sit on exactly the same seams.
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C stops us starting anything new. It does not abandon a half-applied step: the
            // checkpoint has already been written, and 'pco resume' can pick it up.
            e.Cancel = true;
            cancellation.Cancel();
            Output.Line();
            Output.Line("Stopping. Anything already applied is recorded — run 'pco resume' to continue.");
        };

        CliOptions options;

        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            Output.Error(ex.Message);
            return ExitCodes.Error;
        }

        if (options.Command is "help" or "--help" or "-h")
        {
            PrintHelp();
            return ExitCodes.Ok;
        }

        if (options.Command is "version" or "--version")
        {
            Output.Line($"PC Orbit {PcOrbitHost.AppVersion}");
            return ExitCodes.Ok;
        }

        try
        {
            SetCulture(options.Locale);

            using PcOrbitHost host = PcOrbitHost.Create(options);
            var output = new Output(host.Strings, options.Verbose);

            return options.Command switch
            {
                "scan" => await Commands.ScanAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "checkup" => await Commands.CheckupAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "plan" => await Commands.PlanAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "apply" => await Commands.ApplyAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "undo" => await Commands.UndoAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "resume" => await Commands.ResumeAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "history" => await Commands.HistoryAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "diff" => await Commands.DiffAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "timeline" => await Commands.TimelineAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "startup" => await Commands.StartupAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "clean" => await Commands.CleanAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "restore" => await Commands.RestoreAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "drivers" => await Commands.DriversAsync(host, options, output, cancellation.Token).ConfigureAwait(false),
                "outcomes" => Commands.Outcomes(host, options, output),
                "doctor" => Commands.Doctor(host, options, output),
                _ => Unknown(options.Command),
            };
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Error;
        }
        catch (CliUsageException ex)
        {
            Output.Error(ex.Message);
            return ExitCodes.Error;
        }
        catch (DataFileException ex)
        {
            // Shipped data is broken. Loud and specific: this is a packaging bug, and guessing
            // around it would produce a plan with holes in it.
            Output.Error("Bad data file.");
            Output.Error(ex.Message);
            return ExitCodes.Error;
        }
        catch (ActionParameterException ex)
        {
            Output.Error("An action manifest asked for something this build will not do.");
            Output.Error(ex.Message);
            return ExitCodes.Error;
        }
        catch (PlanChangedException ex)
        {
            Output.Error(ex.Message);
            return ExitCodes.NotReached;
        }
        catch (DirectoryNotFoundException ex)
        {
            Output.Error(ex.Message);
            return ExitCodes.Error;
        }
        catch (UnauthorizedAccessException ex)
        {
            Output.Error($"Access denied: {ex.Message}");
            Output.Error("Some checks need administrator rights. Read-only scanning does not.");
            return ExitCodes.Error;
        }
        catch (IOException ex)
        {
            Output.Error($"File error: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    /// <summary>
    /// Sets the culture from <c>--lang</c>.
    /// </summary>
    /// <remarks>
    /// Affects number and date formatting as well as which string catalog loads. Spec 21.11 keeps
    /// technical units (Hz, MT/s, GB) untranslated, which is why those are formatted invariantly
    /// wherever they appear rather than through the current culture.
    /// </remarks>
    private static void SetCulture(string locale)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // An unknown locale is not worth failing over; English is the fallback catalog anyway.
        }
    }

    private static int Unknown(string command)
    {
        Output.Error($"Unknown command '{command}'.");
        PrintHelp();
        return ExitCodes.Error;
    }

    private static void PrintHelp()
    {
        Output.Line($"""
            PC Orbit {PcOrbitHost.AppVersion} — a state and control layer for this PC.

            Usage: pco <command> [options]

            Commands
              scan          Read this machine and save a snapshot. Shows every value and where it came from.
              checkup       Scan, then report what is worth attention, in plain language.
              outcomes      List the outcomes you can ask for.
              plan <id>     Work out what this specific PC still needs for an outcome. Changes nothing.
              apply <id>    Apply that plan as a transaction: preflight, apply, restart, verify.
              undo [tx]     Undo a transaction's changes: a reverse plan through the same
                            preview, apply and verify. Defaults to the most recent one.
              resume        Continue a transaction that was waiting for a restart.
              history       Recent changes, with before and after values.
              diff [snap]   Scan, then show what has moved since the previous scan — or since the
                            scan you name. Values we simply could not read this time are listed
                            apart from values that actually changed.
              timeline      Everything that changed recently, whoever changed it: Windows updates,
                            driver installs, crashes, hardware errors, restore points and our own
                            transactions on one axis.
              startup       What starts with Windows, and whether each one is switched on.
              startup on|off <name>
                            Switch one entry on or off. The name must be one this machine is
                            reporting right now; security entries are refused outright.
              drivers       The driver behind every device, faulty ones first. Never sorted by age.
              clean         Measure reclaimable space. Add --apply to move it to quarantine, where
                            it stays restorable for 30 days. Nothing is deleted.
              restore [id]  List quarantine batches, or put one back.
              doctor        Validate the shipped data and the executor allowlist. Useful in CI.

            Options
              --lang <en|vi>              Interface language. Defaults to the Windows locale.
              --json                      Machine-readable output.
              --dry-run                   With apply: do everything except change the machine.
              --yes, -y                   Do not ask for confirmation before applying.
              --i-have-my-recovery-key    Confirm you can get to your BitLocker recovery key.
                                          Required before firmware or boot changes on an encrypted PC.
              --verbose, -v               Show evidence, action ids and technical detail.
              --limit <n>                 With history and timeline: how many events.
              --days <n>                  With timeline: how far back to look. Defaults to 14.
              --apply                     With clean: move the files. Without it, clean only measures.
              --data <path>               Where the graph, outcomes, actions and strings live.
              --db <path>                 Local database file. Defaults to %LOCALAPPDATA%\PC Orbit.

            Exit codes
              0  ok        1  error        2  not reached / blocked        3  restart needed

            Examples
              pco checkup --lang vi
              pco plan docker-wsl2-ready --verbose
              pco apply docker-wsl2-ready --dry-run
              pco apply docker-wsl2-ready --i-have-my-recovery-key
              pco undo --dry-run
              pco resume
            """);
    }
}
