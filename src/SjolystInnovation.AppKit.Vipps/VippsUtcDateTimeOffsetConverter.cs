using System.Text.Json;
using System.Text.Json.Serialization;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Reads a time without an offset as UTC instead of server local time, so the value does not depend on the
/// server's time zone. A time with an offset keeps it.
/// </summary>
internal sealed class VippsUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var time = reader.GetDateTime();
        return time.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(time, TimeSpan.Zero) : reader.GetDateTimeOffset();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } time) writer.WriteStringValue(time);
        else writer.WriteNullValue();
    }
}
