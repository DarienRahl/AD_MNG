using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace AdMng.Watchdog.Keytab;

/// <summary>
/// A password in a pinned byte array (NUL-terminated for libkrb5), zeroed on <see cref="Dispose"/> – the only place a
/// password of the keytab tool ever lives. Never converted to a string, never printed.
/// </summary>
internal sealed unsafe class SecretBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private bool _disposed;

    public SecretBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = GC.AllocateArray<byte>(capacity + 1, pinned: true);
    }

    public int Length { get; private set; }

    public int Capacity => _buffer.Length - 1;

    /// <summary>Pointer to the NUL-terminated bytes (the array is pinned for its lifetime).</summary>
    public byte* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (byte*)Unsafe.AsPointer(ref _buffer[0]);
        }
    }

    public ReadOnlySpan<byte> Span => _disposed ? throw new ObjectDisposedException(nameof(SecretBuffer)) : _buffer.AsSpan(0, Length);

    /// <summary>Appends one byte; false when the buffer is full.</summary>
    public bool Append(byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Length >= Capacity)
        {
            return false;
        }

        _buffer[Length++] = value;
        _buffer[Length] = 0;
        return true;
    }

    /// <summary>Removes the last byte (backspace at the prompt).</summary>
    public void RemoveLast()
    {
        if (Length > 0)
        {
            _buffer[--Length] = 0;
        }
    }

    public void Clear()
    {
        CryptographicOperations.ZeroMemory(_buffer);
        Length = 0;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_buffer);
        Length = 0;
        _disposed = true;
    }

    public override string ToString() => "SecretBuffer(redacted)";
}
