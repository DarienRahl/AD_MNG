namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>The role groups and callers of the API tests.</summary>
public static class TestRoles
{
    public const string ViewerGroup = "S-1-5-21-3623811015-3361044348-30300820-1301";
    public const string AdminGroup = "S-1-5-21-3623811015-3361044348-30300820-1302";
    public const string DeniedUser = "S-1-5-21-3623811015-3361044348-30300820-2101";
    public const string OtherGroup = "S-1-5-21-3623811015-3361044348-30300820-1399";

    public const string ViewerAccount = @"CONTOSO\łucja.nowak";
    public const string AdminAccount = @"CONTOSO\jan.kowalski";
}
