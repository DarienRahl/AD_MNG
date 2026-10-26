namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Tests that change security descriptors with <c>samba-tool dsacl set</c> run alone (after the parallel collections):
/// while such a change was running, Adds of other test classes of this process on the shared administrator session failed
/// with ServerUnavailable (LDAP 81) on the Samba lab. Read-only <c>samba-tool</c> calls, the same writes over LDAP and runs
/// in another test process did not show it; the cause in Samba or libldap was not found.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SambaToolCollection
{
    public const string Name = "SambaTool";
}
