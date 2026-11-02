using System.Globalization;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// Send runs and their voting responses in "Elementy wysłane" (design note docs/dev/u5-outlook-correspondence.md 4.4): every
/// item sent with a campaign id carries the named properties <c>AdMngCampaign</c> and <c>AdMngKeys</c> (PS_PUBLIC_STRINGS);
/// the Sent Items folder is searched with <c>Items.Restrict</c> (a DASL filter) and the votes are read per recipient
/// (<c>AutoResponse</c>, <c>TrackingStatus</c>, <c>TrackingStatusTime</c>). Nothing is stored locally. Runs on the worker.
/// </summary>
internal sealed class OutlookCampaigns
{
    /// <summary>The most items read in one search (a runaway guard).</summary>
    public const int MaxItems = 100_000;

    /// <summary>Outlook's "no date" (<c>4501-01-01</c>): every date from this year on means none.</summary>
    private const int NoDateYear = 4500;

    private readonly object _application;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly Action<object> _release;

    public OutlookCampaigns(object application, TimeZoneInfo localTimeZone, Action<object> release)
    {
        _application = application;
        _localTimeZone = localTimeZone;
        _release = release;
    }

    /// <summary>A new campaign id: 32 lower-case hex characters.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>The DASL filter of the items of every campaign sent since <paramref name="since"/> (UTC, minutes).</summary>
    public static string CampaignsFilter(DateTimeOffset since) =>
        "@SQL=\"" + MapiProperties.Campaign + "\" IS NOT NULL AND \"urn:schemas:httpmail:date\" >= '"
        + since.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "'";

    /// <summary>The DASL filter of the items of one campaign (the id is validated, so it needs no quoting).</summary>
    public static string ResponsesFilter(string campaignId) => "@SQL=\"" + MapiProperties.Campaign + "\" = '" + campaignId + "'";

    /// <summary>The campaigns sent since <paramref name="since"/>, newest first: id, the subject of the first item, first and
    /// last <c>SentOn</c>, the number of items.</summary>
    /// <exception cref="OutlookException">The folder or the search failed (classified, step campaigns).</exception>
    public IReadOnlyList<OutlookCampaign> Find(DateTimeOffset since)
    {
        var scope = new ComScope(_release);
        try
        {
            var groups = new Dictionary<string, CampaignBuilder>(StringComparer.OrdinalIgnoreCase);
            ForEachItem(CampaignsFilter(since), scope, (item, accessor, _) =>
            {
                var id = ReadString(accessor, MapiProperties.Campaign);
                if (!OutlookMailBuilder.IsCampaignId(id) || ReadDate(item, "SentOn") is not { } sentOn || sentOn < since)
                {
                    return;
                }

                var subject = OutlookProfileReader.Get(() => (string?)((dynamic)item).Subject);
                if (!groups.TryGetValue(id, out var group))
                {
                    groups[id] = group = new CampaignBuilder(id);
                }

                group.Add(subject, sentOn);
            });

            return [.. groups.Values.Select(group => group.Build()).OrderByDescending(campaign => campaign.LastSent).ThenBy(campaign => campaign.Id, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OutlookException)
        {
            throw new OutlookException(OutlookErrors.Classify(ex, OutlookSteps.Campaigns, isElevated: false));
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// The responses of one campaign: per recipient of each item the vote (<c>AutoResponse</c>, null when none), when it
    /// came (<c>TrackingStatusTime</c>; null without a vote or with Outlook's "no date"), the item's <c>SentOn</c>, subject and
    /// keys. In a Bcc message a To recipient that is one of the sender's own mailboxes (<paramref name="ownAddresses"/>:
    /// the accounts of the profile and its current user) is left out.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a campaign id.</exception>
    /// <exception cref="OutlookException">The folder or the search failed (classified, step responses).</exception>
    public IReadOnlyList<OutlookVoteResponse> Responses(string campaignId, IReadOnlySet<string> ownAddresses)
    {
        ArgumentNullException.ThrowIfNull(ownAddresses);
        if (!OutlookMailBuilder.IsCampaignId(campaignId))
        {
            throw new ArgumentException("Not a campaign id.", nameof(campaignId));
        }

        var scope = new ComScope(_release);
        try
        {
            var responses = new List<OutlookVoteResponse>();
            ForEachItem(ResponsesFilter(campaignId), scope, (item, accessor, itemScope) =>
            {
                var id = ReadString(accessor, MapiProperties.Campaign);
                if (!string.Equals(id, campaignId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var sentOn = ReadDate(item, "SentOn") ?? DateTimeOffset.MinValue;
                var subject = OutlookProfileReader.Get(() => (string?)((dynamic)item).Subject);
                IReadOnlyList<string> keys = ReadString(accessor, MapiProperties.Keys)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                responses.AddRange(Recipients(item, ownAddresses, sentOn, subject, keys, itemScope));
            });

            return responses;
        }
        catch (Exception ex) when (ex is not OutlookException)
        {
            throw new OutlookException(OutlookErrors.Classify(ex, OutlookSteps.Responses, isElevated: false));
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Reads the items of Sent Items matching <paramref name="filter"/>, sorted by <c>SentOn</c>, one at a time: each item, its
    /// property accessor (null when unreadable) and every object <paramref name="read"/> tracks in the item's scope are
    /// released before the next item is opened. Exchange in online mode counts every open item against a limit of objects
    /// a session may hold at once (about 250), and a cached store would keep every one of them in memory (MailerWPiA: "przy
    /// setkach wiadomości nieoddane obiekty COM potrafią się uzbierać"). An item that cannot be opened (deleted since the
    /// search) is skipped; a busy or lost Outlook fails the read.
    /// </summary>
    private void ForEachItem(string filter, ComScope scope, Action<object, object?, ComScope> read)
    {
        dynamic app = _application;
        dynamic session = scope.Track((object)app.Session);
        dynamic folder = scope.Track((object)session.GetDefaultFolder(OutlookConstants.OlFolderSentMail));
        dynamic all = scope.Track((object)folder.Items);
        dynamic found = scope.Track((object)all.Restrict(filter));
        try
        {
            found.Sort("[SentOn]", false);
        }
        catch (Exception)
        {
            // Unsorted is fine: the groups are ordered afterwards.
        }

        var count = Math.Min((int)found.Count, MaxItems);
        for (var index = 1; index <= count; index++)
        {
            var itemScope = new ComScope(_release);
            try
            {
                object item;
                try
                {
                    item = itemScope.Track((object)found[index]);
                }
                catch (Exception ex) when (OutlookErrors.Classify(ex, null, isElevated: false).Kind == OutlookFailureKind.Unknown)
                {
                    continue;
                }

                object? accessor = null;
                try
                {
                    accessor = itemScope.Track((object)((dynamic)item).PropertyAccessor);
                }
                catch (Exception)
                {
                    // Without the accessor the item has no readable campaign: skipped by the callers.
                }

                read(item, accessor, itemScope);
            }
            finally
            {
                itemScope.Dispose();
            }
        }
    }

    /// <summary>The rows of one item's recipients; each recipient and what was read through it are released before the
    /// next one is fetched.</summary>
    private List<OutlookVoteResponse> Recipients(object item, IReadOnlySet<string> ownAddresses, DateTimeOffset sentOn, string subject, IReadOnlyList<string> keys, ComScope scope)
    {
        dynamic recipients = scope.Track((object)((dynamic)item).Recipients);
        var count = OutlookProfileReader.GetInt(() => (int)recipients.Count);
        var rows = new List<(int Type, string Address, string? Response, DateTimeOffset? At)>();
        for (var index = 1; index <= count; index++)
        {
            var recipientScope = new ComScope(_release);
            try
            {
                object recipient;
                try
                {
                    recipient = recipientScope.Track((object)recipients[index]);
                }
                catch (Exception)
                {
                    continue;
                }

                dynamic entry = recipient;
                var address = AddressOf(recipient, recipientScope);
                var type = OutlookProfileReader.GetInt(() => (int)entry.Type);
                var vote = OutlookVotingOptions.RestoreVote(OutlookProfileReader.Get(() => (string?)entry.AutoResponse));
                var response = vote.Length > 0 ? vote : null;
                DateTimeOffset? at = null;
                if (response is not null)
                {
                    at = ReadDate(recipient, "TrackingStatusTime");
                }

                rows.Add((type, address, response, at));
            }
            finally
            {
                recipientScope.Dispose();
            }
        }

        var hasBcc = rows.Any(row => row.Type == OutlookConstants.OlBcc);
        return
        [
            .. rows
                .Where(row => !(hasBcc && row.Type == OutlookConstants.OlTo && ownAddresses.Contains(row.Address)))
                .Select(row => new OutlookVoteResponse(row.Address, row.Response, row.At, sentOn, subject, keys)),
        ];
    }

    /// <summary>A recipient's SMTP address: <c>Address</c> when it is one, else PR_SMTP_ADDRESS, else the Exchange user's
    /// primary address, else the name Outlook shows.</summary>
    private static string AddressOf(object recipient, ComScope scope)
    {
        dynamic entry = recipient;
        var address = OutlookProfileReader.Get(() => (string?)entry.Address);
        if (OutlookProfileReader.IsAddress(address))
        {
            return address;
        }

        var smtp = OutlookProfileReader.Get(() =>
            (string?)scope.Track((object)entry.PropertyAccessor).GetProperty(MapiProperties.SmtpAddress));
        if (OutlookProfileReader.IsAddress(smtp))
        {
            return smtp;
        }

        var exchange = OutlookProfileReader.Get(() =>
        {
            dynamic addressEntry = scope.Track((object)entry.AddressEntry);
            object? user = addressEntry.GetExchangeUser();
            return user is null ? null : (string?)scope.Track(user).PrimarySmtpAddress;
        });
        if (OutlookProfileReader.IsAddress(exchange))
        {
            return exchange;
        }

        var name = OutlookProfileReader.Get(() => (string?)entry.Name);
        return name.Length > 0 ? name : address;
    }

    private static string ReadString(object? accessor, string property) =>
        accessor is null ? string.Empty : OutlookProfileReader.Get(() => (string?)((dynamic)accessor).GetProperty(property));

    /// <summary>A local date property of the object model as a <see cref="DateTimeOffset"/>; null when missing or "no date".</summary>
    private DateTimeOffset? ReadDate(object item, string property)
    {
        DateTime value;
        try
        {
            value = property switch
            {
                "SentOn" => (DateTime)((dynamic)item).SentOn,
                "TrackingStatusTime" => (DateTime)((dynamic)item).TrackingStatusTime,
                _ => throw new ArgumentOutOfRangeException(nameof(property)),
            };
        }
        catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
        {
            return null;
        }

        if (value.Year >= NoDateYear || value == DateTime.MinValue)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _localTimeZone.GetUtcOffset(local));
    }

    private sealed class CampaignBuilder(string id)
    {
        private string _subject = string.Empty;
        private DateTimeOffset _first = DateTimeOffset.MaxValue;
        private DateTimeOffset _last = DateTimeOffset.MinValue;
        private int _count;

        public void Add(string subject, DateTimeOffset sentOn)
        {
            if (sentOn < _first)
            {
                _first = sentOn;
                _subject = subject;
            }

            if (sentOn > _last)
            {
                _last = sentOn;
            }

            _count++;
        }

        public OutlookCampaign Build() => new(id, _subject, _first, _last, _count);
    }
}
