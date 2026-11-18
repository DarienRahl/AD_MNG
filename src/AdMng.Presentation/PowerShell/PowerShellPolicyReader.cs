using System.Globalization;
using System.Text.Json;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// Works out, from the raw reads of an <see cref="IPowerShellPolicySource"/>, what PowerShell policies apply to this
/// window (track R, decision D7) - in the order the engine takes them:
/// <list type="number">
/// <item>the computer policy (<c>HKLM</c>), then the user policy (<c>HKCU</c>) of <b>PowerShell 7</b> (<c>PowerShellCore</c> key);
/// while "Use Windows PowerShell Policy setting" is on in either hive, the <b>Windows PowerShell</b> keys stand in for them;</item>
/// <item>then the <c>PowerShellPolicies</c> of the all-users <c>powershell.config.json</c>, then of the user's.</item>
/// </list>
/// For each of transcription, script block logging, module logging and the execution policy the first place that sets it
/// decides - also when it sets it to off, which is how a policy overrides a file that turned it on. Nothing here changes a
/// policy or reads anything of the directory.
/// </summary>
public sealed class PowerShellPolicyReader(IPowerShellPolicySource source) : IPowerShellPolicyReader
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly IPowerShellPolicySource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc/>
    public Task<PowerShellPolicies> ReadAsync(CancellationToken cancellationToken) => Task.Run(Read, cancellationToken);

    /// <summary>Reads the policies on the calling thread.</summary>
    public PowerShellPolicies Read()
    {
        var useWindowsPowerShell = IsOn(PowerShellPolicyScope.Computer) || IsOn(PowerShellPolicyScope.User);
        var store = useWindowsPowerShell ? PowerShellPolicyStore.WindowsPowerShell : PowerShellPolicyStore.PowerShellCore;
        var root = useWindowsPowerShell ? PowerShellPolicyKeys.WindowsRoot : PowerShellPolicyKeys.CoreRoot;
        var layers = new List<Layer>
        {
            new RegistryLayer(_source, root, new PowerShellPolicyOrigin(store, PowerShellPolicyScope.Computer), PowerShellPolicyScope.Computer),
            new RegistryLayer(_source, root, new PowerShellPolicyOrigin(store, PowerShellPolicyScope.User), PowerShellPolicyScope.User),
            new ConfigLayer(ParseConfig(_source.ReadConfigFile(PowerShellPolicyScope.Computer)), new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.Computer)),
            new ConfigLayer(ParseConfig(_source.ReadConfigFile(PowerShellPolicyScope.User)), new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.User)),
        };

        return new PowerShellPolicies
        {
            Transcription = ReadTranscription(layers),
            ScriptBlockLogging = ReadScriptBlockLogging(layers),
            ModuleLogging = ReadModuleLogging(layers),
            ExecutionPolicy = ReadExecutionPolicy(layers),
            UseWindowsPowerShellPolicy = useWindowsPowerShell,
            Lockdown = _source.GetLockdownState(),
        };
    }

    private bool IsOn(PowerShellPolicyScope scope) =>
        _source.GetDword(scope, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.UseWindowsPowerShellPolicy) is { } value && value != 0;

    private static PowerShellTranscriptionPolicy ReadTranscription(List<Layer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer.Flag(Section.Transcription, PowerShellPolicyKeys.EnableTranscripting) is not { } enabled)
            {
                continue;
            }

            var directory = layer.Text(Section.Transcription, PowerShellPolicyKeys.OutputDirectory)?.Trim();
            return new PowerShellTranscriptionPolicy(
                enabled != 0,
                string.IsNullOrEmpty(directory) ? null : directory,
                layer.Flag(Section.Transcription, PowerShellPolicyKeys.EnableInvocationHeader) is { } header and not 0,
                layer.Origin);
        }

        return PowerShellTranscriptionPolicy.NotSet;
    }

    private static PowerShellScriptBlockLoggingPolicy ReadScriptBlockLogging(List<Layer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer.Flag(Section.ScriptBlockLogging, PowerShellPolicyKeys.EnableScriptBlockLogging) is { } enabled)
            {
                return new PowerShellScriptBlockLoggingPolicy(
                    enabled != 0,
                    layer.Flag(Section.ScriptBlockLogging, PowerShellPolicyKeys.EnableScriptBlockInvocationLogging) is { } invocation and not 0,
                    layer.Origin);
            }
        }

        return PowerShellScriptBlockLoggingPolicy.NotSet;
    }

    private static PowerShellModuleLoggingPolicy ReadModuleLogging(List<Layer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer.Flag(Section.ModuleLogging, PowerShellPolicyKeys.EnableModuleLogging) is { } enabled)
            {
                return new PowerShellModuleLoggingPolicy(enabled != 0, layer.Names(Section.ModuleLogging), layer.Origin);
            }
        }

        return PowerShellModuleLoggingPolicy.NotSet;
    }

    private static PowerShellExecutionPolicyPolicy ReadExecutionPolicy(List<Layer> layers)
    {
        foreach (var layer in layers)
        {
            var scripts = layer.Flag(Section.ScriptExecution, PowerShellPolicyKeys.EnableScripts);
            var policy = layer.Text(Section.ScriptExecution, PowerShellPolicyKeys.ExecutionPolicy)?.Trim();
            if (scripts is null && string.IsNullOrEmpty(policy))
            {
                continue;
            }

            // "Turn on script execution" disabled: scripts are off, which is the Restricted policy.
            return new PowerShellExecutionPolicyPolicy(scripts == 0 ? "Restricted" : string.IsNullOrEmpty(policy) ? null : policy, layer.Origin);
        }

        return PowerShellExecutionPolicyPolicy.NotSet;
    }

    /// <summary>The "PowerShellPolicies" object of a config file; null when there is no file, it is not JSON or has no such object.</summary>
    private static JsonElement? ParseConfig(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, JsonOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && Property(document.RootElement, PowerShellPolicyKeys.ConfigPolicies) is { ValueKind: JsonValueKind.Object } policies
                    ? policies.Clone()
                    : null;
        }
        catch (JsonException)
        {
            // A file the engine cannot read does not set a policy either.
            return null;
        }
    }

    /// <summary>A property by name, ignoring case (the engine reads the file case-insensitively).</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private enum Section
    {
        ScriptExecution,
        Transcription,
        ScriptBlockLogging,
        ModuleLogging,
    }

    /// <summary>One place a policy can be set.</summary>
    private abstract class Layer(PowerShellPolicyOrigin origin)
    {
        public PowerShellPolicyOrigin Origin { get; } = origin;

        /// <summary>A switch: null when it is not set, else 0 or 1 (any non-zero number is on).</summary>
        public abstract int? Flag(Section section, string name);

        public abstract string? Text(Section section, string name);

        /// <summary>The module names of the module logging policy.</summary>
        public abstract IReadOnlyList<string> Names(Section section);
    }

    private sealed class RegistryLayer(IPowerShellPolicySource source, string root, PowerShellPolicyOrigin origin, PowerShellPolicyScope scope) : Layer(origin)
    {
        public override int? Flag(Section section, string name) => source.GetDword(scope, KeyOf(section), name);

        public override string? Text(Section section, string name) => source.GetString(scope, KeyOf(section), name);

        public override IReadOnlyList<string> Names(Section section) =>
            [.. source.GetValueNames(scope, PowerShellPolicyKeys.Sub(KeyOf(section), PowerShellPolicyKeys.ModuleNamesKey)).Where(name => !string.IsNullOrWhiteSpace(name))];

        private string KeyOf(Section section) => section switch
        {
            Section.Transcription => PowerShellPolicyKeys.Sub(root, PowerShellPolicyKeys.TranscriptionKey),
            Section.ScriptBlockLogging => PowerShellPolicyKeys.Sub(root, PowerShellPolicyKeys.ScriptBlockLoggingKey),
            Section.ModuleLogging => PowerShellPolicyKeys.Sub(root, PowerShellPolicyKeys.ModuleLoggingKey),
            _ => root,
        };
    }

    private sealed class ConfigLayer(JsonElement? policies, PowerShellPolicyOrigin origin) : Layer(origin)
    {
        public override int? Flag(Section section, string name)
        {
            if (Find(section, name) is not { } value)
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
                _ => null,
            };
        }

        public override string? Text(Section section, string name) =>
            Find(section, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

        public override IReadOnlyList<string> Names(Section section) =>
            Find(section, PowerShellPolicyKeys.ModuleNamesKey) is { ValueKind: JsonValueKind.Array } array
                ? [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(name => !string.IsNullOrWhiteSpace(name))]
                : [];

        private JsonElement? Find(Section section, string name)
        {
            return policies is { } all && Property(all, SectionName(section)) is { ValueKind: JsonValueKind.Object } sectionElement
                ? Property(sectionElement, name)
                : null;
        }

        private static string SectionName(Section section) => section switch
        {
            Section.ScriptExecution => PowerShellPolicyKeys.ConfigScriptExecution,
            Section.Transcription => PowerShellPolicyKeys.TranscriptionKey,
            Section.ScriptBlockLogging => PowerShellPolicyKeys.ScriptBlockLoggingKey,
            _ => PowerShellPolicyKeys.ModuleLoggingKey,
        };
    }
}
