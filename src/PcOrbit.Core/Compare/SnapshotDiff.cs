using PcOrbit.Core.Model;

namespace PcOrbit.Core.Compare;

/// <summary>What happened to one capability between two scans.</summary>
public enum CapabilityChangeKind
{
    /// <summary>Both scans read it, and the value moved.</summary>
    Changed = 0,

    /// <summary>
    /// It was readable before and is not now. Emphatically <em>not</em> a change to "off": the
    /// setting may well be exactly where it was, and only our view of it has gone (spec 6.6).
    /// </summary>
    BecameUnreadable,

    /// <summary>Unknown before, readable now — usually a scan that ran elevated this time.</summary>
    BecameReadable,

    /// <summary>Present only in the newer scan. A capability the older build did not read at all.</summary>
    Added,

    /// <summary>Present only in the older scan.</summary>
    Removed,
}

/// <param name="Before">The value in the older snapshot.</param>
/// <param name="After">The value in the newer snapshot.</param>
/// <param name="BeforeEvidence">
/// Why we believed the old value. Carried because "the reading changed" and "the way we read it
/// changed" look identical without it — a value that moved when the evidence moved from a WMI
/// class to a registry fallback is a different story to one that moved on its own.
/// </param>
public sealed record CapabilityChange(
    CapabilityId Capability,
    CapabilityChangeKind Kind,
    CapabilityValue Before,
    CapabilityValue After,
    Evidence? BeforeEvidence,
    Evidence? AfterEvidence)
{
    /// <summary>
    /// True when the two readings came from different sources, so the difference may be in the
    /// instrument rather than in the machine.
    /// </summary>
    public bool EvidenceChanged =>
        BeforeEvidence is not null
        && AfterEvidence is not null
        && (BeforeEvidence.SourceKind != AfterEvidence.SourceKind
            || !string.Equals(BeforeEvidence.Source, AfterEvidence.Source, StringComparison.Ordinal));
}

/// <param name="SameMachine">
/// False when the two snapshots came from different hardware. The comparison is still produced —
/// there is a legitimate reason to hold two machines side by side — but nothing may present it as
/// a history of one PC.
/// </param>
public sealed record SnapshotComparison(
    string BeforeId,
    DateTimeOffset BeforeAt,
    string AfterId,
    DateTimeOffset AfterAt,
    bool SameMachine,
    IReadOnlyList<CapabilityChange> Changes)
{
    public bool IsUnchanged => Changes.Count == 0;

    /// <summary>
    /// Changes where a value actually moved, as opposed to our ability to see it moving.
    /// </summary>
    /// <remarks>
    /// This is the split that makes a baseline diff usable after a BIOS update or a CMOS reset:
    /// "eleven settings differ" is alarming and usually wrong, because nine of them are readings
    /// the unelevated scan could not repeat (ADR 0004).
    /// </remarks>
    public IReadOnlyList<CapabilityChange> RealChanges =>
        [.. Changes.Where(c => c.Kind is CapabilityChangeKind.Changed
            or CapabilityChangeKind.Added
            or CapabilityChangeKind.Removed)];

    public IReadOnlyList<CapabilityChange> VisibilityChanges =>
        [.. Changes.Where(c => c.Kind is CapabilityChangeKind.BecameUnreadable
            or CapabilityChangeKind.BecameReadable)];
}

/// <summary>
/// Compares two snapshots of the same machine.
/// </summary>
/// <remarks>
/// <para>
/// The engine behind the research's BIOS Baseline &amp; Diff (§8.5), and it needed no new machinery:
/// snapshots are already stored with evidence per reading, so "what did this BIOS update actually
/// change?" is a pure function over two of them.
/// </para>
/// <para>
/// The one thing it must never do is flatten a lost reading into a changed value. A scan run
/// without administrator rights reads Unknown for TPM state and drive encryption; comparing it to
/// an elevated scan would otherwise announce that the TPM was turned off and BitLocker removed.
/// That is why <see cref="CapabilityChangeKind.BecameUnreadable"/> is its own kind and why
/// <see cref="SnapshotComparison.RealChanges"/> excludes it.
/// </para>
/// </remarks>
public static class SnapshotDiff
{
    public static SnapshotComparison Compare(StateSnapshot before, StateSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        Dictionary<CapabilityId, CapabilityReading> old = before.Readings.ToDictionary(r => r.Capability);
        Dictionary<CapabilityId, CapabilityReading> now = after.Readings.ToDictionary(r => r.Capability);

        List<CapabilityChange> changes = [];

        foreach (CapabilityId capability in old.Keys.Union(now.Keys).Order())
        {
            old.TryGetValue(capability, out CapabilityReading? was);
            now.TryGetValue(capability, out CapabilityReading? current);

            CapabilityChangeKind? kind = Classify(was, current);

            if (kind is null)
            {
                continue;
            }

            changes.Add(new CapabilityChange(
                capability,
                kind.Value,
                was?.Value ?? CapabilityValue.Unknown,
                current?.Value ?? CapabilityValue.Unknown,
                was?.Evidence,
                current?.Evidence));
        }

        return new SnapshotComparison(
            before.Id,
            before.TakenAt,
            after.Id,
            after.TakenAt,
            string.Equals(before.Machine.Fingerprint, after.Machine.Fingerprint, StringComparison.Ordinal),
            changes);
    }

    private static CapabilityChangeKind? Classify(CapabilityReading? was, CapabilityReading? current)
    {
        if (was is null)
        {
            return current is null ? null : CapabilityChangeKind.Added;
        }

        if (current is null)
        {
            return CapabilityChangeKind.Removed;
        }

        return (was.Value.IsKnown, current.Value.IsKnown) switch
        {
            // Unknown both times is not news. It was already reported as unreadable by the scan.
            (false, false) => null,
            (true, false) => CapabilityChangeKind.BecameUnreadable,
            (false, true) => CapabilityChangeKind.BecameReadable,
            (true, true) => string.Equals(was.Value.Canonical, current.Value.Canonical, StringComparison.Ordinal)
                ? null
                : CapabilityChangeKind.Changed,
        };
    }
}
