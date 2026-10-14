using System.Net;
using AdMng.Core.Directory;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Directory;

/// <summary>
/// Bootstrap <c>Watchdog:Directory</c> → <see cref="ConnectionOptions"/> (spec 8.4). Production: the service identity
/// (gMSA) with Kerberos sign-and-seal. Lab only: simple bind as <c>LabSimpleBindUser</c> with the password from the
/// environment variable <see cref="PasswordVariable"/> (never from a file); <c>AllowInsecureLab</c> maps to
/// <see cref="ConnectionOptions.AllowInsecureSimpleBind"/> and is refused outside the Development environment.
/// </summary>
public sealed class DirectoryConnectionOptionsFactory(IOptionsMonitor<ServiceOptions> options, IHostEnvironment environment)
{
    /// <summary>The only source of the lab simple-bind password.</summary>
    public const string PasswordVariable = "ADMNG_WATCHDOG_LDAP_PASSWORD";

    /// <summary>The options of the next connection attempt (<paramref name="server"/> overrides the configured DC, e.g.
    /// the PDC emulator) and the credential (lab simple bind only; null = process identity).</summary>
    /// <exception cref="InvalidOperationException">An insecure lab setting outside Development, or a missing password.</exception>
    public (ConnectionOptions Options, NetworkCredential? Credential) Create(string? server = null)
    {
        var directory = options.CurrentValue.Directory;
        var connection = new ConnectionOptions
        {
            Domain = string.IsNullOrWhiteSpace(directory.Domain) ? null : directory.Domain.Trim(),
            Server = server ?? (string.IsNullOrWhiteSpace(directory.Server) ? null : directory.Server.Trim()),
            Port = directory.Port,
            Authentication = directory.Authentication,
            Security = directory.Security,
        };

        if (directory.AllowInsecureLab && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Directory.AllowInsecureLab is refused outside the Development environment.");
        }

        if (directory.Authentication != AuthenticationMode.SimpleBind)
        {
            return (connection, null);
        }

        if (string.IsNullOrWhiteSpace(directory.LabSimpleBindUser))
        {
            throw new InvalidOperationException("Directory.Authentication = SimpleBind requires Directory.LabSimpleBindUser.");
        }

        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException($"The simple-bind password is read only from the environment variable {PasswordVariable}.");
        }

        connection = connection with
        {
            UserName = directory.LabSimpleBindUser.Trim(),
            AllowInsecureSimpleBind = directory.AllowInsecureLab,
        };
        return (connection, new NetworkCredential(directory.LabSimpleBindUser.Trim(), password));
    }
}
