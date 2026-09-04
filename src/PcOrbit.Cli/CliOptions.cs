using System.Globalization;

namespace PcOrbit.Cli;

/// <summary>
/// Parsed command line.
/// </summary>
/// <remarks>
/// Hand-rolled rather than a parsing library: the surface is small, and a privileged tool with no
/// third-party code in its argument path is easier to reason about than one that saves twenty
/// lines (spec 19.1).
/// </remarks>
public sealed record CliOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    string Locale,
    string? DataDirectory,
    string? DatabasePath,
    bool Json,
    bool DryRun,
    bool AssumeYes,
    bool RecoveryKeyConfirmed,
    bool Verbose,
    int Limit,
    int Days,
    bool Apply)
{
    public static CliOptions Parse(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        string command = argv.Count > 0 && !argv[0].StartsWith('-') ? argv[0].ToLowerInvariant() : "help";
        List<string> positional = [];

        string locale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string? data = null;
        string? database = null;
        bool json = false;
        bool dryRun = false;
        bool yes = false;
        bool recoveryKey = false;
        bool verbose = false;
        int limit = 20;
        int days = 14;
        bool apply = false;

        for (int i = command == "help" ? 0 : 1; i < argv.Count; i++)
        {
            string argument = argv[i];

            switch (argument)
            {
                case "--lang" or "-l" when i + 1 < argv.Count:
                    locale = argv[++i];
                    break;

                case "--data" when i + 1 < argv.Count:
                    data = argv[++i];
                    break;

                case "--db" when i + 1 < argv.Count:
                    database = argv[++i];
                    break;

                case "--limit" when i + 1 < argv.Count:
                    limit = int.TryParse(argv[++i], CultureInfo.InvariantCulture, out int parsed) ? parsed : limit;
                    break;

                case "--days" when i + 1 < argv.Count:
                    days = int.TryParse(argv[++i], CultureInfo.InvariantCulture, out int parsedDays) ? parsedDays : days;
                    break;

                case "--json":
                    json = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                // With clean: actually move the files. Preview is the default, so the destructive
                // reading of a bare 'pco clean' is the harmless one.
                case "--apply":
                    apply = true;
                    break;

                case "--yes" or "-y":
                    yes = true;
                    break;

                // Spec 10.3 step 5: applying a firmware or boot change requires the user to have
                // chosen one of "I have the recovery key" or "suspend encryption for one restart".
                // On a command line, this flag is that choice — deliberately spelled out in full so
                // nobody types it by muscle memory.
                case "--i-have-my-recovery-key":
                    recoveryKey = true;
                    break;

                case "--verbose" or "-v":
                    verbose = true;
                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        throw new CliUsageException($"Unknown option '{argument}'. Run 'pco help'.");
                    }

                    positional.Add(argument);
                    break;
            }
        }

        return new CliOptions(
            command,
            positional,
            locale,
            data,
            database,
            json,
            dryRun,
            yes,
            recoveryKey,
            verbose,
            Math.Clamp(limit, 1, 500),

            // A year is the furthest back any of the sources reliably reaches, and one day is the
            // shortest window in which "just before it broke" means anything.
            Math.Clamp(days, 1, 365),
            apply);
    }

    public string? FirstArgument => Arguments.Count > 0 ? Arguments[0] : null;
}

public sealed class CliUsageException(string message) : Exception(message);
