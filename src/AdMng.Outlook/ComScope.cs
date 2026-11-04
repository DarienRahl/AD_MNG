using System.Runtime.InteropServices;

namespace AdMng.Outlook;

/// <summary>
/// The COM objects obtained while working on one item or one read: every object fetched from the late-bound object model is
/// tracked and released in reverse order when the scope ends (MailerWPiA: "przy setkach wiadomości nieoddane obiekty COM
/// potrafią się uzbierać"). Releasing goes through <see cref="ComRelease.Release"/> (only real COM objects) or the
/// releaser of the tests.
/// </summary>
internal sealed class ComScope(Action<object> release) : IDisposable
{
    private readonly List<object> _objects = [];

    /// <summary>Tracks <paramref name="value"/> (null is ignored) and returns it for late-bound use.</summary>
    public dynamic Track(object? value)
    {
        if (value is not null)
        {
            _objects.Add(value);
        }

        return value!;
    }

    /// <summary>Releases the objects tracked so far (reverse order) and empties the scope.</summary>
    public void Dispose()
    {
        for (var index = _objects.Count - 1; index >= 0; index--)
        {
            try
            {
                release(_objects[index]);
            }
            catch (Exception)
            {
                // Releasing never fails the operation.
            }
        }

        _objects.Clear();
    }

    public override string ToString() => $"{nameof(ComScope)} ({_objects.Count})";
}

/// <summary>Release of late-bound objects: <see cref="Marshal.ReleaseComObject"/> only when it is a COM object.</summary>
internal static class ComRelease
{
    /// <summary>Releases one reference of a COM object; CLR objects (the fakes of the tests) and null are ignored.</summary>
    public static void Release(object? value)
    {
        if (value is null || !OperatingSystem.IsWindows() || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
        {
            // Already released.
        }
    }
}
