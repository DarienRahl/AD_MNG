using System.Globalization;
using System.Text;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// Helpers of the recipients and mailbox owners page tests over <see cref="CorrespondenceShellScenario"/>: files in the
/// scenario's temporary folder, the clipboard and the mailbox owner sample of the design note (section 10).
/// </summary>
internal static class PageTests
{
    public static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    static PageTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Windows-1250, the ANSI code page of Polish Windows (the platform of the scenario reports it).</summary>
    public static Encoding Cp1250 => Encoding.GetEncoding(1250);

    /// <summary>A file of <paramref name="name"/> in the scenario's folder (or its sub folder) with <paramref name="content"/>.</summary>
    public static string File(CorrespondenceShellScenario scenario, string name, byte[] content, string? folder = null)
    {
        var directory = folder ?? scenario.Paths.SettingsDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A UTF-8 text file (with BOM).</summary>
    public static string Text(CorrespondenceShellScenario scenario, string name, string content, string? folder = null) =>
        File(scenario, name, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(content)], folder);

    /// <summary>The clipboard holds <paramref name="text"/> (and optionally the CF_HTML <paramref name="html"/>).</summary>
    public static void Clipboard(CorrespondenceShellScenario scenario, string? text, string? html = null) =>
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(null, html, null, text);

    /// <summary>The sample company with the mailbox owners of the design note (adds the salutation sample too).</summary>
    public static MailboxOwnerSample MailboxSample(CorrespondenceShellScenario scenario) =>
        scenario.Harness.Builder.AddMailboxOwnerSample(scenario.Company);

    /// <summary>"Skrzynki" of a window: the OU of the sample as the scope (through the container picker).</summary>
    public static async Task UseSampleOuAsync(CorrespondenceShellScenario scenario, MailboxOwnersPageViewModel page, MailboxOwnerSample sample)
    {
        scenario.Harness.Dialogs.PickedContainers.Enqueue(sample.Ou);
        await page.BrowseScopeCommand.ExecuteAsync(null);
    }

    /// <summary>The row of a mailbox.</summary>
    public static MailboxOwnerRowViewModel Row(MailboxOwnersPageViewModel page, string mailbox) =>
        Assert.Single(page.Rows, r => Core.Ldap.DistinguishedName.AreEqual(r.Mailbox.DistinguishedName, mailbox));

    /// <summary>The recipient row of an address (exactly one).</summary>
    public static CorrespondenceRecipientViewModel Recipient(RecipientsPageViewModel page, string email) =>
        Assert.Single(page.Items, r => r.Email == email);
}
