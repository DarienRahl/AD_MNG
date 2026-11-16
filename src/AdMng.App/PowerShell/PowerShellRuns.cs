using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AdMng.App.Localization;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PowerShell;

namespace AdMng.App.PowerShell;

/// <summary>
/// Draws the pieces of a line of the PowerShell window's scrollback into a <see cref="TextBlock"/> (track R, stage R1, decision D6): the
/// text in the colours <c>Write-Host</c> asked for - mapped to the colours of the theme, so that a colour never makes text unreadable
/// in the dark theme or in High Contrast - and a distinguished name as a link. A click on a link opens the object in the program
/// (<see cref="LinkCommandProperty"/>); its context menu opens it or copies the name. A link is no tab stop (a window of 20,000 lines
/// would be 20,000 of them): a line with a name opens it with Enter, which the window handles.
/// </summary>
public static class PowerShellRuns
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(IReadOnlyList<PowerShellTextRun>), typeof(PowerShellRuns), new PropertyMetadata(null, OnSourceChanged));

    /// <summary>Executed with the distinguished name of a link: <i>Open in the program</i>. Set once on the list; the lines inherit it.</summary>
    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.RegisterAttached(
        "LinkCommand", typeof(ICommand), typeof(PowerShellRuns), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Executed with the distinguished name of a link: <i>Copy the distinguished name</i>.</summary>
    public static readonly DependencyProperty CopyLinkCommandProperty = DependencyProperty.RegisterAttached(
        "CopyLinkCommand", typeof(ICommand), typeof(PowerShellRuns), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static IReadOnlyList<PowerShellTextRun>? GetSource(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IReadOnlyList<PowerShellTextRun>?)element.GetValue(SourceProperty);
    }

    public static void SetSource(DependencyObject element, IReadOnlyList<PowerShellTextRun>? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SourceProperty, value);
    }

    public static ICommand? GetLinkCommand(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ICommand?)element.GetValue(LinkCommandProperty);
    }

    public static void SetLinkCommand(DependencyObject element, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LinkCommandProperty, value);
    }

    public static ICommand? GetCopyLinkCommand(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ICommand?)element.GetValue(CopyLinkCommandProperty);
    }

    public static void SetCopyLinkCommand(DependencyObject element, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(CopyLinkCommandProperty, value);
    }

    /// <summary>The key of the theme brush a console colour is drawn with; null keeps the colour of the line (white, grey and black are the colours of a console, not of this window).</summary>
    internal static string? BrushKeyOf(ConsoleColor color) => color switch
    {
        ConsoleColor.Red or ConsoleColor.DarkRed => "SystemFillColorCriticalBrush",
        ConsoleColor.Yellow or ConsoleColor.DarkYellow => "SystemFillColorCautionBrush",
        ConsoleColor.Green or ConsoleColor.DarkGreen => "SystemFillColorSuccessBrush",
        ConsoleColor.Cyan or ConsoleColor.DarkCyan or ConsoleColor.Blue or ConsoleColor.DarkBlue => "SystemFillColorAttentionBrush",
        ConsoleColor.Magenta or ConsoleColor.DarkMagenta => "AccentTextFillColorPrimaryBrush",
        _ => null,
    };

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<PowerShellTextRun> runs)
        {
            return;
        }

        foreach (var run in runs)
        {
            block.Inlines.Add(run.IsLink ? CreateLink(block, run) : CreateRun(run));
        }
    }

    private static Run CreateRun(PowerShellTextRun piece)
    {
        var run = new Run(piece.Text);
        if (piece.Foreground is { } color && BrushKeyOf(color) is { } key)
        {
            run.SetResourceReference(TextElement.ForegroundProperty, key);
        }

        return run;
    }

    private static Hyperlink CreateLink(TextBlock block, PowerShellTextRun piece)
    {
        var name = piece.DistinguishedName!;
        var localizer = TranslationSource.Localizer;
        var link = new Hyperlink(new Run(piece.Text))
        {
            Focusable = false,
            ToolTip = localizer.Format(L.PowerShell.OpenLinkTip, name),
        };
        link.Click += (_, _) =>
        {
            if (GetLinkCommand(block) is { } command && command.CanExecute(name))
            {
                command.Execute(name);
            }
        };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = localizer[L.PowerShell.OpenInProgram], FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => GetLinkCommand(block)?.Execute(name);
        var copy = new MenuItem { Header = localizer[L.PowerShell.CopyDn] };
        copy.Click += (_, _) => GetCopyLinkCommand(block)?.Execute(name);
        menu.Items.Add(open);
        menu.Items.Add(copy);
        link.ContextMenu = menu;
        return link;
    }
}
