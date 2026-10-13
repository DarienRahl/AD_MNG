using System.Globalization;
using System.Windows.Input;

namespace AdMng.App.Infrastructure;

/// <summary>Keyboard shortcuts of command descriptors (<c>KeyGestureConverter</c> syntax such as "Ctrl+Shift+B").</summary>
public static class ShortcutText
{
    private static readonly (string Name, string Display)[] KeyNames =
    [
        ("OemComma", ","), ("OemPeriod", "."), ("OemPlus", "+"), ("OemMinus", "-"), ("OemQuestion", "/"),
        ("Delete", "Del"), ("Return", "Enter"),
    ];

    /// <summary>Parses a descriptor shortcut; null when empty or not a valid WPF key gesture.</summary>
    public static KeyGesture? TryParse(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return null;
        }

        try
        {
            return new KeyGestureConverter().ConvertFromInvariantString(shortcut) as KeyGesture;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Display text for menus and tool tips ("Ctrl+OemComma" → "Ctrl+,"); empty for no shortcut.</summary>
    public static string Format(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return string.Empty;
        }

        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            foreach (var (name, display) in KeyNames)
            {
                if (string.Equals(parts[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    parts[i] = display;
                }
            }
        }

        return string.Join("+", parts);
    }

    /// <summary>"Title (Ctrl+F)" for tool tips; just the title without a shortcut.</summary>
    public static string WithShortcut(string title, string? shortcut)
    {
        var text = Format(shortcut);
        return text.Length == 0 ? title : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", title, text);
    }
}
