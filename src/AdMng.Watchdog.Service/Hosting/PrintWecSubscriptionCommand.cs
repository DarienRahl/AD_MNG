using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// <c>admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' [--dc …] [--query-only]</c> (stage B design 7.3): prints
/// the OpenWEC subscription <c>admng-dc-security</c> with the configured <c>Watchdog:Collector:SubscriptionUuid</c> (never a
/// fresh one – the DCs' bookmarks are keyed by it) and the output path of <c>Watchdog:Collector</c>, or with
/// <c>--query-only</c> just the <c>QueryList</c> for <c>Get-WinEvent -FilterXml</c> on the DCs. Reads only the
/// <c>Watchdog:Collector</c> section of the bootstrap file (and <c>--Watchdog:Collector:…</c> arguments) and runs no other
/// validation: the instructions run it right after the package install, while <c>appsettings.json</c> still holds the
/// shipped placeholders. Exit 0, or 2 for invalid arguments or settings.
/// </summary>
public static class PrintWecSubscriptionCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The command's arguments (after the verb).</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="bootstrapFile">The bootstrap file (default <see cref="ConfigurationSetup.BootstrapFilePath"/>).</param>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string? bootstrapFile = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var targets = new List<string>();
        var queryOnly = false;
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--dc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                targets.Add(args[++i].Trim());
            }
            else if (arg.StartsWith("--dc=", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(arg[5..].Trim());
            }
            else if (string.Equals(arg, "--query-only", StringComparison.OrdinalIgnoreCase))
            {
                queryOnly = true;
            }
            else
            {
                rest.Add(arg);
            }
        }

        if (queryOnly)
        {
            output.Write(WecSubscription.Query());
            return 0;
        }

        if (targets.Count == 0 || targets.Any(t => !WecSubscription.IsDcPrincipal(t)))
        {
            error.WriteLine("Usage: admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' [--dc 'DC02$@CONTOSO.COM' …] [--query-only]");
            error.WriteLine("Every --dc is the Kerberos principal of a writable DC: NAME$@REALM.");
            return StartupExitCodes.InvalidSettings;
        }

        CollectorBootstrapOptions collector;
        try
        {
            var builder = new ConfigurationBuilder();
            var path = bootstrapFile ?? ConfigurationSetup.BootstrapFilePath;
            if (File.Exists(path))
            {
                builder.AddJsonFile(path, optional: true, reloadOnChange: false);
            }

            builder.AddCommandLine([.. rest.Where(a => a.StartsWith("--" + ServiceOptions.SectionName + ":Collector:", StringComparison.OrdinalIgnoreCase))]);
            collector = builder.Build().GetSection(ServiceOptions.SectionName + ":Collector").Get<CollectorBootstrapOptions>() ?? new CollectorBootstrapOptions();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException)
        {
            error.WriteLine("The section Watchdog:Collector of " + (bootstrapFile ?? ConfigurationSetup.BootstrapFilePath) + " cannot be read ("
                + ex.GetType().Name + ").");
            return StartupExitCodes.InvalidSettings;
        }

        var outputPath = collector.OutputDirectory.TrimEnd('/') + "/{client}/" + collector.FileName;
        try
        {
            output.Write(WecSubscription.Toml(collector.SubscriptionUuid, targets, outputPath));
            return 0;
        }
        catch (ArgumentException)
        {
            error.WriteLine("Watchdog:Collector:OutputDirectory / FileName give no usable output path: " + outputPath);
            return StartupExitCodes.InvalidSettings;
        }
    }
}
