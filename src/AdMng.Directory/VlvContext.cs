using System.Buffers.Binary;

namespace AdMng.Directory;

/// <summary>
/// The VLV context id handed to callers (<see cref="AdMng.Core.Directory.VirtualListPage.ContextId"/>). A server
/// context only means something on the connection that created it - Samba rejects foreign ones with
/// unavailableCriticalExtension - so the opaque value also records the pool and the connection it came from.
/// </summary>
internal static class VlvContext
{
    private const byte Magic = 0xA7;
    private const int HeaderLength = 1 + 4 + 4;

    /// <summary>Wraps a server context id.</summary>
    public static byte[] Encode(int poolId, int connectionId, ReadOnlySpan<byte> serverContext)
    {
        var result = new byte[HeaderLength + serverContext.Length];
        result[0] = Magic;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1, 4), poolId);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(5, 4), connectionId);
        serverContext.CopyTo(result.AsSpan(HeaderLength));
        return result;
    }

    /// <summary>
    /// Unwraps a context id created by <see cref="Encode"/> for this pool. False for foreign or malformed values,
    /// which are then simply not sent (the server starts a new list).
    /// </summary>
    public static bool TryDecode(byte[]? contextId, int poolId, out int connectionId, out byte[] serverContext)
    {
        connectionId = 0;
        serverContext = [];
        if (contextId is not { Length: > HeaderLength } || contextId[0] != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(contextId.AsSpan(1, 4)) != poolId)
        {
            return false;
        }

        connectionId = BinaryPrimitives.ReadInt32LittleEndian(contextId.AsSpan(5, 4));
        serverContext = contextId[HeaderLength..];
        return true;
    }
}
