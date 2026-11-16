using System.Security;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests;

/// <summary>D2: the custom host routes every stream, every prompt and every confirmation into the window.</summary>
public sealed class HostRoutingTests
{
    [Fact]
    public async Task Each_stream_arrives_as_text_of_its_own_stream_without_a_prefix()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(
            session,
            "Write-Warning 'w1'; Write-Verbose 'v1' -Verbose; Write-Debug 'd1' -Debug:$false; Write-Error 'e1'; Write-Output 'o1'; Write-Information 'i1' -InformationAction Continue");

        Assert.Equal("w1\n", TestSessions.Text(items, PowerShellStream.Warning));
        Assert.Equal("v1\n", TestSessions.Text(items, PowerShellStream.Verbose));
        Assert.Contains("e1", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal("o1\ni1\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task Write_Host_keeps_its_colours_and_is_not_duplicated()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Write-Host 'red' -ForegroundColor Red");

        var text = Assert.Single(items.OfType<PowerShellTextItem>());
        Assert.Equal("red\n", text.Text);
        Assert.Equal(ConsoleColor.Red, text.Foreground);
        Assert.Equal(PowerShellStream.Output, text.Stream);
    }

    [Fact]
    public async Task Write_Progress_arrives_as_a_progress_item()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Write-Progress -Activity 'Reading' -Status 'half' -PercentComplete 50");

        var progress = Assert.Single(items.OfType<PowerShellProgressItem>());
        Assert.Equal("Reading", progress.Activity);
        Assert.Equal("half", progress.Status);
        Assert.Equal(50, progress.PercentComplete);
        Assert.False(progress.Completed);
    }

    [Fact]
    public async Task Read_Host_asks_the_window_and_the_pipeline_goes_on_with_the_answer()
    {
        await using var session = await TestSessions.CreateAsync();
        PowerShellPromptItem? asked = null;

        var items = await TestSessions.RunAsync(
            session,
            "$name = Read-Host 'Name'; \"hello $name\"",
            prompt =>
            {
                asked = prompt;
                prompt.Respond("Łukasz");
                return Task.CompletedTask;
            });

        Assert.Equal(PowerShellPromptKind.Text, asked!.Kind);
        Assert.Contains("Name", asked.Label ?? asked.Message ?? asked.Caption, StringComparison.Ordinal);
        Assert.Equal("hello Łukasz\n", TestSessions.Text(items).Replace("Name: ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_secret_prompt_is_answered_with_a_secure_string_and_the_item_does_not_show_it()
    {
        await using var session = await TestSessions.CreateAsync();
        PowerShellPromptItem? asked = null;
        using var secret = new SecureString();
        foreach (var c in "s3cret")
        {
            secret.AppendChar(c);
        }

        var items = await TestSessions.RunAsync(
            session,
            "$s = Read-Host 'Secret' -AsSecureString; $s.GetType().Name; $s.Length",
            prompt =>
            {
                asked = prompt;
                prompt.RespondSecret(secret.Copy());
                return Task.CompletedTask;
            });

        Assert.Equal(PowerShellPromptKind.Secret, asked!.Kind);
        Assert.DoesNotContain("s3cret", asked.ToString(), StringComparison.Ordinal);
        Assert.Equal("SecureString\n6\n", TestSessions.Text(items).Replace("Secret: ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_mandatory_parameter_is_asked_for_in_the_window()
    {
        await using var session = await TestSessions.CreateAsync();
        PowerShellPromptItem? asked = null;

        var items = await TestSessions.RunAsync(
            session,
            "function Get-Twice { param([Parameter(Mandatory)][int]$Number) $Number * 2 }; Get-Twice",
            prompt =>
            {
                asked = prompt;
                prompt.Respond("21");
                return Task.CompletedTask;
            });

        Assert.Equal(PowerShellPromptKind.Text, asked!.Kind);
        Assert.Contains("Number", asked.Label, StringComparison.Ordinal);
        Assert.Contains("42\n", TestSessions.Text(items), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "1\n")]
    [InlineData(2, "")]
    public async Task A_confirmation_is_a_choice_prompt_and_the_answer_decides_whether_the_action_runs(int choice, string expected)
    {
        await using var session = await TestSessions.CreateAsync();
        PowerShellPromptItem? asked = null;

        var items = await TestSessions.RunAsync(
            session,
            "Set-Variable -Name tmp -Value 1 -Confirm; $tmp",
            prompt =>
            {
                asked = prompt;
                prompt.RespondChoice(choice);
                return Task.CompletedTask;
            });

        Assert.True(asked is not null, TestSessions.Dump(items));
        Assert.Equal(PowerShellPromptKind.Choice, asked.Kind);
        Assert.NotEmpty(asked.Choices);
        Assert.Equal(expected, TestSessions.Text(items));
    }

    [Fact]
    public async Task Cancelling_a_prompt_stops_the_command()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(
            session,
            "Read-Host 'x'; 'not reached'",
            prompt =>
            {
                prompt.Cancel();
                return Task.CompletedTask;
            });

        Assert.DoesNotContain("not reached", TestSessions.Text(items), StringComparison.Ordinal);
        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
    }

    [Fact]
    public async Task Stopping_a_run_that_waits_at_a_prompt_ends_it()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("Read-Host 'x'", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellPromptItem)
            {
                await session.StopAsync();
            }
        }

        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
    }

    [Fact]
    public async Task A_credential_prompt_is_refused_because_the_window_creates_no_credential_object()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Get-Credential");

        Assert.Contains("credential", TestSessions.Text(items, PowerShellStream.Error), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(items, item => item is PowerShellPromptItem);
    }

    [Fact]
    public async Task A_nested_prompt_is_not_available()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "$Host.EnterNestedPrompt()");

        Assert.True(TestSessions.End(items).HadErrors);
    }

    [Fact]
    public async Task The_host_names_itself_and_follows_the_given_culture()
    {
        await using var session = await PowerShellSession.CreateAsync(
            new PowerShellSessionOptions { Culture = new System.Globalization.CultureInfo("pl-PL"), LockdownProbe = new FakeLockdownProbe() },
            TestContext.Current.CancellationToken);

        var items = await TestSessions.RunAsync(session, "$Host.Name; $Host.CurrentCulture.Name");

        Assert.Equal("AD Manager PowerShell\npl-PL\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task No_item_shows_what_it_carries_in_its_ToString()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunObjectsAsync("'CN=Łukasz Kowalski,OU=Secret,DC=contoso,DC=test'; Write-Progress -Activity 'Reading CN=Secret' -Status 'Łukasz'; Write-Warning 'Łukasz'", TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Contains(items, item => item is PowerShellObjectItem);
        Assert.Contains(items, item => item is PowerShellTextItem);
        Assert.Contains(items, item => item is PowerShellProgressItem);
        Assert.All(items, item =>
        {
            var text = item.ToString();
            Assert.DoesNotContain("Łukasz", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
        });
    }
}
