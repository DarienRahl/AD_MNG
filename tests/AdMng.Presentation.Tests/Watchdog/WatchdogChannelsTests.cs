using System.Globalization;
using System.Text.Json;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>"Kanały i SMTP": SMTP, write-only secrets, recipient groups, Teams / webhook channels and their test sends.</summary>
public sealed class WatchdogChannelsTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Opening_TheEditors_DoesNotChangeTheConfiguration()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);

            Assert.Equal(Json(session.Envelope!.Configuration.Smtp), Json(channels.Smtp!.Build()));
            Assert.Equal(["teams-it", "siem"], channels.Channels.Select(c => c.Id));
            Assert.All(channels.Channels, c => Assert.Equal(Json(session.Envelope.Configuration.Channels[c.Index]), Json(c.Build())));
            Assert.Equal(["Administratorzy IT (2)", "Helpdesk (1)"], channels.RecipientGroups!.Groups.Select(g => g.DisplayText));
            Assert.False(session.IsDirty);
            Assert.Same(channels.Channels[0], channels.SelectedChannel);
        });
    }

    [Fact]
    public async Task Smtp_EditsReachTheWorkingConfiguration()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var smtp = channels.Smtp!;
            Assert.True(smtp.IsBasic);
            Assert.False(smtp.IsOAuth);
            Assert.False(smtp.IsPinned);

            smtp.Host = " smtp.office365.com ";
            smtp.Port = "25";
            smtp.Security = smtp.SecurityOptions.Single(o => o.Value == SmtpSecurity.SslOnConnect);
            smtp.Authentication = smtp.AuthenticationOptions.Single(o => o.Value == SmtpAuthentication.OAuth2ClientCredentials);
            smtp.Cloud = smtp.CloudOptions.Single(o => o.Value == EntraCloud.UsGovernment);
            smtp.TenantId = "8f2c3e4a-1b2c-4d5e-8f90-123456789abc";
            smtp.ClientId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
            smtp.CertificateThumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
            smtp.From = "Strażnik AD <straznik@contoso.com>";
            smtp.ReplyTo = "";
            smtp.LocalDomain = "wd01.contoso.com";
            smtp.CertificateValidation = smtp.CertificateOptions.Single(o => o.Value == CertificateValidationMode.PinnedSha256);
            smtp.PinnedSha256 = "ab12";
            smtp.CheckRevocation = false;
            smtp.MaxMessagesPerMinute = "5";
            smtp.MaxRecipientsPerMessage = "10";
            smtp.TimeoutSeconds = "90";

            Assert.True(smtp.IsOAuth);
            Assert.True(smtp.UsesUserName);
            Assert.True(smtp.IsPinned);
            Assert.Null(smtp.NumbersError);
            var stored = session.Working!.Smtp;
            Assert.Equal("smtp.office365.com", stored.Host);
            Assert.Equal(25, stored.Port);
            Assert.Equal(SmtpSecurity.SslOnConnect, stored.Security);
            Assert.Equal(SmtpAuthentication.OAuth2ClientCredentials, stored.Authentication);
            Assert.Equal(EntraCloud.UsGovernment, stored.OAuth.Cloud);
            Assert.Equal("8f2c3e4a-1b2c-4d5e-8f90-123456789abc", stored.OAuth.TenantId);
            Assert.Equal("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", stored.OAuth.ClientId);
            Assert.Equal("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", stored.OAuth.CertificateThumbprint);
            Assert.Equal("Strażnik AD <straznik@contoso.com>", stored.From);
            Assert.Null(stored.ReplyTo);
            Assert.Equal("wd01.contoso.com", stored.LocalDomain);
            Assert.Equal(CertificateValidationMode.PinnedSha256, stored.CertificateValidation);
            Assert.Equal("ab12", stored.PinnedSha256);
            Assert.False(stored.CheckRevocation);
            Assert.Equal(5, stored.MaxMessagesPerMinute);
            Assert.Equal(10, stored.MaxRecipientsPerMessage);
            Assert.Equal(TimeSpan.FromSeconds(90), stored.Timeout);
            Assert.True(session.IsDirty);

            smtp.Port = "dwadzieścia pięć";
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.ValidationKey("range")], smtp.NumbersError);
            Assert.Equal(587, session.Working.Smtp.Port);
        });
    }

    [Fact]
    public async Task Secret_IsWriteOnly_TypedValueGoesToTheSaveButIsNeverShown()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var password = channels.Smtp!.Password;
            Assert.True(password.IsSet);
            Assert.Equal("ustawione", password.StateText);
            Assert.False(password.UndoCommand.CanExecute(null));

            password.SetNewValue("Zażółć-Gęślą-2026!");

            Assert.True(password.HasNewValue);
            Assert.Equal("nowa wartość – zapisz, aby zastosować", password.StateText);
            Assert.Equal("Zażółć-Gęślą-2026!", session.Working!.Smtp.Password.NewValue);
            Assert.True(session.IsDirty);
            Assert.True(password.UndoCommand.CanExecute(null));
            AssertNeverExposed(password, "Zażółć-Gęślą-2026!");
            AssertNeverExposed(channels.Smtp, "Zażółć-Gęślą-2026!");
            Assert.DoesNotContain("Zażółć", session.Working.Smtp.Password.ToString(), StringComparison.Ordinal);

            Assert.True(await session.SaveAsync());

            // Saving rebuilds the editors: the typed value is gone, the service only reports "set".
            Assert.NotSame(password, channels.Smtp.Password);
            Assert.False(channels.Smtp.Password.HasNewValue);
            Assert.True(channels.Smtp.Password.IsSet);
            Assert.Null(session.Working!.Smtp.Password.NewValue);
            Assert.Equal("Zażółć-Gęślą-2026!", h.Client.SavedEnvelopes[0].Configuration.Smtp.Password.NewValue);
        });
    }

    [Fact]
    public async Task Secret_ClearAndUndo()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var url = channels.Channels.Single(c => c.Id == "siem").Url;
            Assert.Equal(SampleConfiguration.WebhookUrlHint, url.Hint);
            Assert.True(url.HasHint);

            url.SetNewValue("https://siem.contoso.com/hooks/nowy");
            url.ClearCommand.Execute(null);

            Assert.True(url.IsCleared);
            Assert.False(url.HasNewValue);
            Assert.Equal("zostanie usunięte", url.StateText);
            var field = session.Working!.Channels[1].Url;
            Assert.True(field.Clear);
            Assert.Null(field.NewValue);

            url.UndoCommand.Execute(null);
            Assert.False(url.IsCleared);
            Assert.Equal("ustawione", url.StateText);
            Assert.False(session.IsDirty);

            // Clearing a secret that is not stored is not a change.
            var clientSecret = channels.Smtp!.ClientSecret;
            Assert.Equal("nieustawione", clientSecret.StateText);
            clientSecret.ClearCommand.Execute(null);
            Assert.False(clientSecret.IsCleared);
            Assert.False(session.Working.Smtp.OAuth.ClientSecret.Clear);
        });
    }

    [Fact]
    public async Task Revert_ForgetsTypedSecrets()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            channels.Smtp!.Password.SetNewValue("tajne-hasło");
            channels.Channels[1].SigningSecret.SetNewValue("whsec_dGFqbmU=");

            session.RevertCommand.Execute(null);

            Assert.False(channels.Smtp!.Password.HasNewValue);
            Assert.False(channels.Channels[1].SigningSecret.HasNewValue);
            Assert.False(session.IsDirty);
        });
    }

    [Fact]
    public async Task TestEmail_UsesTheUnsavedSettings_AndShowsTheStages()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            channels.Smtp!.From = "Strażnik AD <straznik@contoso.com>";
            channels.TestRecipients = "jan.kowalski@contoso.com; zaneta.szymanska@contoso.com";
            Assert.True(channels.SendTestEmailCommand.CanExecute(null));

            await channels.SendTestEmailCommand.ExecuteAsync(null);

            var request = Assert.Single(h.Client.EmailTests);
            Assert.Equal(["jan.kowalski@contoso.com", "zaneta.szymanska@contoso.com"], request.Recipients);
            Assert.Equal("Strażnik AD <straznik@contoso.com>", request.Settings!.From);
            var test = channels.EmailTest;
            Assert.True(test.Success);
            Assert.Equal("Test zakończony powodzeniem (840 ms).", test.SummaryText);
            Assert.Equal(7, test.Stages.Count);
            Assert.All(test.Stages, s => Assert.Equal("✓", s.Mark));
            Assert.Equal("250 2.0.0 OK", test.Stages[^1].Detail);
            Assert.Equal("120 ms", test.Stages[0].DurationText);
            Assert.Empty(test.Hints);
            Assert.False(test.IsRunning);
            Assert.True(session.IsDirty);
            Assert.Empty(h.Client.SavedEnvelopes);
        });
    }

    [Fact]
    public async Task TestEmail_WithAnotherServer_AsksForThePasswordAgain()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (_, channels) = await OpenAsync(h);
            var focused = 0;
            channels.Smtp!.Password.FocusRequested += (_, _) => focused++;
            channels.Smtp.Host = "smtp.innafirma.pl";
            channels.TestRecipients = "jan.kowalski@contoso.com";

            await channels.SendTestEmailCommand.ExecuteAsync(null);

            var test = channels.EmailTest;
            Assert.False(test.Success);
            Assert.Equal("Adres docelowy się zmienił – wpisz hasło/sekret ponownie.", test.Error);
            Assert.Equal(h.Ad.Localizer.HintText(DeliveryHints.SecretRebindRequired), Assert.Single(test.Hints));
            Assert.Equal(test.Error, channels.Smtp.Password.Error);
            Assert.Equal(1, focused);

            // Typing the password again lets the test through.
            channels.Smtp.Password.SetNewValue("Hasło-Dla-Innej-Firmy1");
            await channels.SendTestEmailCommand.ExecuteAsync(null);
            Assert.True(channels.EmailTest.Success);
        });
    }

    [Fact]
    public async Task DeliveryTest_ShowsFailedStagesAndHints()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        h.Client.EmailTestHandler = _ => new DeliveryTestResult
        {
            Success = false,
            Duration = TimeSpan.FromMilliseconds(1250),
            Stages =
            [
                new DeliveryStageResult { Stage = DeliveryStage.Connect, Success = true, Detail = "poczta.contoso.com:587", Duration = TimeSpan.FromMilliseconds(40) },
                new DeliveryStageResult
                {
                    Stage = DeliveryStage.Authenticate,
                    Success = false,
                    Detail = "Authentication unsuccessful, basic authentication is disabled.",
                    StatusCode = 535,
                    EnhancedStatus = "5.7.139",
                    Duration = TimeSpan.FromMilliseconds(1210),
                },
            ],
            Hints = [DeliveryHints.BasicAuthDisabled, "brand.newHint"],
        };
        await h.Ui.RunAsync(async () =>
        {
            var (_, channels) = await OpenAsync(h);
            channels.TestRecipients = "jan.kowalski@contoso.com";

            await channels.SendTestEmailCommand.ExecuteAsync(null);

            var test = channels.EmailTest;
            Assert.False(test.Success);
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.TestFailed, "1,3 s"), test.SummaryText);
            var failed = test.Stages[1];
            Assert.Equal("✗", failed.Mark);
            Assert.Equal(h.Ad.Localizer.StageName(DeliveryStage.Authenticate), failed.StageText);
            Assert.Equal("535 5.7.139", failed.StatusText);
            Assert.Equal("1,2 s", failed.DurationText);
            Assert.Equal(string.Empty, test.Stages[0].StatusText);
            Assert.Equal([h.Ad.Localizer[WatchdogDisplay.HintKey(DeliveryHints.BasicAuthDisabled)], "brand.newHint"], test.Hints);
        });
    }

    [Fact]
    public async Task DeliveryTest_ConnectionFailure_AndCancel()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (_, channels) = await OpenAsync(h);
            var teams = channels.Channels[0];
            h.Client.FailNext(nameof(IWatchdogClient.SendTestChannelAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));

            await teams.TestCommand.ExecuteAsync(null);
            Assert.False(teams.Test.Success);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.TestNotSent], teams.Test.SummaryText);
            Assert.Equal(h.Connection.Describe(new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down")), teams.Test.Error);

            h.Client.Hold(nameof(IWatchdogClient.SendTestChannelAsync));
            var running = teams.TestCommand.ExecuteAsync(null);
            Assert.True(teams.Test.IsRunning);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.TestRunning], teams.Test.SummaryText);
            Assert.True(teams.Test.CancelCommand.CanExecute(null));
            teams.Test.CancelCommand.Execute(null);
            await running;
            h.Client.Release(nameof(IWatchdogClient.SendTestChannelAsync));

            Assert.False(teams.Test.IsRunning);
            Assert.Null(teams.Test.Success);
            Assert.Equal(h.Ad.Localizer[L.Errors.Cancelled], teams.Test.SummaryText);
            Assert.Null(teams.Test.Error);
        });
    }

    [Fact]
    public async Task Teams_ScopesFollowTheCloud_AndTheTestSendIsAcceptedNotDelivered()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var teams = channels.Channels.Single(c => c.Id == "teams-it");
            Assert.True(teams.IsTeams);
            Assert.False(teams.IsEntraAuth);
            Assert.Equal(EntraCloudInfo.TeamsScopes(EntraCloud.Public), teams.Scopes);
            Assert.Equal("Teams – Administratorzy IT (Teams)", teams.DisplayText);

            teams.AuthMode = teams.AuthModes.Single(m => m.Value == TeamsAuthMode.EntraClientCredentials);
            Assert.True(teams.IsEntraAuth);
            teams.Cloud = teams.Clouds.Single(c => c.Value == EntraCloud.UsGovernment);
            Assert.Equal(EntraCloudInfo.TeamsScopes(EntraCloud.UsGovernment), teams.Scopes);
            Assert.Equal("https://gov.service.flow.microsoft.us//.default", teams.Scope);
            teams.Scope = teams.Scopes[3];
            teams.Cloud = teams.Clouds.Single(c => c.Value == EntraCloud.China);
            Assert.Equal("https://service.powerautomate.cn//.default", teams.Scope);
            teams.TenantId = "8f2c3e4a-1b2c-4d5e-8f90-123456789abc";
            teams.TeamsClientId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
            teams.ClientSecret.SetNewValue("sekret-klienta-Entra");
            teams.Enabled = false;

            var stored = session.Working!.Channels[0];
            Assert.Equal(TeamsAuthMode.EntraClientCredentials, stored.Teams!.AuthMode);
            Assert.Equal(EntraCloud.China, stored.Teams.Cloud);
            Assert.Equal("https://service.powerautomate.cn//.default", stored.Teams.Scope);
            Assert.Equal("8f2c3e4a-1b2c-4d5e-8f90-123456789abc", stored.Teams.TenantId);
            Assert.Equal("sekret-klienta-Entra", stored.Teams.ClientSecret.NewValue);
            Assert.False(stored.Enabled);
            Assert.Null(stored.Webhook);
            Assert.EndsWith(h.Ad.Localizer[L.Watchdog.ChannelDisabled] + ")", teams.DisplayText, StringComparison.Ordinal);

            await teams.TestCommand.ExecuteAsync(null);

            var (channelId, request) = Assert.Single(h.Client.ChannelTests);
            Assert.Equal("teams-it", channelId);
            Assert.Equal(EntraCloud.China, request.Settings!.Teams!.Cloud);
            Assert.True(teams.Test.Success);
            Assert.Equal("Teams przyjął wiadomość (202); dostarczenie na kanał nie jest potwierdzane – sprawdź kanał.", Assert.Single(teams.Test.Hints));
            Assert.Equal("202", teams.Test.Stages[^1].StatusText);
        });
    }

    [Fact]
    public async Task Webhook_GeneratedSecret_IsWhsecPlus32RandomBytes_CopiedAsASecret_AndGoneAfterSave()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var webhook = channels.Channels.Single(c => c.Id == "siem");
            Assert.True(webhook.IsWebhook);
            Assert.False(webhook.HasGeneratedSecret);
            Assert.False(webhook.CopyGeneratedSecretCommand.CanExecute(null));

            webhook.GenerateSecretCommand.Execute(null);

            var secret = webhook.GeneratedSecret!;
            Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
            Assert.Equal(ChannelEditorViewModel.GeneratedSecretBytes, Convert.FromBase64String(secret["whsec_".Length..]).Length);
            Assert.True(webhook.SigningSecret.HasNewValue);
            Assert.Equal(secret, session.Working!.Channels[1].Webhook!.SigningSecret.NewValue);

            webhook.GenerateSecretCommand.Execute(null);
            Assert.NotEqual(secret, webhook.GeneratedSecret);
            secret = webhook.GeneratedSecret!;

            webhook.CopyGeneratedSecretCommand.Execute(null);
            Assert.Equal(secret, h.Ad.Clipboard.Text);
            Assert.True(h.Ad.Clipboard.IsSecret);
            Assert.Contains(h.Ad.Notifications.Items, n => n.Severity == NotificationSeverity.Info);

            Assert.True(await session.SaveAsync());

            var rebuilt = channels.Channels.Single(c => c.Id == "siem");
            Assert.False(rebuilt.HasGeneratedSecret);
            Assert.False(rebuilt.SigningSecret.HasNewValue);
            Assert.Equal("whsec_…" + secret[^4..], rebuilt.SigningSecret.Hint);
        });
    }

    [Fact]
    public async Task Webhook_FieldsAndLimits()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var webhook = channels.Channels.Single(c => c.Id == "siem");
            var changes = 0;
            webhook.Changed += (_, _) => changes++;
            Assert.Equal("15", webhook.HeartbeatMinutes);
            Assert.Equal("siem.contoso.com", webhook.AllowedHosts);

            webhook.Name = "SIEM – Sentinel";
            webhook.DetailLevel = webhook.DetailLevels.Single(d => d.Value == DetailLevel.Standard);
            webhook.SendGitHubSignature = true;
            webhook.AllowedHosts = "siem.contoso.com\r\nsentinel.contoso.com";
            webhook.TimeoutSeconds = "10";
            webhook.IncludeDigest = false;
            webhook.HeartbeatMinutes = "";
            webhook.PerSecond = "2";
            webhook.PerMinute = "60";
            webhook.PerHour = "1000";
            webhook.PreviousSigningSecret.SetNewValue("whsec_c3RhcnktcG9kcGlz");

            var stored = session.Working!.Channels[1];
            Assert.Equal("SIEM – Sentinel", stored.Name);
            Assert.Equal(DetailLevel.Standard, stored.DetailLevel);
            Assert.True(stored.Webhook!.SendGitHubSignature);
            Assert.Equal(["siem.contoso.com", "sentinel.contoso.com"], stored.Webhook.AllowedHosts);
            Assert.Equal(TimeSpan.FromSeconds(10), stored.Webhook.Timeout);
            Assert.False(stored.Webhook.IncludeDigest);
            Assert.Null(stored.Webhook.HeartbeatInterval);
            Assert.Equal(new RateLimitSettings { PerSecond = 2, PerMinute = 60, PerHour = 1000 }, stored.RateLimit);
            Assert.Equal("whsec_c3RhcnktcG9kcGlz", stored.Webhook.PreviousSigningSecret.NewValue);
            Assert.Null(stored.Teams);
            Assert.Equal(11, changes);
            Assert.Null(webhook.NumbersError);

            webhook.PerHour = "-1";
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.ValidationKey("range")], webhook.NumbersError);
            // While the field holds no number the stored value stays in the configuration.
            Assert.Equal(5000, session.Working.Channels[1].RateLimit.PerHour);
        });
    }

    [Fact]
    public async Task Webhook_TestToANewHost_AsksForTheSigningSecretAgain()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (_, channels) = await OpenAsync(h);
            var webhook = channels.Channels.Single(c => c.Id == "siem");
            var focused = 0;
            webhook.SigningSecret.FocusRequested += (_, _) => focused++;
            webhook.Url.SetNewValue("https://sentinel.contoso.com/api/hooks/ad");

            await webhook.TestCommand.ExecuteAsync(null);

            Assert.False(webhook.Test.Success);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.TestRebindRequired], webhook.SigningSecret.Error);
            Assert.Null(webhook.Url.Error);
            Assert.Equal(1, focused);
        });
    }

    [Fact]
    public async Task Channels_AreAddedAndRemoved()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);

            channels.AddTeamsChannelCommand.Execute(null);
            Assert.Equal("teams", channels.SelectedChannel!.Id);
            Assert.Equal("Teams – nowy kanał", channels.SelectedChannel.Name);
            Assert.Equal(DetailLevel.Standard, channels.SelectedChannel.DetailLevel.Value);
            Assert.False(channels.SelectedChannel.Url.IsSet);
            channels.AddTeamsChannelCommand.Execute(null);
            Assert.Equal("teams-2", channels.SelectedChannel!.Id);
            channels.AddWebhookChannelCommand.Execute(null);
            Assert.Equal("webhook", channels.SelectedChannel!.Id);
            Assert.Equal("Webhook – nowy kanał", channels.SelectedChannel.Name);
            Assert.Equal(DetailLevel.Minimal, channels.SelectedChannel.DetailLevel.Value);
            Assert.Equal(["teams-it", "siem", "teams", "teams-2", "webhook"], session.Working!.Channels.Select(c => c.Id));

            // Declined: nothing removed.
            channels.SelectedChannel = channels.Channels[1];
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(false);
            await channels.RemoveChannelCommand.ExecuteAsync(null);
            Assert.Equal(5, session.Working.Channels.Count);
            Assert.Contains(h.Ad.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal) && s.Contains("SIEM (Splunk)", StringComparison.Ordinal));

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await channels.RemoveChannelCommand.ExecuteAsync(null);
            Assert.Equal(["teams-it", "teams", "teams-2", "webhook"], session.Working.Channels.Select(c => c.Id));
            Assert.Equal(["teams-it", "teams", "teams-2", "webhook"], channels.Channels.Select(c => c.Id));
            Assert.Equal([0, 1, 2, 3], channels.Channels.Select(c => c.Index));
            Assert.Equal("teams", channels.SelectedChannel!.Id);

            channels.SelectedChannel = channels.Channels[^1];
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await channels.RemoveChannelCommand.ExecuteAsync(null);
            Assert.Equal("teams-2", channels.SelectedChannel!.Id);
        });
    }

    [Fact]
    public async Task RecipientGroups_AreAddedEditedAndRemoved()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var groups = channels.RecipientGroups!;

            groups.AddGroupCommand.Execute(null);
            var added = groups.SelectedGroup!;
            Assert.Equal("nowa-grupa", added.Id);
            Assert.Equal("Nowa grupa", added.Name);
            added.Name = "Kierownicy oddziałów";
            added.Id = "kierownicy";
            added.Addresses = "pawel.lewandowski@contoso.com;\r\n malgorzata.wojcik@contoso.com ,";
            added.DetailLevel = added.DetailLevels.Single(d => d.Value == DetailLevel.Minimal);
            Assert.Equal("Kierownicy oddziałów (2)", added.DisplayText);

            var stored = session.Working!.RecipientGroups[2];
            Assert.Equal("kierownicy", stored.Id);
            Assert.Equal(["pawel.lewandowski@contoso.com", "malgorzata.wojcik@contoso.com"], stored.Addresses);
            Assert.Equal(DetailLevel.Minimal, stored.DetailLevel);

            groups.AddGroupCommand.Execute(null);
            Assert.Equal("nowa-grupa", groups.SelectedGroup!.Id);
            groups.AddGroupCommand.Execute(null);
            Assert.Equal("nowa-grupa-2", groups.SelectedGroup!.Id);

            groups.SelectedGroup = groups.Groups[0];
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await groups.RemoveGroupCommand.ExecuteAsync(null);
            Assert.Equal(["helpdesk", "kierownicy", "nowa-grupa", "nowa-grupa-2"], session.Working.RecipientGroups.Select(g => g.Id));
            Assert.Equal("helpdesk", groups.SelectedGroup!.Id);
        });
    }

    [Fact]
    public async Task ServiceIssues_AreShownNextToTheFields()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Validator = _ =>
        [
            new ConfigIssue { Path = "smtp.host", Code = "invalidHostName", Severity = IssueSeverity.Error, Message = "x" },
            new ConfigIssue { Path = "smtp.password", Code = "secretRebindRequired", Severity = IssueSeverity.Error, Message = "x" },
            new ConfigIssue { Path = "channels[1].webhook.signingSecret", Code = "required", Severity = IssueSeverity.Error, Message = "x" },
            new ConfigIssue { Path = "recipientGroups[1].addresses[0]", Code = "invalidEmail", Severity = IssueSeverity.Error, Message = "x" },
        ];
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var localizer = h.Ad.Localizer;
            channels.RecipientGroups!.SelectedGroup = channels.RecipientGroups.Groups[0];
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await channels.RecipientGroups.RemoveGroupCommand.ExecuteAsync(null);

            Assert.False(await session.ValidateAsync());

            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("invalidHostName")], channels.Smtp!.Errors["host"]);
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("secretRebindRequired")], channels.Smtp.Password.Error);
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("required")], channels.Channels[1].SigningSecret.Error);
            Assert.Null(channels.Channels[0].Url.Error);

            // recipientGroups[1] no longer exists after removing the first group – nothing is shown on "Helpdesk" (now [0]).
            Assert.Null(channels.RecipientGroups.Groups[0].Errors["addresses[0]"]);
        });
    }

    [Fact]
    public async Task Viewer_SeesARedactedReadOnlyCopy()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = false;
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = false };
            await session.LoadAsync();
            using var channels = new WatchdogChannelsViewModel(h.Services, session);

            Assert.False(channels.Smtp!.CanEdit);
            Assert.False(channels.SendTestEmailCommand.CanExecute(null));
            Assert.False(channels.AddTeamsChannelCommand.CanExecute(null));
            Assert.False(channels.RemoveChannelCommand.CanExecute(null));
            Assert.False(channels.Channels[0].TestCommand.CanExecute(null));
            Assert.False(channels.Channels[1].GenerateSecretCommand.CanExecute(null));
            Assert.False(channels.RecipientGroups!.AddGroupCommand.CanExecute(null));
            Assert.All(channels.RecipientGroups.Groups, g => Assert.Equal(string.Empty, g.Addresses));

            channels.Smtp.Host = "smtp.innafirma.pl";
            channels.Channels[1].Name = "Zmieniona nazwa";
            Assert.Same(session.Envelope!.Configuration, session.Working);
            Assert.False(session.IsDirty);
        });
    }

    [Fact]
    public async Task UserMessaging_SwitchLimitsAndSenders_ReachTheWorkingConfiguration_AndShowTheServicesIssues()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Validator = c => c.UserMessages.AllowedSenders.Count > 1
            ? [new ConfigIssue { Path = "userMessages.allowedSenders[1]", Code = "invalidAddress", Severity = IssueSeverity.Error, Message = "x" }]
            : [];
        await h.Ui.RunAsync(async () =>
        {
            var (session, channels) = await OpenAsync(h);
            var messaging = channels.UserMessaging!;
            Assert.False(messaging.Enabled);
            Assert.Equal(("300", "2000", string.Empty), (messaging.MaxMessagesPerHour, messaging.MaxMessagesPerDay, messaging.AllowedSenders));
            Assert.False(session.IsDirty);

            messaging.Enabled = true;
            messaging.MaxMessagesPerHour = "120";
            messaging.MaxMessagesPerDay = "dużo";
            messaging.AllowedSenders = "helpdesk@contoso.com\r\n@kadry.contoso.com; pomoc@contoso.com";

            var stored = session.Working!.UserMessages;
            Assert.True(stored.Enabled);
            Assert.Equal((120, 2000), (stored.MaxMessagesPerHour, stored.MaxMessagesPerDay));
            Assert.Equal(["helpdesk@contoso.com", "@kadry.contoso.com", "pomoc@contoso.com"], stored.AllowedSenders);
            Assert.NotNull(messaging.NumbersError);
            Assert.True(session.IsDirty);

            Assert.False(await session.ValidateAsync());
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.ValidationKey("invalidAddress")], messaging.AllowedSendersError);
        });
    }

    private static async Task<(WatchdogConfigSession Session, WatchdogChannelsViewModel Channels)> OpenAsync(WatchdogHarness h)
    {
        var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
        await session.LoadAsync();
        return (session, new WatchdogChannelsViewModel(h.Services, session));
    }

    /// <summary>No public property (also of nested objects' string properties) shows the secret.</summary>
    private static void AssertNeverExposed(object target, string secret)
    {
        foreach (var property in target.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
        {
            var value = property.GetValue(target);
            Assert.False(value is string text && text.Contains(secret, StringComparison.Ordinal), $"{property.Name} exposes the secret.");
        }

        Assert.DoesNotContain(secret, target.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, WatchdogJson.Options);
}
