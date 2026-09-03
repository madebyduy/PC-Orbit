using System.Text.Json;
using System.Text.Json.Serialization;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Serialization;

/// <summary>
/// Writes a capability id as its plain dotted string, so a persisted snapshot or transaction is
/// readable by a human debugging a support report.
/// </summary>
public sealed class CapabilityIdJsonConverter : JsonConverter<CapabilityId>
{
    public override CapabilityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? raw = reader.GetString();

        return CapabilityId.TryParse(raw, out CapabilityId id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid capability id.");
    }

    public override void Write(Utf8JsonWriter writer, CapabilityId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Writes a capability value as its canonical text: <c>enabled</c>, <c>unknown</c>, <c>165</c>.
/// </summary>
/// <remarks>
/// The state words are reserved: a scalar whose text happens to be "enabled" comes back as the
/// Enabled state. That is the same fact either way, and the readability is worth more here than
/// a lossless-but-unreadable <c>{ status, raw }</c> object in every persisted record.
/// </remarks>
public sealed class CapabilityValueJsonConverter : JsonConverter<CapabilityValue>
{
    public override CapabilityValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? raw = reader.GetString();

        return string.IsNullOrWhiteSpace(raw) ? CapabilityValue.Unknown : CapabilityValue.Parse(raw);
    }

    public override void Write(Utf8JsonWriter writer, CapabilityValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Canonical);
    }
}
