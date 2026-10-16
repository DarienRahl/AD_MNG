using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using AdMng.Presentation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Presentation.Settings;

/// <summary>Loads, changes and persists <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>The in-memory settings. Read on any thread; change only through <see cref="UpdateAsync"/>.</summary>
    AppSettings Current { get; }

    /// <summary>Loads settings from storage (defaults when missing; corrupt files are backed up and reset).</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="change"/> to <see cref="Current"/>, raises <see cref="Changed"/> and persists.
    /// A failed write (read-only profile, file locked by another instance or an antivirus scan) is logged and does not
    /// throw: the change stays in effect for this session - losing a column width must never break a command.
    /// </summary>
    Task UpdateAsync(Action<AppSettings> change, CancellationToken cancellationToken = default);

    /// <summary>Raised (on the thread that called <see cref="UpdateAsync"/> / <see cref="LoadAsync"/>) after a change.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// <see cref="ISettingsStore"/> backed by <c>settings.json</c> in <see cref="IAppPaths.SettingsDirectory"/>.
/// Writes are atomic (temporary file + replace) and serialized; a corrupt file is renamed to
/// <c>settings.corrupt-{timestamp}.json</c> and the defaults are used.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";

    private readonly string _directory;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppSettings _current = new();

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = paths.SettingsDirectory;
        _logger = logger ?? NullLogger<JsonSettingsStore>.Instance;
    }

    public event EventHandler? Changed;

    /// <summary>JSON options used for settings and for view import/export.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    public AppSettings Current => _current;

    public string FilePath => Path.Combine(_directory, FileName);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = FilePath;
        AppSettings? loaded = null;
        if (File.Exists(path))
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogWarning("Settings file is corrupt ({ErrorType}); it is backed up and defaults are used.", ex.GetType().Name);
                BackUpCorruptFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable (locked, no access): start with defaults but keep the file as it is.
                _logger.LogWarning("Settings file could not be read ({ErrorType}); defaults are used.", ex.GetType().Name);
            }
        }

        loaded ??= new AppSettings();
        loaded.Normalize();
        _current = loaded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateAsync(Action<AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(_current);
        _current.Normalize();

        // Snapshot synchronously on the caller's thread so later in-memory changes cannot race the serializer.
        var json = JsonSerializer.Serialize(_current, SerializerOptions);
        Changed?.Invoke(this, EventArgs.Empty);
        await WriteAtomicAsync(json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes settings exactly as they are written to disk (used by tests and diagnostics).</summary>
    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, SerializerOptions);

    private async Task WriteAtomicAsync(string json, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Settings could not be written ({ErrorType}); the change applies to this session only.", ex.GetType().Name);
            TryDelete(temp);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next successful write replaces it.
        }
    }

    private void BackUpCorruptFile(string path)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(path, Path.Combine(_directory, $"settings.corrupt-{stamp}.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Corrupt settings file could not be backed up ({ErrorType}).", ex.GetType().Name);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Keep Polish letters readable in the file (views are shared within the team); HTML-sensitive
            // characters are still escaped.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
