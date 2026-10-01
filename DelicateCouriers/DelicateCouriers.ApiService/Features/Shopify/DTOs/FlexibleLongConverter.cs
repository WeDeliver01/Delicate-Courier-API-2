using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Shopify.DTOs
{
    /// <summary>
    /// Reads a nullable long from JSON whether the value arrives as a number
    /// or a quoted string. Shopify's carrier-service payloads have historically
    /// sent monetary fields (e.g. item <c>price</c>) as strings in some API
    /// versions and as numbers in others — without this, a string value would
    /// throw during deserialization and cost us an otherwise-valid quote.
    /// </summary>
    public sealed class FlexibleLongConverter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var n) ? n : (long?)null;
                case JsonTokenType.String:
                    var s = reader.GetString();
                    return long.TryParse(s, out var parsed) ? parsed : (long?)null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
