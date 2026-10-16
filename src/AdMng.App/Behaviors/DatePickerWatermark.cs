using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AdMng.App.Behaviors;

/// <summary>
/// Placeholder text of a <see cref="DatePicker"/> from the application's own localization (e.g.
/// <c>b:DatePickerWatermark.Text="{l:Tr Ui.SelectDate}"</c>). WPF's built-in "Select a date" follows the language
/// resources installed with .NET, not the application language, and its watermark property is internal; the text is
/// put into the template's watermark part instead and follows language switches through the binding.
/// </summary>
internal static class DatePickerWatermark
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(DatePickerWatermark),
        new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(TextProperty);
    }

    public static void SetText(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DatePicker picker)
        {
            return;
        }

        picker.Loaded -= OnLoaded;
        picker.Loaded += OnLoaded;
        Apply(picker);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((DatePicker)sender);

    private static void Apply(DatePicker picker)
    {
        if (GetText(picker) is not { } text
            || picker.Template?.FindName("PART_TextBox", picker) is not DatePickerTextBox textBox)
        {
            return;
        }

        textBox.ApplyTemplate();
        if (textBox.Template?.FindName("PART_Watermark", textBox) is ContentControl watermark)
        {
            // Replaces the template's binding to the internal Watermark property, so later resets by the DatePicker
            // (format or culture changes) do not bring the framework text back.
            watermark.Content = text;
        }
    }
}
