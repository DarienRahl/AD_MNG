using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// Sample Group Policy data for the smoke test (<c>AdMng.exe --smoke-test</c>) and the UI tests: a GPO, a WMI filter and a
/// change plan with SYSVOL rows - rendered without any domain controller. Never written anywhere.
/// </summary>
internal static class GroupPolicySamples
{
    public const string DomainDn = "DC=contoso,DC=test";

    public static readonly Guid PasswordsId = new("5A7E3C21-9B4D-4E61-8F0A-1C2D3E4F5A6B");

    public static readonly Guid RemoteDesktopId = new("6B8F4D32-AC5E-4F72-901B-2D3E4F5A6B7C");

    public static GpoInfo Gpo(Guid id, string name) => new()
    {
        Id = id,
        Dn = GpLinkValue.GpoDn(id, DomainDn),
        DisplayName = name,
        DirectoryVersion = new GpoVersion(0, 4),
        SysvolVersion = new GpoVersion(0, 4),
    };

    public static WmiFilterInfo Filter() => new()
    {
        Dn = "CN={8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B},CN=SOM,CN=WMIPolicy,CN=System," + DomainDn,
        Id = "{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B}",
        Name = "Tylko stacje robocze",
        Description = "Systemy klienckie (ProductType = 1) – bez serwerów",
        Queries = [new WmiFilterQuery(WmiFilterQueries.DefaultNamespace, "SELECT * FROM Win32_OperatingSystem WHERE ProductType = 1")],
    };

    /// <summary>An import-like plan: a changed security template, a new comment file and the version raise.</summary>
    public static ChangePlan Plan(string server)
    {
        var gpo = Gpo(PasswordsId, "Stacje robocze – Zasady haseł");
        var template = SecurityTemplate.RelativePath;
        return new ChangePlan
        {
            Title = "Import ustawień do „Stacje robocze – Zasady haseł”",
            Server = server,
            Sequential = true,
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = gpo.Dn,
                    DisplayName = gpo.DisplayName,
                    Kind = ObjectKind.GroupPolicyContainer,
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.WriteGpoFile,
                            Sysvol = new SysvolChange
                            {
                                GpoId = gpo.Id,
                                RelativePath = template,
                                OldContent = Utf16("[Unicode]\r\nUnicode=yes\r\n[System Access]\r\nMinimumPasswordLength = 8\r\n"),
                                NewContent = Utf16("[Unicode]\r\nUnicode=yes\r\n[System Access]\r\nMinimumPasswordLength = 12\r\nLockoutBadCount = 5\r\n"),
                            },
                        },
                        new PlannedChange
                        {
                            Action = ChangeAction.WriteGpoFile,
                            Sysvol = new SysvolChange { GpoId = gpo.Id, RelativePath = "GPO.cmt", NewContent = Encoding.UTF8.GetBytes("Hasła min. 12 znaków – Łukasz") },
                        },
                        new PlannedChange
                        {
                            Action = ChangeAction.SetGpoVersion,
                            GpoVersion = new GpoVersionChange(gpo.DirectoryVersion, gpo.SysvolVersion, new GpoVersion(1, 5)),
                        },
                    ],
                },
            ],
        };
    }

    private static byte[] Utf16(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
}
