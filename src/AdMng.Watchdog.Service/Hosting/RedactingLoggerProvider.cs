using AdMng.Watchdog.Core.Diagnostics;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Wraps a real logger provider (console, Event Log) so that no exception object ever reaches it (spec 0.1, 19.1):
/// framework components (Kestrel, routing, hosting) log exceptions whose messages may contain directory values,
/// addresses or secrets; the wrapper drops the exception and appends <see cref="SafeExceptionLog.Describe"/> (types,
/// codes, stack traces) to the message instead. The watchdog's own code never passes exceptions to a logger.
/// </summary>
public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;

    public RedactingLoggerProvider(ILoggerProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_inner.CreateLogger(categoryName));

    public void Dispose() => _inner.Dispose();

    /// <summary>Replaces every <see cref="ILoggerProvider"/> registration in <paramref name="services"/> with a wrapped
    /// one (call after the providers were added).</summary>
    public static void WrapRegisteredProviders(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(ILoggerProvider) || descriptor.IsKeyedService)
            {
                continue;
            }

            services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(provider => new RedactingLoggerProvider(Create(provider, descriptor)));
        }
    }

    private static ILoggerProvider Create(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ILoggerProvider instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (ILoggerProvider)factory(provider);
        }

        return (ILoggerProvider)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is null)
            {
                inner.Log(logLevel, eventId, state, null, formatter);
                return;
            }

            var safe = SafeExceptionLog.Describe(exception);
            inner.Log(logLevel, eventId, state, null, (s, _) => formatter(s, null) + Environment.NewLine + safe);
        }
    }
}
