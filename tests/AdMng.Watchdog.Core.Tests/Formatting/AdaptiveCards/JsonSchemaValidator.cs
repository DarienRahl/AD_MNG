using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdMng.Watchdog.Core.Tests.Formatting.AdaptiveCards;

/// <summary>
/// A minimal JSON Schema (draft-06) validator for the keywords the Adaptive Cards schema uses: <c>$ref</c> (local
/// <c>#/definitions/…</c>), <c>type</c>, <c>enum</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties</c>,
/// <c>items</c>, <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c> and <c>pattern</c> (annotations such as <c>description</c>,
/// <c>format</c>, <c>version</c> are ignored).
/// </summary>
internal sealed class JsonSchemaValidator
{
    private readonly JsonElement _root;

    public JsonSchemaValidator(string schemaJson)
    {
        _root = JsonDocument.Parse(schemaJson).RootElement.Clone();
    }

    /// <summary>The errors of <paramref name="instance"/> against the root schema (empty = valid).</summary>
    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Validate(_root, instance, "$", errors);
        return errors;
    }

    private void Validate(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True || (schema.ValueKind == JsonValueKind.Object && !schema.EnumerateObject().Any()))
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{path}: not allowed");
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            Validate(Resolve(reference.GetString()!), instance, path, errors);
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, instance))
        {
            errors.Add($"{path}: expected type {type}, got {instance.ValueKind}");
            return;
        }

        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => JsonEquals(v, instance)))
        {
            errors.Add($"{path}: value {instance} not in enum");
        }

        if (schema.TryGetProperty("pattern", out var pattern) && instance.ValueKind == JsonValueKind.String
            && !Regex.IsMatch(instance.GetString()!, pattern.GetString()!))
        {
            errors.Add($"{path}: '{instance.GetString()}' does not match {pattern.GetString()}");
        }

        if (instance.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(schema, instance, path, errors);
        }

        if (instance.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var item in instance.EnumerateArray())
            {
                Validate(items, item, $"{path}[{index++}]", errors);
            }
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var sub in allOf.EnumerateArray())
            {
                Validate(sub, instance, path, errors);
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var branches = anyOf.EnumerateArray().Select(sub => Errors(sub, instance, path)).ToList();
            if (branches.All(b => b.Count > 0))
            {
                errors.Add($"{path}: no anyOf branch matches (closest: {string.Join("; ", branches.MinBy(b => b.Count)!.Take(3))})");
            }
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.EnumerateArray().Count(sub => Errors(sub, instance, path).Count == 0) != 1)
        {
            errors.Add($"{path}: not exactly one oneOf branch matches");
        }
    }

    private void ValidateObject(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!instance.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}: missing required property '{name}'");
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var property in instance.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Validate(propertySchema, property.Value, $"{path}.{property.Name}", errors);
            }
            else if (additional.ValueKind == JsonValueKind.False)
            {
                errors.Add($"{path}: additional property '{property.Name}' not allowed");
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                Validate(additional, property.Value, $"{path}.{property.Name}", errors);
            }
        }
    }

    private List<string> Errors(JsonElement schema, JsonElement instance, string path)
    {
        var errors = new List<string>();
        Validate(schema, instance, path, errors);
        return errors;
    }

    private JsonElement Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Only local references are supported: " + reference);
        }

        var node = _root;
        foreach (var segment in reference[2..].Split('/'))
        {
            node = node.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return node;
    }

    private static bool MatchesType(JsonElement type, JsonElement instance) => type.ValueKind == JsonValueKind.Array
        ? type.EnumerateArray().Any(t => MatchesType(t.GetString()!, instance))
        : MatchesType(type.GetString()!, instance);

    private static bool MatchesType(string type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static bool JsonEquals(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
}
