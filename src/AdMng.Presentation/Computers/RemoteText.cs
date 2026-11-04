using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Computers;

/// <summary>
/// Localized texts of remote actions: error kinds with their hint (<c>RemoteActions.Error.{kind}</c>,
/// <c>RemoteActions.Hint.{kind}</c>) and ping results.
/// </summary>
public static class RemoteText
{
    public static string ErrorKey(RemoteErrorKind kind) => $"RemoteActions.Error.{kind}";

    public static string HintKey(RemoteErrorKind kind) => $"RemoteActions.Hint.{kind}";

    /// <summary>"Komputer nie odpowiada. Sprawdź nazwę DNS…" – the error and what to do about it.</summary>
    public static string Error(ILocalizer localizer, RemoteErrorKind kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var hint = localizer[HintKey(kind)];
        return string.IsNullOrWhiteSpace(hint) ? localizer[ErrorKey(kind)] : $"{localizer[ErrorKey(kind)]} {hint}";
    }

    /// <summary>"Ping pc01.contoso.test (10.1.2.3): 4 z 4 odpowiedzi, średnio 3 ms" or "brak odpowiedzi".</summary>
    public static string Ping(ILocalizer localizer, PingResult result)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);
        var host = result.Address is { } address ? $"{result.Host} ({address})" : result.Host;
        return result.Succeeded
            ? localizer.Format(
                L.Computers.PingReplies,
                host,
                result.Received.ToString(localizer.Culture),
                result.RoundTrips.Count.ToString(localizer.Culture),
                Math.Max(0, (int)Math.Round(result.Average!.Value.TotalMilliseconds)).ToString(localizer.Culture))
            : localizer.Format(L.Computers.PingNoReply, host);
    }
}
