namespace AdMng.Core.ActiveDirectory;

/// <summary>userAccountControl flags ([MS-ADTS] 2.2.16).</summary>
[Flags]
public enum UserAccountControl
{
    None = 0,
    Script = 0x0001,
    AccountDisable = 0x0002,
    HomeDirRequired = 0x0008,
    Lockout = 0x0010,
    PasswordNotRequired = 0x0020,
    PasswordCantChange = 0x0040,
    EncryptedTextPasswordAllowed = 0x0080,
    TempDuplicateAccount = 0x0100,
    NormalAccount = 0x0200,
    InterdomainTrustAccount = 0x0800,
    WorkstationTrustAccount = 0x1000,
    ServerTrustAccount = 0x2000,
    DontExpirePassword = 0x10000,
    MnsLogonAccount = 0x20000,
    SmartcardRequired = 0x40000,
    TrustedForDelegation = 0x80000,
    NotDelegated = 0x100000,
    UseDesKeyOnly = 0x200000,
    DontRequirePreauth = 0x400000,
    PasswordExpired = 0x800000,
    TrustedToAuthForDelegation = 0x1000000,
    PartialSecretsAccount = 0x4000000,
}

/// <summary>groupType flags ([MS-ADTS] 2.2.12).</summary>
[Flags]
public enum GroupTypeFlags : uint
{
    None = 0,
    BuiltinLocal = 0x00000001,
    Global = 0x00000002,
    DomainLocal = 0x00000004,
    Universal = 0x00000008,
    AppBasic = 0x00000010,
    AppQuery = 0x00000020,
    Security = 0x80000000,
}

/// <summary>Group scope as shown in ADUC.</summary>
public enum GroupScope
{
    Unknown,
    DomainLocal,
    Global,
    Universal,
    BuiltinLocal,
}

/// <summary>Helpers around the groupType attribute.</summary>
public static class GroupTypes
{
    /// <summary>Decodes scope from the raw (signed) groupType value.</summary>
    public static GroupScope GetScope(int groupType)
    {
        var flags = (GroupTypeFlags)unchecked((uint)groupType);
        if (flags.HasFlag(GroupTypeFlags.BuiltinLocal))
        {
            return GroupScope.BuiltinLocal;
        }

        if (flags.HasFlag(GroupTypeFlags.Universal))
        {
            return GroupScope.Universal;
        }

        if (flags.HasFlag(GroupTypeFlags.DomainLocal))
        {
            return GroupScope.DomainLocal;
        }

        return flags.HasFlag(GroupTypeFlags.Global) ? GroupScope.Global : GroupScope.Unknown;
    }

    /// <summary>True for security groups, false for distribution groups.</summary>
    public static bool IsSecurity(int groupType) =>
        ((GroupTypeFlags)unchecked((uint)groupType)).HasFlag(GroupTypeFlags.Security);

    /// <summary>Composes a groupType value as stored in AD (signed 32-bit).</summary>
    public static int Compose(GroupScope scope, bool security)
    {
        var flags = scope switch
        {
            GroupScope.Global => GroupTypeFlags.Global,
            GroupScope.DomainLocal => GroupTypeFlags.DomainLocal,
            GroupScope.Universal => GroupTypeFlags.Universal,
            GroupScope.BuiltinLocal => GroupTypeFlags.BuiltinLocal | GroupTypeFlags.DomainLocal,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown group scope."),
        };
        if (security)
        {
            flags |= GroupTypeFlags.Security;
        }

        return unchecked((int)(uint)flags);
    }
}

/// <summary>sAMAccountType values ([MS-ADTS] 2.2.15) - indexed, ideal for fast type filters.</summary>
public static class SamAccountType
{
    public const int DomainObject = 0x0;
    public const int GroupObject = 0x10000000;
    public const int NonSecurityGroupObject = 0x10000001;
    public const int AliasObject = 0x20000000;
    public const int NonSecurityAliasObject = 0x20000001;
    public const int UserObject = 0x30000000;
    public const int MachineAccount = 0x30000001;
    public const int TrustAccount = 0x30000002;
    public const int AppBasicGroup = 0x40000000;
    public const int AppQueryGroup = 0x40000001;
}
