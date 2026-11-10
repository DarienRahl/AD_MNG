using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdMng.Presentation.Settings;

/// <summary>
/// Reads and writes <see cref="IconStylePreference"/> by name. A value this version does not know (a settings file
/// written by a later version or edited by hand) reads as the default, <see cref="IconStylePreference.Colour"/>,
/// instead of failing: a failed read makes the settings store treat the whole file - connection profiles, saved
/// views - as corrupt and start from the defaults.
/// </summary>
public sealed class IconStylePreferenceJsonConverter : JsonConverter<IconStylePreference>
{
    private const IconStylePreference Default = IconStylePreference.Colour;

    /// <summary>A JSON null is read here as well (as the default), not rejected by the serializer.</summary>
    public override bool HandleNull => true;

    public override IconStylePreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<IconStylePreference>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named)
                    ? named
                    : Default;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined((IconStylePreference)number)
                    ? (IconStylePreference)number
                    : Default;
            default:
                // Null, a boolean, an object or an array: consumed as one value.
                reader.Skip();
                return Default;
        }
    }

    public override void Write(Utf8JsonWriter writer, IconStylePreference value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : Default.ToString());
    }
}
