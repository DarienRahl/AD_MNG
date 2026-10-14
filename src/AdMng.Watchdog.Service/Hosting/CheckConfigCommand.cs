using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Storage;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// <c>--check-config</c> (spec 19.1): validates the bootstrap settings (<see cref="ServiceOptionsValidator"/>) and, when
/// the database exists, the stored runtime configuration (validator + secret bindings), prints one line per issue and
/// exits 0 (no errors) or 2. No migration runs and no listener starts (the data folder is created when missing).
/// </summary>
public static class CheckConfigCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var app = WatchdogHost.Build(args);
        var issues = new List<ConfigIssue>();
        var options = app.Services.GetRequiredService<IOptions<ServiceOptions>>().Value;
        issues.AddRange(ServiceOptionsValidator.Validate(options, app.Environment.EnvironmentName));

        var database = app.Services.GetRequiredService<SqliteDatabase>();
        if (File.Exists(database.DatabasePath))
        {
            try
            {
                var stored = await app.Services.GetRequiredService<IConfigStore>().GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (stored is not null)
                {
                    var outcome = await app.Services.GetRequiredService<ConfigValidationService>()
                        .ValidateAsync(stored.Configuration, previous: null, CancellationToken.None).ConfigureAwait(false);
                    issues.AddRange(outcome.Result.Issues);
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
            {
                issues.Add(new ConfigIssue
                {
                    Path = "database",
                    Code = "unreadable",
                    Severity = IssueSeverity.Error,
                    Message = "The stored configuration cannot be read (" + SafeExceptionLog.ErrorCode(ex) + ").",
                });
            }
        }

        foreach (var issue in issues)
        {
            await Console.Out.WriteLineAsync($"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Path} {issue.Code} – {issue.Message}").ConfigureAwait(false);
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        await Console.Out.WriteLineAsync(errors == 0 ? "Configuration OK." : $"{errors} error(s).").ConfigureAwait(false);
        return errors == 0 ? 0 : StartupExitCodes.InvalidSettings;
    }
}
