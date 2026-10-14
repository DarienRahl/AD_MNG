namespace AdMng.Watchdog.Core.Formatting;

/// <summary>How the values of one attribute change may be shown at a detail level (<see cref="DetailLevelFilter"/>).</summary>
public enum ValueVisibility
{
    /// <summary>Values are shown (capped).</summary>
    Visible,

    /// <summary>Minimal detail level: the attribute name only.</summary>
    NameOnly,

    /// <summary>Personal data at the Standard level (or values removed upstream): "zmieniono".</summary>
    Hidden,

    /// <summary>Secret, confidential or redacted: "zmieniono (wartość ukryta)" at every level.</summary>
    Secret,
}
