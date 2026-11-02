namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Tests that change security descriptors with <c>samba-tool dsacl set</c> run alone (after the parallel collections).
/// Members: the ACL delegations test (U4b) and the GPO tests (U4c: <c>samba-tool gpo</c> and a <c>dsacl set</c> deny of
/// Apply Group Policy; K2, K3). The collection was introduced because Adds of other test classes of this process on the
/// shared administrator session failed with ServerUnavailable (LDAP 81) while samba-tool ran – never from another
/// process. The cause was in the client: the exit of the samba-tool child sent SIGCHLD into this process, which
/// interrupted libldap's <c>poll()</c> in a request and made libldap report "server down"; fixed with
/// <c>LDAP_OPT_RESTART</c> (<c>OpenLdapRestart</c>, <c>Integration/SignalTests</c>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SambaToolCollection
{
    public const string Name = "SambaTool";
}
