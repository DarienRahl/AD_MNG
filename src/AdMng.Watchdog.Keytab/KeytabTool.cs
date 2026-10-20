using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Watchdog.Keytab.Native;

namespace AdMng.Watchdog.Keytab;

/// <summary>
/// <c>admng-watchdog-keytab</c> (design 4.4–4.6): <c>init</c> with the one-time password, <c>rotate</c> (timer),
/// <c>verify</c> and <c>status</c> for the Watchdog's own computer account. Every keytab it writes starts with the account
/// principal (newest kvno first), then <c>HTTP/</c> and <c>host/</c>, aes256 + aes128 only, and keeps the previous kvno;
/// it is written as a new file in the keytab's folder and renamed over the old one. The password exists only in pinned
/// buffers that are zeroed after the key derivation; output and <c>status.json</c> are value-free.
/// </summary>
public sealed class KeytabTool
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitFailure = 2;

    private const long MinimumFreeBytes = 1024 * 1024;
    private const string KeytabGroup = "admng-keytab";

    private readonly KeytabToolHost _host;
    private readonly KeytabArguments _args;

    private KeytabTool(KeytabToolHost host, KeytabArguments args)
    {
        _host = host;
        _args = args;
    }

    /// <summary>Runs one command; returns the exit code (0 ok, 1 usage, 2 failure).</summary>
    public static int Run(IReadOnlyList<string> args, KeytabToolHost host, string? krb5ConfigVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        KeytabArguments parsed;
        try
        {
            parsed = KeytabArguments.Parse(args, krb5ConfigVariable);
        }
        catch (ArgumentException ex)
        {
            host.Error.WriteLine("admng-watchdog-keytab: " + ex.Message);
            host.Error.WriteLine(KeytabArguments.Usage);
            return ExitUsage;
        }

        if (parsed.Command == "help")
        {
            host.Out.WriteLine(KeytabArguments.Usage);
            return ExitOk;
        }

        var tool = new KeytabTool(host, parsed);
        try
        {
            return parsed.Command switch
            {
                "init" => tool.Init(),
                "rotate" => tool.Rotate(),
                "verify" => tool.VerifyCommand(),
                _ => tool.PrintStatus(),
            };
        }
        catch (DllNotFoundException)
        {
            tool.Error("libkrb5.so.3 / libk5crypto.so.3 not found - install libkrb5-3.");
            return ExitFailure;
        }
        catch (Exception ex) when (ex is Krb5Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            tool.Error(ex.Message);
            return ExitFailure;
        }
    }

    // ---- init ------------------------------------------------------------------------------------------------------------

    private int Init()
    {
        if (!_args.PasswordStdin && !_host.InputIsTerminal)
        {
            Error("init reads the one-time password from a terminal; stdin is not one (--password-stdin is for the lab only).");
            return ExitUsage;
        }

        var realm = _args.Principal is null ? KerberosConfig.DefaultRealm(_args.Krb5Config) : null;
        var account = AccountPrincipal.Parse(_args.Principal ?? "ADMNG-WD$@" + (realm ?? throw new InvalidOperationException(
            "The Kerberos configuration has no default_realm; pass --principal 'NAME$@REALM'.")));
        var principals = account.PrincipalsFor(_args.Fqdn ?? account.DefaultFqdn);

        using var instance = AcquireLock();
        if (instance is null)
        {
            return ExitFailure;
        }

        using var config = KerberosConfig.Load(_args.Krb5Config, account.Realm);
        var previous = KeytabStatus.Read(_args.StateDirectory);
        using var otp = ReadOneTimePassword(account);
        if (otp is null)
        {
            return ExitUsage;
        }

        using var context = Krb5Context.Open(config.ProfileFor(config.PrimaryKdc));
        using var client = context.ParseName(account.Principal);
        var code = context.GetInitialCredentials(client, otp, "kadmin/changepw", IntPtr.Zero, out var creds);
        if (code != 0)
        {
            return Failed(account, previous, KeytabResults.ChangeFailed, Krb5ErrorNames.Of(code), code switch
            {
                Krb5ErrorNames.KdcErrPreauthFailed or Krb5ErrorNames.PreauthFailed => "The one-time password was not accepted by the KDC.",
                Krb5ErrorNames.KdcErrClientRevoked => "The account is disabled or locked (enable it first - design 4.2).",
                Krb5ErrorNames.KdcErrClientPrincipalUnknown => $"The KDC does not know {account.Principal}.",
                Krb5ErrorNames.KdcUnreachable => $"No KDC of {account.Realm} answered (port 88 to {config.PrimaryKdc}).",
                _ => "The one-time password could not be used.",
            });
        }

        // The kvno the one-time password produced: the new password gets the next one.
        uint? kvnoBefore = context.GetServiceKvno(client, otp, null, KvnoService(principals), out var otpKvno) == 0 ? otpKvno : null;
        int resultCode;
        var newPassword = PasswordGenerator.Generate();
        try
        {
            code = context.ChangePassword(ref creds, newPassword, out resultCode);
        }
        catch
        {
            newPassword.Dispose();
            throw;
        }
        finally
        {
            context.FreeCredentials(ref creds);
        }

        if (code != 0 || (resultCode != 0 && resultCode != Krb5ErrorNames.KpasswdSoftError))
        {
            newPassword.Dispose();
            return Failed(account, previous, KeytabResults.ChangeFailed, code != 0 ? Krb5ErrorNames.Of(code) : Krb5ErrorNames.OfKpasswdResult(resultCode),
                $"The password change was refused or kpasswd ({config.PasswordServer ?? config.PrimaryKdc}, port 464) did not answer.");
        }

        if (resultCode == Krb5ErrorNames.KpasswdSoftError)
        {
            // Q5: the KDC refuses an immediate change (e.g. minimum password age) - the keytab gets the one-time
            // password's keys and the timer changes the password as soon as the KDC allows it.
            newPassword.Dispose();
            Warn($"The KDC refused the password change by policy ({Krb5ErrorNames.OfKpasswdResult(resultCode)}, e.g. minimum password age); "
                + "the keytab is written from the one-time password (status otpActive) and the daily timer changes the password.");
            return Commit(context, config, account, principals, otp, kvnoBefore, previous, passwordChanged: false,
                KeytabResults.OtpActive, Krb5ErrorNames.OfKpasswdResult(resultCode));
        }

        otp.Clear();
        using (newPassword)
        {
            return Commit(context, config, account, principals, newPassword, kvnoBefore + 1, previous, passwordChanged: true, KeytabResults.Ok, null);
        }
    }

    // ---- rotate ----------------------------------------------------------------------------------------------------------

    private int Rotate()
    {
        if (!File.Exists(_args.Keytab))
        {
            Error($"{_args.Keytab} does not exist - run 'admng-watchdog-keytab init' first.");
            return ExitFailure;
        }

        using var instance = AcquireLock();
        if (instance is null)
        {
            return ExitFailure;
        }

        var previous = KeytabStatus.Read(_args.StateDirectory);
        var now = _host.Time.GetUtcNow();
        var otpActive = previous?.Result == KeytabResults.OtpActive;
        if (!otpActive && previous?.LastChange is { } lastChange && now - lastChange < _args.IfOlderThan)
        {
            Info($"The password is younger than {Format(_args.IfOlderThan)} (changed {lastChange:yyyy-MM-dd}); verifying only.");
            return Verify(previous);
        }

        var (account, principals, keytabKvno) = ReadKeytabLayout(previous?.Principal);
        using var config = KerberosConfig.Load(_args.Krb5Config, account.Realm);
        if (Preflight() is { } problem)
        {
            return Failed(account, previous, KeytabResults.ChangeFailed, problem, "Pre-flight check failed; the password was not changed.");
        }

        using var context = Krb5Context.Open(config.ProfileFor(config.PrimaryKdc));
        using var client = context.ParseName(account.Principal);
        var code = context.GetInitialCredentials(client, _args.Keytab, "kadmin/changepw", IntPtr.Zero, out var creds);
        if (code != 0)
        {
            return Failed(account, previous, otpActive ? KeytabResults.OtpActive : KeytabResults.ChangeFailed, Krb5ErrorNames.Of(code), code switch
            {
                Krb5ErrorNames.KdcErrPreauthFailed or Krb5ErrorNames.PreauthFailed => "The keytab no longer matches the account (password reset elsewhere?) - "
                    + RemedyText(account),
                Krb5ErrorNames.KdcUnreachable => $"No KDC of {account.Realm} answered (port 88 to {config.PrimaryKdc}); nothing was changed.",
                _ => "The keytab could not authenticate; nothing was changed.",
            });
        }

        int resultCode;
        var newPassword = PasswordGenerator.Generate();
        try
        {
            code = context.ChangePassword(ref creds, newPassword, out resultCode);
        }
        catch
        {
            newPassword.Dispose();
            throw;
        }
        finally
        {
            context.FreeCredentials(ref creds);
        }

        if (code != 0 || resultCode != 0)
        {
            newPassword.Dispose();
            var error = code != 0 ? Krb5ErrorNames.Of(code) : Krb5ErrorNames.OfKpasswdResult(resultCode);
            return Failed(account, previous, otpActive ? KeytabResults.OtpActive : KeytabResults.ChangeFailed, error,
                "The password change was refused or kpasswd did not answer; the password and the keytab are unchanged (next attempt by the timer).");
        }

        using (newPassword)
        {
            return Commit(context, config, account, principals, newPassword, keytabKvno + 1, previous, passwordChanged: true, KeytabResults.Ok, null);
        }
    }

    // ---- commit (init, rotate, otpActive fallback) -------------------------------------------------------------------------

    /// <summary>
    /// Learns the new kvno from a fresh service ticket, derives the keys, writes the new keytab next to the old one (new
    /// entries + the old keytab's newest kvno), verifies it at the primary KDC, renames it into place and verifies against
    /// every KDC. A failure after a successful change loses the new password (it exists nowhere else) – the remedy is a
    /// new one-time password and <c>init</c> (design 4.6).
    /// </summary>
    private int Commit(
        Krb5Context context,
        KerberosConfig config,
        AccountPrincipal account,
        IReadOnlyList<string> principals,
        SecretBuffer password,
        uint? expectedKvno,
        KeytabStatus? previous,
        bool passwordChanged,
        string result,
        string? resultError)
    {
        string? temp = null;
        uint kvno;
        try
        {
            _host.FaultPoint?.Invoke("changed");
            using var client = context.ParseName(account.Principal);
            var code = context.GetServiceKvno(client, password, null, KvnoService(principals), out var confirmed);
            if (code == 0)
            {
                kvno = confirmed;
                if (expectedKvno is { } expected && expected != kvno)
                {
                    Warn($"The KDC reports kvno {kvno}, expected {expected} (another change in between?).");
                }
            }
            else
            {
                kvno = expectedKvno ?? throw new Krb5Exception(code, "Reading the kvno of the new key");
                Warn($"The new kvno could not be confirmed ({Krb5ErrorNames.Of(code)}); using the expected kvno {kvno}.");
            }

            using (var keys = context.DeriveKeys(password, account.Salt))
            {
                password.Clear();
                temp = WriteKeytab(context, principals, kvno, keys);
            }

            _host.FaultPoint?.Invoke("written");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            password.Clear();
            DeleteQuietly(temp);
            return WriteFailed(account, previous, passwordChanged, ex);
        }

        // Pre-commit check at the primary KDC (wrong salt or kvno would only show at use time - design 4.3).
        using var verifier = context.ParseName(account.Principal);
        var primaryCode = context.GetInitialCredentials(verifier, temp, null, IntPtr.Zero, out var check);
        if (primaryCode == 0)
        {
            context.FreeCredentials(ref check);
        }

        try
        {
            CommitFile(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temp);
            return WriteFailed(account, previous, passwordChanged, ex);
        }

        var now = _host.Time.GetUtcNow();
        var kept = ListKeytab(context).Where(e => e.Kvno != kvno).Select(e => e.Kvno).Distinct().ToList();
        Info($"Keytab {_args.Keytab} written: {principals.Count} principals with kvno {kvno}"
            + (kept.Count > 0 ? $", previous kvno {string.Join(", ", kept)} kept." : "."));

        string? error = resultError;
        if (primaryCode != 0)
        {
            Error($"The new keytab does not authenticate at {config.PrimaryKdc} ({Krb5ErrorNames.Of(primaryCode)}).");
            result = KeytabResults.VerifyFailed;
            error = Krb5ErrorNames.Of(primaryCode);
        }
        else
        {
            var (allReplicated, pendingError) = WaitForReplication(config, account);
            if (!allReplicated && result == KeytabResults.Ok)
            {
                result = KeytabResults.ReplicationPending;
                error = pendingError;
            }
        }

        var status = new KeytabStatus
        {
            Principal = account.Principal,
            Kvno = kvno,
            LastChange = passwordChanged || result == KeytabResults.OtpActive ? now : previous?.LastChange,
            LastAttempt = now,
            Result = result,
            ErrorCode = error,
            NextDue = result == KeytabResults.OtpActive ? now : now + _args.IfOlderThan,
        };
        status.Write(_args.StateDirectory);
        Info($"Status: {result}" + (error is null ? "." : $" ({error})."));
        return result is KeytabResults.Ok or KeytabResults.ReplicationPending or KeytabResults.OtpActive ? ExitOk : ExitFailure;
    }

    private int WriteFailed(AccountPrincipal account, KeytabStatus? previous, bool passwordChanged, Exception error)
    {
        var code = error is Krb5Exception k ? k.Name : error.GetType().Name;
        if (passwordChanged)
        {
            Error("The password was changed but the new keytab could not be written (" + code + "): the new password is lost. "
                + "Existing tickets keep working for up to 10 hours. " + RemedyText(account));
        }
        else
        {
            Error("The keytab could not be written (" + code + "); the account password is unchanged - run init again.");
        }

        return Failed(account, previous, KeytabResults.KeytabWriteFailed, code, null);
    }

    /// <summary>The remedy of a lost or foreign password (design 4.6 – every remedy text uses this exact form).</summary>
    internal static string RemedyText(AccountPrincipal account) =>
        $"A Tier 0 administrator sets a new one-time password (Set-ADAccountPassword -Identity '{account.Name}' -Reset -NewPassword $otp, "
        + "$otp generated as in the instructions), then run: sudo admng-watchdog-keytab init";

    // ---- verify ----------------------------------------------------------------------------------------------------------

    private int VerifyCommand()
    {
        using var instance = AcquireLock();
        return instance is null ? ExitFailure : Verify(KeytabStatus.Read(_args.StateDirectory));
    }

    /// <summary>Entry order, authentication from the keytab at every KDC, kvno of a fresh ticket for the <c>HTTP/</c>
    /// principal; updates <c>status.json</c> (a pending failure state – otpActive, changeFailed, keytabWriteFailed – is
    /// kept unless the check fails).</summary>
    private int Verify(KeytabStatus? previous)
    {
        if (!File.Exists(_args.Keytab))
        {
            Error($"{_args.Keytab} does not exist.");
            return ExitFailure;
        }

        var (account, principals, kvno) = ReadKeytabLayout(_args.Principal ?? previous?.Principal);
        using var config = KerberosConfig.Load(_args.Krb5Config, account.Realm);
        string result = KeytabResults.Ok;
        string? error = null;

        List<KeytabEntryInfo> entries;
        using (var listing = Krb5Context.Open(config.Path))
        {
            entries = ListKeytab(listing);
        }

        foreach (var group in entries.GroupBy(e => e.Principal))
        {
            Info($"{group.Key}: kvno {string.Join(", ", group.Select(e => e.Kvno).Distinct())}; "
                + string.Join(", ", group.Select(e => e.EnctypeName).Distinct()));
        }

        if (!string.Equals(entries[0].Principal, account.Principal, StringComparison.Ordinal))
        {
            Error($"The first keytab entry is {entries[0].Principal}, not {account.Principal} (LDAP binds through KRB5_CLIENT_KTNAME would use it).");
            result = KeytabResults.VerifyFailed;
            error = "keytabOrder";
        }

        foreach (var kdc in config.Kdcs)
        {
            using var context = Krb5Context.Open(config.ProfileFor(kdc));
            using var client = context.ParseName(account.Principal);
            var code = context.GetInitialCredentials(client, _args.Keytab, null, IntPtr.Zero, out var creds);
            if (code == 0)
            {
                context.FreeCredentials(ref creds);
                Info($"{kdc}: the keytab authenticates.");
                continue;
            }

            Warn($"{kdc}: the keytab does not authenticate ({Krb5ErrorNames.Of(code)}).");
            if (result == KeytabResults.Ok)
            {
                (result, error) = string.Equals(kdc, config.PrimaryKdc, StringComparison.OrdinalIgnoreCase)
                    ? (KeytabResults.VerifyFailed, Krb5ErrorNames.Of(code))
                    : (KeytabResults.ReplicationPending, Krb5ErrorNames.Of(code));
            }
        }

        if (result != KeytabResults.VerifyFailed)
        {
            using var context = Krb5Context.Open(config.ProfileFor(config.PrimaryKdc));
            using var client = context.ParseName(account.Principal);
            var service = principals.FirstOrDefault(p => p.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) ?? KvnoService(principals);
            var code = context.GetServiceKvno(client, null, _args.Keytab, service, out var ticketKvno);
            if (code != 0)
            {
                Error($"No service ticket for {service} ({Krb5ErrorNames.Of(code)}).");
                (result, error) = (KeytabResults.VerifyFailed, Krb5ErrorNames.Of(code));
            }
            else if (ticketKvno != kvno)
            {
                Error($"The KDC issues tickets with kvno {ticketKvno}, the keytab's newest kvno is {kvno} (stale keytab). " + RemedyText(account));
                (result, error) = (KeytabResults.VerifyFailed, "kvnoMismatch");
            }
            else
            {
                Info($"A fresh ticket for {service} has kvno {ticketKvno} = the keytab's newest kvno.");
            }
        }

        var keep = previous?.Result is KeytabResults.OtpActive or KeytabResults.ChangeFailed or KeytabResults.KeytabWriteFailed;
        var status = (previous ?? new KeytabStatus { Principal = account.Principal }) with
        {
            Principal = account.Principal,
            Kvno = kvno,
            Result = keep && result != KeytabResults.VerifyFailed ? previous!.Result : result,
            ErrorCode = keep && result != KeytabResults.VerifyFailed ? previous!.ErrorCode : error,
        };
        status.Write(_args.StateDirectory);
        Info($"Verify: {result}" + (error is null ? "." : $" ({error})."));
        return result == KeytabResults.VerifyFailed ? ExitFailure : ExitOk;
    }

    private int PrintStatus()
    {
        var status = KeytabStatus.Read(_args.StateDirectory);
        if (status is null)
        {
            Error($"No status ({Path.Combine(_args.StateDirectory, KeytabStatus.FileName)}) - run 'admng-watchdog-keytab init'.");
            return ExitFailure;
        }

        _host.Out.WriteLine(KeytabStatus.Serialize(status));
        return ExitOk;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>The account principal, the principals of the newest kvno (account first) and that kvno.</summary>
    private (AccountPrincipal Account, IReadOnlyList<string> Principals, uint Kvno) ReadKeytabLayout(string? expectedPrincipal)
    {
        List<KeytabEntryInfo> entries;
        using (var context = Krb5Context.Open(_args.Krb5Config))
        {
            entries = ListKeytab(context);
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException($"{_args.Keytab} holds no entries - run 'admng-watchdog-keytab init'.");
        }

        var accountName = expectedPrincipal
            ?? entries.Select(e => e.Principal).FirstOrDefault(p => !p.Contains('/', StringComparison.Ordinal) && p.Split('@')[0].EndsWith('$'))
            ?? throw new InvalidOperationException("The keytab holds no computer account principal (NAME$@REALM); pass --principal.");
        var account = AccountPrincipal.Parse(accountName);
        var newest = entries.Where(e => string.Equals(e.Principal, account.Principal, StringComparison.Ordinal)).Select(e => e.Kvno).DefaultIfEmpty().Max();
        if (newest == 0)
        {
            throw new InvalidOperationException($"The keytab holds no entry for {account.Principal}.");
        }

        var principals = new List<string> { account.Principal };
        principals.AddRange(entries.Where(e => e.Kvno == newest).Select(e => e.Principal).Distinct(StringComparer.Ordinal)
            .Where(p => !string.Equals(p, account.Principal, StringComparison.Ordinal)));
        return (account, principals, newest);
    }

    private List<KeytabEntryInfo> ListKeytab(Krb5Context context) => context.ListKeytab(_args.Keytab);

    /// <summary>The principal whose fresh service ticket tells the kvno (design 4.4: <c>host/</c>).</summary>
    private static string KvnoService(IReadOnlyList<string> principals) =>
        principals.FirstOrDefault(p => p.StartsWith("host/", StringComparison.OrdinalIgnoreCase))
        ?? principals.FirstOrDefault(p => p.Contains('/', StringComparison.Ordinal))
        ?? principals[0];

    /// <summary>
    /// New keytab in the keytab's folder: per principal (in order) the new kvno's aes256 + aes128 entries, then the old
    /// keytab's entries of its newest kvno (live tickets of the previous key – design 4.3). Mode 0600 until committed.
    /// </summary>
    private string WriteKeytab(Krb5Context context, IReadOnlyList<string> principals, uint kvno, DerivedKeys keys)
    {
        var directory = Path.GetDirectoryName(_args.Keytab) ?? "/";
        var temp = Path.Combine(directory, "." + Path.GetFileName(_args.Keytab) + ".new-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write([0x05, 0x02]); // keytab format version 0x502
        }

        var existing = ListKeytab(context);
        var keep = existing.Where(e => e.Kvno != kvno && principals.Contains(e.Principal, StringComparer.Ordinal)).Select(e => (uint?)e.Kvno).Max();
        var now = _host.Time.GetUtcNow();
        var keytab = context.ResolveKeytab(temp);
        try
        {
            foreach (var name in principals)
            {
                using var principal = context.ParseName(name);
                for (var i = 0; i < keys.Count; i++)
                {
                    context.AddEntry(keytab, principal, kvno, ref keys[i], now);
                }

                if (keep is { } previousKvno)
                {
                    context.CopyEntries(_args.Keytab, keytab, e => e.Kvno == previousKvno && string.Equals(e.Principal, principal.Name, StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            context.CloseKeytab(keytab);
        }

        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Flush(flushToDisk: true);
        }

        return temp;
    }

    /// <summary>root:admng-keytab 0640 (group when it exists), then an atomic rename over the keytab.</summary>
    private void CommitFile(string temp)
    {
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Posix.ChownRootGroup(temp, KeytabGroup);
        File.Move(temp, _args.Keytab, overwrite: true);
        Posix.SyncDirectory(Path.GetDirectoryName(_args.Keytab) ?? "/");
    }

    /// <summary>Authenticates from the committed keytab at every KDC, polling the non-primary ones until
    /// <c>--replication-timeout</c>. Returns whether all of them accept the new key.</summary>
    private (bool AllReplicated, string? Error) WaitForReplication(KerberosConfig config, AccountPrincipal account)
    {
        var pending = config.Kdcs.Where(k => !string.Equals(k, config.PrimaryKdc, StringComparison.OrdinalIgnoreCase)).ToList();
        var deadline = _host.Time.GetUtcNow() + _args.ReplicationTimeout;
        string? lastError = null;
        while (true)
        {
            foreach (var kdc in pending.ToList())
            {
                using var context = Krb5Context.Open(config.ProfileFor(kdc));
                using var client = context.ParseName(account.Principal);
                var code = context.GetInitialCredentials(client, _args.Keytab, null, IntPtr.Zero, out var creds);
                if (code == 0)
                {
                    context.FreeCredentials(ref creds);
                    pending.Remove(kdc);
                    Info($"{kdc}: the new key is accepted.");
                }
                else
                {
                    lastError = Krb5ErrorNames.Of(code);
                }
            }

            if (pending.Count == 0)
            {
                return (true, null);
            }

            if (_host.Time.GetUtcNow() >= deadline)
            {
                Warn($"Not yet replicated to {string.Join(", ", pending)} ({lastError}); primary_kdc covers it, the next verify clears it.");
                return (false, lastError);
            }

            Thread.Sleep(_host.ReplicationPollInterval);
        }
    }

    /// <summary>Design 4.4 pre-flight of <c>rotate</c>: keytab folder writable, ≥ 1 MiB free. Returns a problem code.</summary>
    private string? Preflight()
    {
        var directory = Path.GetDirectoryName(_args.Keytab) ?? "/";
        var probe = Path.Combine(directory, "." + Path.GetFileName(_args.Keytab) + ".probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        try
        {
            using (new FileStream(probe, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead }))
            {
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error($"The keytab folder {directory} is not writable.");
            return "keytabDirectoryNotWritable";
        }

        try
        {
            if (new DriveInfo(directory).AvailableFreeSpace < MinimumFreeBytes)
            {
                Error($"Less than 1 MiB free in {directory}.");
                return "diskFull";
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private int Failed(AccountPrincipal account, KeytabStatus? previous, string result, string errorCode, string? message)
    {
        if (message is not null)
        {
            Error(message + $" ({errorCode})");
        }

        var now = _host.Time.GetUtcNow();
        var status = (previous ?? new KeytabStatus()) with
        {
            Principal = account.Principal,
            LastAttempt = now,
            Result = result,
            ErrorCode = errorCode,
            NextDue = previous?.NextDue ?? now,
        };
        try
        {
            status.Write(_args.StateDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error($"status.json could not be written ({ex.GetType().Name}).");
        }

        return ExitFailure;
    }

    /// <summary>One instance at a time: <c>flock</c> on <c>&lt;state-dir&gt;/lock</c> (FileShare.None).</summary>
    private FileStream? AcquireLock()
    {
        Directory.CreateDirectory(_args.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        try
        {
            return new FileStream(Path.Combine(_args.StateDirectory, "lock"), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (IOException)
        {
            Error("Another admng-watchdog-keytab instance is running.");
            return null;
        }
    }

    /// <summary>The one-time password: <c>--password-stdin</c> (first line) or the terminal without echo.</summary>
    private SecretBuffer? ReadOneTimePassword(AccountPrincipal account)
    {
        var buffer = new SecretBuffer(256);
        try
        {
            if (_args.PasswordStdin)
            {
                int b;
                while ((b = _host.Input.ReadByte()) >= 0 && b != '\n')
                {
                    if (b != '\r' && !buffer.Append((byte)b))
                    {
                        break;
                    }
                }
            }
            else
            {
                _host.Error.Write($"One-time password of {account.Principal}: ");
                _host.Error.Flush();
                Span<byte> utf8 = stackalloc byte[4];
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        break;
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        buffer.RemoveLast();
                        continue;
                    }

                    var count = Encoding.UTF8.GetBytes([key.KeyChar], utf8);
                    foreach (var b in utf8[..count])
                    {
                        buffer.Append(b);
                    }

                    utf8.Clear();
                }

                _host.Error.WriteLine();
            }

            if (buffer.Length == 0)
            {
                Error("No one-time password was entered.");
                buffer.Dispose();
                return null;
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Format(TimeSpan value) => value.TotalDays >= 1 && value.TotalDays == Math.Floor(value.TotalDays)
        ? value.TotalDays.ToString(CultureInfo.InvariantCulture) + "d"
        : value.ToString("c", CultureInfo.InvariantCulture);

    private void Info(string message) => _host.Out.WriteLine(Prefix(6) + "admng-watchdog-keytab: " + message);

    private void Warn(string message) => _host.Error.WriteLine(Prefix(4) + "admng-watchdog-keytab: " + message);

    private void Error(string message) => _host.Error.WriteLine(Prefix(3) + "admng-watchdog-keytab: " + message);

    private string Prefix(int priority) => _host.Journal ? "<" + priority.ToString(CultureInfo.InvariantCulture) + ">" : string.Empty;
}
