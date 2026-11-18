using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.PowerShell;
using AdMng.Presentation.Tests.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The PowerShell window's services in the composition of a connection window (track R, stage R1; docs/dev/multi-window.md 2.2):
/// scoped, built from what the host registered, and a window that comes from the connection's factory.
/// </summary>
public sealed class PowerShellRegistrationTests
{
    [Fact]
    public async Task AHostWithoutTheEngine_StillGetsTheServices_AndTheWindowSaysItHasNone()
    {
        await using var host = new ConnectionScopeHost();
        await using var connection = host.CreateConnection();
        await connection.ConnectAsync();

        var services = connection.Get<PowerShellServices>();
        Assert.Null(services.Sessions);
        Assert.Null(services.Policies);
        Assert.Same(UnavailablePowerShellWindowActions.Instance, services.Actions);

        using var window = connection.Get<IDialogViewModelFactory>().CreatePowerShell();
        await window.InitializeAsync();
        Assert.False(window.IsReady);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Contains("This installation has no PowerShell engine.", window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheHostRegisters_IsWhatTheWindowUses_ThePolicyReaderComesFromThePolicySource()
    {
        var factory = new FakePowerShellSessionFactory();
        var source = new FakePowerShellPolicySource();
        source.Core(PowerShellPolicyScope.Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);
        await using var host = new ConnectionScopeHost(services =>
        {
            services.AddSingleton<IPowerShellSessionFactory>(factory);
            services.AddSingleton<IPowerShellPolicySource>(source);
            services.AddScoped<IPowerShellWindowActions, FakePowerShellWindowActions>();
        });
        await using var connection = host.CreateConnection();
        await connection.ConnectAsync();

        var services = connection.Get<PowerShellServices>();
        Assert.Same(factory, services.Sessions);
        Assert.IsType<PowerShellPolicyReader>(services.Policies);
        Assert.Same(connection.Get<IPowerShellWindowActions>(), services.Actions);

        using var window = connection.Get<IDialogViewModelFactory>().CreatePowerShell();
        await window.InitializeAsync();

        Assert.True(window.IsReady);
        Assert.Single(factory.Requests);
        Assert.Same(services.Actions, factory.Requests[0].Actions);
        Assert.Contains("lockdown", source.Calls);
        Assert.True(window.Policies.Transcription.Enabled);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
    }

    [Fact]
    public async Task AReaderTheHostRegistered_WinsOverTheDefaultOne()
    {
        var reader = new FakePowerShellPolicyReader();
        await using var host = new ConnectionScopeHost(services =>
        {
            services.AddSingleton<IPowerShellPolicyReader>(reader);
            services.AddSingleton<IPowerShellPolicySource>(new FakePowerShellPolicySource());
        });
        await using var connection = host.CreateConnection();

        Assert.Same(reader, connection.Get<PowerShellServices>().Policies);
    }

    [Fact]
    public async Task TwoConnectionWindows_HaveTheirOwnActions_AndTheirOwnServices()
    {
        await using var host = new ConnectionScopeHost(services =>
        {
            services.AddSingleton<IPowerShellSessionFactory>(new FakePowerShellSessionFactory());
            services.AddScoped<IPowerShellWindowActions, FakePowerShellWindowActions>();
        });
        await using var first = host.CreateConnection();
        await using var second = host.CreateConnection();

        Assert.NotSame(first.Get<PowerShellServices>(), second.Get<PowerShellServices>());
        Assert.NotSame(first.Get<PowerShellServices>().Actions, second.Get<PowerShellServices>().Actions);
        Assert.Same(first.Get<PowerShellServices>().Actions, first.Get<PowerShellServices>().Actions);
    }

    [Fact]
    public async Task AFactoryBuiltWithoutTheServices_RefusesToCreateTheWindow()
    {
        var harness = await PowerShellHarness.CreateAsync();

        Assert.Throws<InvalidOperationException>(() => harness.Directory.Factory.CreatePowerShell());
    }

    [Fact]
    public async Task EachWindowOfAConnection_HasItsOwnRunspace_TheEnginesSessionIsNeverShared()
    {
        var factories = new FakePowerShellSessionFactory();
        await using var host = new ConnectionScopeHost(services => services.AddScoped<IPowerShellSessionFactory>(_ => factories));
        await using var connection = host.CreateConnection();
        await connection.ConnectAsync();
        var creator = connection.Get<IDialogViewModelFactory>();

        using var one = creator.CreatePowerShell();
        using var two = creator.CreatePowerShell();
        await one.InitializeAsync();
        await two.InitializeAsync();

        // The factory is asked once per window; a factory that hands out one session per call is the host's duty (the engine's).
        Assert.Equal(2, factories.Requests.Count);
        Assert.NotSame(one, two);
        Assert.NotSame(one.Output, two.Output);
        Assert.NotSame(one.History, two.History);
    }
}
