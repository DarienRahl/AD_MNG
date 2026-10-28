using System.Runtime.InteropServices;

namespace AdMng.Outlook.Tests.FakeOutlook;

// A CLR fake of the classic Outlook object model (design note docs/dev/u5-outlook-correspondence.md 4.5). The engine binds
// late ('dynamic'), so public classes with Outlook's member names stand in for the COM objects: 1-based indexers,
// PropertyAccessor, Items.Restrict/Sort, Recipients.ResolveAll. Every call is written to the shared OutlookLog (with the
// thread it ran on), every object handed out by a member is recorded so the tests can check that the engine released each
// one, and any member can be made to throw (COMException with an HRESULT, as the real object model does).

/// <summary>The record of one fake Outlook: calls, the objects handed out and released, and scripted failures.</summary>
public sealed class OutlookLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<int> _threads = [];
    private readonly List<object> _handedOut = [];
    private readonly List<object> _released = [];
    private readonly Dictionary<string, (Exception Exception, int Skip, int Times)> _failures = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The managed thread ids the calls ran on.</summary>
    public IReadOnlyList<int> Threads
    {
        get
        {
            lock (_gate)
            {
                return [.. _threads];
            }
        }
    }

    public IReadOnlyList<object> HandedOut
    {
        get
        {
            lock (_gate)
            {
                return [.. _handedOut];
            }
        }
    }

    public IReadOnlyList<object> Released
    {
        get
        {
            lock (_gate)
            {
                return [.. _released];
            }
        }
    }

    /// <summary>The calls that start with one of <paramref name="prefixes"/>.</summary>
    public IReadOnlyList<string> CallsOf(params string[] prefixes) =>
        [.. Calls.Where(call => prefixes.Any(prefix => call.StartsWith(prefix, StringComparison.Ordinal)))];

    /// <summary>Makes the calls starting with <paramref name="member"/> ("MailItem.Send", "MailItem.Subject=", …) throw
    /// <paramref name="times"/> times after letting <paramref name="skip"/> of them pass.</summary>
    public void Fail(string member, Exception exception, int times = int.MaxValue, int skip = 0)
    {
        lock (_gate)
        {
            _failures[member] = (exception, skip, times);
        }
    }

    /// <summary>Makes <paramref name="member"/> throw a COMException with <paramref name="hresult"/> (its message names an
    /// address, which must never reach a failure).</summary>
    public void FailWith(string member, uint hresult, int times = int.MaxValue, int skip = 0) =>
        Fail(member, new COMException("Fake failure for jan.kowalski@contoso.test", unchecked((int)hresult)), times, skip);

    /// <summary>The releaser handed to the engine.</summary>
    public void Release(object value)
    {
        lock (_gate)
        {
            _released.Add(value);
        }
    }

    internal void Call(string call)
    {
        Exception? failure = null;
        lock (_gate)
        {
            _calls.Add(call);
            _threads.Add(Environment.CurrentManagedThreadId);
            foreach (var (member, (exception, skip, times)) in _failures)
            {
                if (times <= 0 || !call.StartsWith(member, StringComparison.Ordinal))
                {
                    continue;
                }

                if (skip > 0)
                {
                    _failures[member] = (exception, skip - 1, times);
                    break;
                }

                _failures[member] = (exception, 0, times - 1);
                failure = exception;
                break;
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    internal T Out<T>(T value)
        where T : class
    {
        lock (_gate)
        {
            _handedOut.Add(value);
        }

        return value;
    }
}

/// <summary>Outlook.Application.</summary>
public sealed class Application
{
    public Application(OutlookLog? log = null)
    {
        Log = log ?? new OutlookLog();
        NameSpace = new NameSpace(this);
    }

    public OutlookLog Log { get; }

    /// <summary>The fake's own namespace (not logged, not handed out).</summary>
    public NameSpace NameSpace { get; }

    /// <summary>Addresses ResolveAll / Resolve cannot resolve.</summary>
    public HashSet<string> Unresolvable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reading Recipient.Resolved of new recipients throws.</summary>
    public bool ResolvedUnreadable { get; set; }

    /// <summary>Every item created, in order.</summary>
    public List<MailItem> Created { get; } = [];

    /// <summary>Called on every new item (e.g. to make its SendUsingAccount setter fail).</summary>
    public Action<MailItem>? ConfigureItem { get; set; }

    public string Version
    {
        get
        {
            Log.Call("Application.Version");
            return "16.0.0.17928";
        }
    }

    public NameSpace Session
    {
        get
        {
            Log.Call("Application.Session");
            return Log.Out(NameSpace);
        }
    }

    public MailItem CreateItem(int itemType)
    {
        Log.Call($"Application.CreateItem({itemType})");
        var item = new MailItem(this);
        ConfigureItem?.Invoke(item);
        lock (Created)
        {
            Created.Add(item);
        }

        return Log.Out(item);
    }
}

/// <summary>Outlook.NameSpace (Application.Session).</summary>
public sealed class NameSpace(Application application)
{
    private readonly Dictionary<int, Folder> _folders = [];

    public string CurrentProfileName
    {
        get
        {
            application.Log.Call("NameSpace.CurrentProfileName");
            return ProfileName;
        }
    }

    public string ProfileName { get; set; } = "Outlook";

    /// <summary>The current user (null: reading it throws).</summary>
    public Recipient? User { get; set; }

    public Recipient CurrentUser
    {
        get
        {
            application.Log.Call("NameSpace.CurrentUser");
            return application.Log.Out(User ?? throw new COMException("No current user.", unchecked((int)0x8004010F)));
        }
    }

    public Accounts AccountList { get; } = new(application);

    public Accounts Accounts
    {
        get
        {
            application.Log.Call("NameSpace.Accounts");
            return application.Log.Out(AccountList);
        }
    }

    public Folder SentItems => Folder(5);

    public Folder Folder(int id)
    {
        lock (_folders)
        {
            if (!_folders.TryGetValue(id, out var folder))
            {
                _folders[id] = folder = new Folder(application);
            }

            return folder;
        }
    }

    public Folder GetDefaultFolder(int folderType)
    {
        application.Log.Call($"NameSpace.GetDefaultFolder({folderType})");
        return application.Log.Out(Folder(folderType));
    }
}

/// <summary>Outlook.Accounts (1-based).</summary>
public sealed class Accounts(Application application)
{
    public List<Account> Items { get; } = [];

    public int Count
    {
        get
        {
            application.Log.Call("Accounts.Count");
            return Items.Count;
        }
    }

    public Account this[int index]
    {
        get
        {
            application.Log.Call($"Accounts[{index}]");
            return application.Log.Out(Items[index - 1]);
        }
    }

    public Account Add(string displayName, string? smtpAddress, Recipient? currentUser = null)
    {
        var account = new Account(application) { DisplayName = displayName, SmtpAddress = smtpAddress, User = currentUser };
        Items.Add(account);
        return account;
    }
}

/// <summary>Outlook.Account.</summary>
public sealed class Account(Application application)
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The SMTP address (null: reading it throws, as for some account types).</summary>
    public string? SmtpAddress
    {
        get => field ?? throw new COMException("No SMTP address.", unchecked((int)0x8004010F));
        set;
    }

    public Recipient? User { get; set; }

    public Recipient CurrentUser => application.Log.Out(User ?? throw new COMException("No user.", unchecked((int)0x8004010F)));

    public override string ToString() => $"Account({DisplayName})";
}

/// <summary>Outlook.Recipient.</summary>
public sealed class Recipient(Application application)
{
    private bool _resolved;

    public string Address { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Type
    {
        get;
        set
        {
            application.Log.Call($"Recipient.Type={value}");
            field = value;
        }
    }

    /// <summary>Reading Resolved throws when set.</summary>
    public bool ResolvedThrows { get; set; }

    public bool Resolved
    {
        get => ResolvedThrows ? throw new COMException("Busy.", unchecked((int)0x80004005)) : _resolved;
        set => _resolved = value;
    }

    public AddressEntry? Entry { get; set; }

    public AddressEntry AddressEntry => application.Log.Out(Entry ?? throw new COMException("No entry.", unchecked((int)0x8004010F)));

    public string AutoResponse { get; set; } = string.Empty;

    public int TrackingStatus { get; set; }

    public DateTime TrackingStatusTime { get; set; } = new(4501, 1, 1);

    public PropertyAccessor Accessor { get; } = new(application, "Recipient");

    public PropertyAccessor PropertyAccessor => application.Log.Out(Accessor);

    public bool Resolve()
    {
        application.Log.Call($"Recipient.Resolve({Address})");
        _resolved = !application.Unresolvable.Contains(Address);
        return _resolved;
    }

    internal void ResolveQuietly() => _resolved = !application.Unresolvable.Contains(Address);
}

/// <summary>Outlook.AddressEntry.</summary>
public sealed class AddressEntry(Application application)
{
    public ExchangeUser? Exchange { get; set; }

    public ExchangeUser? GetExchangeUser() => Exchange is null ? null : application.Log.Out(Exchange);
}

/// <summary>Outlook.ExchangeUser.</summary>
public sealed class ExchangeUser
{
    public string PrimarySmtpAddress { get; set; } = string.Empty;
}

/// <summary>Outlook.Recipients (1-based).</summary>
public sealed class Recipients(Application application, string name)
{
    public List<Recipient> Items { get; } = [];

    public int Count
    {
        get
        {
            application.Log.Call($"{name}.Count");
            return Items.Count;
        }
    }

    public Recipient this[int index] => application.Log.Out(Items[index - 1]);

    public Recipient Add(string address)
    {
        application.Log.Call($"{name}.Add({address})");
        var recipient = new Recipient(application) { Address = address, Name = address, ResolvedThrows = application.ResolvedUnreadable };
        Items.Add(recipient);
        return application.Log.Out(recipient);
    }

    public bool ResolveAll()
    {
        application.Log.Call($"{name}.ResolveAll()");
        foreach (var recipient in Items)
        {
            recipient.ResolveQuietly();
        }

        return Items.All(recipient => !application.Unresolvable.Contains(recipient.Address));
    }
}

/// <summary>Outlook.PropertyAccessor: values by schema name; reading a missing one throws (MAPI_E_NOT_FOUND).</summary>
public sealed class PropertyAccessor(Application application, string owner)
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public void SetProperty(string schemaName, object value)
    {
        application.Log.Call($"{owner}.PropertyAccessor.SetProperty({schemaName})");
        Values[schemaName] = value;
    }

    public object GetProperty(string schemaName)
    {
        application.Log.Call($"{owner}.PropertyAccessor.GetProperty({schemaName})");
        return Values.TryGetValue(schemaName, out var value) ? value : throw new COMException("Not found.", unchecked((int)0x8004010F));
    }
}

/// <summary>Outlook.Attachments (1-based).</summary>
public sealed class Attachments(Application application)
{
    public List<Attachment> Items { get; } = [];

    public int Count
    {
        get
        {
            application.Log.Call("Attachments.Count");
            return Items.Count;
        }
    }

    public Attachment this[int index]
    {
        get
        {
            application.Log.Call($"Attachments[{index}]");
            return application.Log.Out(Items[index - 1]);
        }
    }

    /// <summary>Copies the file into the item (as Outlook does): the bytes are read now, the source may go afterwards.</summary>
    public Attachment Add(object source, object? type = null, object? position = null, object? displayName = null)
    {
        var path = (string)source;
        var fileName = Path.GetFileName(path);
        application.Log.Call($"Attachments.Add({fileName},{Describe(type)},{Describe(position)},{Describe(displayName)})");
        var attachment = new Attachment(application)
        {
            FileName = fileName,
            SourcePath = path,
            Data = File.ReadAllBytes(path),
            Position = position,
            DisplayName = displayName as string,
        };
        Items.Add(attachment);
        return application.Log.Out(attachment);
    }

    private static string Describe(object? value) => value switch
    {
        null => "-",
        System.Reflection.Missing => "missing",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "-",
    };
}

/// <summary>Outlook.Attachment.</summary>
public sealed class Attachment(Application application)
{
    public string FileName { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];

    public object? Position { get; set; }

    public string? DisplayName { get; set; }

    public PropertyAccessor Accessor { get; } = new(application, "Attachment");

    public PropertyAccessor PropertyAccessor => application.Log.Out(Accessor);

    public void SaveAsFile(string path)
    {
        application.Log.Call($"Attachment.SaveAsFile({FileName})");
        File.WriteAllBytes(path, Data);
    }
}

/// <summary>Outlook.MailItem.</summary>
public sealed class MailItem
{
    private readonly Application _application;

    public MailItem(Application application)
    {
        _application = application;
        RecipientList = new Recipients(application, "Recipients");
        ReplyList = new Recipients(application, "ReplyRecipients");
        AttachmentList = new Attachments(application);
        Accessor = new PropertyAccessor(application, "MailItem");
    }

    public Recipients RecipientList { get; }

    public Recipients ReplyList { get; }

    public Attachments AttachmentList { get; }

    public PropertyAccessor Accessor { get; }

    public int SaveCount { get; private set; }

    public bool WasSent { get; private set; }

    public bool WasDeleted { get; private set; }

    public int? ClosedWith { get; private set; }

    public int BodyFormat
    {
        get;
        set
        {
            Call($"MailItem.BodyFormat={value}");
            field = value;
        }
    }

    public object? SendUsingAccount
    {
        get;
        set
        {
            Call($"MailItem.SendUsingAccount={value}");
            field = value;
        }
    }

    public string SentOnBehalfOfName
    {
        get;
        set
        {
            Call($"MailItem.SentOnBehalfOfName={value}");
            field = value;
        }
    } = string.Empty;

    public Recipients Recipients => _application.Log.Out(RecipientList);

    public Recipients ReplyRecipients => _application.Log.Out(ReplyList);

    public string Subject
    {
        get;
        set
        {
            Call($"MailItem.Subject={value}");
            field = value;
        }
    } = string.Empty;

    /// <summary>What reading HTMLBody returns after the save (Outlook rewrites the HTML); null: the HTML as set.</summary>
    public string? RenderedHtml { get; set; }

    public string HTMLBody
    {
        get => SaveCount > 0 && RenderedHtml is not null ? RenderedHtml : field;
        set
        {
            Call("MailItem.HTMLBody=");
            field = value;
        }
    } = string.Empty;

    public bool ReadReceiptRequested
    {
        get;
        set
        {
            Call($"MailItem.ReadReceiptRequested={value}");
            field = value;
        }
    }

    public DateTime DeferredDeliveryTime
    {
        get;
        set
        {
            Call($"MailItem.DeferredDeliveryTime={value:yyyy-MM-dd HH:mm}");
            field = value;
        }
    } = new(4501, 1, 1);

    public string VotingOptions
    {
        get;
        set
        {
            Call($"MailItem.VotingOptions={value}");
            field = value;
        }
    } = string.Empty;

    public int Importance
    {
        get;
        set
        {
            Call($"MailItem.Importance={value}");
            field = value;
        }
    } = 1;

    public DateTime SentOn { get; set; } = new(4501, 1, 1);

    public PropertyAccessor PropertyAccessor => _application.Log.Out(Accessor);

    public Attachments Attachments => _application.Log.Out(AttachmentList);

    public void Save()
    {
        Call("MailItem.Save()");
        SaveCount++;
    }

    public void Send()
    {
        Call("MailItem.Send()");
        WasSent = true;
    }

    public void Close(int saveMode)
    {
        Call($"MailItem.Close({saveMode})");
        ClosedWith = saveMode;
    }

    public void Delete()
    {
        Call("MailItem.Delete()");
        WasDeleted = true;
    }

    private void Call(string call) => _application.Log.Call(call);
}

/// <summary>Outlook.Folder.</summary>
public sealed class Folder(Application application)
{
    public FakeItems All { get; } = new(application, []);

    public FakeItems Items => application.Log.Out(All);
}

/// <summary>
/// Outlook.Items (1-based) with Restrict and Sort. Restrict understands the two filters of the engine: "IS NOT NULL" on a
/// named property keeps the items that have it, "= 'value'" the items whose value is equal (the date clause is not
/// evaluated: the engine checks SentOn itself).
/// </summary>
public sealed class FakeItems(Application application, List<MailItem> items)
{
    public List<MailItem> List { get; } = items;

    public List<string> Filters { get; } = [];

    public int Count
    {
        get
        {
            application.Log.Call("Items.Count");
            return List.Count;
        }
    }

    public MailItem this[int index]
    {
        get
        {
            application.Log.Call($"Items[{index}]");
            return application.Log.Out(List[index - 1]);
        }
    }

    public FakeItems Restrict(string filter)
    {
        application.Log.Call("Items.Restrict(" + filter + ")");
        Filters.Add(filter);
        var property = filter.Split('"')[1];
        var equals = System.Text.RegularExpressions.Regex.Match(filter, "\" = '([^']*)'");
        var kept = List.Where(item => item.Accessor.Values.TryGetValue(property, out var value)
            && (!equals.Success || string.Equals(value as string, equals.Groups[1].Value, StringComparison.Ordinal)));
        return application.Log.Out(new FakeItems(application, [.. kept]));
    }

    public void Sort(string property, bool descending)
    {
        application.Log.Call($"Items.Sort({property},{descending})");
        List.Sort((a, b) => descending ? b.SentOn.CompareTo(a.SentOn) : a.SentOn.CompareTo(b.SentOn));
    }
}

/// <summary>An application provider of fakes: attach / start answers scripted per call.</summary>
internal sealed class FakeApplicationProvider(Application application) : IOutlookApplicationProvider
{
    private readonly Queue<Exception> _attachFailures = new();
    private readonly Queue<Exception> _startFailures = new();

    public Application Application { get; } = application;

    public int AttachCalls { get; private set; }

    public int StartCalls { get; private set; }

    public List<int> Threads { get; } = [];

    /// <summary>The clock of <see cref="AttachTimes"/> (the harness's manual clock).</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>When each attach was attempted.</summary>
    public List<DateTimeOffset> AttachTimes { get; } = [];

    /// <summary>Attach throws (Outlook not running) unless enabled.</summary>
    public bool Running { get; set; } = true;

    public void FailAttach(params Exception[] failures)
    {
        foreach (var failure in failures)
        {
            _attachFailures.Enqueue(failure);
        }
    }

    public void FailStart(params Exception[] failures)
    {
        foreach (var failure in failures)
        {
            _startFailures.Enqueue(failure);
        }
    }

    public object Attach()
    {
        AttachCalls++;
        Threads.Add(Environment.CurrentManagedThreadId);
        if (Clock is not null)
        {
            AttachTimes.Add(Clock());
        }

        if (_attachFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        return Running ? Application : throw new COMException("Operation unavailable.", unchecked((int)0x800401E3));
    }

    public object Start()
    {
        StartCalls++;
        Threads.Add(Environment.CurrentManagedThreadId);
        if (_startFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        Running = true;
        return Application;
    }
}
