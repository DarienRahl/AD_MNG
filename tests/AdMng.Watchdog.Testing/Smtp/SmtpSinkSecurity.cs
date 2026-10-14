namespace AdMng.Watchdog.Testing.Smtp;

/// <summary>Transport security of a <see cref="SmtpSink"/> endpoint.</summary>
public enum SmtpSinkSecurity
{
    /// <summary>Plain SMTP, no STARTTLS offered (a relay without TLS).</summary>
    Plain,

    /// <summary>STARTTLS offered (submission on 587, relays on 25).</summary>
    StartTls,

    /// <summary>TLS from the first byte (465).</summary>
    ImplicitTls,
}
