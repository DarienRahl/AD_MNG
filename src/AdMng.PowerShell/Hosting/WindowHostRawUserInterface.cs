using System.Management.Automation.Host;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The minimal raw user interface of the window: the engine's formatting needs a width (tables and lists wrap to it), colours
/// and a few sizes; there is no cursor and no key reading (nothing in R1 asks for either).
/// </summary>
internal sealed class WindowHostRawUserInterface : PSHostRawUserInterface
{
    private const int BufferHeight = 3000;
    private const int WindowHeight = 40;
    private int _width;

    public WindowHostRawUserInterface(int width) => Width = width;

    /// <summary>The columns the formatting uses; settable while the window is resized.</summary>
    public int Width
    {
        get => Volatile.Read(ref _width);
        set => Volatile.Write(ref _width, Clamp(value));
    }

    /// <summary>The narrowest and the widest the formatting is made: 40 to 400 columns.</summary>
    public static int Clamp(int width) => Math.Clamp(width, 40, 400);

    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;

    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;

    public override Coordinates CursorPosition { get; set; }

    public override int CursorSize { get; set; } = 25;

    public override Size BufferSize
    {
        get => new(Width, BufferHeight);
        set => Width = value.Width;
    }

    public override Coordinates WindowPosition { get; set; }

    public override Size WindowSize
    {
        get => new(Width, WindowHeight);
        set => Width = value.Width;
    }

    public override Size MaxWindowSize => new(Width, WindowHeight);

    public override Size MaxPhysicalWindowSize => new(400, BufferHeight);

    public override bool KeyAvailable => false;

    public override string WindowTitle { get; set; } = "AD Manager PowerShell";

    public override void FlushInputBuffer()
    {
    }

    public override KeyInfo ReadKey(ReadKeyOptions options) =>
        throw new NotSupportedException("The AD Manager PowerShell window does not read single keys.");

    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents)
    {
    }

    /// <summary>Called when a command clears the whole buffer (<c>Clear-Host</c>): the host's user interface clears the window.</summary>
    public Action? Cleared { get; set; }

    /// <summary>
    /// The rectangle -1, -1, -1, -1 is the whole buffer: <c>Clear-Host</c> asks for it (the window's own command, and the engine's
    /// function on Windows), and the window clears its output. Any other rectangle has nothing to fill: the window has no buffer.
    /// </summary>
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill)
    {
        if (rectangle is { Left: -1, Top: -1, Right: -1, Bottom: -1 })
        {
            Cleared?.Invoke();
        }
    }

    public override BufferCell[,] GetBufferContents(Rectangle rectangle) => new BufferCell[0, 0];

    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill)
    {
    }
}
