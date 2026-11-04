using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using AdMng.Core.Messaging.Html;
using AdMng.Presentation.Services;

namespace AdMng.App.Services;

/// <summary>
/// Reads what "Wklej sformatowaną treść" needs from the Windows clipboard (U1c, design note u1c-u1d section 2), on the UI
/// thread (the clipboard is STA): the <c>HTML Format</c> (CF_HTML) as <b>raw bytes</b> through Win32 – its offsets are UTF-8
/// byte offsets and WPF's string conversion may decode with the ANSI code page, which breaks Polish letters – with the
/// platform's string as the fallback (parsed in text mode); a <c>PNG</c> (or a DIB converted to PNG in memory); the Unicode
/// text. Nothing is logged or kept; larger than the parser's limit is cut at the limit + 1 byte (refused by the workflow).
/// </summary>
internal static class ClipboardHtmlReader
{
    /// <summary>The CF_HTML clipboard format name.</summary>
    public const string HtmlFormat = "HTML Format";

    /// <summary>The PNG clipboard format of Office, browsers and the Snipping Tool.</summary>
    public const string PngFormat = "PNG";

    private const int OpenAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>The formatted clipboard content, or null when it holds nothing usable.</summary>
    /// <exception cref="ExternalException">The clipboard could not be opened by WPF (another process holds it).</exception>
    public static ClipboardContent? Read()
    {
        var html = ReadRaw(HtmlFormat, CfHtmlParser.MaxBytes + 1);
        string? htmlText = null;
        if (html is null && Clipboard.ContainsData(DataFormats.Html))
        {
            htmlText = Clipboard.GetData(DataFormats.Html) as string;
        }

        var png = ReadRaw(PngFormat, 64 * 1024 * 1024) ?? BitmapAsPng();
        var text = Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        var content = new ClipboardContent(html, htmlText, png, text);
        return content.IsEmpty ? null : content;
    }

    /// <summary>The bytes of a registered clipboard format (OpenClipboard with 5 retries × 50 ms), at most
    /// <paramref name="maxBytes"/>; null when absent or the clipboard stays busy.</summary>
    public static byte[]? ReadRaw(string format, int maxBytes)
    {
        var id = RegisterClipboardFormat(format);
        if (id == 0 || !IsClipboardFormatAvailable(id))
        {
            return null;
        }

        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(RetryDelay);
                continue;
            }

            try
            {
                var handle = GetClipboardData(id);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var size = (long)GlobalSize(handle);
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero || size <= 0)
                {
                    return null;
                }

                try
                {
                    var bytes = new byte[(int)Math.Min(size, maxBytes)];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return null;
    }

    /// <summary>A bitmap (CF_DIBV5 / CF_DIB) of the clipboard as PNG, or null.</summary>
    private static byte[]? BitmapAsPng()
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } bitmap)
        {
            return null;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}

/// <summary>Expands 8.3 short names of an existing local path (<c>C:\Users\JKOWAL~1\…</c>) – the temp folder and Outlook's
/// clipboard image paths often use them (U1c, <see cref="Presentation.UserMessages.PastedImageFileSource"/>).</summary>
internal static class ShortPathNames
{
    /// <summary>The long form of <paramref name="path"/>, or null when it does not exist.</summary>
    public static string? Expand(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var buffer = new char[512];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);
}
