namespace AdMng.Presentation.Infrastructure;

/// <summary>A value with its localized text, for combo boxes and radio lists (enums are never shown raw).</summary>
public sealed record Option<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
