using PcOrbit.Adapters.Windows;
using PcOrbit.Adapters.Windows.Executors;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Localization;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Serialization;
using PcOrbit.Core.Transactions;
using PcOrbit.Store;

namespace PcOrbit.Cli;

/// <summary>
/// The composition root: loads the shipped data, wires the engine to the Windows adapters, and
/// opens the local store.
/// </summary>
/// <remarks>
/// Everything is constructed here and nowhere else. There is no service locator and no dependency
/// injection container — the whole graph fits on a screen, and being able to read the wiring of a
/// privileged process in one place is worth more than the convenience of hiding it.
/// </remarks>
public sealed class PcOrbitHost : IDisposable
{
    public const string AppVersion = "0.1.0";

    private PcOrbitHost(
        CapabilityGraph graph,
        ActionCatalog catalog,
        IReadOnlyList<Outcome> outcomes,
        IReadOnlyDictionary<string, GuideData> guides,
        IStringCatalog strings,
        WindowsStateScanner scanner,
        WindowsCapabilityReader reader,
        ExecutorRegistry executors,
        TransactionEngine engine,
        PcOrbitDatabase database,
        string dataDirectory)
    {
        Graph = graph;
        Catalog = catalog;
        Outcomes = outcomes;
        Guides = guides;
        Strings = strings;
        Scanner = scanner;
        Reader = reader;
        Executors = executors;
        Engine = engine;
        Database = database;
        DataDirectory = dataDirectory;

        Snapshots = new SqliteSnapshotStore(database);
        Transactions = new SqliteTransactionStore(database);
        Events = new SqliteEventLog(database);
    }

    public CapabilityGraph Graph { get; }

    public ActionCatalog Catalog { get; }

    public IReadOnlyList<Outcome> Outcomes { get; }

    public IReadOnlyDictionary<string, GuideData> Guides { get; }

    public IStringCatalog Strings { get; }

    public WindowsStateScanner Scanner { get; }

    public WindowsCapabilityReader Reader { get; }

    public ExecutorRegistry Executors { get; }

    public TransactionEngine Engine { get; }

    public PcOrbitDatabase Database { get; }

    public string DataDirectory { get; }

    public ISnapshotStore Snapshots { get; }

    public ITransactionStore Transactions { get; }

    public IEventLog Events { get; }

    public IElevationContext Elevation { get; } = WindowsElevationContext.Instance;

    public IBootSession Boot { get; } = WindowsBootSession.Instance;

    public IClock Clock { get; } = SystemClock.Instance;

    /// <param name="safeApplyConfirmation">
    /// How Safe Apply asks "can you still see the screen?" (spec 10.1). The console countdown by
    /// default; the desktop app passes its own dialog. Never <see cref="NeverConfirms"/> for an
    /// interactive surface — silence must stay distinguishable from "yes".
    /// </param>
    public static PcOrbitHost Create(CliOptions options, ISafeApplyConfirmation? safeApplyConfirmation = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        string dataDirectory = DataLocator.FindDataDirectory(options.DataDirectory);

        CapabilityGraph graph = CapabilityGraphLoader.LoadDirectory(Path.Combine(dataDirectory, "graph"));
        ActionCatalog catalog = ActionCatalogLoader.LoadDirectory(Path.Combine(dataDirectory, "actions"));
        IReadOnlyList<Outcome> outcomes = OutcomeLoader.LoadDirectory(Path.Combine(dataDirectory, "outcomes"));
        IReadOnlyDictionary<string, GuideData> guides = GuideDataLoader.LoadDirectory(Path.Combine(dataDirectory, "guides"));
        IStringCatalog strings = JsonStringCatalog.LoadForLocale(Path.Combine(dataDirectory, "i18n"), options.Locale);

        var scanner = new WindowsStateScanner(graph);
        var reader = new WindowsCapabilityReader(scanner);

        // The executor allowlist. Adding an entry here is the moment a capability becomes writable,
        // which is why it is a hand-written list and not a scan for implementations of an interface.
        var executors = new ExecutorRegistry(
        [
            new OptionalFeatureExecutor(enable: true),
            new OptionalFeatureExecutor(enable: false),
            new WslDefaultVersionExecutor(),
            new SystemRestoreExecutor(),
            new BitLockerSuspendExecutor(),
            new DisplayRefreshRateExecutor(safeApplyConfirmation ?? new ConsoleSafeApplyConfirmation()),
            new GuidedFirmwareExecutor(guides),
            new DellFirmwareExecutor(),
        ]);

        var engine = new TransactionEngine(
            executors,
            reader,
            SystemClock.Instance,
            GuidIdGenerator.Instance,
            WindowsBootSession.Instance,
            AppVersion);

        PcOrbitDatabase database = string.IsNullOrWhiteSpace(options.DatabasePath)
            ? PcOrbitDatabase.OpenDefault()
            : PcOrbitDatabase.Open(options.DatabasePath);

        return new PcOrbitHost(
            graph,
            catalog,
            outcomes,
            guides,
            strings,
            scanner,
            reader,
            executors,
            engine,
            database,
            dataDirectory);
    }

    public Outcome? FindOutcome(string idOrSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrSuffix);

        return Outcomes.FirstOrDefault(o => string.Equals(o.Id, idOrSuffix, StringComparison.OrdinalIgnoreCase))
            ?? Outcomes.FirstOrDefault(o => o.Id.EndsWith("." + idOrSuffix, StringComparison.OrdinalIgnoreCase));
    }

    public StateCompiler CreateCompiler() => new(Graph, Catalog);

    public static CheckupEngine Checkup => CheckupEngine.Default;

    public void Dispose() => Reader.Dispose();
}
