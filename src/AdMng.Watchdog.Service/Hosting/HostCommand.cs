namespace AdMng.Watchdog.Service.Hosting;

/// <summary>What the process was started for (spec 19.1).</summary>
public enum HostCommandKind
{
    /// <summary>The service (Windows service or console).</summary>
    Run,

    /// <summary><c>--smoke-test</c>: temporary data folder, loopback, exit code 0/1 (CI).</summary>
    SmokeTest,

    /// <summary><c>--check-config</c>: validate bootstrap and stored configuration, print issues.</summary>
    CheckConfig,

    /// <summary><c>--health</c>: GET <c>/healthz</c> on the configured URL, exit code 0/1.</summary>
    Health,

    /// <summary><c>print-wec-subscription</c>: the OpenWEC subscription of stage B design 7.3 (exit code 0/2).</summary>
    PrintWecSubscription,
}

/// <summary>The parsed command line: the command, <c>--console</c>, and the remaining host arguments
/// (<c>--environment</c>, <c>--contentRoot</c>, <c>--Watchdog:…=…</c>).</summary>
public sealed record HostCommand(HostCommandKind Kind, bool Console, IReadOnlyList<string> HostArgs)
{
    /// <summary>Splits the watchdog switches from the host arguments.</summary>
    public static HostCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var kind = HostCommandKind.Run;
        var console = false;
        var rest = new List<string>(args.Count);
        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--console":
                    console = true;
                    break;
                case "--smoke-test":
                    kind = HostCommandKind.SmokeTest;
                    break;
                case "--check-config":
                    kind = HostCommandKind.CheckConfig;
                    break;
                case "--health":
                    kind = HostCommandKind.Health;
                    break;
                case "print-wec-subscription" when kind == HostCommandKind.Run:
                    kind = HostCommandKind.PrintWecSubscription;
                    break;
                default:
                    rest.Add(arg);
                    break;
            }
        }

        return new HostCommand(kind, console, rest);
    }
}
