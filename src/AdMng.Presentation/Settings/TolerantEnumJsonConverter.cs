using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdMng.Presentation.Settings;

/// <summary>
/// Reads and writes an enum setting by name. A value this version does not know (a settings file written by a later
/// version or edited by hand) reads as the default instead of failing: a failed read makes the settings store treat the
/// whole file - connection profiles, saved views - as corrupt and start from the defaults. The default is the zero
/// value of the enum, so the member a missing or unknown value stands for is declared first
/// (<see cref="IconStylePreference.Colour"/>, <see cref="WindowBackgroundPreference.Theme"/>).
/// </summary>
/// <typeparam name="TEnum">The enum of the setting.</typeparam>
public sealed class TolerantEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>A JSON null is read here as well (as the default), not rejected by the serializer.</summary>
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named)
                    ? named
                    : default;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var number))
                {
                    var numbered = (TEnum)Enum.ToObject(typeof(TEnum), number);
                    return Enum.IsDefined(numbered) ? numbered : default;
                }

                return default;
            default:
                // Null, a boolean, an object or an array: consumed as one value.
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : default(TEnum).ToString());
    }
}
