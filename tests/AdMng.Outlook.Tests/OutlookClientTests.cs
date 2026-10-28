using System.Runtime.InteropServices;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook.Tests;

/// <summary>Connecting (design note 4.1): attach or start, 3 attempts 700 ms apart, the profile, the platform guard.</summary>
public class OutlookClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Availability_IsNotSupportedOffWindows_AndAProgIdLookupOnWindows()
    {
        var client = new OutlookClient();

        // On Windows only the registry is read (the ProgID): Outlook is neither started nor attached to.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(client.Availability, new[] { OutlookAvailability.Installed, OutlookAvailability.NotInstalled });
        }
        else
        {
            Assert.Equal(OutlookAvailability.NotSupported, client.Availability);
        }
    }

    [Fact]
    public async Task ConnectAsync_OffWindows_IsNotSupported_WithoutAnyComCall()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Off Windows only (on Windows the real Outlook would be contacted).");
        var client = new OutlookClient();

        var result = await client.ConnectAsync(Ct);

        Assert.Null(result.Session);
        var failure = Assert.IsType<OutlookFailure>(result.Failure);
        Assert.Equal(OutlookFailureKind.NotSupported, failure.Kind);
        Assert.Equal("outlook.notSupported", failure.Code);
        Assert.Equal("connect", failure.Step);
        Assert.True(failure.StopsRun);
    }

    [Fact]
    public async Task ConnectAsync_AttachesToTheRunningOutlook_OnTheWorker_AndReadsTheProfile()
    {
        await using var harness = new OutlookHarness();

        var session = await harness.ConnectAsync();

        Assert.Equal(1, harness.Provider.AttachCalls);
        Assert.Equal(0, harness.Provider.StartCalls);
        Assert.All(harness.Provider.Threads, thread => Assert.Equal(session.Worker.ThreadId, thread));
        Assert.All(harness.Log.Threads, thread => Assert.Equal(session.Worker.ThreadId, thread));
        Assert.True(session.IsConnected);
        Assert.Same(harness.Application, session.Application);
        var profile = session.Profile;
        Assert.Equal("16.0.0.17928", profile.Version);
        Assert.Equal("Outlook", profile.ProfileName);
        Assert.Equal(OutlookHarness.Own, profile.CurrentUserAddress);
        Assert.Equal(
            [
                new OutlookAccount("Jan Kowalski", OutlookHarness.Own),
                new OutlookAccount("Sekretariat – Łódź", OutlookHarness.Secretariat),
                new OutlookAccount("Archiwum Żółć", OutlookHarness.Archive),
            ],
            profile.Accounts);
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task ConnectAsync_StartsOutlook_WhenNoneRuns()
    {
        await using var harness = new OutlookHarness();
        harness.Provider.Running = false;

        var session = await harness.ConnectAsync();

        Assert.Equal(1, harness.Provider.AttachCalls);
        Assert.Equal(1, harness.Provider.StartCalls);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_RetriesThreeTimes_700msApart_ThenSucceeds()
    {
        await using var harness = new OutlookHarness();
        var busy = new COMException("busy", unchecked((int)0x8001010A));
        harness.Provider.FailAttach(busy, busy);
        harness.Provider.FailStart(busy, busy);

        var result = await CompleteAsync(harness, harness.Client.ConnectAsync(Ct));

        Assert.NotNull(result.Session);
        Assert.Equal(3, harness.Provider.AttachCalls);
        Assert.Equal(2, harness.Provider.StartCalls);
        AssertApart(harness.Provider.AttachTimes);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_GivesUpAfterThreeAttempts_WithTheLastFailureClassified()
    {
        await using var harness = new OutlookHarness();
        harness.Provider.Running = false;
        harness.Provider.FailStart(
            new COMException("a", unchecked((int)0x8001010A)),
            new COMException("b", unchecked((int)0x8001010A)),
            new COMException("c", unchecked((int)0x800706BA)));

        var result = await CompleteAsync(harness, harness.Client.ConnectAsync(Ct));

        Assert.Null(result.Session);
        AssertApart(harness.Provider.AttachTimes);
        Assert.Equal(3, harness.Provider.AttachTimes.Count);
        Assert.Equal(OutlookFailureKind.Disconnected, result.Failure!.Kind);
        Assert.Equal("connect", result.Failure.Step);
        Assert.Equal(unchecked((int)0x800706BA), result.Failure.HResult);
        Assert.Equal(3, harness.Provider.StartCalls);
    }

    [Fact]
    public async Task ConnectAsync_NotInstalled_StopsAtOnce()
    {
        await using var harness = new OutlookHarness();
        harness.Provider.Running = false;
        harness.Provider.FailStart(new COMException("class not registered", unchecked((int)0x80040154)));

        var result = await harness.Client.ConnectAsync(Ct);

        Assert.Equal(OutlookFailureKind.NotInstalled, result.Failure!.Kind);
        Assert.Equal(1, harness.Provider.StartCalls);
    }

    [Fact]
    public async Task ConnectAsync_ElevatedProcessAgainstANormalOutlook_IsElevated_AndNotRetried()
    {
        await using var harness = new OutlookHarness(elevated: true);
        harness.Provider.Running = false;
        harness.Provider.FailStart(new COMException("server exec failure", unchecked((int)0x80080005)));

        var result = await harness.Client.ConnectAsync(Ct);

        Assert.Equal(OutlookFailureKind.Elevated, result.Failure!.Kind);
        Assert.Equal("outlook.elevated", result.Failure.Code);
        Assert.Equal(1, harness.Provider.StartCalls);
    }

    [Fact]
    public async Task ConnectAsync_ProfileUnreadable_ReleasesTheApplication_AndRetries()
    {
        await using var harness = new OutlookHarness();
        harness.Log.FailWith("NameSpace.Accounts", 0x8001010A, times: 1);

        var result = await CompleteAsync(harness, harness.Client.ConnectAsync(Ct));

        Assert.NotNull(result.Session);
        Assert.Equal(2, harness.Provider.AttachCalls);
        Assert.Contains(harness.Application, harness.Log.Released);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_ProfileWithoutAccounts_UsesTheCurrentUsersMailbox()
    {
        await using var harness = new OutlookHarness();
        harness.Application.NameSpace.AccountList.Items.Clear();

        var session = await harness.ConnectAsync();

        Assert.Equal([new OutlookAccount(OutlookHarness.Own, OutlookHarness.Own)], session.Profile.Accounts);
    }

    [Fact]
    public async Task ConnectAsync_UnreadableAccountsAndUser_AreSkipped_NotFatal()
    {
        await using var harness = new OutlookHarness();
        harness.Application.NameSpace.User = null;
        harness.Application.NameSpace.AccountList.Items.Clear();
        harness.Application.NameSpace.AccountList.Add("Bez adresu", null);

        var session = await harness.ConnectAsync();

        Assert.Null(session.Profile.CurrentUserAddress);
        Assert.Equal([new OutlookAccount("Bez adresu", string.Empty)], session.Profile.Accounts);
    }

    [Fact]
    public async Task ConnectAsync_Cancelled_Throws()
    {
        await using var harness = new OutlookHarness();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.ConnectAsync(cancellation.Token));
        Assert.Equal(0, harness.Provider.AttachCalls);
    }

    [Fact]
    public async Task ConnectAsync_CancelledDuringTheDelay_Throws_AndEndsTheWorker()
    {
        await using var harness = new OutlookHarness();
        harness.Provider.Running = false;
        harness.Provider.FailStart(new COMException("busy", unchecked((int)0x8001010A)));
        using var cancellation = new CancellationTokenSource();

        var connecting = harness.Client.ConnectAsync(cancellation.Token);
        while (harness.Provider.StartCalls == 0)
        {
            await Task.Delay(5, Ct);
        }

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(1, harness.Provider.StartCalls);
    }

    [Fact]
    public async Task ConnectAsync_RemovesStaleTempFoldersOfDeadProcesses_Once()
    {
        await using var harness = new OutlookHarness();
        var stale = Path.Combine(harness.TempRoot, "AdMng-Outlook-999999-0123456789abcdef");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "logo.png"), [1, 2, 3]);

        await harness.ConnectAsync();

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void ToString_NamesTheType() => Assert.Equal(nameof(OutlookClient), new OutlookClient().ToString());

    /// <summary>Advances the manual clock in 50 ms steps while the client waits between attempts, until it is done.</summary>
    private static async Task<OutlookConnectResult> CompleteAsync(OutlookHarness harness, Task<OutlookConnectResult> connecting)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!connecting.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, Ct);
            harness.Time.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(connecting.IsCompleted, "The connection did not finish.");
        return await connecting;
    }

    private static void AssertApart(IReadOnlyList<DateTimeOffset> times)
    {
        for (var index = 1; index < times.Count; index++)
        {
            Assert.True(times[index] - times[index - 1] >= OutlookClient.AttemptDelay, $"attempt {index + 1} came too early");
        }
    }
}
