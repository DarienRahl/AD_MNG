using System.Text.Json.Nodes;

namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

// The Adaptive Card 1.4 subset the watchdog emits (section 14.4): Container, ColumnSet/Column, TextBlock and FactSet for
// watchdog-generated text only (they render Markdown links), RichTextBlock/TextRun for every AD-sourced string (TextRun is
// plain text by schema), Action.ToggleVisibility and Action.OpenUrl. No Table, no heading style, no Action.Submit.
// The tiny element records live together in this file (section 3.2).

/// <summary>A body element of an Adaptive Card.</summary>
public abstract record AdaptiveElement
{
    /// <summary>Element id (targets of <see cref="ToggleVisibilityAction"/>).</summary>
    public string? Id { get; init; }

    /// <summary>False = hidden until toggled.</summary>
    public bool? IsVisible { get; init; }

    /// <summary>"none", "small", "medium", "large" …</summary>
    public string? Spacing { get; init; }

    public bool Separator { get; init; }

    /// <summary>The element as card JSON.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Id is not null)
        {
            json["id"] = Id;
        }

        WriteProperties(json);
        if (IsVisible is { } visible)
        {
            json["isVisible"] = visible;
        }

        if (Spacing is not null)
        {
            json["spacing"] = Spacing;
        }

        if (Separator)
        {
            json["separator"] = true;
        }

        return json;
    }

    protected abstract string Type { get; }

    protected abstract void WriteProperties(JsonObject json);
}

/// <summary>Watchdog-generated text (labels, titles, counts) – never AD-sourced strings (TextBlock renders Markdown).</summary>
public sealed record TextBlock(string Text) : AdaptiveElement
{
    /// <summary>"small", "default", "medium", "large", "extraLarge".</summary>
    public string? Size { get; init; }

    /// <summary>"lighter", "default", "bolder".</summary>
    public string? Weight { get; init; }

    public bool IsSubtle { get; init; }

    public bool Wrap { get; init; } = true;

    protected override string Type => "TextBlock";

    protected override void WriteProperties(JsonObject json)
    {
        json["text"] = Text;
        if (Size is not null)
        {
            json["size"] = Size;
        }

        if (Weight is not null)
        {
            json["weight"] = Weight;
        }

        if (IsSubtle)
        {
            json["isSubtle"] = true;
        }

        if (Wrap)
        {
            json["wrap"] = true;
        }
    }
}

/// <summary>One plain-text run (any AD-sourced string goes here).</summary>
public sealed record TextRun(string Text)
{
    public string? Weight { get; init; }

    public bool IsSubtle { get; init; }

    public bool Strikethrough { get; init; }

    public bool Italic { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = "TextRun", ["text"] = Text };
        if (Weight is not null)
        {
            json["weight"] = Weight;
        }

        if (IsSubtle)
        {
            json["isSubtle"] = true;
        }

        if (Strikethrough)
        {
            json["strikethrough"] = true;
        }

        if (Italic)
        {
            json["italic"] = true;
        }

        return json;
    }
}

/// <summary>A block of text runs.</summary>
public sealed record RichTextBlock(IReadOnlyList<TextRun> Inlines) : AdaptiveElement
{
    /// <summary>One run of plain text.</summary>
    public static RichTextBlock Of(string text, string? weight = null, bool isSubtle = false, bool strikethrough = false) =>
        new([new TextRun(text) { Weight = weight, IsSubtle = isSubtle, Strikethrough = strikethrough }]);

    protected override string Type => "RichTextBlock";

    protected override void WriteProperties(JsonObject json) => json["inlines"] = new JsonArray([.. Inlines.Select(i => (JsonNode)i.ToJson())]);
}

/// <summary>A group of elements, optionally styled ("default", "emphasis", "attention", "warning", "accent", "good").</summary>
public sealed record Container(IReadOnlyList<AdaptiveElement> Items) : AdaptiveElement
{
    public string? Style { get; init; }

    public bool Bleed { get; init; }

    protected override string Type => "Container";

    protected override void WriteProperties(JsonObject json)
    {
        if (Style is not null)
        {
            json["style"] = Style;
        }

        if (Bleed)
        {
            json["bleed"] = true;
        }

        json["items"] = new JsonArray([.. Items.Select(i => (JsonNode)i.ToJson())]);
    }
}

/// <summary>One column of a <see cref="ColumnSet"/> with a relative width.</summary>
public sealed record Column(IReadOnlyList<AdaptiveElement> Items, int Width = 1)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = "Column",
        ["width"] = Width,
        ["items"] = new JsonArray([.. Items.Select(i => (JsonNode)i.ToJson())]),
    };
}

/// <summary>Columns side by side (label/value rows, change rows).</summary>
public sealed record ColumnSet(IReadOnlyList<Column> Columns) : AdaptiveElement
{
    protected override string Type => "ColumnSet";

    protected override void WriteProperties(JsonObject json) => json["columns"] = new JsonArray([.. Columns.Select(c => (JsonNode)c.ToJson())]);
}

/// <summary>A watchdog-generated title/value pair (counts, rule names, times).</summary>
public sealed record Fact(string Title, string Value)
{
    public JsonObject ToJson() => new() { ["title"] = Title, ["value"] = Value };
}

/// <summary>Watchdog-generated facts only.</summary>
public sealed record FactSet(IReadOnlyList<Fact> Facts) : AdaptiveElement
{
    protected override string Type => "FactSet";

    protected override void WriteProperties(JsonObject json) => json["facts"] = new JsonArray([.. Facts.Select(f => (JsonNode)f.ToJson())]);
}

/// <summary>A card action.</summary>
public abstract record AdaptiveAction(string Title)
{
    public abstract JsonObject ToJson();
}

/// <summary>Opens the watchdog's HTML event page or service (only with <c>PublicBaseUrl</c>).</summary>
public sealed record OpenUrlAction(string Title, string Url) : AdaptiveAction(Title)
{
    public override JsonObject ToJson() => new() { ["type"] = "Action.OpenUrl", ["title"] = Title, ["url"] = Url };
}

/// <summary>Shows or hides elements by id ("Pokaż wszystkie zmiany").</summary>
public sealed record ToggleVisibilityAction(string Title, IReadOnlyList<string> TargetElements) : AdaptiveAction(Title)
{
    public override JsonObject ToJson() => new()
    {
        ["type"] = "Action.ToggleVisibility",
        ["title"] = Title,
        ["targetElements"] = new JsonArray([.. TargetElements.Select(t => (JsonNode)JsonValue.Create(t))]),
    };
}
