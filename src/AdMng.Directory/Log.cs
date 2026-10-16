using System.Text.RegularExpressions;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging;

namespace AdMng.Directory;

/// <summary>Kinds of directory operations, used for logging and error messages.</summary>
internal enum OperationKind
{
    Bind,
    RootDse,
    Search,
    Read,
    RangedRead,
    EffectivePermissions,
    VirtualList,
    Modify,
    Add,
    Delete,
    Move,
    ResetPassword,
    Restore,
    Watch,
    WhoAmI,
}

/// <summary>
/// Log messages of the directory layer. Privacy rules (CLAUDE.md): Information and above never contain DNs,
/// filters or attribute values; DNs only appear at Debug, filter shapes (values masked) at Trace; values and
/// passwords never.
/// </summary>
internal static partial class Log
{
    /// <summary>
    /// The structure of an LDAP filter without its assertion values, e.g.
    /// <c>(&amp;(objectCategory=…)(sAMAccountName=…))</c> - which attributes were searched, not for what.
    /// </summary>
    public static string MaskFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Values never contain raw parentheses (RFC 4515 escapes them as \28 / \29).
        return FilterItem().Replace(filter, "(${attr}${op}…)");
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Connecting to {Server}:{Port} ({Authentication}, {Security})")]
    public static partial void Connecting(ILogger logger, string server, int port, AuthenticationMode authentication, TransportSecurity security);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Connected to {Server}:{Port} in {ElapsedMs} ms (schema: {AttributeCount} attributes, {ClassCount} classes)")]
    public static partial void Connected(ILogger logger, string server, int port, long elapsedMs, int attributeCount, int classCount);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Connection to {Server}:{Port} failed: {Kind} (LDAP {ResultCode})")]
    public static partial void ConnectFailed(ILogger logger, string server, int port, DirectoryErrorKind kind, int? resultCode);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Session pinned to {Server} (connected through {ConnectedHost})")]
    public static partial void Pinned(ILogger logger, string server, string connectedHost);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Domain controller {Server} located for {Domain} (site {Site})")]
    public static partial void DomainControllerLocated(ILogger logger, string server, string domain, string? site);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Domain controller locator failed for {Domain} (Win32 error {Error}); falling back to DNS")]
    public static partial void LocatorFailed(ILogger logger, string domain, int error);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug,
        Message = "Schema loaded in {ElapsedMs} ms: {AttributeCount} attributes, {ClassCount} classes, {LanguageCount} display specifier languages")]
    public static partial void SchemaLoaded(ILogger logger, long elapsedMs, int attributeCount, int classCount, int languageCount);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning,
        Message = "Optional connect step {Step} failed: {Kind} (LDAP {ResultCode}); continuing without it")]
    public static partial void ConnectStepFailed(ILogger logger, string step, DirectoryErrorKind kind, int? resultCode);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug,
        Message = "No display specifiers for locale {Locale}")]
    public static partial void DisplaySpecifiersMissing(ILogger logger, string locale);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning,
        Message = "TLS server certificate validation is disabled for {Server} (lab only)")]
    public static partial void CertificateValidationDisabled(ILogger logger, string server);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning,
        Message = "Domain controller {Server} does not resolve; further connections use {ConnectedHost} and may reach another domain controller")]
    public static partial void PinningFailed(ILogger logger, string server, string connectedHost);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug,
        Message = "Pooled LDAP connection opened to {Server} ({Count}/{Max})")]
    public static partial void PoolConnectionOpened(ILogger logger, string server, int count, int max);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Pooled LDAP connection to {Server} dropped: {Reason}")]
    public static partial void PoolConnectionDropped(ILogger logger, string server, string reason);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning,
        Message = "Could not open an additional LDAP connection to {Server}: {Kind}")]
    public static partial void PoolGrowthFailed(ILogger logger, string server, DirectoryErrorKind kind);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "{Operation} hit a broken connection; retrying once on a new connection")]
    public static partial void RetryingOnNewConnection(ILogger logger, OperationKind operation);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Debug,
        Message = "Idle LDAP connection to {Server} recycled")]
    public static partial void PoolConnectionRecycled(ILogger logger, string server);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information,
        Message = "{Operation} completed in {ElapsedMs} ms")]
    public static partial void OperationCompleted(ILogger logger, OperationKind operation, long elapsedMs);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Debug,
        Message = "{Operation} on {Dn} completed in {ElapsedMs} ms")]
    public static partial void OperationCompletedOn(ILogger logger, OperationKind operation, string dn, long elapsedMs);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
        Message = "Search ({Scope}) returned {Count} entries in {Pages} pages, {ElapsedMs} ms")]
    public static partial void SearchCompleted(ILogger logger, SearchScope scope, int count, int pages, long elapsedMs);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Debug,
        Message = "Search below {BaseDn} ({Scope}) returned {Count} entries in {Pages} pages, {ElapsedMs} ms")]
    public static partial void SearchCompletedBelow(ILogger logger, string baseDn, SearchScope scope, int count, int pages, long elapsedMs);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Trace,
        Message = "Search filter {Filter}")]
    public static partial void SearchFilter(ILogger logger, string filter);

    [GeneratedRegex(@"\((?<attr>[^()=~<>]+?)(?<op>:?[~<>]?=)[^()]*\)")]
    private static partial Regex FilterItem();

    [LoggerMessage(EventId = 1205, Level = LogLevel.Debug,
        Message = "Search page {Page} received with {Count} entries")]
    public static partial void SearchPageReceived(ILogger logger, int page, int count);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning,
        Message = "{Operation} failed: {Kind} (LDAP {ResultCode})")]
    public static partial void OperationFailed(ILogger logger, OperationKind operation, DirectoryErrorKind kind, int? resultCode);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Debug,
        Message = "{Operation} on {Dn} failed: {Kind} (LDAP {ResultCode})")]
    public static partial void OperationFailedOn(ILogger logger, OperationKind operation, string dn, DirectoryErrorKind kind, int? resultCode);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Debug,
        Message = "{Operation} cancelled after {ElapsedMs} ms")]
    public static partial void OperationCancelled(ILogger logger, OperationKind operation, long elapsedMs);

    [LoggerMessage(EventId = 1300, Level = LogLevel.Debug,
        Message = "Change notifications started for {Dn}")]
    public static partial void WatchStarted(ILogger logger, string dn);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Debug,
        Message = "Change notifications stopped")]
    public static partial void WatchStopped(ILogger logger);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning,
        Message = "Change notifications ended: {Kind} (LDAP {ResultCode})")]
    public static partial void WatchFailed(ILogger logger, DirectoryErrorKind kind, int? resultCode);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Warning,
        Message = "Change notification callback threw {ExceptionType}")]
    public static partial void WatchCallbackFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Debug,
        Message = "Change notifications are not supported by the server")]
    public static partial void WatchNotSupported(ILogger logger);
}
