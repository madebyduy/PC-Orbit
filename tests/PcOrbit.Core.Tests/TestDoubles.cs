using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Serialization;
using PcOrbit.Core.Transactions;

namespace PcOrbit.Core.Tests;

/// <summary>A clock that does not move unless a test moves it.</summary>
public sealed class FakeClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset Now { get; private set; } =
        start ?? new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Predictable ids, so a golden test can assert on whole records.</summary>
public sealed class SequentialIds : IIdGenerator
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public string NewId(string prefix)
    {
        _counters.TryGetValue(prefix, out int count);
        _counters[prefix] = count + 1;
        return $"{prefix}-{count + 1:D3}";
    }
}

public sealed class FakeBootSession(string bootId = "boot-first") : IBootSession
{
    public string CurrentBootId { get; private set; } = bootId;

    public DateTimeOffset BootedAt { get; private set; } = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Simulates the restart the transaction was waiting for.</summary>
    public void Reboot(string bootId = "boot-second")
    {
        CurrentBootId = bootId;
        BootedAt = BootedAt.AddHours(1);
    }
}

/// <summary>
/// A capability reader backed by a dictionary a test can mutate, so "the machine changed" is
/// something a test can actually express.
/// </summary>
public sealed class FakeReader : ICapabilityReader
{
    private readonly Dictionary<CapabilityId, CapabilityValue> _values = [];

    public int InvalidateCount { get; private set; }

    public FakeReader Set(string capability, CapabilityValue value)
    {
        _values[CapabilityId.Parse(capability)] = value;
        return this;
    }

    public FakeReader Remove(string capability)
    {
        _values.Remove(CapabilityId.Parse(capability));
        return this;
    }

    public Task<CapabilityReading?> ReadAsync(CapabilityId capability, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(capability, out CapabilityValue value)
            ? new CapabilityReading(
                capability,
                value,
                new Evidence(EvidenceSourceKind.Wmi, "fake", Confidence.High),
                DateTimeOffset.UnixEpoch)
            : null);

    public void Invalidate() => InvalidateCount++;
}

/// <summary>
/// An executor a test can program: what it returns, and what it does to the fake machine when it
/// runs. That second part matters — an executor that claims success without changing anything is
/// how we test that verification is genuinely independent.
/// </summary>
public sealed class ScriptedExecutor(string id) : IActionExecutor
{
    private readonly List<string> _applied = [];
    private readonly List<ActionExecutionContext> _rolledBack = [];

    public string Id { get; } = id;

    public IReadOnlyList<string> Applied => _applied;

    /// <summary>Every rollback call, with its full context — so a test can assert on the restore target.</summary>
    public IReadOnlyList<ActionExecutionContext> RolledBack => _rolledBack;

    public ApplyOutcome Result { get; set; } = ApplyOutcome.Applied("scripted");

    public ApplyOutcome RollbackResult { get; set; } = ApplyOutcome.Applied("scripted rollback");

    /// <summary>Runs when Apply is called. Use it to move the fake machine, or not.</summary>
    public Action<ActionExecutionContext>? OnApply { get; set; }

    /// <summary>Runs when Rollback is called — the undo-direction twin of <see cref="OnApply"/>.</summary>
    public Action<ActionExecutionContext>? OnRollback { get; set; }

    public Exception? ThrowOnApply { get; set; }

    public Task<ApplyOutcome> ApplyAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        _applied.Add(context.Action.Id);

        if (ThrowOnApply is { } error)
        {
            throw error;
        }

        OnApply?.Invoke(context);
        return Task.FromResult(Result);
    }

    public Task<ApplyOutcome> RollbackAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        _rolledBack.Add(context);
        OnRollback?.Invoke(context);
        return Task.FromResult(RollbackResult);
    }
}

/// <summary>An event log a test can read back without a database.</summary>
public sealed class RecordingEventLog : IEventLog
{
    private readonly List<ChangeEvent> _events = [];

    public IReadOnlyList<ChangeEvent> Events => _events;

    public Task AppendAsync(ChangeEvent changeEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(changeEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChangeEvent>>([.. _events.OrderByDescending(e => e.Timestamp).Take(query.Limit)]);
}

/// <summary>An in-memory transaction store that also records how often it was checkpointed.</summary>
public sealed class RecordingTransactionStore : ITransactionStore
{
    private readonly Dictionary<string, Transaction> _byId = new(StringComparer.Ordinal);

    public int SaveCount { get; private set; }

    public List<TransactionState> StatesSeen { get; } = [];

    public Task SaveAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        StatesSeen.Add(transaction.State);
        _byId[transaction.Id] = transaction;
        return Task.CompletedTask;
    }

    public Task<Transaction?> LoadAsync(string transactionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.TryGetValue(transactionId, out Transaction? found) ? found : null);

    public Task<IReadOnlyList<Transaction>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Transaction>>([.. _byId.Values.Where(t => !t.IsFinished)]);

    public Task<IReadOnlyList<Transaction>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Transaction>>([.. _byId.Values.Take(limit)]);
}

/// <summary>Locates and loads the data files that ship with the product.</summary>
public static class ShippedData
{
    private static readonly Lazy<string> DirectoryPath = new(Find);

    public static string Directory => DirectoryPath.Value;

    public static CapabilityGraph Graph() =>
        CapabilityGraphLoader.LoadDirectory(Path.Combine(Directory, "graph"));

    public static ActionCatalog Catalog() =>
        ActionCatalogLoader.LoadDirectory(Path.Combine(Directory, "actions"));

    public static IReadOnlyList<Outcomes.Outcome> Outcomes() =>
        OutcomeLoader.LoadDirectory(Path.Combine(Directory, "outcomes"));

    public static IReadOnlyDictionary<string, Guides.GuideData> Guides() =>
        GuideDataLoader.LoadDirectory(Path.Combine(Directory, "guides"));

    public static string I18nDirectory => Path.Combine(Directory, "i18n");

    private static string Find()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "data");

        if (System.IO.Directory.Exists(Path.Combine(beside, "graph")))
        {
            return beside;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "data");

            if (System.IO.Directory.Exists(Path.Combine(candidate, "graph")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the shipped data directory from the test output folder.");
    }
}

/// <summary>Machine descriptions used across the compiler tests.</summary>
public static class Machines
{
    public static MachineIdentity AsusAmdDesktop { get; } = new(
        SystemVendor: "System manufacturer",
        SystemModel: "System Product Name",
        BaseBoardVendor: "ASUSTeK COMPUTER INC.",
        BaseBoardProduct: "TUF GAMING B650-PLUS WIFI",
        BiosVersion: "2413",
        CpuName: "AMD Ryzen 7 7800X3D 8-Core Processor",
        CpuVendor: CpuVendor.Amd,
        OsBuild: 26100,
        OsEdition: "Professional",
        IsLaptop: false,
        IsVirtualMachine: false);

    public static MachineIdentity DellIntelLaptop { get; } = new(
        SystemVendor: "Dell Inc.",
        SystemModel: "Latitude 5440",
        BaseBoardVendor: "Dell Inc.",
        BaseBoardProduct: "0ABCDE",
        BiosVersion: "1.15.0",
        CpuName: "Intel(R) Core(TM) i7-1365U",
        CpuVendor: CpuVendor.Intel,
        OsBuild: 26100,
        OsEdition: "Professional",
        IsLaptop: true,
        IsVirtualMachine: false);

    /// <summary>
    /// A Windows 10 machine, for the rules that only have anything to say before the upgrade.
    /// </summary>
    public static MachineIdentity Windows10Desktop { get; } = new(
        SystemVendor: "Micro-Star International Co., Ltd.",
        SystemModel: "MS-7C56",
        BaseBoardVendor: "Micro-Star International Co., Ltd.",
        BaseBoardProduct: "B550-A PRO",
        BiosVersion: "1.90",
        CpuName: "AMD Ryzen 5 3600 6-Core Processor",
        CpuVendor: CpuVendor.Amd,
        OsBuild: 19045,
        OsEdition: "Core",
        IsLaptop: false,
        IsVirtualMachine: false);

    public static MachineIdentity VirtualMachine { get; } = new(
        SystemVendor: "VMware, Inc.",
        SystemModel: "VMware Virtual Platform",
        BaseBoardVendor: "Intel Corporation",
        BaseBoardProduct: "440BX Desktop Reference Platform",
        BiosVersion: "6.00",
        CpuName: "Intel(R) Xeon(R) Gold 6248R",
        CpuVendor: CpuVendor.Intel,
        OsBuild: 26100,
        OsEdition: "Professional",
        IsLaptop: false,
        IsVirtualMachine: true);
}

/// <summary>Builds snapshots for tests without repeating the evidence boilerplate.</summary>
public sealed class SnapshotBuilder(MachineIdentity? machine = null, string id = "snap-test")
{
    private readonly List<CapabilityReading> _readings = [];
    private readonly MachineIdentity _machine = machine ?? Machines.AsusAmdDesktop;

    public static SnapshotBuilder For(MachineIdentity machine) => new(machine);

    public SnapshotBuilder With(string capability, CapabilityValue value, Confidence confidence = Confidence.High)
    {
        _readings.Add(new CapabilityReading(
            CapabilityId.Parse(capability),
            value,
            new Evidence(EvidenceSourceKind.Wmi, $"test:{capability}", confidence),
            DateTimeOffset.UnixEpoch));

        return this;
    }

    public SnapshotBuilder WithUnknown(string capability, string reason = "not read in this test")
    {
        _readings.Add(new CapabilityReading(
            CapabilityId.Parse(capability),
            CapabilityValue.Unknown,
            Evidence.Missing(reason),
            DateTimeOffset.UnixEpoch));

        return this;
    }

    public StateSnapshot Build() =>
        new(id, new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), _machine, _readings);
}
