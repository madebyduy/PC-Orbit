using PcOrbit.Adapters.Windows;
using PcOrbit.Adapters.Windows.Executors;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Apps;
using PcOrbit.Core.Firmware;
using PcOrbit.Core.Missions;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Localization;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Serialization;
using PcOrbit.Core.Setup;
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
        AppCatalog apps,
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
        Apps = apps;
        Strings = strings;
        AppService = new WindowsAppService(apps);
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

    private MissionCatalog? _missions;

    /// <summary>
    /// What people want done, in their words, each pointing at a page or an outcome (ADR 0008).
    /// </summary>
    public MissionCatalog Missions => _missions ??=
        MissionCatalogLoader.LoadDirectory(Path.Combine(DataDirectory, "missions"));

    public IReadOnlyDictionary<string, GuideData> Guides { get; }

    /// <summary>
    /// The applications this product will install. Shipped data, and the allowlist: an id outside
    /// it never reaches winget (ADR 0006).
    /// </summary>
    public AppCatalog Apps { get; }

    /// <summary>Installing and removing them, verified by asking winget again afterwards.</summary>
    public IAppService AppService { get; }

    /// <summary>
    /// Office through Microsoft own deployment tool. Ships no keys and bypasses no licensing.
    /// </summary>
    public IOfficeService Office { get; } = new WindowsOfficeService();

    /// <summary>
    /// Reinstalling Windows over itself from an ISO. The repair install only — laying an image onto
    /// a chosen partition is not here and will not be (ADR 0006).
    /// </summary>
    public IWindowsMediaService Media { get; } = new WindowsMediaService();

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

    /// <summary>
    /// Read-only windows onto changes PC Orbit did not make. Listed here for the same reason the
    /// executor allowlist is: what a privileged process reads is worth being able to see in one
    /// place.
    /// </summary>
    public IReadOnlyList<IChangeSource> ChangeSources { get; } = WindowsChangeSources.All();

    /// <summary>What starts with Windows.</summary>
    public IStartupInventory Startup { get; } = new WindowsStartupInventory();

    /// <summary>
    /// Turning a startup entry on or off. The permitted set is the machine's own inventory read at
    /// the moment of the change, plus a fixed refusal list (ADR 0005).
    /// </summary>
    public IStartupController StartupControl { get; } = new WindowsStartupController();

    /// <summary>
    /// Moving this installation between Windows editions. Read-only until the user supplies their
    /// own product key — this product ships none (ADR 0006).
    /// </summary>
    public IEditionService Editions { get; } = new WindowsEditionService();

    /// <summary>
    /// Firmware settings through the manufacturer's own interface, where the machine has one.
    /// </summary>
    /// <remarks>
    /// Not an action, and never selected by the State Compiler. The gate in
    /// <c>data/actions/pending-verification/</c> is about a firmware write the compiler picks on
    /// its own inside a plan; this is a person choosing one setting and confirming it. Different
    /// risks, different rules (ADR 0007).
    /// </remarks>
    public IFirmwareSettings Firmware { get; } = new WindowsFirmwareSettings();

    /// <summary>What this product has written to the firmware, with the values it replaced.</summary>
    public IFirmwareChangeLog FirmwareChanges { get; } = new WindowsFirmwareChangeLog();

    /// <summary>
    /// What is currently defending the machine, and whether Windows would even let it be changed.
    /// </summary>
    public WindowsProtectionState Protection { get; } = new();

    /// <summary>What is driving the hardware, and Windows' own verdict on each device.</summary>
    public IDriverInventory Drivers { get; } = new WindowsDriverInventory();

    /// <summary>Measures reclaimable space. Read-only; moving files is the store's job.</summary>
    public ICleanupScanner CleanupScanner { get; } = new WindowsCleanupScanner();

    /// <summary>
    /// Where removed files go so they can come back. The primitive cleanup could not exist without.
    /// </summary>
    public IQuarantineStore Quarantine { get; } = new WindowsQuarantineStore();

    public IElevationContext Elevation { get; } = WindowsElevationContext.Instance;

    public IBootSession Boot { get; } = WindowsBootSession.Instance;

    public IClock Clock { get; } = SystemClock.Instance;

    /// <summary>Today, for the knowledge-pack expiry checks. Through the clock, so it is testable.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Clock.Now.LocalDateTime);

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
        AppCatalog apps = AppCatalogLoader.LoadDirectory(Path.Combine(dataDirectory, "apps"));
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
            new RegistrySettingExecutor(),
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
            apps,
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
