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

    /// <summary>
    /// Audit PS-06: a console program last in the pipeline ran with the process's own standard output and error, because the window's
    /// pipeline ends in Out-Default - in AdMng.exe on Windows a hidden console, so <c>whoami</c> showed nothing. Its output and its
    /// errors are the window's now, as the captured forms (<c>whoami | Out-String</c>) always were.
    /// </summary>
    [Fact]
    public async Task A_console_program_last_in_the_pipeline_writes_into_the_window()
    {
        await using var session = await TestSessions.CreateAsync();
        var script = OperatingSystem.IsWindows() ? "cmd.exe /c echo native-direct" : "/bin/echo native-direct";

        var items = await TestSessions.RunAsync(session, script);

        Assert.Equal(["native-direct"], Lines(TestSessions.Text(items)));
        Assert.Equal(PowerShellRunState.Completed, TestSessions.End(items).State);
    }

    /// <summary>
    /// What a console program writes to its error output arrives in the window too, in its place among the output, as plain text - the
    /// way PowerShell 7 shows a program's error output whose stream is captured (it is not an error record of the engine, so the run
    /// does not count as one with errors).
    /// </summary>
    [Fact]
    public async Task What_a_console_program_writes_to_its_error_output_arrives_in_the_window_in_order()
    {
        await using var session = await TestSessions.CreateAsync();
        var script = OperatingSystem.IsWindows() ? "cmd.exe /c 'echo to-stderr 1>&2'; 'after'" : "/bin/sh -c 'echo to-stderr 1>&2'; 'after'";

        var items = await TestSessions.RunAsync(session, script);

        Assert.Equal(["to-stderr", "after"], Lines(TestSessions.Text(items)));
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false), TestSessions.End(items));
    }

    [Fact]
    public async Task An_error_of_a_command_beside_a_console_program_is_still_an_error_of_the_window()
    {
        await using var session = await TestSessions.CreateAsync();
        var native = OperatingSystem.IsWindows() ? "cmd.exe /c echo native" : "/bin/echo native";

        var items = await TestSessions.RunAsync(session, $"{native}; Write-Error 'e1'; 'after'");

        Assert.Equal(["native", "after"], Lines(TestSessions.Text(items)));
        Assert.Contains("e1", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.True(TestSessions.End(items).HadErrors);
    }

    [Fact]
    public async Task A_console_program_inside_a_script_block_and_after_other_output_writes_into_the_window_in_order()
    {
        await using var session = await TestSessions.CreateAsync();
        var native = OperatingSystem.IsWindows() ? "cmd.exe /c echo second" : "/bin/echo second";

        var items = await TestSessions.RunAsync(session, $"'first'; & {{ {native} }}; 'third'");

        Assert.Equal(["first", "second", "third"], Lines(TestSessions.Text(items)));
    }

    [Fact]
    public async Task The_windows_pass_through_is_not_a_command_of_the_session()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Write-AdmWindowOutput 'x'");

        Assert.Contains("Write-AdmWindowOutput", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal(string.Empty, TestSessions.Text(items));
    }

    private static string[] Lines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r', ' ')).Where(line => line.Length > 0)];

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
        Assert.Equal("Number", asked.Label);
        Assert.Contains("42\n", TestSessions.Text(items), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_array_parameter_is_asked_for_element_by_element_and_a_distinguished_name_stays_one_element()
    {
        // Audit PS-05: the answer was split at commas, so a DN typed at the -Identity prompt became four identities.
        await using var session = await TestSessions.CreateAsync();
        var asked = new List<PowerShellPromptItem>();
        var answers = new Queue<string>(["CN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=test", "jan.kowalski", string.Empty]);

        var items = await TestSessions.RunAsync(
            session,
            "function Get-Ids { param([Parameter(Mandatory)][string[]]$Identity) \"count=$($Identity.Count)\"; $Identity }; Get-Ids",
            prompt =>
            {
                asked.Add(prompt);
                prompt.Respond(answers.Dequeue());
                return Task.CompletedTask;
            });

        Assert.Equal(
            "count=2\nCN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=test\njan.kowalski\n",
            TestSessions.Text(items));
        Assert.Equal(["Identity[0]", "Identity[1]", "Identity[2]"], asked.Select(p => p.Label));
        Assert.All(asked, prompt => Assert.Equal(PowerShellPromptKind.Text, prompt.Kind));
        Assert.Equal(PowerShellRunState.Completed, TestSessions.End(items).State);
    }

    [Fact]
    public async Task A_missing_int_array_parameter_converts_each_element()
    {
        await using var session = await TestSessions.CreateAsync();
        var answers = new Queue<string>(["2", "5", string.Empty]);

        var items = await TestSessions.RunAsync(
            session,
            "function Get-Sum { param([Parameter(Mandatory)][int[]]$Number) ($Number | Measure-Object -Sum).Sum }; Get-Sum",
            prompt =>
            {
                prompt.Respond(answers.Dequeue());
                return Task.CompletedTask;
            });

        Assert.EndsWith("7\n", TestSessions.Text(items), StringComparison.Ordinal);
        Assert.False(TestSessions.End(items).HadErrors, TestSessions.Dump(items));
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
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);

        var items = await TestSessions.RunAsync(session, "$Host.EnterNestedPrompt()");

        Assert.Contains("Nested prompts are not available", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
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
