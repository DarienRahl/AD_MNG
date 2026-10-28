using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// The profile of a connected Outlook (port of MailerWPiA <c>Connect</c>/<c>LoadAccounts</c>/<c>CurrentUserSmtp</c>):
/// version, profile name, the accounts with their SMTP address (account → its current user → the Exchange user) and the
/// current user's address. Every COM object read is tracked by the caller's <see cref="ComScope"/>. Runs on the worker.
/// </summary>
internal static class OutlookProfileReader
{
    /// <summary>Reads the profile. Access to <c>Session</c> and <c>Session.Accounts</c> must work; the rest is best effort.</summary>
    public static OutlookProfile Read(object application, ComScope scope)
    {
        dynamic app = application;
        object session = scope.Track((object)app.Session);
        var version = Get(() => (string?)app.Version);
        var profileName = Get(() => (string?)((dynamic)session).CurrentProfileName);
        var current = CurrentUserAddress(session, scope);
        var accounts = Accounts(session, scope).Select(account => account.Info).ToList();
        if (accounts.Count == 0 && current is not null)
        {
            // A profile without explicit accounts: the mailbox of the current user (MailerWPiA).
            accounts.Add(new OutlookAccount(current, current));
        }

        return new OutlookProfile(version, profileName, accounts, current);
    }

    /// <summary>The accounts of <c>Session.Accounts</c> (1-based) with their SMTP address; unreadable entries are skipped.</summary>
    public static IReadOnlyList<(object Account, OutlookAccount Info)> Accounts(object session, ComScope scope)
    {
        dynamic accounts = scope.Track((object)((dynamic)session).Accounts);
        var count = GetInt(() => (int)accounts.Count);
        var result = new List<(object, OutlookAccount)>(Math.Max(0, count));
        for (var index = 1; index <= count; index++)
        {
            object account;
            try
            {
                account = scope.Track((object)accounts[index]);
            }
            catch (Exception)
            {
                continue;
            }

            var display = Get(() => (string?)((dynamic)account).DisplayName);
            var smtp = SmtpOf(account, scope);
            result.Add((account, new OutlookAccount(display.Length > 0 ? display : smtp, smtp)));
        }

        return result;
    }

    /// <summary>
    /// The account of the profile whose SMTP address is <paramref name="address"/> (case-insensitive), or null. The other
    /// accounts read on the way are tracked by <paramref name="scope"/> too.
    /// </summary>
    public static object? FindAccount(object session, string address, ComScope scope)
    {
        var wanted = address.Trim();
        foreach (var (account, info) in Accounts(session, scope))
        {
            if (string.Equals(info.SmtpAddress, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return account;
            }
        }

        return null;
    }

    /// <summary>The SMTP address of the current user (Exchange primary address, else the address when it is one), or null.</summary>
    public static string? CurrentUserAddress(object session, ComScope scope)
    {
        var exchange = Get(() => ExchangeAddress((object)((dynamic)session).CurrentUser, scope));
        if (IsAddress(exchange))
        {
            return exchange;
        }

        var plain = Get(() => (string?)scope.Track((object)((dynamic)session).CurrentUser).Address);
        return IsAddress(plain) ? plain : null;
    }

    internal static bool IsAddress(string? value) => !string.IsNullOrWhiteSpace(value) && value.Contains('@', StringComparison.Ordinal);

    /// <summary>A best-effort read (MailerWPiA <c>Get</c>): trimmed, "" on any failure.</summary>
    internal static string Get(Func<string?> read)
    {
        try
        {
            return (read() ?? string.Empty).Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static int GetInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>An account's SMTP address: <c>SmtpAddress</c>, else <c>CurrentUser.Address</c>, else the Exchange user's
    /// primary address (MailerWPiA order); "" when none.</summary>
    private static string SmtpOf(object account, ComScope scope)
    {
        var smtp = Get(() => (string?)((dynamic)account).SmtpAddress);
        if (smtp.Length == 0)
        {
            smtp = Get(() => (string?)scope.Track((object)((dynamic)account).CurrentUser).Address);
        }

        if (!IsAddress(smtp))
        {
            var exchange = Get(() => ExchangeAddress((object)((dynamic)account).CurrentUser, scope));
            if (IsAddress(exchange))
            {
                smtp = exchange;
            }
        }

        return smtp;
    }

    /// <summary><c>recipient.AddressEntry.GetExchangeUser().PrimarySmtpAddress</c> (throws when any part is missing).</summary>
    private static string? ExchangeAddress(object recipient, ComScope scope)
    {
        dynamic user = scope.Track(recipient);
        dynamic entry = scope.Track((object)user.AddressEntry);
        object? exchangeUser = entry.GetExchangeUser();
        return exchangeUser is null ? null : (string?)scope.Track(exchangeUser).PrimarySmtpAddress;
    }
}
