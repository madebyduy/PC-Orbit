namespace PcOrbit.Core.Firmware;

/// <param name="From">
/// The value read before the write. Null when the firmware reported the setting without a value,
/// in which case there is nothing to put back and the entry says so rather than offering to.
/// </param>
/// <param name="Verified">Whether reading the firmware back afterwards agreed with the write.</param>
public sealed record FirmwareChange(
    string Id,
    DateTimeOffset When,
    string Vendor,
    string Setting,
    string? From,
    string To,
    bool Verified)
{
    /// <summary>Whether there is a previous value to offer putting back.</summary>
    public bool CanRevert => From is { Length: > 0 } && !string.Equals(From, To, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every firmware setting this product has written on this machine, oldest to newest.
/// </summary>
/// <remarks>
/// <para>
/// Everything else this product changes is undone by a reverse transaction that restores the value
/// read before the step. A firmware write went around that engine — it is a service, not an action,
/// for the reasons in ADR 0007 — and so it had no record and no way back except the user
/// remembering what the value used to be. That is the one thing this product promises not to make
/// people do.
/// </para>
/// <para>
/// The log is the memory. Each entry carries the value read before the write, so "put it back" is
/// another write through the same confirmation, sized to the same risk. It is not a transaction —
/// firmware has no boot-session boundary to resume across — but it is the same promise: nothing
/// changed without a record of what it was.
/// </para>
/// </remarks>
public interface IFirmwareChangeLog
{
    Task RecordAsync(FirmwareChange change, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<FirmwareChange>> ListAsync(CancellationToken cancellationToken = default);
}
