using System.Text.Json;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// Works out, from the raw reads of an <see cref="IPowerShellPolicySource"/>, what PowerShell policies apply to this window (track R,
/// decision D7), by the engine's own rules (<c>System.Management.Automation.dll</c> 7.6, <c>Utils.GetPolicySetting</c>; audit W-02).
/// For each policy - transcription, script block logging, module logging, the execution policy - on its own:
/// <list type="number">
/// <item>the hives in the order <c>HKLM</c>, then <c>HKCU</c>: the policy's own PowerShell 7 key (<c>PowerShellCore</c> for the execution
/// policy, <c>PowerShellCore\Transcription</c> and so on). When that key's <c>UseWindowsPowerShellPolicySetting</c> is non-zero, the same
/// hive's Windows PowerShell key of that policy is read instead (an absent one sets nothing in this hive). The first hive whose key sets
/// <b>any</b> value of the policy decides the whole policy - the hives are not mixed value by value;</item>
/// <item>only when no hive sets it: the <c>PowerShellPolicies</c> of the all-users <c>powershell.config.json</c>, then of the user's. The
/// first file that has the policy's section decides it, also an empty section; a file the engine cannot convert as a whole sets nothing.</item>
/// </list>
/// The execution policy is the engine's machine policy (<c>HKLM</c>, then the all-users file) before its user policy (<c>HKCU</c>, then
/// the user's file); a place that turns scripts neither on nor off defines none. A registry switch counts only as the <c>REG_DWORD</c> 0 or
/// 1, a text only as a <c>REG_SZ</c> / <c>REG_EXPAND_SZ</c>. A policy set to off is a decision too - that is how a policy overrides a file
/// that turned it on. Nothing here changes a policy or reads anything of the directory.
/// </summary>
public sealed class PowerShellPolicyReader(IPowerShellPolicySource source) : IPowerShellPolicyReader
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly PowerShellPolicyScope[] Hives = [PowerShellPolicyScope.Computer, PowerShellPolicyScope.User];

    private readonly IPowerShellPolicySource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc/>
    public Task<PowerShellPolicies> ReadAsync(CancellationToken cancellationToken) => Task.Run(Read, cancellationToken);

    /// <summary>Reads the policies on the calling thread.</summary>
    public PowerShellPolicies Read()
    {
        var files = new Dictionary<PowerShellPolicyScope, ConfigFile?>
        {
            [PowerShellPolicyScope.Computer] = ConfigFile.Parse(_source.ReadConfigFile(PowerShellPolicyScope.Computer)),
            [PowerShellPolicyScope.User] = ConfigFile.Parse(_source.ReadConfigFile(PowerShellPolicyScope.User)),
        };

        return new PowerShellPolicies
        {
            Transcription = ReadTranscription(Find(Policy.Transcription, Hives, files)),
            ScriptBlockLogging = ReadScriptBlockLogging(Find(Policy.ScriptBlockLogging, Hives, files)),
            ModuleLogging = ReadModuleLogging(Find(Policy.ModuleLogging, Hives, files)),
            ExecutionPolicy = ReadExecutionPolicy(files),
            Lockdown = _source.GetLockdownState(),
        };
    }

    private static PowerShellTranscriptionPolicy ReadTranscription(Settings? settings)
    {
        if (settings is null)
        {
            return PowerShellTranscriptionPolicy.NotSet;
        }

        var directory = settings.Text(PowerShellPolicyKeys.OutputDirectory)?.Trim();
        return new PowerShellTranscriptionPolicy(
            settings.Flag(PowerShellPolicyKeys.EnableTranscripting) == true,
            string.IsNullOrEmpty(directory) ? null : directory,
            settings.Flag(PowerShellPolicyKeys.EnableInvocationHeader) == true,
            settings.Origin);
    }

    private static PowerShellScriptBlockLoggingPolicy ReadScriptBlockLogging(Settings? settings) => settings is null
        ? PowerShellScriptBlockLoggingPolicy.NotSet
        : new PowerShellScriptBlockLoggingPolicy(
            settings.Flag(PowerShellPolicyKeys.EnableScriptBlockLogging) == true,
            settings.Flag(PowerShellPolicyKeys.EnableScriptBlockInvocationLogging) == true,
            settings.Origin);

    private static PowerShellModuleLoggingPolicy ReadModuleLogging(Settings? settings) => settings is null
        ? PowerShellModuleLoggingPolicy.NotSet
        : new PowerShellModuleLoggingPolicy(
            settings.Flag(PowerShellPolicyKeys.EnableModuleLogging) == true,
            [.. (settings.Names(PowerShellPolicyKeys.ModuleNamesKey) ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)],
            settings.Origin);

    /// <summary>The machine policy (<c>HKLM</c>, then the all-users file), then the user policy (<c>HKCU</c>, then the user's file).</summary>
    private PowerShellExecutionPolicyPolicy ReadExecutionPolicy(Dictionary<PowerShellPolicyScope, ConfigFile?> files)
    {
        foreach (var hive in Hives)
        {
            if (Find(Policy.ScriptExecution, [hive], files) is not { } settings)
            {
                continue;
            }

            // "Turn on script execution" disabled: scripts are off, which is the Restricted policy; enabled: the policy it names.
            var policy = settings.Flag(PowerShellPolicyKeys.EnableScripts) switch
            {
                false => "Restricted",
                true => settings.Text(PowerShellPolicyKeys.ExecutionPolicy)?.Trim(),
                null => null,
            };
            if (!string.IsNullOrEmpty(policy))
            {
                return new PowerShellExecutionPolicyPolicy(policy, settings.Origin);
            }
        }

        return PowerShellExecutionPolicyPolicy.NotSet;
    }

    /// <summary>The engine's <c>GetPolicySetting</c>: the registry of the hives in order, then the config files in the same order.</summary>
    private Settings? Find(Policy policy, PowerShellPolicyScope[] order, Dictionary<PowerShellPolicyScope, ConfigFile?> files)
    {
        foreach (var hive in order)
        {
            if (FromRegistry(policy, hive) is { } settings)
            {
                return settings;
            }
        }

        foreach (var scope in order)
        {
            if (files[scope]?.Section(policy.Section) is { } values)
            {
                return new Settings(values, new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, scope));
            }
        }

        return null;
    }

    /// <summary>The engine's <c>GetPolicySettingFromGPOImpl</c> for one hive: null when this hive sets no value of the policy.</summary>
    private Settings? FromRegistry(Policy policy, PowerShellPolicyScope hive)
    {
        var key = policy.CoreKey;
        var store = PowerShellPolicyStore.PowerShellCore;
        switch (_source.GetValue(hive, policy.CoreKey, PowerShellPolicyKeys.UseWindowsPowerShellPolicy))
        {
            case null or 0:
                break;
            case int:
                key = policy.WindowsKey;
                store = PowerShellPolicyStore.WindowsPowerShell;
                break;
            default:
                // The engine casts the value to a number: another type makes it fail, so what it records is not known.
                throw new PowerShellPolicyUnreadableException(PowerShellPolicyPlace.SwitchValue, HiveName(hive) + "\\" + policy.CoreKey);
        }

        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in policy.Properties)
        {
            var raw = _source.GetValue(hive, key, name);
            var value = raw is null
                ? type == PropertyType.Names ? _source.GetValueNames(hive, PowerShellPolicyKeys.Sub(key, name))?.ToArray() : null
                : FromRegistryValue(raw, type);
            if (value is not null)
            {
                values[name] = value;
            }
        }

        return values.Count == 0 ? null : new Settings(values, new PowerShellPolicyOrigin(store, hive));
    }

    /// <summary>The engine's conversion of a registry value: a switch is the <c>REG_DWORD</c> 0 or 1, a text a string, a list a multi-string or one string.</summary>
    private static object? FromRegistryValue(object raw, PropertyType type) => type switch
    {
        PropertyType.Flag => raw switch
        {
            1 => true,
            0 => false,
            _ => null,
        },
        PropertyType.Text => raw as string,
        _ => raw switch
        {
            string[] names => names,
            string name => new[] { name },
            _ => null,
        },
    };

    private static string HiveName(PowerShellPolicyScope hive) => hive == PowerShellPolicyScope.Computer ? "HKLM" : "HKCU";

    private enum PropertyType
    {
        Flag,
        Text,
        Names,
    }

    /// <summary>A policy class of the engine: its keys, its section of the config file and its properties (the value names).</summary>
    private sealed record Policy(string Section, string CoreKey, string WindowsKey, (string Name, PropertyType Type)[] Properties)
    {
        public static Policy ScriptExecution { get; } = new(
            PowerShellPolicyKeys.ConfigScriptExecution,
            PowerShellPolicyKeys.CoreRoot,
            PowerShellPolicyKeys.WindowsRoot,
            [(PowerShellPolicyKeys.ExecutionPolicy, PropertyType.Text), (PowerShellPolicyKeys.EnableScripts, PropertyType.Flag)]);

        public static Policy Transcription { get; } = Of(
            PowerShellPolicyKeys.TranscriptionKey,
            [(PowerShellPolicyKeys.EnableTranscripting, PropertyType.Flag), (PowerShellPolicyKeys.EnableInvocationHeader, PropertyType.Flag), (PowerShellPolicyKeys.OutputDirectory, PropertyType.Text)]);

        public static Policy ScriptBlockLogging { get; } = Of(
            PowerShellPolicyKeys.ScriptBlockLoggingKey,
            [(PowerShellPolicyKeys.EnableScriptBlockInvocationLogging, PropertyType.Flag), (PowerShellPolicyKeys.EnableScriptBlockLogging, PropertyType.Flag)]);

        public static Policy ModuleLogging { get; } = Of(
            PowerShellPolicyKeys.ModuleLoggingKey,
            [(PowerShellPolicyKeys.EnableModuleLogging, PropertyType.Flag), (PowerShellPolicyKeys.ModuleNamesKey, PropertyType.Names)]);

        /// <summary>
        /// Every section of the engine's <c>PowerShellPolicies</c> with its properties: the engine converts the object as a whole, so a
        /// value it cannot convert in any section - also one the window does not show - makes the whole file set nothing.
        /// </summary>
        public static IReadOnlyDictionary<string, (string Name, PropertyType Type)[]> ConfigSections { get; } =
            new Dictionary<string, (string, PropertyType)[]>(StringComparer.OrdinalIgnoreCase)
            {
                [ScriptExecution.Section] = ScriptExecution.Properties,
                [Transcription.Section] = Transcription.Properties,
                [ScriptBlockLogging.Section] = ScriptBlockLogging.Properties,
                [ModuleLogging.Section] = ModuleLogging.Properties,
                ["ProtectedEventLogging"] = [("EnableProtectedEventLogging", PropertyType.Flag), ("EncryptionCertificate", PropertyType.Names)],
                ["UpdatableHelp"] = [("EnableUpdateHelpDefaultSourcePath", PropertyType.Flag), ("DefaultSourcePath", PropertyType.Text)],
                ["ConsoleSessionConfiguration"] = [("EnableConsoleSessionConfiguration", PropertyType.Flag), ("ConsoleSessionConfigurationName", PropertyType.Text)],
            };

        private static Policy Of(string key, (string, PropertyType)[] properties) =>
            new(key, PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, key), PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.WindowsRoot, key), properties);
    }

    /// <summary>The values one place sets for a policy, and the place.</summary>
    private sealed class Settings(IReadOnlyDictionary<string, object> values, PowerShellPolicyOrigin origin)
    {
        public PowerShellPolicyOrigin Origin { get; } = origin;

        public bool? Flag(string name) => values.TryGetValue(name, out var value) && value is bool flag ? flag : null;

        public string? Text(string name) => values.TryGetValue(name, out var value) ? value as string : null;

        public string?[]? Names(string name) => values.TryGetValue(name, out var value) ? value as string?[] : null;
    }

    /// <summary>
    /// The <c>PowerShellPolicies</c> of a config file as the engine converts them (Newtonsoft.Json into its policy classes): names ignore
    /// case and the last of the same name wins, a switch is <c>true</c> / <c>false</c>, a number (non-zero is on) or the text
    /// <c>"true"</c> / <c>"false"</c>, a text is a string or a number or switch as text, a list is an array of those. Anything else -
    /// a file that is not JSON, a section that is not an object, a value of another kind - and the engine takes no policy of the file.
    /// </summary>
    private sealed class ConfigFile
    {
        private readonly Dictionary<string, Dictionary<string, object>?> _sections;

        private ConfigFile(Dictionary<string, Dictionary<string, object>?> sections) => _sections = sections;

        /// <summary>The values of a section; null when the file has no such section (or it is <c>null</c>). An empty section is a section.</summary>
        public IReadOnlyDictionary<string, object>? Section(string name) => _sections.TryGetValue(name, out var section) ? section : null;

        public static ConfigFile? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(text, JsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || RootProperty(document.RootElement, PowerShellPolicyKeys.ConfigPolicies) is not { } policies
                    || policies.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var sections = new Dictionary<string, Dictionary<string, object>?>(StringComparer.OrdinalIgnoreCase);
                foreach (var section in policies.EnumerateObject())
                {
                    if (!Policy.ConfigSections.TryGetValue(section.Name, out var properties))
                    {
                        continue;
                    }

                    sections[section.Name] = section.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.Object => ReadSection(section.Value, properties),
                        _ => throw new FormatException("A section that is not an object."),
                    };
                }

                return new ConfigFile(sections);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // A file the engine cannot convert sets no policy (it warns about the user's file and takes none of its policies).
                return null;
            }
        }

        private static Dictionary<string, object> ReadSection(JsonElement section, (string Name, PropertyType Type)[] properties)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in section.EnumerateObject())
            {
                var known = Array.FindIndex(properties, candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (known < 0)
                {
                    continue;
                }

                var (name, type) = properties[known];
                if (Convert(property.Value, type) is { } value)
                {
                    values[name] = value;
                }
                else
                {
                    values.Remove(name);
                }
            }

            return values;
        }

        private static object? Convert(JsonElement value, PropertyType type) => type switch
        {
            PropertyType.Flag => value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Number => value.GetDouble() != 0,
                JsonValueKind.String when string.IsNullOrEmpty(value.GetString()) => null,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var flag) => flag,
                _ => throw new FormatException("A switch the engine cannot convert."),
            },
            PropertyType.Text => TextOf(value),
            _ => value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Array => value.EnumerateArray().Select(TextOf).ToArray(),
                _ => throw new FormatException("A list the engine cannot convert."),
            },
        };

        private static string? TextOf(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => throw new FormatException("A text the engine cannot convert."),
        };

        /// <summary>The property of that name, exactly, else the first that matches ignoring case (as the engine finds its root key).</summary>
        private static JsonElement? RootProperty(JsonElement element, string name)
        {
            JsonElement? ignoringCase = null;
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    return property.Value;
                }

                if (ignoringCase is null && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    ignoringCase = property.Value;
                }
            }

            return ignoringCase;
        }
    }
}
