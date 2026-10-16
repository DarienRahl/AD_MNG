namespace AdMng.Watchdog.Core.Attribution;

/// <summary>How a candidate Security event fits the detected change (<see cref="AttributionConfirmation"/>).</summary>
public enum CandidateFit
{
    /// <summary>The record describes a different change of the same object (another attribute, member, value or DN) –
    /// it is not a candidate.</summary>
    Contradicts,

    /// <summary>Same object, nothing confirms the specific change (e.g. a 4738 without SAM fields for a department
    /// change).</summary>
    Unconfirmed,

    /// <summary>The record confirms the change (attribute name and value, UAC bit, member SID, the kind's own event id).</summary>
    Confirmed,
}
