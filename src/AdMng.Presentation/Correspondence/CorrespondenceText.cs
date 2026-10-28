namespace AdMng.Presentation.Correspondence;

/// <summary>
/// Localization key families of Korespondencja (design note 6.5): <c>Correspondence.Outlook.Code.{code}</c>,
/// <c>Correspondence.Outlook.State.{state}</c>, <c>Correspondence.Mailboxes.Status.{status}</c>, <c>.Selection.{value}</c>,
/// <c>.NameSource.{value}</c>, <c>.Filter.{value}</c>, <c>Correspondence.Lint.{kind}</c>,
/// <c>Correspondence.SendMode.{value}</c>, <c>Correspondence.Width.{value}</c>, <c>Correspondence.Status.{kind}</c>,
/// <c>Correspondence.Format.{format}</c>.
/// </summary>
public static class CorrespondenceText
{
    /// <summary>Every key of the dynamic families (the localization test checks both languages); the tracks add theirs.</summary>
    public static IReadOnlyList<string> AllDynamicKeys() => [];
}
