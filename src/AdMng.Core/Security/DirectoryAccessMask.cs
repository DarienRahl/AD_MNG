namespace AdMng.Core.Security;

/// <summary>
/// The access mask bits of Active Directory ACEs (ADS_RIGHTS_ENUM) and the mapping of the generic rights to the
/// specific ones the DC applies (MS-ADTS 5.1.3.2: GENERIC_ALL = all specific and standard rights …).
/// </summary>
public static class DirectoryAccessMask
{
    /// <summary>ADS_RIGHT_DS_CREATE_CHILD (CC).</summary>
    public const int CreateChild = 0x0000_0001;

    /// <summary>ADS_RIGHT_DS_DELETE_CHILD (DC).</summary>
    public const int DeleteChild = 0x0000_0002;

    /// <summary>ADS_RIGHT_ACTRL_DS_LIST (LC).</summary>
    public const int ListChildren = 0x0000_0004;

    /// <summary>ADS_RIGHT_DS_SELF (SW): validated writes.</summary>
    public const int Self = 0x0000_0008;

    /// <summary>ADS_RIGHT_DS_READ_PROP (RP).</summary>
    public const int ReadProperty = 0x0000_0010;

    /// <summary>ADS_RIGHT_DS_WRITE_PROP (WP).</summary>
    public const int WriteProperty = 0x0000_0020;

    /// <summary>ADS_RIGHT_DS_DELETE_TREE (DT).</summary>
    public const int DeleteTree = 0x0000_0040;

    /// <summary>ADS_RIGHT_DS_LIST_OBJECT (LO).</summary>
    public const int ListObject = 0x0000_0080;

    /// <summary>ADS_RIGHT_DS_CONTROL_ACCESS (CR): extended rights, confidential attributes.</summary>
    public const int ControlAccess = 0x0000_0100;

    /// <summary>DELETE (SD).</summary>
    public const int Delete = 0x0001_0000;

    /// <summary>READ_CONTROL (RC).</summary>
    public const int ReadControl = 0x0002_0000;

    /// <summary>WRITE_DAC (WD).</summary>
    public const int WriteDacl = 0x0004_0000;

    /// <summary>WRITE_OWNER (WO).</summary>
    public const int WriteOwner = 0x0008_0000;

    /// <summary>GENERIC_ALL (GA).</summary>
    public const int GenericAll = 0x1000_0000;

    /// <summary>GENERIC_EXECUTE (GX).</summary>
    public const int GenericExecute = 0x2000_0000;

    /// <summary>GENERIC_WRITE (GW).</summary>
    public const int GenericWrite = 0x4000_0000;

    /// <summary>GENERIC_READ (GR).</summary>
    public const int GenericRead = unchecked((int)0x8000_0000);

    /// <summary>Every specific and standard right of a directory object ("full control", 0xF01FF).</summary>
    public const int FullControl = 0x000F_01FF;

    /// <summary>The specific rights of GENERIC_READ (RC, LC, RP, LO).</summary>
    public const int MappedRead = ReadControl | ListChildren | ReadProperty | ListObject;

    /// <summary>The specific rights of GENERIC_WRITE (RC, SW, WP).</summary>
    public const int MappedWrite = ReadControl | Self | WriteProperty;

    /// <summary>The specific rights of GENERIC_EXECUTE (RC, LC).</summary>
    public const int MappedExecute = ReadControl | ListChildren;

    /// <summary>The mask with the generic bits replaced by the specific rights they grant.</summary>
    public static int Normalize(int mask)
    {
        var result = mask & ~(GenericAll | GenericExecute | GenericWrite | GenericRead);
        if ((mask & GenericAll) != 0)
        {
            result |= FullControl;
        }

        if ((mask & GenericRead) != 0)
        {
            result |= MappedRead;
        }

        if ((mask & GenericWrite) != 0)
        {
            result |= MappedWrite;
        }

        if ((mask & GenericExecute) != 0)
        {
            result |= MappedExecute;
        }

        return result;
    }

    /// <summary>Every bit of <paramref name="rights"/> is in <paramref name="mask"/> (both normalized).</summary>
    public static bool Has(int mask, int rights) => (Normalize(mask) & rights) == rights;

    /// <summary>Full control: every specific and standard right (normalized).</summary>
    public static bool IsFullControl(int mask) => (Normalize(mask) & FullControl) == FullControl;
}
