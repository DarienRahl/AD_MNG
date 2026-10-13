using System.Windows;

namespace AdMng.App.Controls;

/// <summary>
/// Makes a DataContext reachable from places outside the visual tree (context menus, item container styles):
/// declare <c>&lt;c:BindingProxy x:Key="ShellProxy" Data="{Binding}"/&gt;</c> in the window resources and bind with
/// <c>{Binding Data.SomeCommand, Source={StaticResource ShellProxy}}</c>. Freezables in resources inherit the
/// DataContext of their owner.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
