using System.Globalization;

namespace PcOrbit.Core.Model;

/// <summary>
/// What a capability currently is. <see cref="CapabilityStatus.Unknown"/> is the default
/// on purpose: spec 8.3.1 and 21.3 forbid inferring <c>Disabled</c> from a failed read.
/// </summary>
public enum CapabilityStatus
{
    Unknown = 0,
    Supported,
    NotSupported,
    Enabled,
    Disabled,
    Present,
    Absent,

    /// <summary>A measured value rather than a state: 165 (Hz), 5600 (MT/s), "2", "uefi".</summary>
    Value,
}

/// <summary>
/// A capability value plus enough structure to compare it against what an outcome or an
/// action manifest asked for, without either side having to guess string formats.
/// </summary>
public readonly record struct CapabilityValue
{
    private CapabilityValue(CapabilityStatus status, string? raw)
    {
        Status = status;
        Raw = raw;
    }

    public CapabilityStatus Status { get; }

    /// <summary>The measured text for <see cref="CapabilityStatus.Value"/>; otherwise null.</summary>
    public string? Raw { get; }

    public static CapabilityValue Unknown => new(CapabilityStatus.Unknown, null);
    public static CapabilityValue Supported => new(CapabilityStatus.Supported, null);
    public static CapabilityValue NotSupported => new(CapabilityStatus.NotSupported, null);
    public static CapabilityValue Enabled => new(CapabilityStatus.Enabled, null);
    public static CapabilityValue Disabled => new(CapabilityStatus.Disabled, null);
    public static CapabilityValue Present => new(CapabilityStatus.Present, null);
    public static CapabilityValue Absent => new(CapabilityStatus.Absent, null);

    public static CapabilityValue Scalar(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        return new CapabilityValue(CapabilityStatus.Value, raw.Trim());
    }

    public static CapabilityValue Scalar(int raw) =>
        new(CapabilityStatus.Value, raw.ToString(CultureInfo.InvariantCulture));

    /// <summary>A read that produced nothing usable. Distinct from "off" (spec 27.13).</summary>
    public bool IsKnown => Status != CapabilityStatus.Unknown;

    /// <summary>
    /// Stable, locale-independent text used in evidence, plan hashes and the event log.
    /// The UI never shows this directly; it looks up <c>status.*</c> in the string catalog.
    /// </summary>
    public string Canonical => Status switch
    {
        CapabilityStatus.Unknown => "unknown",
        CapabilityStatus.Supported => "supported",
        CapabilityStatus.NotSupported => "notSupported",
        CapabilityStatus.Enabled => "enabled",
        CapabilityStatus.Disabled => "disabled",
        CapabilityStatus.Present => "present",
        CapabilityStatus.Absent => "absent",
        CapabilityStatus.Value => Raw ?? "unknown",
        _ => "unknown",
    };

    /// <summary>
    /// Parses the text form used in outcome and action manifests. Anything that is not a
    /// known state word becomes a scalar, so <c>"2"</c> and <c>"uefi"</c> both work.
    /// </summary>
    public static CapabilityValue Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return text.Trim().ToLowerInvariant() switch
        {
            "unknown" => Unknown,
            "supported" => Supported,
            "notsupported" or "not-supported" => NotSupported,
            "enabled" or "on" => Enabled,
            "disabled" or "off" => Disabled,
            "present" => Present,
            "absent" => Absent,
            _ => Scalar(text),
        };
    }

    /// <summary>
    /// True when this reading satisfies <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Unknown never satisfies anything: we do not let a failed read pass as success
    /// (spec 6.6, 27.13). Scalars support a <c>&gt;=</c> prefix so an outcome can ask for
    /// "at least this much" without the compiler needing to know units.
    /// </remarks>
    public bool Satisfies(CapabilityValue expected)
    {
        if (!IsKnown)
        {
            return false;
        }

        if (expected.Status != CapabilityStatus.Value)
        {
            return Status == expected.Status;
        }

        string want = expected.Raw ?? string.Empty;

        if (want.StartsWith(">=", StringComparison.Ordinal))
        {
            return TryCompareNumeric(want.AsSpan(2), out int comparison) && comparison >= 0;
        }

        return Status == CapabilityStatus.Value
            && string.Equals(Raw, want, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryCompareNumeric(ReadOnlySpan<char> wanted, out int comparison)
    {
        comparison = 0;

        if (Status != CapabilityStatus.Value
            || !decimal.TryParse(Raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal mine)
            || !decimal.TryParse(wanted, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal theirs))
        {
            return false;
        }

        comparison = mine.CompareTo(theirs);
        return true;
    }

    public override string ToString() => Canonical;
}
