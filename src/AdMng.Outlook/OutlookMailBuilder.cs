using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging.Images;

namespace AdMng.Outlook;

/// <summary>Outlook object model constants (<c>OlItemType</c>, <c>OlBodyFormat</c>, …) used by the engine.</summary>
internal static class OutlookConstants
{
    public const int OlMailItem = 0;
    public const int OlFormatHtml = 2;
    public const int OlTo = 1;
    public const int OlCc = 2;
    public const int OlBcc = 3;
    public const int OlByValue = 1;
    public const int OlDiscard = 1;
    public const int OlFolderSentMail = 5;
}

/// <summary>MAPI property names of <c>PropertyAccessor</c> (design note 4.2 and 4.4).</summary>
internal static class MapiProperties
{
    /// <summary>PS_PUBLIC_STRINGS.</summary>
    public const string PublicStrings = "{00020329-0000-0000-C000-000000000046}";

    /// <summary>PS_INTERNET_HEADERS.</summary>
    public const string InternetHeaders = "{00020386-0000-0000-C000-000000000046}";

    public const string AttachContentId = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";
    public const string AttachmentHidden = "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B";
    public const string AttachMimeTag = "http://schemas.microsoft.com/mapi/proptag/0x370E001F";

    /// <summary>PR_SMTP_ADDRESS of a recipient.</summary>
    public const string SmtpAddress = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";

    /// <summary>The campaign id of a sent item (named string property in PS_PUBLIC_STRINGS).</summary>
    public const string Campaign = "http://schemas.microsoft.com/mapi/string/" + PublicStrings + "/AdMngCampaign";

    /// <summary>The keys of a sent item (named string property in PS_PUBLIC_STRINGS), joined by "\n".</summary>
    public const string Keys = "http://schemas.microsoft.com/mapi/string/" + PublicStrings + "/AdMngKeys";

    /// <summary>An internet header of the sent message (PS_INTERNET_HEADERS).</summary>
    public static string Header(string name) => "http://schemas.microsoft.com/mapi/string/" + InternetHeaders + "/" + name;
}

/// <summary>
/// Builds one Outlook item from an <see cref="OutlookMessage"/> (design note docs/dev/u5-outlook-correspondence.md 4.2 – the
/// port of MailerWPiA <c>OutlookService.Send</c>, <c>RenderViaOutlook</c> and <c>MarkInlineImages</c>, same order of steps):
/// create, HTML format, send-as account, on behalf of, recipients (resolved; an unresolved one refuses the item), reply-to,
/// subject, body, read receipt, deferred delivery, voting, importance, named properties and internet headers (best effort),
/// inline images (temp files, content id, hidden), attachments, save, inline images marked again after the save, send. A
/// failure after the save leaves the draft in "Wersje robocze" (<see cref="OutlookItemResult.SavedAsDraft"/>); a failure
/// before it discards the item. Every COM object is released and the temp folder deleted when the item is done. Runs on
/// the session's worker thread only.
/// </summary>
internal sealed partial class OutlookMailBuilder
{
    /// <summary>The longest <c>AdMngKeys</c> value (design note 4.4).</summary>
    public const int MaxKeysLength = 4000;

    /// <summary>The largest image read back by <see cref="Render"/>.</summary>
    public const int MaxRenderedImageBytes = 32 * 1024 * 1024;

    private const int MaxHeaderValueLength = 998;

    private readonly object _application;
    private readonly OutlookTempFiles _tempFiles;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly Action<object> _release;

    /// <param name="application">The late-bound Outlook application.</param>
    /// <param name="tempFiles">The temp folders of inline images.</param>
    /// <param name="localTimeZone">The time zone of the PC (deferred delivery is set in local time).</param>
    /// <param name="release">Releases one COM object (tests record it).</param>
    public OutlookMailBuilder(object application, OutlookTempFiles tempFiles, TimeZoneInfo localTimeZone, Action<object> release)
    {
        _application = application;
        _tempFiles = tempFiles;
        _localTimeZone = localTimeZone;
        _release = release;
    }

    /// <summary>Builds the item and sends it (or only saves it in <see cref="OutlookSendMode.Draft"/>). Never throws.</summary>
    public OutlookItemResult Send(OutlookMessage message, OutlookSendMode mode)
    {
        var scope = new ComScope(_release);
        var images = new ImageFiles();
        object? mail = null;
        var step = OutlookSteps.Create;
        var saved = false;
        try
        {
            mail = scope.Track((object)((dynamic)_application).CreateItem(OutlookConstants.OlMailItem));
            dynamic item = mail;
            BestEffort(() => item.BodyFormat = OutlookConstants.OlFormatHtml);

            if (!string.IsNullOrWhiteSpace(message.SendUsingAccount))
            {
                step = OutlookSteps.Account;
                SelectAccount(mail, message.SendUsingAccount, scope);
            }

            if (!string.IsNullOrWhiteSpace(message.SentOnBehalfOf))
            {
                step = OutlookSteps.OnBehalf;
                item.SentOnBehalfOfName = message.SentOnBehalfOf.Trim();
            }

            step = OutlookSteps.Recipients;
            AddRecipients(mail, message, refuseUnresolved: true, includeCopies: true, scope);

            if (message.ReplyTo.Any(address => !string.IsNullOrWhiteSpace(address)))
            {
                step = OutlookSteps.ReplyTo;
                dynamic replyTo = scope.Track((object)item.ReplyRecipients);
                foreach (var address in message.ReplyTo.Where(address => !string.IsNullOrWhiteSpace(address)))
                {
                    scope.Track((object)replyTo.Add(address.Trim()));
                }
            }

            step = OutlookSteps.Subject;
            item.Subject = message.Subject ?? string.Empty;

            step = OutlookSteps.Body;
            item.HTMLBody = message.HtmlBody ?? string.Empty;

            if (message.ReadReceipt)
            {
                step = OutlookSteps.Receipt;
                BestEffort(() => item.ReadReceiptRequested = true);
            }

            if (message.DeferredDelivery is { } deferred)
            {
                step = OutlookSteps.Defer;
                item.DeferredDeliveryTime = LocalTime(deferred);
            }

            if (NormalizeVoting(message.VotingOptions) is { } voting)
            {
                step = OutlookSteps.Voting;
                item.VotingOptions = voting;
            }

            if (message.Importance != OutlookImportance.Normal)
            {
                step = OutlookSteps.Importance;
                BestEffort(() => item.Importance = (int)message.Importance);
            }

            step = OutlookSteps.Properties;
            SetProperties(mail, message, scope);

            if (message.InlineImages.Count > 0)
            {
                step = OutlookSteps.Images;
                AddImages(mail, message.InlineImages, images, scope);
            }

            if (message.Attachments.Count > 0)
            {
                step = OutlookSteps.Attachments;
                AddAttachments(mail, message.Attachments, refuseMissing: true, scope);
            }

            step = OutlookSteps.Save;
            item.Save();
            saved = true;

            if (images.Count > 0)
            {
                // Only after Save() the item is in the store and MAPI keeps these properties; set before, they are dropped
                // silently and the images arrive as plain attachments (MailerWPiA).
                step = OutlookSteps.Inline;
                MarkInlineImages(mail, images, scope);
                item.Save();
            }

            if (mode == OutlookSendMode.Send)
            {
                step = OutlookSteps.Send;
                item.Send();
                return OutlookItemResult.Sent;
            }

            return OutlookItemResult.Draft;
        }
        catch (Exception ex)
        {
            if (!saved && mail is not null)
            {
                BestEffort(() => ((dynamic)mail).Close(OutlookConstants.OlDiscard));
            }

            return new OutlookItemResult(false, saved, OutlookErrors.Classify(ex, step, isElevated: false));
        }
        finally
        {
            scope.Dispose();
            images.Dispose();
        }
    }

    /// <summary>
    /// Builds the item as Outlook renders it (MailerWPiA <c>RenderViaOutlook</c>: account, To recipients without the refusal of
    /// unresolved ones, subject, body, images, existing attachments, save), reads the HTML and the images with a content id
    /// back into memory (<c>cid:</c> kept) and deletes the draft (it lands in "Elementy usunięte").
    /// </summary>
    /// <exception cref="OutlookException">A step failed (classified).</exception>
    public OutlookRenderedMessage Render(OutlookMessage message)
    {
        var scope = new ComScope(_release);
        var images = new ImageFiles();
        object? mail = null;
        var step = OutlookSteps.Create;
        var saved = false;
        try
        {
            mail = scope.Track((object)((dynamic)_application).CreateItem(OutlookConstants.OlMailItem));
            dynamic item = mail;
            BestEffort(() => item.BodyFormat = OutlookConstants.OlFormatHtml);
            if (!string.IsNullOrWhiteSpace(message.SendUsingAccount))
            {
                step = OutlookSteps.Account;
                SelectAccount(mail, message.SendUsingAccount, scope);
            }

            step = OutlookSteps.Recipients;
            AddRecipients(mail, message, refuseUnresolved: false, includeCopies: false, scope);

            step = OutlookSteps.Subject;
            item.Subject = message.Subject ?? string.Empty;

            step = OutlookSteps.Body;
            item.HTMLBody = message.HtmlBody ?? string.Empty;

            if (message.InlineImages.Count > 0)
            {
                step = OutlookSteps.Images;
                AddImages(mail, message.InlineImages, images, scope);
            }

            if (message.Attachments.Count > 0)
            {
                step = OutlookSteps.Attachments;
                AddAttachments(mail, message.Attachments, refuseMissing: false, scope);
            }

            step = OutlookSteps.Save;
            item.Save();
            saved = true;
            if (images.Count > 0)
            {
                step = OutlookSteps.Inline;
                MarkInlineImages(mail, images, scope);
                item.Save();
            }

            step = OutlookSteps.Render;
            var html = (string?)item.HTMLBody ?? string.Empty;
            dynamic attachments = scope.Track((object)item.Attachments);
            var count = (int)attachments.Count;
            var rendered = new Dictionary<string, OutlookRenderedImage>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index <= count; index++)
            {
                if (ReadImage(scope.Track((object)attachments[index]), images, scope) is { } image)
                {
                    rendered.TryAdd(image.ContentId, image);
                }
            }

            step = OutlookSteps.Delete;
            item.Delete();
            return new OutlookRenderedMessage(html, rendered, count);
        }
        catch (Exception ex)
        {
            var failure = OutlookErrors.Classify(ex, step, isElevated: false);
            if (mail is not null)
            {
                // A preview never leaves anything behind: the saved draft is deleted, an unsaved item discarded.
                BestEffort(() =>
                {
                    if (saved && step != OutlookSteps.Delete)
                    {
                        ((dynamic)mail).Delete();
                    }
                    else if (!saved)
                    {
                        ((dynamic)mail).Close(OutlookConstants.OlDiscard);
                    }
                });
            }

            throw new OutlookException(failure);
        }
        finally
        {
            scope.Dispose();
            images.Dispose();
        }
    }

    /// <summary><c>CreateItem</c> + subject + body + <c>Close(olDiscard)</c> without sending (diagnostics).</summary>
    public OutlookFailure? TestItem()
    {
        var scope = new ComScope(_release);
        try
        {
            dynamic item = scope.Track((object)((dynamic)_application).CreateItem(OutlookConstants.OlMailItem));
            item.Subject = "test";
            item.HTMLBody = "<p>test</p>";
            item.Close(OutlookConstants.OlDiscard);
            return null;
        }
        catch (Exception ex)
        {
            return OutlookErrors.Classify(ex, OutlookSteps.TestItem, isElevated: false);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>"Tak; Nie ;;" → "Tak;Nie"; null when no option is left.</summary>
    internal static string? NormalizeVoting(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return null;
        }

        var parts = options.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(';', parts);
    }

    /// <summary>The keys joined by "\n" (line breaks inside a key become spaces, empty keys dropped), whole keys only up to
    /// <see cref="MaxKeysLength"/> characters.</summary>
    internal static string JoinKeys(IEnumerable<string> keys)
    {
        var builder = new StringBuilder();
        foreach (var raw in keys)
        {
            var key = ControlCharacters().Replace(raw ?? string.Empty, " ").Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var needed = key.Length + (builder.Length > 0 ? 1 : 0);
            if (builder.Length + needed > MaxKeysLength)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(key);
        }

        return builder.ToString();
    }

    /// <summary>The internet headers that can be set safely: a token name (letters, digits, '-'), the value without control
    /// characters (header injection) and at most 998 characters.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> SafeHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || !HeaderName().IsMatch(name))
            {
                continue;
            }

            var clean = ControlCharacters().Replace(value ?? string.Empty, " ").Trim();
            if (clean.Length > MaxHeaderValueLength)
            {
                clean = clean[..MaxHeaderValueLength];
            }

            if (clean.Length > 0)
            {
                result.Add(new KeyValuePair<string, string>(name, clean));
            }
        }

        return result;
    }

    /// <summary>A campaign id that is safe in a DASL filter: 1–64 letters, digits or '-'.</summary>
    internal static bool IsCampaignId(string? id) => id is not null && CampaignIdPattern().IsMatch(id);

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // MailerWPiA: these settings never fail the item.
        }
    }

    private static void TrySetProperty(object accessor, string name, object value)
    {
        BestEffort(() => ((dynamic)accessor).SetProperty(name, value));
    }

    /// <summary>
    /// SendUsingAccount (MailerWPiA <c>SetSendUsingAccount</c>): the account matched by SMTP address among
    /// <c>Session.Accounts</c>; set as a property, else through <c>InvokeMember</c> with <c>PutRefDispProperty</c> (a
    /// by-reference property). Unknown address → <see cref="OutlookFailureKind.InvalidAccount"/>, except the current user's
    /// own address in a profile without explicit accounts (the default account already sends as it).
    /// </summary>
    private void SelectAccount(object mail, string address, ComScope scope)
    {
        var session = (object)scope.Track((object)((dynamic)_application).Session);
        var account = OutlookProfileReader.FindAccount(session, address, scope);
        if (account is null)
        {
            if (string.Equals(OutlookProfileReader.CurrentUserAddress(session, scope), address.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new OutlookErrors.OutlookStepException(OutlookFailureKind.InvalidAccount, OutlookSteps.Account);
        }

        try
        {
            ((dynamic)mail).SendUsingAccount = (dynamic)account;
        }
        catch (Exception)
        {
            mail.GetType().InvokeMember(
                "SendUsingAccount",
                BindingFlags.PutRefDispProperty | BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                target: mail,
                args: [account],
                culture: CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Recipients.Add + Type + Resolve per address, then ResolveAll. With <paramref name="refuseUnresolved"/>, any recipient
    /// Outlook could not resolve refuses the item (<see cref="OutlookFailureKind.RecipientUnresolved"/>): nothing is sent.
    /// </summary>
    private static void AddRecipients(object mail, OutlookMessage message, bool refuseUnresolved, bool includeCopies, ComScope scope)
    {
        dynamic recipients = scope.Track((object)((dynamic)mail).Recipients);
        var added = new List<object>();
        Add(message.To, OutlookConstants.OlTo);
        if (includeCopies)
        {
            Add(message.Cc, OutlookConstants.OlCc);
            Add(message.Bcc, OutlookConstants.OlBcc);
        }

        if (!refuseUnresolved)
        {
            BestEffort(() => recipients.ResolveAll());
            return;
        }

        if (added.Count == 0)
        {
            throw new OutlookErrors.OutlookStepException(OutlookFailureKind.RecipientUnresolved, OutlookSteps.Recipients);
        }

        bool? all;
        try
        {
            all = (bool)recipients.ResolveAll();
        }
        catch (Exception ex) when (OutlookErrors.Classify(ex, OutlookSteps.Recipients, isElevated: false).Kind == OutlookFailureKind.Unknown)
        {
            // MailerWPiA ignored a failing ResolveAll; the state of every recipient decides below. The object model guard,
            // a busy or a lost Outlook still fail the item.
            all = null;
        }

        var known = 0;
        foreach (var recipient in added)
        {
            bool resolved;
            try
            {
                resolved = (bool)((dynamic)recipient).Resolved;
            }
            catch (Exception)
            {
                continue;
            }

            known++;
            if (!resolved)
            {
                throw new OutlookErrors.OutlookStepException(OutlookFailureKind.RecipientUnresolved, OutlookSteps.Recipients);
            }
        }

        if (known == 0 && all == false)
        {
            throw new OutlookErrors.OutlookStepException(OutlookFailureKind.RecipientUnresolved, OutlookSteps.Recipients);
        }

        void Add(IReadOnlyList<string> addresses, int type)
        {
            foreach (var address in addresses)
            {
                if (string.IsNullOrWhiteSpace(address))
                {
                    continue;
                }

                dynamic recipient = scope.Track((object)recipients.Add(address.Trim()));

                // The type is not best effort here: a Bcc recipient that stayed To would show every address to everyone.
                recipient.Type = type;
                BestEffort(() => recipient.Resolve());
                added.Add((object)recipient);
            }
        }
    }

    /// <summary>Named properties AdMngCampaign / AdMngKeys and the internet headers (best effort, MailerWPiA style).</summary>
    private static void SetProperties(object mail, OutlookMessage message, ComScope scope)
    {
        var campaign = IsCampaignId(message.CampaignId) ? message.CampaignId : null;
        var keys = campaign is null ? string.Empty : JoinKeys(message.CampaignKeys);
        var headers = SafeHeaders(message.InternetHeaders);
        if (campaign is null && headers.Count == 0)
        {
            return;
        }

        object accessor;
        try
        {
            accessor = scope.Track((object)((dynamic)mail).PropertyAccessor);
        }
        catch (Exception)
        {
            return;
        }

        if (campaign is not null)
        {
            TrySetProperty(accessor, MapiProperties.Campaign, campaign);
            if (keys.Length > 0)
            {
                TrySetProperty(accessor, MapiProperties.Keys, keys);
            }
        }

        foreach (var (name, value) in headers)
        {
            TrySetProperty(accessor, MapiProperties.Header(name), value);
        }
    }

    /// <summary>Writes each image to the item's temp folder and adds it at position 0 (not in the attachment list) with its
    /// content id and the hidden flag (MailerWPiA).</summary>
    private void AddImages(object mail, IReadOnlyList<MessageImage> images, ImageFiles files, ComScope scope)
    {
        dynamic attachments = scope.Track((object)((dynamic)mail).Attachments);
        var folder = files.Folder(_tempFiles);
        foreach (var image in images)
        {
            var path = folder.Write(image.FileName, image.Data);
            var fileName = Path.GetFileName(path);
            files.Add(fileName, image);
            dynamic attachment = scope.Track((object)attachments.Add(path, OutlookConstants.OlByValue, 0, fileName));
            object accessor;
            try
            {
                accessor = scope.Track((object)attachment.PropertyAccessor);
            }
            catch (Exception)
            {
                continue;
            }

            TrySetProperty(accessor, MapiProperties.AttachContentId, image.ContentId);
            TrySetProperty(accessor, MapiProperties.AttachmentHidden, true);
        }
    }

    private static void AddAttachments(object mail, IReadOnlyList<OutlookAttachment> files, bool refuseMissing, ComScope scope)
    {
        dynamic attachments = scope.Track((object)((dynamic)mail).Attachments);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
            {
                if (refuseMissing)
                {
                    throw new OutlookErrors.OutlookStepException(OutlookFailureKind.AttachmentMissing, OutlookSteps.Attachments);
                }

                continue;
            }

            object added = string.IsNullOrWhiteSpace(file.DisplayName)
                ? attachments.Add(file.Path)
                : attachments.Add(file.Path, OutlookConstants.OlByValue, Type.Missing, file.DisplayName);
            scope.Track(added);
        }
    }

    /// <summary>After the save: content id, MIME type and the hidden flag again on the saved image attachments (matched by
    /// file name; MailerWPiA <c>MarkInlineImages</c>; PR_ATTACH_FLAGS is blocked by the object model and not needed).</summary>
    private static void MarkInlineImages(object mail, ImageFiles files, ComScope scope)
    {
        dynamic attachments = scope.Track((object)((dynamic)mail).Attachments);
        var count = OutlookProfileReader.GetInt(() => (int)attachments.Count);
        for (var index = 1; index <= count; index++)
        {
            object attachment;
            try
            {
                attachment = scope.Track((object)attachments[index]);
            }
            catch (Exception)
            {
                continue;
            }

            var fileName = OutlookProfileReader.Get(() => (string?)((dynamic)attachment).FileName);
            if (!files.TryGet(fileName, out var image))
            {
                continue;
            }

            object accessor;
            try
            {
                accessor = scope.Track((object)((dynamic)attachment).PropertyAccessor);
            }
            catch (Exception)
            {
                continue;
            }

            TrySetProperty(accessor, MapiProperties.AttachContentId, image.ContentId);
            TrySetProperty(accessor, MapiProperties.AttachMimeTag, image.MediaType);
            TrySetProperty(accessor, MapiProperties.AttachmentHidden, true);
        }
    }

    /// <summary>An attachment with a content id saved to the temp folder, read into memory and deleted; null otherwise.</summary>
    private OutlookRenderedImage? ReadImage(object attachment, ImageFiles files, ComScope scope)
    {
        dynamic item = attachment;
        var accessor = (object?)null;
        var contentId = OutlookProfileReader.Get(() =>
        {
            accessor = scope.Track((object)item.PropertyAccessor);
            return (string?)((dynamic)accessor).GetProperty(MapiProperties.AttachContentId);
        });
        if (contentId.Length == 0)
        {
            return null;
        }

        var fileName = OutlookProfileReader.Get(() => (string?)item.FileName);
        var folder = files.Folder(_tempFiles);
        var path = Path.Combine(folder.Path, folder.Reserve(fileName.Length > 0 ? fileName : "image"));
        try
        {
            item.SaveAsFile(path);
            var data = ReadBounded(path);
            if (data is null)
            {
                return null;
            }

            var mimeTag = accessor is null
                ? string.Empty
                : OutlookProfileReader.Get(() => (string?)((dynamic)accessor).GetProperty(MapiProperties.AttachMimeTag));
            return new OutlookRenderedImage(contentId, MediaTypeOf(data, mimeTag, fileName), data);
        }
        catch (Exception ex) when (OutlookErrors.Classify(ex, OutlookSteps.Render, isElevated: false).Kind == OutlookFailureKind.Unknown)
        {
            // An image that cannot be saved is left out of the preview (MailerWPiA); a lost or busy Outlook still fails it.
            return null;
        }
        finally
        {
            BestEffort(() => File.Delete(path));
        }
    }

    private static byte[]? ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxRenderedImageBytes)
        {
            return null;
        }

        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static string MediaTypeOf(ReadOnlySpan<byte> data, string mimeTag, string fileName)
    {
        if (ImageFormats.Detect(data) is { } kind)
        {
            return ImageFormats.MediaType(kind);
        }

        if (mimeTag.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && MediaTypeToken().IsMatch(mimeTag))
        {
            return mimeTag.ToLowerInvariant();
        }

        return ImageFormats.FromExtension(fileName) is { } byName ? ImageFormats.MediaType(byName) : "application/octet-stream";
    }

    /// <summary>The local time of the PC (DeferredDeliveryTime is a local date of the object model).</summary>
    private DateTime LocalTime(DateTimeOffset value) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(value, _localTimeZone).DateTime, DateTimeKind.Unspecified);

    [GeneratedRegex(@"[\u0000-\u001F\u007F]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacters();

    [GeneratedRegex("^[A-Za-z0-9-]{1,76}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CampaignIdPattern();

    [GeneratedRegex(@"^image/[a-z0-9.+-]{1,40}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MediaTypeToken();

    /// <summary>The temp folder of one item (created on first use) and its images by file name.</summary>
    private sealed class ImageFiles : IDisposable
    {
        private readonly Dictionary<string, MessageImage> _byFileName = new(StringComparer.OrdinalIgnoreCase);
        private OutlookTempFolder? _folder;

        public int Count => _byFileName.Count;

        public OutlookTempFolder Folder(OutlookTempFiles tempFiles) => _folder ??= tempFiles.Create();

        public void Add(string fileName, MessageImage image) => _byFileName[fileName] = image;

        public bool TryGet(string fileName, out MessageImage image) => _byFileName.TryGetValue(fileName, out image!);

        public void Dispose() => _folder?.Dispose();
    }
}
