using System.Globalization;
using System.Resources;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>Localized texts of the service itself (test sends) from <c>Resources/ServiceStrings(.pl).resx</c>, always with
/// an explicit culture (the notification language of the configuration, never the thread culture).</summary>
public static class ServiceText
{
    private static readonly ResourceManager Resources = new("AdMng.Watchdog.Service.Resources.ServiceStrings", typeof(ServiceText).Assembly);

    /// <summary>"pl" → Polish, anything else → English (the neutral resources).</summary>
    public static CultureInfo Culture(string? language) =>
        string.Equals(language, "pl", StringComparison.OrdinalIgnoreCase) ? CultureInfo.GetCultureInfo("pl-PL") : CultureInfo.InvariantCulture;

    public static string Get(string key, CultureInfo culture) =>
        Resources.GetString(key, culture) ?? throw new InvalidOperationException($"Missing service string '{key}'.");

    public static string Format(string key, CultureInfo culture, params object?[] args) =>
        string.Format(culture, Get(key, culture), args);
}
