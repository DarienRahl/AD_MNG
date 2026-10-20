using System.Globalization;

namespace AdMng.Watchdog.Keytab;

/// <summary>Command line of <c>admng-watchdog-keytab</c> (design 4.4).</summary>
internal sealed record KeytabArguments
{
    public const string DefaultKeytab = "/etc/admng-watchdog/admng-wd.keytab";
    public const string DefaultStateDirectory = "/var/lib/admng-watchdog-keytab";

    public const string Usage = """
        Usage: admng-watchdog-keytab COMMAND [options]

          init     [--principal 'ADMNG-WD$@REALM'] [--fqdn admng-wd.example.com] [--password-stdin]
                   set a new random password with the one-time password (prompted without echo) and write the keytab
          rotate   [--if-older-than 30d]   change the password and rotate the keytab when it is older (timer)
          verify   check the keytab: entry order, authentication against every KDC, kvno of a fresh ticket
          status   print status.json

        Options: --keytab PATH (/etc/admng-watchdog/admng-wd.keytab), --krb5-config PATH ($KRB5_CONFIG or
        /etc/admng-watchdog/krb5.conf), --state-dir DIR (/var/lib/admng-watchdog-keytab), --replication-timeout 15m.
        Exit codes: 0 ok, 1 usage, 2 failure. Passwords are never written, printed or logged.
        """;

    public required string Command { get; init; }

    public string? Principal { get; init; }

    public string? Fqdn { get; init; }

    public string Keytab { get; init; } = DefaultKeytab;

    public required string Krb5Config { get; init; }

    public string StateDirectory { get; init; } = DefaultStateDirectory;

    public bool PasswordStdin { get; init; }

    public TimeSpan ReplicationTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan IfOlderThan { get; init; } = TimeSpan.FromDays(30);

    /// <exception cref="ArgumentException">Unknown command or option, missing value.</exception>
    public static KeytabArguments Parse(IReadOnlyList<string> args, string? krb5ConfigVariable)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("A command is required.");
        }

        var command = args[0];
        if (command is "-h" or "--help" or "help")
        {
            return new KeytabArguments { Command = "help", Krb5Config = KerberosConfig.DefaultPath };
        }

        if (command is not ("init" or "rotate" or "verify" or "status"))
        {
            throw new ArgumentException($"Unknown command '{command}'.");
        }

        var result = new KeytabArguments
        {
            Command = command,
            Krb5Config = string.IsNullOrWhiteSpace(krb5ConfigVariable) ? KerberosConfig.DefaultPath : krb5ConfigVariable.Trim(),
        };
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{option} needs a value.");
            result = option switch
            {
                "--principal" when command is "init" or "verify" => result with { Principal = Value() },
                "--fqdn" when command is "init" => result with { Fqdn = Value() },
                "--password-stdin" when command is "init" => result with { PasswordStdin = true },
                "--if-older-than" when command is "rotate" => result with { IfOlderThan = ParseDuration(Value()) },
                "--replication-timeout" when command is "init" or "rotate" => result with { ReplicationTimeout = ParseDuration(Value()) },
                "--keytab" => result with { Keytab = Path.GetFullPath(Value()) },
                "--krb5-config" => result with { Krb5Config = Value() },
                "--state-dir" => result with { StateDirectory = Path.GetFullPath(Value()) },
                _ => throw new ArgumentException($"Unknown option '{option}' for {command}."),
            };
        }

        return result;
    }

    /// <summary><c>30d</c>, <c>12h</c>, <c>15m</c>, <c>90s</c> or a bare number of seconds.</summary>
    /// <exception cref="ArgumentException">Not a non-negative duration.</exception>
    public static TimeSpan ParseDuration(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        var unit = text.Length > 0 && char.IsLetter(text[^1]) ? char.ToLowerInvariant(text[^1]) : 's';
        var number = char.IsLetter(text.Length > 0 ? text[^1] : '0') ? text[..^1] : text;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount < 0 || double.IsInfinity(amount))
        {
            throw new ArgumentException($"'{value}' is not a duration (e.g. 30d, 12h, 15m).");
        }

        return unit switch
        {
            'd' => TimeSpan.FromDays(amount),
            'h' => TimeSpan.FromHours(amount),
            'm' => TimeSpan.FromMinutes(amount),
            's' => TimeSpan.FromSeconds(amount),
            _ => throw new ArgumentException($"'{value}' is not a duration (units d, h, m, s)."),
        };
    }
}
