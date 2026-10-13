using System.Windows.Controls;
using AdMng.Presentation.Services;

namespace AdMng.App.Services;

/// <summary>
/// <see cref="IPasswordInput"/> over a <see cref="PasswordBox"/>: the password is read only when the connect command
/// runs and the box is cleared after a successful bind. Nothing is cached here.
/// </summary>
public sealed class PasswordBoxInput(PasswordBox box) : IPasswordInput
{
    private readonly PasswordBox _box = box ?? throw new ArgumentNullException(nameof(box));

    public string GetPassword() => _box.Password;

    public void Clear() => _box.Clear();

    /// <summary>Never reveals the password.</summary>
    public override string ToString() => nameof(PasswordBoxInput);
}
