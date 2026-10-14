using System.Globalization;
using System.Text;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>Builds <c>key=value&amp;…</c> query strings with URI-escaped values (shared by the query DTOs).</summary>
internal sealed class QueryStringWriter
{
    private readonly StringBuilder _builder = new();

    public void Add(string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (_builder.Length > 0)
        {
            _builder.Append('&');
        }

        _builder.Append(key).Append('=').Append(Uri.EscapeDataString(value));
    }

    public void Add(string key, long? value) => Add(key, value?.ToString(CultureInfo.InvariantCulture));

    public void Add(string key, DateTimeOffset? value) => Add(key, value?.ToString("O", CultureInfo.InvariantCulture));

    public void AddAll(string key, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            Add(key, value);
        }
    }

    public void AddAll<TEnum>(string key, IEnumerable<TEnum> values)
        where TEnum : struct, Enum
    {
        foreach (var value in values)
        {
            Add(key, value.ToString());
        }
    }

    public override string ToString() => _builder.ToString();
}
