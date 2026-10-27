using System.Runtime.Versioning;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Directory;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace AdMng.Remote;

/// <summary>
/// <see cref="IRemoteComputerClient"/> over CIM / WS-Management (Microsoft.Management.Infrastructure, K1 design note 4.7):
/// one CIM session per action, Kerberos only (no Negotiate/NTLM fallback, so an admin credential is never handed to a
/// host by NTLM), the window's Windows account or a read-only copy of its in-memory password that is disposed with the
/// session. Blocking MI calls run on the thread pool with the operation timeout and the caller's token. Nothing is logged
/// or kept (CLAUDE.md rules 1 and 2). Windows only: elsewhere every call fails with <see cref="RemoteErrorKind.NotSupported"/>.
/// </summary>
public sealed class CimRemoteComputerClient(TimeProvider? time = null) : IRemoteComputerClient
{
    /// <summary>How long a gpupdate is watched before "still running" is reported.</summary>
    public static readonly TimeSpan PolicyRefreshWait = TimeSpan.FromMinutes(10);

    /// <summary>The interval of the gpupdate process checks.</summary>
    public static readonly TimeSpan PolicyPollInterval = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IRemoteComputerSession> ConnectAsync(RemoteTarget target, IDirectorySession session, RemoteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        var logon = SessionLogons.Of(session);
        return ConnectOnWindowsAsync(target.DnsHostName, logon, options, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task<IRemoteComputerSession> ConnectOnWindowsAsync(string host, SessionLogon logon, RemoteOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var cim = await Task.Run(() => CimCalls.Open(host, logon, options), cancellationToken).ConfigureAwait(false);
            return new CimRemoteSession(cim, logon, options, _time);
        }
        catch
        {
            logon.Dispose();
            throw;
        }
    }
}

/// <summary>One CIM session (one remote action).</summary>
[SupportedOSPlatform("windows")]
internal sealed class CimRemoteSession(CimSession cim, SessionLogon logon, RemoteOptions options, TimeProvider time) : IRemoteComputerSession
{
    private int _disposed;

    public Task<RemoteOperatingSystem> GetOperatingSystemAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken, operation =>
    {
        var os = CimCalls.QuerySingle(cim, RemoteCommands.CimNamespace, "SELECT Caption, Version, BuildNumber, LastBootUpTime FROM Win32_OperatingSystem", operation);
        var ubr = CimCalls.ReadDword(cim, RemoteCommands.CurrentVersionKey, "UBR", operation);
        return CimMapping.OperatingSystem(os, ubr);
    });

    public Task<IReadOnlyList<LoggedOnUser>> GetLoggedOnUsersAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken, operation =>
    {
        var console = CimCalls.QuerySingle(cim, RemoteCommands.CimNamespace, "SELECT UserName FROM Win32_ComputerSystem", operation);
        var explorers = new List<(IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>?)>();
        foreach (var process in cim.QueryInstances(RemoteCommands.CimNamespace, "WQL", "SELECT Handle, SessionId, CreationDate FROM Win32_Process WHERE Name = 'explorer.exe'", operation))
        {
            using (process)
            {
                IReadOnlyDictionary<string, object?>? owner = null;
                try
                {
                    using var parameters = new CimMethodParametersCollection();
                    using var result = cim.InvokeMethod(RemoteCommands.CimNamespace, process, "GetOwner", parameters, operation);
                    owner = CimCalls.OutParameters(result);
                }
                catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.NotFound)
                {
                    // The process ended meanwhile.
                }

                explorers.Add((CimCalls.Properties(process), owner));
            }
        }

        return CimMapping.LoggedOnUsers(explorers, console.TryGetValue("UserName", out var user) ? user as string : null);
    });

    public Task<IReadOnlyList<RemoteService>> GetServicesAsync(CancellationToken cancellationToken) => RunAsync<IReadOnlyList<RemoteService>>(cancellationToken, operation =>
    {
        IEnumerable<IReadOnlyDictionary<string, object?>> services;
        try
        {
            services = CimCalls.Query(cim, RemoteCommands.CimNamespace, "SELECT Name, DisplayName, State, StartMode, DelayedAutoStart, StartName FROM Win32_Service", operation);
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.InvalidQuery or NativeErrorCode.NoSuchProperty)
        {
            // Before Windows 10 / Server 2016 Win32_Service has no DelayedAutoStart.
            services = CimCalls.Query(cim, RemoteCommands.CimNamespace, "SELECT Name, DisplayName, State, StartMode, StartName FROM Win32_Service", operation);
        }

        return [.. services.Select(CimMapping.Service)];
    });

    public Task<GroupPolicyRefreshResult> RefreshGroupPolicyAsync(GroupPolicyRefreshTarget target, CancellationToken cancellationToken) => RunAsync(cancellationToken, operation =>
    {
        var processId = CimCalls.CreateProcess(cim, RemoteCommands.GpUpdateComputer, operation);
        var deadline = time.GetUtcNow() + CimRemoteComputerClient.PolicyRefreshWait;
        var completed = false;
        while (!completed && time.GetUtcNow() < deadline)
        {
            cancellationToken.WaitHandle.WaitOne(CimRemoteComputerClient.PolicyPollInterval);
            cancellationToken.ThrowIfCancellationRequested();
            completed = !CimCalls.Query(cim, RemoteCommands.CimNamespace, $"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {processId}", operation).Any();
        }

        var high = CimCalls.ReadDword(cim, RemoteCommands.MachinePolicyStateKey, "EndTimeHi", operation);
        var low = CimCalls.ReadDword(cim, RemoteCommands.MachinePolicyStateKey, "EndTimeLo", operation);
        var status = CimMapping.ToInt(CimCalls.ReadDword(cim, RemoteCommands.MachinePolicyStateKey, "Status", operation));
        var users = target == GroupPolicyRefreshTarget.ComputerAndUsers && options.RefreshUserPolicy && ScheduleUserRefresh(operation);
        return new GroupPolicyRefreshResult(completed, CimMapping.GroupPolicyEnd(high, low), status, users);
    });

    public Task ScheduleRestartAsync(TimeSpan delay, string message, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var timeout = RemoteCommands.RestartTimeout(delay);
        return RunAsync<object?>(cancellationToken, operation =>
        {
            foreach (var os in cim.QueryInstances(RemoteCommands.CimNamespace, "WQL", "SELECT * FROM Win32_OperatingSystem", operation))
            {
                using (os)
                {
                    using var parameters = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("Timeout", timeout, CimType.UInt32, CimFlags.In),
                        CimMethodParameter.Create("Comment", message, CimType.String, CimFlags.In),
                        CimMethodParameter.Create("ReasonCode", RemoteCommands.PlannedOtherReason, CimType.UInt32, CimFlags.In),
                        CimMethodParameter.Create("Flags", RemoteCommands.RestartFlags(force), CimType.SInt32, CimFlags.In),
                    };
                    using var result = cim.InvokeMethod(RemoteCommands.CimNamespace, os, "Win32ShutdownTracker", parameters, operation);
                    CimCalls.EnsureSucceeded(result);
                    return null;
                }
            }

            throw new RemoteActionException(RemoteErrorKind.Failed);
        });
    }

    public Task AbortRestartAsync(CancellationToken cancellationToken) =>
        RunAsync<object?>(cancellationToken, operation =>
        {
            CimCalls.CreateProcess(cim, RemoteCommands.AbortShutdown, operation);
            return null;
        });

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            cim.Dispose();
            logon.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The users' refresh: a one-time task for INTERACTIVE through the Task Scheduler CIM provider.</summary>
    private bool ScheduleUserRefresh(CimOperationOptions operation)
    {
        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("TaskName", RemoteCommands.UserPolicyTaskName(Guid.NewGuid()), CimType.String, CimFlags.In),
            CimMethodParameter.Create("TaskPath", "\\", CimType.String, CimFlags.In),
            CimMethodParameter.Create("Xml", RemoteCommands.UserPolicyTaskXml(time.GetUtcNow()), CimType.String, CimFlags.In),
            CimMethodParameter.Create("Force", false, CimType.Boolean, CimFlags.In),
        };
        using var result = cim.InvokeMethod(RemoteCommands.TaskSchedulerNamespace, "PS_ScheduledTask", "RegisterByXml", parameters, operation);
        return result.ReturnValue?.Value is null or 0u or 0;
    }

    private Task<T> RunAsync<T>(CancellationToken cancellationToken, Func<CimOperationOptions, T> call)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(
            () =>
            {
                using var operation = new CimOperationOptions { Timeout = options.Timeout, CancellationToken = cancellationToken };
                try
                {
                    return call(operation);
                }
                catch (CimException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw CimCalls.Translate(ex);
                }
                catch (CimException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            },
            cancellationToken);
    }
}

/// <summary>The MI calls shared by the client and the session.</summary>
[SupportedOSPlatform("windows")]
internal static class CimCalls
{
    /// <summary>Opens a WS-Management CIM session with Kerberos and checks that WinRM answers.</summary>
    public static CimSession Open(string host, SessionLogon logon, RemoteOptions options)
    {
        using var sessionOptions = new WSManSessionOptions
        {
            DestinationPort = (uint)options.EffectivePort,
            UseSsl = options.UseHttps,
            Timeout = options.Timeout,
        };
        var credential = logon.IsWindowsAccount
            ? new CimCredential(ImpersonatedAuthenticationMechanism.Kerberos)
            : new CimCredential(PasswordAuthenticationMechanism.Kerberos, logon.Domain, logon.UserName, logon.Password);
        sessionOptions.AddDestinationCredentials(credential);
        CimSession? session = null;
        try
        {
            session = CimSession.Create(host, sessionOptions);
            if (!session.TestConnection(out var identity, out var error))
            {
                using (error)
                {
                    throw error is null ? new RemoteActionException(RemoteErrorKind.WinRmNotAvailable) : Translate(error);
                }
            }

            identity?.Dispose();
            return session;
        }
        catch (CimException ex)
        {
            session?.Dispose();
            throw Translate(ex);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    public static RemoteActionException Translate(CimException exception)
    {
        uint? code = null;
        if (exception.ErrorData is { } data && data.CimInstanceProperties["error_Code"]?.Value is { } value)
        {
            code = value switch
            {
                uint u => u,
                int i => unchecked((uint)i),
                long l => unchecked((uint)l),
                _ => null,
            };
        }

        var kind = RemoteErrors.Classify((int)exception.NativeErrorCode, code ?? (exception.StatusCode == 0 ? null : exception.StatusCode));
        return new RemoteActionException(kind, exception) { ErrorCode = code is { } c ? unchecked((int)c) : (int)exception.NativeErrorCode };
    }

    public static IReadOnlyDictionary<string, object?> Properties(CimInstance instance)
    {
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in instance.CimInstanceProperties)
        {
            bag[property.Name] = property.Value;
        }

        return bag;
    }

    public static IReadOnlyDictionary<string, object?> OutParameters(CimMethodResult result)
    {
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in result.OutParameters)
        {
            bag[parameter.Name] = parameter.Value;
        }

        if (result.ReturnValue is { } returnValue)
        {
            bag["ReturnValue"] = returnValue.Value;
        }

        return bag;
    }

    /// <summary>The instances of a WQL query as property bags (each instance disposed).</summary>
    public static List<IReadOnlyDictionary<string, object?>> Query(CimSession session, string ns, string query, CimOperationOptions operation)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var instance in session.QueryInstances(ns, "WQL", query, operation))
        {
            using (instance)
            {
                rows.Add(Properties(instance));
            }
        }

        return rows;
    }

    public static IReadOnlyDictionary<string, object?> QuerySingle(CimSession session, string ns, string query, CimOperationOptions operation) =>
        Query(session, ns, query, operation).FirstOrDefault() ?? throw new RemoteActionException(RemoteErrorKind.NotSupported);

    /// <summary>A REG_DWORD of HKLM through StdRegProv; null when the value does not exist.</summary>
    public static object? ReadDword(CimSession session, string key, string name, CimOperationOptions operation)
    {
        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", RemoteCommands.LocalMachine, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", key, CimType.String, CimFlags.In),
            CimMethodParameter.Create("sValueName", name, CimType.String, CimFlags.In),
        };
        using var result = session.InvokeMethod(RemoteCommands.CimNamespace, "StdRegProv", "GetDWORDValue", parameters, operation);
        var output = OutParameters(result);
        return CimMapping.ToInt(output.GetValueOrDefault("ReturnValue")) is 0 or null ? output.GetValueOrDefault("uValue") : null;
    }

    /// <summary>Win32_Process.Create of a constant command line; returns the process id.</summary>
    public static uint CreateProcess(CimSession session, string commandLine, CimOperationOptions operation)
    {
        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("CommandLine", commandLine, CimType.String, CimFlags.In),
        };
        using var result = session.InvokeMethod(RemoteCommands.CimNamespace, "Win32_Process", "Create", parameters, operation);
        EnsureSucceeded(result);
        return OutParameters(result).GetValueOrDefault("ProcessId") is uint id ? id : 0;
    }

    public static void EnsureSucceeded(CimMethodResult result)
    {
        var value = result.ReturnValue?.Value;
        var code = value switch
        {
            uint u => u,
            int i => unchecked((uint)i),
            _ => 0u,
        };
        if (code != 0)
        {
            throw RemoteErrors.FromReturnValue(code);
        }
    }
}
