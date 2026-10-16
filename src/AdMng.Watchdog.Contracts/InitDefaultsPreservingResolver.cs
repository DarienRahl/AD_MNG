using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AdMng.Watchdog.Contracts;

/// <summary>
/// Keeps the record defaults of properties that are missing from the JSON (section 8.2: "missing properties take the
/// record defaults"; section 5: collections are never null).
/// </summary>
/// <remarks>
/// Source-generated metadata creates a record with <c>init</c>-only properties through one object initializer that
/// assigns EVERY such property – a property absent from the JSON is assigned <c>default</c> (null, 0, false) and the
/// record default (<c>= []</c>, <c>= new()</c>, <c>= "Europe/Warsaw"</c>, …) is lost. For object types with a public
/// parameterless constructor this resolver therefore returns reflection metadata (default constructor + setters for
/// the properties present only). Everything else – enums as strings, collections, positional records – keeps the
/// source-generated metadata of <see cref="WatchdogJsonContext"/>; types outside the context stay unsupported.
/// </remarks>
internal sealed class InitDefaultsPreservingResolver(IJsonTypeInfoResolver generated) : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver _reflection = new();

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var info = generated.GetTypeInfo(type, options);
        return info is { Kind: JsonTypeInfoKind.Object } && type.GetConstructor(Type.EmptyTypes) is not null
            ? _reflection.GetTypeInfo(type, options)
            : info;
    }
}
