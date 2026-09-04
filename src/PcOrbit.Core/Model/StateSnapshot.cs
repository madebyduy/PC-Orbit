using System.Collections.Immutable;

namespace PcOrbit.Core.Model;

/// <summary>
/// Everything we read about the machine at one moment. The compiler is a pure function of
/// this plus the graph and rule versions, which is what makes plans reproducible and
/// golden-testable (spec 12.3, 28).
/// </summary>
public sealed class StateSnapshot
{
    private readonly ImmutableDictionary<CapabilityId, CapabilityReading> _readings;
    private readonly ImmutableArray<CapabilityReading> _ordered;

    public StateSnapshot(
        string id,
        DateTimeOffset takenAt,
        MachineIdentity machine,
        IEnumerable<CapabilityReading> readings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(readings);

        Id = id;
        TakenAt = takenAt;
        Machine = machine;

        // Last write wins: a probe with better evidence may deliberately override an earlier one —
        // the TPM is read from the privileged class when we have the rights and from the device
        // tree when we do not, and the better reading should be the one that survives.
        //
        // Built through a mutable dictionary rather than ToImmutableDictionary, which throws on a
        // duplicate key instead of overwriting. That made the documented behaviour a lie and left a
        // crash waiting for the first probe pair that both reported a capability.
        Dictionary<CapabilityId, CapabilityReading> latest = [];

        foreach (CapabilityReading reading in readings)
        {
            latest[reading.Capability] = reading;
        }

        _readings = latest.ToImmutableDictionary();
        _ordered = [.. _readings.Values.OrderBy(r => r.Capability)];
    }

    public string Id { get; }

    public DateTimeOffset TakenAt { get; }

    public MachineIdentity Machine { get; }

    public IReadOnlyList<CapabilityReading> Readings => _ordered;

    /// <summary>
    /// The value we read, or <see cref="CapabilityValue.Unknown"/> when we never looked or
    /// the read failed. Never <c>Disabled</c> by omission (spec 21.3).
    /// </summary>
    public CapabilityValue ValueOf(CapabilityId capability) =>
        _readings.TryGetValue(capability, out CapabilityReading? reading)
            ? reading.Value
            : CapabilityValue.Unknown;

    public CapabilityReading? ReadingOf(CapabilityId capability) =>
        _readings.TryGetValue(capability, out CapabilityReading? reading) ? reading : null;

    public bool Has(CapabilityId capability) => _readings.ContainsKey(capability);

    public static SnapshotBuilder Builder(string id, DateTimeOffset takenAt, MachineIdentity machine) =>
        new(id, takenAt, machine);

    public sealed class SnapshotBuilder(string id, DateTimeOffset takenAt, MachineIdentity machine)
    {
        private readonly List<CapabilityReading> _readings = [];

        public SnapshotBuilder Add(CapabilityReading reading)
        {
            ArgumentNullException.ThrowIfNull(reading);
            _readings.Add(reading);
            return this;
        }

        public SnapshotBuilder Add(CapabilityId capability, CapabilityValue value, Evidence evidence) =>
            Add(new CapabilityReading(capability, value, evidence, takenAt));

        public SnapshotBuilder AddRange(IEnumerable<CapabilityReading> readings)
        {
            ArgumentNullException.ThrowIfNull(readings);
            _readings.AddRange(readings);
            return this;
        }

        public StateSnapshot Build() => new(id, takenAt, machine, _readings);
    }
}

/// <summary>One observed capability, with the reason we believe it.</summary>
public sealed record CapabilityReading(
    CapabilityId Capability,
    CapabilityValue Value,
    Evidence Evidence,
    DateTimeOffset ObservedAt);
