namespace AdMng.Watchdog.Testing.Fixtures.Metadata;

/// <summary>
/// Replication metadata fixtures (watchdog specification 22.5). <see cref="LabUserBlobBase64"/> was captured from the
/// Samba lab (CONTOSO.TEST, 2026-10-14) with <c>ldapsearch</c> as <c>odczyt.test</c> from a throwaway user
/// (created with department, then department replaced and title added in one modify; the OU was deleted afterwards).
/// <see cref="WindowsXmlValues"/> are <c>msDS-ReplAttributeMetaData</c> values in the Windows format, including the NUL
/// terminator and an unescaped <c>&amp;</c> in a DN.
/// </summary>
public static class ReplicationMetadataFixtures
{
    /// <summary>Binary <c>replPropertyMetaData</c> of the lab user (26 entries, sorted by ATTRTYP).</summary>
    public const string LabUserBlobBase64 =
        "AQAAAAAAAAAaAAAAAAAAAAAAAAABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAAMAAAABAAAAhCvgIAMAAAD0QliEbW96TqXN" +
        "T1gnZZLlGlsCAAAAAAAaWwIAAAAAAAQAAAABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAAwAAAABAAAAhCvgIAMAAAD0QliE" +
        "bW96TqXNT1gnZZLlG1sCAAAAAAAbWwIAAAAAACoAAAABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAAEAAgABAAAAhCvgIAMA" +
        "AAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAAIAAgABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAA0AAgABAAAA" +
        "hCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAI0AAgACAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlG1sCAAAAAAAbWwIAAAAAABkB" +
        "AgABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAAEACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIA" +
        "AAAAAAgACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAABAACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAA" +
        "AAAaWwIAAAAAABkACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAADcACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLl" +
        "GlsCAAAAAAAaWwIAAAAAAEAACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAFoACQABAAAAhCvgIAMAAAD0QliEbW96TqXN" +
        "T1gnZZLlGlsCAAAAAAAaWwIAAAAAAF4ACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAGAACQABAAAAhCvgIAMAAAD0QliE" +
        "bW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAGIACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAJIACQABAAAAhCvgIAMA" +
        "AAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAJ8ACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAKAACQABAAAA" +
        "hCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAN0ACQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAC4B" +
        "CQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIAAAAAAA4DCQABAAAAhCvgIAMAAAD0QliEbW96TqXNT1gnZZLlGlsCAAAAAAAaWwIA" +
        "AAAAAA==";

    /// <summary>Number of entries in <see cref="LabUserBlobBase64"/>.</summary>
    public const int LabUserEntryCount = 26;

    /// <summary>invocationId of the lab DC (vm.contoso.test) – the originating DC of every entry.</summary>
    public static readonly Guid LabDcInvocationId = new("845842f4-6f6d-4e7a-a5cd-4f58276592e5");

    /// <summary>uSNCreated of the lab user (objectClass, givenName … carry it as usnLocal).</summary>
    public const long LabUserUsnCreated = 154394;

    /// <summary>uSNChanged of the lab user (the modify: department version 2, title version 1).</summary>
    public const long LabUserUsnChanged = 154395;

    /// <summary>whenCreated of the lab user (originating time of the creation entries, seconds precision).</summary>
    public static readonly DateTimeOffset LabUserCreated = new(2026, 10, 14, 15, 6, 44, TimeSpan.Zero);

    /// <summary>ATTRTYP of department (1.2.840.113556.1.2.141) – version 2 in the blob.</summary>
    public const uint DepartmentAttrTyp = 0x0002008D;

    /// <summary>ATTRTYP of title (2.5.4.12) – version 1, usnLocal = <see cref="LabUserUsnChanged"/>.</summary>
    public const uint TitleAttrTyp = 0x0000000C;

    /// <summary>ATTRTYP of sAMAccountName (1.2.840.113556.1.4.221).</summary>
    public const uint SamAccountNameAttrTyp = 0x000900DD;

    /// <summary>Windows-format msDS-ReplAttributeMetaData values (NUL terminators, an unescaped &amp; in a DN).</summary>
    public static IReadOnlyList<string> WindowsXmlValues { get; } =
    [
        "<DS_REPL_ATTR_META_DATA>\n\t<pszAttributeName>department</pszAttributeName>\n\t<dwVersion>2</dwVersion>\n" +
        "\t<ftimeLastOriginatingChange>2026-10-14T07:12:01Z</ftimeLastOriginatingChange>\n" +
        "\t<uuidLastOriginatingDsaInvocationID>845842f4-6f6d-4e7a-a5cd-4f58276592e5</uuidLastOriginatingDsaInvocationID>\n" +
        "\t<usnOriginatingChange>184467</usnOriginatingChange>\n\t<usnLocalChange>184467</usnLocalChange>\n" +
        "\t<pszLastOriginatingDsaDN>CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=com</pszLastOriginatingDsaDN>\n" +
        "</DS_REPL_ATTR_META_DATA>\n\0",
        "<DS_REPL_ATTR_META_DATA>\n\t<pszAttributeName>unicodePwd</pszAttributeName>\n\t<dwVersion>5</dwVersion>\n" +
        "\t<ftimeLastOriginatingChange>2026-10-13T21:40:12Z</ftimeLastOriginatingChange>\n" +
        "\t<uuidLastOriginatingDsaInvocationID>0d2c0000-0000-4000-8000-000000000002</uuidLastOriginatingDsaInvocationID>\n" +
        "\t<usnOriginatingChange>90211</usnOriginatingChange>\n\t<usnLocalChange>184401</usnLocalChange>\n" +
        "\t<pszLastOriginatingDsaDN>CN=NTDS Settings,CN=DC02,CN=Servers,CN=Kraków & Łódź,CN=Sites,CN=Configuration,DC=contoso,DC=com</pszLastOriginatingDsaDN>\n" +
        "</DS_REPL_ATTR_META_DATA>\n\0",
        "<DS_REPL_ATTR_META_DATA>\n\t<pszAttributeName>pwdLastSet</pszAttributeName>\n\t<dwVersion>7</dwVersion>\n" +
        "\t<ftimeLastOriginatingChange>2026-10-14T07:12:01Z</ftimeLastOriginatingChange>\n" +
        "\t<uuidLastOriginatingDsaInvocationID>845842f4-6f6d-4e7a-a5cd-4f58276592e5</uuidLastOriginatingDsaInvocationID>\n" +
        "\t<usnOriginatingChange>184468</usnOriginatingChange>\n\t<usnLocalChange>184468</usnLocalChange>\n" +
        "\t<pszLastOriginatingDsaDN></pszLastOriginatingDsaDN>\n" +
        "</DS_REPL_ATTR_META_DATA>\n\0",
    ];

    /// <summary>The decoded lab blob bytes.</summary>
    public static byte[] LabUserBlob() => Convert.FromBase64String(LabUserBlobBase64);
}
