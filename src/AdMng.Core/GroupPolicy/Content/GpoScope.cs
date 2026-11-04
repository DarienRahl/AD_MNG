namespace AdMng.Core.GroupPolicy.Content;

/// <summary>Part of a GPO a file belongs to: the computer part (<c>Machine/</c>), the user part (<c>User/</c>) or the GPO
/// itself (root files such as <c>GPO.cmt</c>).</summary>
public enum GpoScope
{
    Gpo = 0,
    Computer = 1,
    User = 2,
}
