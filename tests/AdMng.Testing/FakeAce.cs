namespace AdMng.Testing;

/// <summary>One access control entry for <see cref="FakeSecurityDescriptor.Build"/>.</summary>
/// <param name="Type">ACE type: 0 allowed, 1 denied, 5 allowed object, 6 denied object.</param>
/// <param name="Flags">ACE flags (0x02 container inherit, 0x10 inherited …).</param>
/// <param name="Mask">Access mask (e.g. 0x100 control access, 0x20 write property, 0xF01FF full control).</param>
/// <param name="Sid">Trustee SID (S-1-…).</param>
/// <param name="ObjectType">Object ACEs: extended right / property GUID.</param>
/// <param name="InheritedObjectType">Object ACEs: inherited object type GUID.</param>
public sealed record FakeAce(byte Type, byte Flags, int Mask, string Sid, Guid? ObjectType = null, Guid? InheritedObjectType = null)
{
    public const byte AccessAllowed = 0x00;
    public const byte AccessDenied = 0x01;
    public const byte AccessAllowedObject = 0x05;
    public const byte AccessDeniedObject = 0x06;

    /// <summary>Full control for a trustee.</summary>
    public static FakeAce FullControl(string sid) => new(AccessAllowed, 0, 0xF01FF, sid);

    /// <summary>An extended right (control access) for a trustee, e.g. DS-Replication-Get-Changes-All.</summary>
    public static FakeAce ExtendedRight(string sid, Guid right) => new(AccessAllowedObject, 0, 0x100, sid, right);
}
