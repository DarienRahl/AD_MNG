using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// The OpenWEC subscription <c>admng-dc-security</c> (stage B design 7.3), generated from <see cref="SecurityEventCatalog"/>
/// so that the subscription and the ingestion set cannot drift: the change-related set in four <c>Select</c>s (user and
/// computer accounts with 4719, security groups, distribution groups, directory service and event-log service records –
/// each within <see cref="SecurityEventQueryBuilder.MaxEventIdsPerSelect"/>), 4624 of logon types 3 and 10, 4625 and 4771,
/// failed 4776, and the suppressed <c>dSCorePropagationData</c> 5136. The uuid is always the configured one
/// (<c>Watchdog:Collector:SubscriptionUuid</c>) and the name always <see cref="Name"/>: a new uuid would lose the DCs'
/// bookmarks.
/// </summary>
public static partial class WecSubscription
{
    /// <summary>The subscription name.</summary>
    public const string Name = "admng-dc-security";

    private const string Log = "Security";

    private static readonly int[] SecurityGroupCodes = [4727, 4728, 4729, 4730, 4731, 4732, 4733, 4734, 4735, 4737, 4754, 4755, 4756, 4757, 4758, 4764];

    private static readonly int[] DistributionGroupCodes = [4744, 4745, 4746, 4747, 4748, 4749, 4750, 4751, 4752, 4753, 4759, 4760, 4761, 4762, 4763];

    /// <summary>The event ids of the change-related <c>Select</c>s, in catalog order.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> ChangeGroups { get; } = BuildGroups();

    /// <summary>The <c>QueryList</c> (also for <c>Get-WinEvent -FilterXml</c> on the DCs before loading).</summary>
    public static string Query()
    {
        var builder = new StringBuilder();
        builder.Append("<QueryList>\n");
        builder.Append(CultureInfo.InvariantCulture, $"  <Query Id=\"0\" Path=\"{Log}\">\n");
        foreach (var group in ChangeGroups)
        {
            Select(builder, $"*[System[({EventIds(group)})]]");
        }

        Select(builder, $"*[System[({EventIds([SecurityEventCatalog.LogonSucceeded])})]] and *[EventData[Data[@Name='LogonType']='3' or Data[@Name='LogonType']='10']]");
        Select(builder, $"*[System[({EventIds([SecurityEventCatalog.LogonFailed, SecurityEventCatalog.KerberosPreAuthFailed])})]]");
        Select(builder, $"*[System[({EventIds([SecurityEventCatalog.CredentialValidation])})]] and *[EventData[Data[@Name='Status']!='0x0']]");
        builder.Append(CultureInfo.InvariantCulture,
            $"    <Suppress Path=\"{Log}\">*[System[({EventIds([SecurityEventCatalog.ObjectModified])})]] and *[EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]</Suppress>\n");
        builder.Append("  </Query>\n");
        builder.Append("</QueryList>\n");
        return builder.ToString();
    }

    /// <summary>The subscription file for <c>openwec subscriptions load</c>.</summary>
    /// <param name="uuid"><c>Watchdog:Collector:SubscriptionUuid</c>.</param>
    /// <param name="targets">The DCs' Kerberos principals, e.g. <c>DC01$@CONTOSO.COM</c> (at least one).</param>
    /// <param name="outputPath">The Files driver path with <c>{client}</c>, e.g. <c>/var/lib/openwec/out/{client}/security.jsonl</c>.</param>
    /// <exception cref="ArgumentException">No target, a target that is not <c>NAME$@REALM</c>, or an unsafe path.</exception>
    public static string Toml(Guid uuid, IReadOnlyList<string> targets, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one DC principal (NAME$@REALM) is required.", nameof(targets));
        }

        foreach (var target in targets)
        {
            if (!IsDcPrincipal(target))
            {
                throw new ArgumentException("A DC principal must look like DC01$@CONTOSO.COM.", nameof(targets));
            }
        }

        if (!SafePath().IsMatch(outputPath) || !outputPath.Contains("{client}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The output path must be absolute, contain {client} and no quotes or backslashes.", nameof(outputPath));
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"uuid = \"{uuid.ToString("D", CultureInfo.InvariantCulture)}\"\n");
        builder.Append(CultureInfo.InvariantCulture, $"name = \"{Name}\"\n");
        builder.Append("query = \"\"\"\n");
        builder.Append(Query());
        builder.Append("\"\"\"\n\n");
        builder.Append("""
            [options]
            enabled = true
            heartbeat_interval = 900
            connection_retry_count = 5
            connection_retry_interval = 60
            max_time = 30
            max_envelope_size = 512000
            read_existing_events = false
            content_format = "Raw"
            ignore_channel_error = true

            [filter]
            operation = "Only"
            type = "Client"
            flags = "CaseInsensitive"

            """.ReplaceLineEndings("\n"));
        builder.Append("targets = [").Append(string.Join(", ", targets.Select(t => "\"" + t + "\""))).Append("]\n\n");
        builder.Append("[[outputs]]\n");
        builder.Append("driver = \"Files\"\n");
        builder.Append("format = \"RawJson\"\n");
        builder.Append(CultureInfo.InvariantCulture, $"config = {{ path = \"{outputPath}\" }}\n");
        return builder.ToString();
    }

    /// <summary>True for a DC's Kerberos principal <c>NAME$@REALM</c> (letters, digits, '.', '-', '_').</summary>
    public static bool IsDcPrincipal(string? value) => value is not null && DcPrincipal().IsMatch(value);

    private static void Select(StringBuilder builder, string expression) =>
        builder.Append(CultureInfo.InvariantCulture, $"    <Select Path=\"{Log}\">{expression}</Select>\n");

    private static string EventIds(IEnumerable<int> codes) =>
        string.Join(" or ", codes.Select(c => string.Create(CultureInfo.InvariantCulture, $"EventID={c}")));

    private static List<IReadOnlyList<int>> BuildGroups()
    {
        var accounts = new List<int>();
        var security = new List<int>();
        var distribution = new List<int>();
        var directory = new List<int>();
        int? auditPolicy = null;
        foreach (var code in SecurityEventCatalog.IngestionSet)
        {
            if (code == SecurityEventCatalog.AuditPolicyChanged)
            {
                auditPolicy = code;
            }
            else if (SecurityGroupCodes.Contains(code))
            {
                security.Add(code);
            }
            else if (DistributionGroupCodes.Contains(code))
            {
                distribution.Add(code);
            }
            else if (SecurityEventCatalog.DirectoryServiceCodes.Contains(code) || SecurityEventCatalog.EventLogServiceCodes.Contains(code))
            {
                directory.Add(code);
            }
            else
            {
                accounts.Add(code);
            }
        }

        if (auditPolicy is { } policy)
        {
            accounts.Add(policy);
        }

        return [.. new[] { accounts, security, distribution, directory }
            .Where(g => g.Count > 0)
            .SelectMany(g => g.Chunk(SecurityEventQueryBuilder.MaxEventIdsPerSelect))
            .Select(c => (IReadOnlyList<int>)c)];
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\$@[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DcPrincipal();

    [GeneratedRegex(@"^/[A-Za-z0-9._/{}@-]{1,1024}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafePath();
}
