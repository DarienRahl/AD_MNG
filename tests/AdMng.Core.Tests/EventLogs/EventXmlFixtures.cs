namespace AdMng.Core.Tests.EventLogs;

/// <summary>
/// Event XML in the shapes Windows produces: the Microsoft documentation samples of the Security events (indented, as
/// Event Viewer's XML view and exports show them) with the sample company's names, the compact single-quoted form of
/// <c>EventRecord.ToXml()</c>, a 1102 with <c>UserData</c>, Group Policy operational records and a classic Application
/// record with unnamed data.
/// </summary>
internal static class EventXmlFixtures
{
    /// <summary>4625 – jan.kowalski failed an interactive logon because the account is locked out (%%2307, 0xc0000234).</summary>
    public const string LogonFailed4625 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
         <System>
          <Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" />
          <EventID>4625</EventID>
          <Version>0</Version>
          <Level>0</Level>
          <Task>12544</Task>
          <Opcode>0</Opcode>
          <Keywords>0x8010000000000000</Keywords>
          <TimeCreated SystemTime="2026-11-03T07:13:55.437838600Z" />
          <EventRecordID>1182</EventRecordID>
          <Correlation />
          <Execution ProcessID="508" ThreadID="1272" />
          <Channel>Security</Channel>
          <Computer>WAW-PC001.contoso.test</Computer>
          <Security />
         </System>
         <EventData>
          <Data Name="SubjectUserSid">S-1-5-18</Data>
          <Data Name="SubjectUserName">WAW-PC001$</Data>
          <Data Name="SubjectDomainName">CONTOSO</Data>
          <Data Name="SubjectLogonId">0x3e7</Data>
          <Data Name="TargetUserSid">S-1-0-0</Data>
          <Data Name="TargetUserName">jan.kowalski</Data>
          <Data Name="TargetDomainName">CONTOSO</Data>
          <Data Name="Status">0xc0000234</Data>
          <Data Name="FailureReason">%%2307</Data>
          <Data Name="SubStatus">0x0</Data>
          <Data Name="LogonType">2</Data>
          <Data Name="LogonProcessName">User32 </Data>
          <Data Name="AuthenticationPackageName">Negotiate</Data>
          <Data Name="WorkstationName">WAW-PC001</Data>
          <Data Name="TransmittedServices">-</Data>
          <Data Name="LmPackageName">-</Data>
          <Data Name="KeyLength">0</Data>
          <Data Name="ProcessId">0x1bc</Data>
          <Data Name="ProcessName">C:\Windows\System32\winlogon.exe</Data>
          <Data Name="IpAddress">127.0.0.1</Data>
          <Data Name="IpPort">0</Data>
         </EventData>
        </Event>
        """;

    /// <summary>4740 – zofia.zak locked out on DC01; the caller computer KRK-LT001 is logged as TargetDomainName.</summary>
    public const string AccountLockedOut4740 =
        """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}'/><EventID>4740</EventID><Version>0</Version><Level>0</Level><Task>13824</Task><Opcode>0</Opcode><Keywords>0x8020000000000000</Keywords><TimeCreated SystemTime='2026-11-03T08:06:08.5768875Z'/><EventRecordID>175703</EventRecordID><Correlation/><Execution ProcessID='520' ThreadID='1112'/><Channel>Security</Channel><Computer>DC01.contoso.test</Computer><Security/></System><EventData><Data Name='TargetUserName'>zofia.zak</Data><Data Name='TargetDomainName'>KRK-LT001</Data><Data Name='TargetSid'>S-1-5-21-3623811015-3361044348-30300820-1105</Data><Data Name='SubjectUserSid'>S-1-5-18</Data><Data Name='SubjectUserName'>DC01$</Data><Data Name='SubjectDomainName'>CONTOSO</Data><Data Name='SubjectLogonId'>0x3e7</Data></EventData></Event>""";

    /// <summary>4771 – Kerberos pre-authentication failed for łukasz.wiśniewski from 10.20.5.40 (0x18, PreAuthType 2).</summary>
    public const string KerberosPreAuthFailed4771 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
        <System>
         <Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" />
         <EventID>4771</EventID>
         <Version>0</Version>
         <Level>0</Level>
         <Task>14339</Task>
         <Opcode>0</Opcode>
         <Keywords>0x8010000000000000</Keywords>
         <TimeCreated SystemTime="2026-11-03T08:10:21.495462300Z" />
         <EventRecordID>166708</EventRecordID>
         <Correlation />
         <Execution ProcessID="520" ThreadID="1084" />
         <Channel>Security</Channel>
         <Computer>DC02.contoso.test</Computer>
         <Security />
        </System>
        <EventData>
         <Data Name="TargetUserName">łukasz.wiśniewski</Data>
         <Data Name="TargetSid">S-1-5-21-3623811015-3361044348-30300820-1106</Data>
         <Data Name="ServiceName">krbtgt/CONTOSO.TEST</Data>
         <Data Name="TicketOptions">0x40810010</Data>
         <Data Name="Status">0x18</Data>
         <Data Name="PreAuthType">2</Data>
         <Data Name="IpAddress">::ffff:10.20.5.40</Data>
         <Data Name="IpPort">49254</Data>
         <Data Name="CertIssuerName" />
         <Data Name="CertSerialNumber" />
         <Data Name="CertThumbprint" />
        </EventData>
        </Event>
        """;

    /// <summary>4776 – NTLM credential validation of grażyna.dąbrowska from WAW-PC002 failed with a bad password.</summary>
    public const string CredentialValidation4776 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
         <System>
         <Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" />
         <EventID>4776</EventID>
         <Version>0</Version>
         <Level>0</Level>
         <Task>14336</Task>
         <Opcode>0</Opcode>
         <Keywords>0x8010000000000000</Keywords>
         <TimeCreated SystemTime="2026-11-03T04:38:11.003163100Z" />
         <EventRecordID>165437</EventRecordID>
         <Correlation />
         <Execution ProcessID="500" ThreadID="532" />
         <Channel>Security</Channel>
         <Computer>DC01.contoso.test</Computer>
         <Security />
         </System>
         <EventData>
         <Data Name="PackageName">MICROSOFT_AUTHENTICATION_PACKAGE_V1_0</Data>
         <Data Name="TargetUserName">grażyna.dąbrowska</Data>
         <Data Name="Workstation">\\WAW-PC002</Data>
         <Data Name="Status">0xc000006a</Data>
         </EventData>
        </Event>
        """;

    /// <summary>1102 – the Security log was cleared by jan.kowalski (fields under UserData/LogFileCleared).</summary>
    public const string LogCleared1102 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
         <System>
         <Provider Name="Microsoft-Windows-Eventlog" Guid="{fc65ddd8-d6ef-4962-83d5-6e5cfe9ce148}" />
         <EventID>1102</EventID>
         <Version>0</Version>
         <Level>4</Level>
         <Task>104</Task>
         <Opcode>0</Opcode>
         <Keywords>0x4020000000000000</Keywords>
         <TimeCreated SystemTime="2026-11-02T22:39:58.656871200Z" />
         <EventRecordID>1087729</EventRecordID>
         <Correlation />
         <Execution ProcessID="820" ThreadID="2644" />
         <Channel>Security</Channel>
         <Computer>DC02.contoso.test</Computer>
         <Security />
         </System>
         <UserData>
         <LogFileCleared xmlns="http://manifests.microsoft.com/win/2004/08/windows/eventlog">
         <SubjectUserSid>S-1-5-21-3623811015-3361044348-30300820-1104</SubjectUserSid>
         <SubjectUserName>jan.kowalski</SubjectUserName>
         <SubjectDomainName>CONTOSO</SubjectDomainName>
         <SubjectLogonId>0x55cd1d</SubjectLogonId>
         </LogFileCleared>
         </UserData>
        </Event>
        """;

    /// <summary>GroupPolicy/Operational 8001 – user policy processing of CONTOSO\jan.kowalski finished in 3 s (ActivityID,
    /// Security/@UserID, level 4, opcode 2).</summary>
    public const string GroupPolicyUserProcessed8001 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-GroupPolicy" Guid="{AEA1B4FA-97D1-45F2-A64C-4D69FFFD92C9}" />
            <EventID>8001</EventID>
            <Version>0</Version>
            <Level>4</Level>
            <Task>0</Task>
            <Opcode>2</Opcode>
            <Keywords>0x4000000000000000</Keywords>
            <TimeCreated SystemTime="2026-11-03T07:42:13.4458161Z" />
            <EventRecordID>48211</EventRecordID>
            <Correlation ActivityID="{5B1C3F2A-8E4D-4C0B-9A47-1D2E3F405162}" />
            <Execution ProcessID="1424" ThreadID="5120" />
            <Channel>Microsoft-Windows-GroupPolicy/Operational</Channel>
            <Computer>WAW-PC001.contoso.test</Computer>
            <Security UserID="S-1-5-18" />
          </System>
          <EventData>
            <Data Name="PolicyElaspedTimeInSeconds">3</Data>
            <Data Name="ErrorCode">0</Data>
            <Data Name="PrincipalSamName">CONTOSO\jan.kowalski</Data>
            <Data Name="IsMachine">0</Data>
            <Data Name="IsConnectivityFailure">false</Data>
          </EventData>
        </Event>
        """;

    /// <summary>GroupPolicy/Operational 5312 – the list of applicable GPOs (a multi-line value with escaped markup and
    /// Polish GPO names).</summary>
    public const string GroupPolicyApplicable5312 =
        """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-GroupPolicy' Guid='{AEA1B4FA-97D1-45F2-A64C-4D69FFFD92C9}'/><EventID>5312</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x4000000000000000</Keywords><TimeCreated SystemTime='2026-11-03T07:42:12.1000000Z'/><EventRecordID>48207</EventRecordID><Correlation ActivityID='{5B1C3F2A-8E4D-4C0B-9A47-1D2E3F405162}'/><Execution ProcessID='1424' ThreadID='5120'/><Channel>Microsoft-Windows-GroupPolicy/Operational</Channel><Computer>WAW-PC001.contoso.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='DescriptionString'>Domyślne zasady domeny&#13;&#10;WAW – Stacje robocze&#13;&#10;</Data><Data Name='GPOInfoList'>&lt;GPO ID="{31B2F340-016D-11D2-945F-00C04FB984F9}"&gt;&lt;Name&gt;Default Domain Policy&lt;/Name&gt;&lt;/GPO&gt;</Data></EventData></Event>""";

    /// <summary>Application 11707 of the classic source MsiInstaller (EventID with Qualifiers, unnamed data, a Binary).</summary>
    public const string ClassicApplication11707 =
        """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='MsiInstaller'/><EventID Qualifiers='0'>11707</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-11-03T06:15:00.1234567Z'/><EventRecordID>9912</EventRecordID><Correlation/><Execution ProcessID='6120' ThreadID='0'/><Channel>Application</Channel><Computer>SRV-PLIKI01.contoso.test</Computer><Security UserID='S-1-5-21-3623811015-3361044348-30300820-1104'/></System><EventData><Data>Produkt: Narzędzia administracyjne – instalacja zakończona pomyślnie.</Data><Data>(NULL)</Data><Data>-</Data><Data></Data><Data>(NULL)</Data><Binary>7B42363345453143442D303639452D343132372D394141362D3838443735314234433038437D</Binary></EventData></Event>""";

    /// <summary>A forwarded record with RenderingInfo (as in ForwardedEvents and Event Viewer exports with display
    /// information) – the rendered texts are ignored.</summary>
    public const string ForwardedWithRenderingInfo4624 =
        """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}'/><EventID>4624</EventID><Version>2</Version><Level>0</Level><Task>12544</Task><Opcode>0</Opcode><Keywords>0x8020000000000000</Keywords><TimeCreated SystemTime='2026-11-03T08:00:00.0000000Z'/><EventRecordID>77</EventRecordID><Correlation ActivityID='{00000000-0000-0000-0000-000000000000}'/><Execution ProcessID='652' ThreadID='700'/><Channel>Security</Channel><Computer>SRV-PLIKI01.contoso.test</Computer><Security/></System><EventData><Data Name='TargetUserName'>zofia.zak</Data><Data Name='LogonType'>3</Data><Data Name='ImpersonationLevel'>%%1833</Data><Data Name='ElevatedToken'>%%1842</Data></EventData><RenderingInfo Culture='pl-PL'><Message>Konto zostało pomyślnie zalogowane.</Message><Level>Informacje</Level><Task>Logon</Task><Opcode>Informacje</Opcode><Channel>Zabezpieczenia</Channel><Provider>Microsoft Windows security auditing.</Provider><Keywords><Keyword>Inspekcja zakończona sukcesem</Keyword></Keywords></RenderingInfo></Event>""";
}
