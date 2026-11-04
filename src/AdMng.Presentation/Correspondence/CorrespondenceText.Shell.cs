using System.Globalization;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Correspondence;

public static partial class CorrespondenceText
{
    /// <summary><c>Correspondence.Outlook.State.{state}</c>.</summary>
    public static string OutlookStateKey(OutlookState state) => "Correspondence.Outlook.State." + state;

    /// <summary><c>Correspondence.Outlook.Code.{code}</c> (<see cref="OutlookFailureCodes"/>).</summary>
    public static string OutlookCodeKey(string code) => "Correspondence.Outlook.Code." + code;

    /// <summary>
    /// The localized text of an Outlook failure: the text of its code (a code outside <see cref="OutlookFailureCodes"/>:
    /// the text of <see cref="OutlookFailureCodes.Unknown"/>), with the HRESULT of an unknown failure. Never an address or
    /// a message of the original exception (a failure has none).
    /// </summary>
    public static string FailureText(ILocalizer localizer, OutlookFailure failure)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(failure);
        var known = OutlookFailureCodes.IsKnown(failure.Code);
        var text = localizer[OutlookCodeKey(known ? failure.Code : OutlookFailureCodes.Unknown)];
        return (failure.Kind == OutlookFailureKind.Unknown || !known) && failure.HResult is { } hresult
            ? localizer.Format(L.CorrespondenceOutlook.WithHResult, text, "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture))
            : text;
    }

    /// <summary>Dynamic keys of the window, the Outlook connection, the U1 Outlook transport (track shell).</summary>
    private static partial IEnumerable<string> ShellKeys() =>
    [
        .. Enum.GetValues<OutlookState>().Select(OutlookStateKey),
        .. OutlookFailureCodes.All.Select(OutlookCodeKey),
    ];
}
