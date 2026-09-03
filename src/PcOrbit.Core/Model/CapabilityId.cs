using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace PcOrbit.Core.Model;

/// <summary>
/// The immutable technical identity of a capability, e.g. <c>firmware.cpu.virtualization</c>.
/// </summary>
/// <remarks>
/// Spec 21.11: the id is never localised and never renamed. Display names live in the
/// string catalog keyed off this id, so translating the UI can never change behaviour,
/// and an evidence trail recorded a year ago still resolves today.
/// </remarks>
public readonly record struct CapabilityId : IComparable<CapabilityId>
{
    private static readonly Regex Pattern = new(
        "^[a-z0-9]+([.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private readonly string? _value;

    private CapabilityId(string value) => _value = value;

    /// <summary>The dotted id. Never null once parsed.</summary>
    public string Value => _value ?? throw new InvalidOperationException(
        "Default-constructed CapabilityId. Use CapabilityId.Parse.");

    public static CapabilityId Parse(string value)
    {
        if (!TryParse(value, out CapabilityId id))
        {
            throw new FormatException(
                $"'{value}' is not a valid capability id. Expected lowercase dotted segments, e.g. firmware.cpu.virtualization.");
        }

        return id;
    }

    public static bool TryParse([NotNullWhen(true)] string? value, out CapabilityId id)
    {
        if (!string.IsNullOrEmpty(value) && value.Length <= 120 && Pattern.IsMatch(value))
        {
            id = new CapabilityId(value);
            return true;
        }

        id = default;
        return false;
    }

    public int CompareTo(CapabilityId other) =>
        string.CompareOrdinal(_value, other._value);

    public static bool operator <(CapabilityId left, CapabilityId right) => left.CompareTo(right) < 0;

    public static bool operator <=(CapabilityId left, CapabilityId right) => left.CompareTo(right) <= 0;

    public static bool operator >(CapabilityId left, CapabilityId right) => left.CompareTo(right) > 0;

    public static bool operator >=(CapabilityId left, CapabilityId right) => left.CompareTo(right) >= 0;

    public override string ToString() => _value ?? "(none)";
}
