using System.Diagnostics;
using System.Text;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>
/// Minimal client of the Samba lab (tools/testlab) through the OpenLDAP command-line tools – the watchdog test project
/// has no reference to AdMng.Directory. Configured by <c>ADMNG_TEST_LDAP_SERVER</c>, <c>ADMNG_TEST_LDAP_USER</c> and
/// <c>ADMNG_TEST_LDAP_PASSWORD</c>; the password goes to the child process only (never written or logged). Output is
/// parsed in memory (<c>-LLL -o ldif-wrap=no</c>, base64 values decoded).
/// </summary>
internal sealed class LabLdap
{
    public const string DomainDn = "DC=contoso,DC=test";
    public const string ShowDeletedControl = "!1.2.840.113556.1.4.417";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _server;
    private readonly string _user;
    private readonly string _password;

    private LabLdap(string server, string user, string password)
    {
        _server = server;
        _user = user;
        _password = password;
    }

    /// <summary>The lab client, or null when the environment is not configured or the tools are missing.</summary>
    public static LabLdap? FromEnvironment()
    {
        var server = Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_SERVER");
        var user = Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_USER");
        var password = Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_PASSWORD");
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || !File.Exists("/usr/bin/ldapsearch"))
        {
            return null;
        }

        return new LabLdap(server, user, password);
    }

    /// <summary>Runs ldapsearch; each entry maps attribute names (case-insensitive) to raw values.</summary>
    public async Task<IReadOnlyList<(string Dn, Dictionary<string, List<byte[]>> Attributes)>> SearchAsync(
        string baseDn, string scope, string filter, IEnumerable<string> attributes, bool showDeleted, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-LLL", "-o", "ldif-wrap=no", "-b", baseDn, "-s", scope };
        if (showDeleted)
        {
            arguments.AddRange(["-E", ShowDeletedControl]);
        }

        arguments.Add(filter);
        arguments.AddRange(attributes);
        var output = await RunAsync("/usr/bin/ldapsearch", arguments, null, cancellationToken).ConfigureAwait(false);
        return Parse(output);
    }

    /// <summary>The first value of one attribute of a base read as text, or null.</summary>
    public async Task<string?> ReadAsync(string dn, string attribute, CancellationToken cancellationToken)
    {
        var entries = await SearchAsync(dn, "base", "(objectClass=*)", [attribute], showDeleted: false, cancellationToken).ConfigureAwait(false);
        return entries.Count == 1 && entries[0].Attributes.TryGetValue(attribute, out var values) ? Encoding.UTF8.GetString(values[0]) : null;
    }

    /// <summary>Applies an LDIF change record (ldapmodify, LDIF on stdin).</summary>
    public Task ModifyAsync(string ldif, CancellationToken cancellationToken) =>
        RunAsync("/usr/bin/ldapmodify", [], ldif, cancellationToken);

    /// <summary>Deletes one object (ignores "no such object").</summary>
    public async Task DeleteAsync(string dn, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync("/usr/bin/ldapdelete", [dn], null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exit code 32", StringComparison.Ordinal))
        {
            // Already gone.
        }
    }

    private async Task<string> RunAsync(string tool, IEnumerable<string> arguments, string? input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
        };
        start.Environment["LDAPTLS_REQCERT"] = "never";
        foreach (var argument in new[] { "-x", "-H", $"ldap://{_server}", "-D", _user, "-w", _password }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start.");
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return process.ExitCode == 0
            ? text
            : throw new InvalidOperationException($"{Path.GetFileName(tool)} failed with exit code {process.ExitCode}.");
    }

    private static List<(string Dn, Dictionary<string, List<byte[]>> Attributes)> Parse(string ldif)
    {
        var entries = new List<(string, Dictionary<string, List<byte[]>>)>();
        string? dn = null;
        Dictionary<string, List<byte[]>>? attributes = null;
        foreach (var line in ldif.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.Length == 0)
            {
                if (dn is not null)
                {
                    entries.Add((dn, attributes!));
                }

                dn = null;
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            var base64 = separator + 1 < line.Length && line[separator + 1] == ':';
            var raw = line[(separator + (base64 ? 2 : 1))..].TrimStart(' ');
            var value = base64 ? Convert.FromBase64String(raw) : Encoding.UTF8.GetBytes(raw);
            if (name == "dn")
            {
                dn = Encoding.UTF8.GetString(value);
                attributes = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (attributes is not null)
            {
                if (!attributes.TryGetValue(name, out var values))
                {
                    attributes[name] = values = [];
                }

                values.Add(value);
            }
        }

        if (dn is not null)
        {
            entries.Add((dn, attributes!));
        }

        return entries;
    }
}
