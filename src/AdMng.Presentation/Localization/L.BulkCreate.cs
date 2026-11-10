namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The bulk creation wizard <em>Zakładanie wielu kont</em> (docs/dev/phase3-bulk-create.md 2.6, 2.9). The contract keys
    /// (command, sub-steps, the host's header and footer) are the lead's; B3 adds the keys of steps 1–3 below them. The
    /// dynamic key families are in <see cref="global::AdMng.Presentation.BulkCreate.BulkCreateTextKeys"/>.
    /// </summary>
    public static class BulkCreate
    {
        // ---- Contract (lead) ----

        /// <summary><em>Wiele kont…</em> (palette <c>new.bulkCreate</c>, the <em>Nowy</em> menus).</summary>
        public const string Command = "BulkCreate.Command";

        /// <summary>The window title with the container: <c>{0}</c> the kind's title, <c>{1}</c> the container's name.</summary>
        public const string TitleInContainer = "BulkCreate.TitleInContainer";

        public const string StepSource = "BulkCreate.StepSource";
        public const string StepColumns = "BulkCreate.StepColumns";
        public const string StepRules = "BulkCreate.StepRules";
        public const string StepCheck = "BulkCreate.StepCheck";

        /// <summary><em>Rodzaj obiektów</em> (the kind combo).</summary>
        public const string Kind = "BulkCreate.Kind";

        /// <summary><em>Kontener docelowy</em>.</summary>
        public const string Container = "BulkCreate.Container";

        /// <summary>No container chosen yet.</summary>
        public const string NoContainer = "BulkCreate.NoContainer";

        /// <summary>Asks before a kind change replaces changed naming rules with the kind's default profile.</summary>
        public const string ConfirmKindChange = "BulkCreate.ConfirmKindChange";

        /// <summary>The did-you-mean of a pattern problem: <c>{0}</c> the suggestion (<c>{givenName}</c>).</summary>
        public const string PatternSuggestion = "BulkCreate.PatternSuggestion";

        // ---- B3 (steps 1–3, profiles) ----

        /// <summary><em>Skopiuj zakres w Excelu (z wierszem nagłówków lub bez) i naciśnij Ctrl+V albo kliknij Wklej. Możesz też wcz…</em></summary>
        public const string SourceHint = "BulkCreate.SourceHint";

        /// <summary><em>Wklej</em></summary>
        public const string Paste = "BulkCreate.Paste";

        /// <summary><em>Dołącza wiersze ze schowka (Ctrl+V); pusta tabela zostanie wypełniona</em></summary>
        public const string PasteToolTip = "BulkCreate.PasteToolTip";

        /// <summary><em>Wklej i zastąp</em></summary>
        public const string ReplaceWithPaste = "BulkCreate.ReplaceWithPaste";

        /// <summary><em>Zastępuje całą tabelę wierszami ze schowka</em></summary>
        public const string ReplaceWithPasteToolTip = "BulkCreate.ReplaceWithPasteToolTip";

        /// <summary><em>Wczytaj plik…</em></summary>
        public const string LoadFile = "BulkCreate.LoadFile";

        /// <summary><em>Dodaj wiersz</em></summary>
        public const string AddRow = "BulkCreate.AddRow";

        /// <summary><em>Dodaj kolumnę</em></summary>
        public const string AddColumn = "BulkCreate.AddColumn";

        /// <summary><em>Nagłówek nowej kolumny</em></summary>
        public const string NewColumnHeader = "BulkCreate.NewColumnHeader";

        /// <summary><em>Usuń zaznaczone wiersze</em></summary>
        public const string RemoveRows = "BulkCreate.RemoveRows";

        /// <summary><em>Pierwszy wiersz zawiera nagłówki</em></summary>
        public const string HeaderModeLabel = "BulkCreate.HeaderModeLabel";

        /// <summary><em>Wiersz nagłówków</em></summary>
        public const string HeaderLineLabel = "BulkCreate.HeaderLineLabel";

        /// <summary><em>Pierwszy wiersz danych w Excelu</em></summary>
        public const string ExcelRowOffsetLabel = "BulkCreate.ExcelRowOffsetLabel";

        /// <summary><em>Wiersz</em></summary>
        public const string ExcelRowColumn = "BulkCreate.ExcelRowColumn";

        /// <summary><em>Wczytywanie danych…</em></summary>
        public const string Reading = "BulkCreate.Reading";

        /// <summary><em>Dołącz mimo to</em></summary>
        public const string AppendRepeated = "BulkCreate.AppendRepeated";

        /// <summary><em>Zastąp</em></summary>
        public const string ReplaceWithRepeated = "BulkCreate.ReplaceWithRepeated";

        /// <summary><em>Schowek nie zawiera tabeli. Skopiuj zakres komórek w Excelu i spróbuj ponownie.</em></summary>
        public const string ClipboardEmpty = "BulkCreate.ClipboardEmpty";

        /// <summary><em>Nie znaleziono żadnych wierszy danych.</em></summary>
        public const string NoRowsRead = "BulkCreate.NoRowsRead";

        /// <summary><em>Plik jest większy niż 50 MB i nie został wczytany.</em></summary>
        public const string FileTooLarge = "BulkCreate.FileTooLarge";

        /// <summary><em>Nie udało się odczytać pliku jako arkusza ani pliku CSV.</em></summary>
        public const string InvalidFile = "BulkCreate.InvalidFile";

        /// <summary><em>Wczytaj dane nowych kont</em></summary>
        public const string OpenFileTitle = "BulkCreate.OpenFileTitle";

        /// <summary><em>Arkusze i pliki CSV (*.xlsx;*.csv;*.txt)</em></summary>
        public const string FileFilter = "BulkCreate.FileFilter";

        /// <summary><em>Wczytaj plik</em></summary>
        public const string LoadFileTitle = "BulkCreate.LoadFileTitle";

        /// <summary><em>Plik zastąpi wczytane dane (wiersze: {0}). Kontynuować?</em></summary>
        public const string ConfirmReplaceWithFile = "BulkCreate.ConfirmReplaceWithFile";

        /// <summary><em>Wyczyść tabelę</em></summary>
        public const string ClearTitle = "BulkCreate.ClearTitle";

        /// <summary><em>Usunąć wszystkie wczytane dane (wiersze: {0})? Poprawki z kroku Sprawdzenie również zostaną usunięte.</em></summary>
        public const string ConfirmClear = "BulkCreate.ConfirmClear";

        /// <summary><em>Tabela nie może mieć więcej kolumn (limit: {0}).</em></summary>
        public const string TooManyColumns = "BulkCreate.TooManyColumns";

        /// <summary><em>Wiersz {0} nie należy do wklejonego zakresu (wiersze: {1}).</em></summary>
        public const string HeaderLineOutOfRange = "BulkCreate.HeaderLineOutOfRange";

        /// <summary><em>Wiersze: {0}, kolumny: {1} – bez wiersza nagłówków (kolumny oznaczone literami)</em></summary>
        public const string SummaryNoHeader = "BulkCreate.SummaryNoHeader";

        /// <summary><em>Wiersze: {0}, kolumny: {1} – nagłówki rozpoznane</em></summary>
        public const string SummaryHeaderDetected = "BulkCreate.SummaryHeaderDetected";

        /// <summary><em>Wiersze: {0}, kolumny: {1} – z wierszem nagłówków</em></summary>
        public const string SummaryHeader = "BulkCreate.SummaryHeader";

        /// <summary><em>Pozostałe ostrzeżenia: {0}</em></summary>
        public const string MoreWarnings = "BulkCreate.MoreWarnings";

        /// <summary><em>Te wiersze są już wklejone (powtórzone: {0} z {1}). Dołączyć je mimo to, zastąpić tabelę czy anulować?</em></summary>
        public const string RepeatedPaste = "BulkCreate.RepeatedPaste";

        /// <summary><em>Imię|Nazwisko|E-mail|Telefon komórkowy|Dział</em></summary>
        public const string StarterHeaders = "BulkCreate.StarterHeaders";

        /// <summary><em>Ta kolumna wygląda na hasła: jest ukryta i nie zostanie użyta</em></summary>
        public const string MaskedColumnToolTip = "BulkCreate.MaskedColumnToolTip";

        /// <summary><em>Każda kolumna arkusza trafia do atrybutu, jest używana tylko we wzorcach albo zostaje pominięta. Kolumny ro…</em></summary>
        public const string ColumnsHint = "BulkCreate.ColumnsHint";

        /// <summary><em>Kolumna</em></summary>
        public const string ColumnHeader = "BulkCreate.ColumnHeader";

        /// <summary><em>Przykłady</em></summary>
        public const string SamplesHeader = "BulkCreate.SamplesHeader";

        /// <summary><em>Przeznaczenie</em></summary>
        public const string TargetHeader = "BulkCreate.TargetHeader";

        /// <summary><em>Atrybut</em></summary>
        public const string AttributeHeader = "BulkCreate.AttributeHeader";

        /// <summary><em>Kolejność</em></summary>
        public const string OrderHeader = "BulkCreate.OrderHeader";

        /// <summary><em>Wielkość liter</em></summary>
        public const string CaseHeader = "BulkCreate.CaseHeader";

        /// <summary><em>Uwagi</em></summary>
        public const string NoteHeader = "BulkCreate.NoteHeader";

        /// <summary><em>Rozpoznaj ponownie</em></summary>
        public const string AutoMap = "BulkCreate.AutoMap";

        /// <summary><em>Format międzynarodowy</em></summary>
        public const string UseInternationalPhones = "BulkCreate.UseInternationalPhones";

        /// <summary><em>wygląda na hasła – nigdy nie jest używana</em></summary>
        public const string PasswordColumnNote = "BulkCreate.PasswordColumnNote";

        /// <summary><em>jak zapamiętano w profilu</em></summary>
        public const string ProfileMappingNote = "BulkCreate.ProfileMappingNote";

        /// <summary><em>identyfikator osobowy – pominięto (przypisuj go tylko wtedy, gdy to konieczne)</em></summary>
        public const string PersonalIdentifierNote = "BulkCreate.PersonalIdentifierNote";

        /// <summary><em>inna kolumna ma już to przeznaczenie – pominięto</em></summary>
        public const string DuplicateTargetNote = "BulkCreate.DuplicateTargetNote";

        /// <summary><em>Tabela nie ma kolumn.</em></summary>
        public const string NoColumns = "BulkCreate.NoColumns";

        /// <summary><em>Kolumna „{0}”: wybierz atrybut.</em></summary>
        public const string ChooseAttribute = "BulkCreate.ChooseAttribute";

        /// <summary><em>Kolumna „{0}”: atrybutu {1} nie można ustawić przy zakładaniu obiektów.</em></summary>
        public const string AttributeNotSettable = "BulkCreate.AttributeNotSettable";

        /// <summary><em>Kolumna „{0}”: „{1}” nie jest dostępne dla tego rodzaju obiektów.</em></summary>
        public const string TargetNotForKind = "BulkCreate.TargetNotForKind";

        /// <summary><em>Kolumny „{0}” i „{1}” mają to samo przeznaczenie – pomiń jedną z nich.</em></summary>
        public const string DuplicateTarget = "BulkCreate.DuplicateTarget";

        /// <summary><em>Wszystkie kolumny są pominięte – przypisz co najmniej jedną kolumnę.</em></summary>
        public const string NothingMapped = "BulkCreate.NothingMapped";

        /// <summary><em>Nazwy</em></summary>
        public const string SectionNames = "BulkCreate.SectionNames";

        /// <summary><em>Konto</em></summary>
        public const string SectionAccount = "BulkCreate.SectionAccount";

        /// <summary><em>Grupy</em></summary>
        public const string SectionGroups = "BulkCreate.SectionGroups";

        /// <summary><em>Zaawansowane</em></summary>
        public const string SectionAdvanced = "BulkCreate.SectionAdvanced";

        /// <summary><em>Profil nazewnictwa</em></summary>
        public const string ProfileLabel = "BulkCreate.ProfileLabel";

        /// <summary><em>Profil nazewnictwa</em></summary>
        public const string ProfileTitle = "BulkCreate.ProfileTitle";

        /// <summary><em>Standardowy</em></summary>
        public const string ProfileStandard = "BulkCreate.ProfileStandard";

        /// <summary><em>Konta numerowane (kd201…)</em></summary>
        public const string ProfileNumbered = "BulkCreate.ProfileNumbered";

        /// <summary><em>(zmieniony)</em></summary>
        public const string ProfileModified = "BulkCreate.ProfileModified";

        /// <summary><em>Zapisz</em></summary>
        public const string SaveProfile = "BulkCreate.SaveProfile";

        /// <summary><em>Zapisz jako…</em></summary>
        public const string SaveProfileAs = "BulkCreate.SaveProfileAs";

        /// <summary><em>Usuń</em></summary>
        public const string DeleteProfile = "BulkCreate.DeleteProfile";

        /// <summary><em>Domyślny dla tego kontenera</em></summary>
        public const string DefaultForContainer = "BulkCreate.DefaultForContainer";

        /// <summary><em>Nazwa profilu</em></summary>
        public const string NewProfileNameLabel = "BulkCreate.NewProfileNameLabel";

        /// <summary><em>{0} (kopia)</em></summary>
        public const string ProfileCopyName = "BulkCreate.ProfileCopyName";

        /// <summary><em>Profil zapamięta też przypisanie kolumn: {0}.</em></summary>
        public const string SaveHeaders = "BulkCreate.SaveHeaders";

        /// <summary><em>Profil zapisze tylko wzorce i opcje (brak nagłówków kolumn do zapamiętania).</em></summary>
        public const string SaveNoHeaders = "BulkCreate.SaveNoHeaders";

        /// <summary><em>Zapisano profil „{0}”.</em></summary>
        public const string ProfileSaved = "BulkCreate.ProfileSaved";

        /// <summary><em>Zapisano profil „{0}”. Zapamiętane kolumny: {1}.</em></summary>
        public const string ProfileSavedWithHeaders = "BulkCreate.ProfileSavedWithHeaders";

        /// <summary><em>Wpisz nazwę profilu.</em></summary>
        public const string ProfileNameRequired = "BulkCreate.ProfileNameRequired";

        /// <summary><em>Profil o nazwie „{0}” już istnieje.</em></summary>
        public const string ProfileNameTaken = "BulkCreate.ProfileNameTaken";

        /// <summary><em>Nie można zapisać więcej profili (limit: {0}). Usuń profil, który nie jest już potrzebny.</em></summary>
        public const string TooManyProfiles = "BulkCreate.TooManyProfiles";

        /// <summary><em>Usuń profil</em></summary>
        public const string DeleteProfileTitle = "BulkCreate.DeleteProfileTitle";

        /// <summary><em>Usunąć profil nazewnictwa „{0}”? Utworzone już obiekty nie zostaną zmienione.</em></summary>
        public const string ConfirmDeleteProfile = "BulkCreate.ConfirmDeleteProfile";

        /// <summary><em>Usunięto profil „{0}”.</em></summary>
        public const string ProfileDeleted = "BulkCreate.ProfileDeleted";

        /// <summary><em>Profil wbudowany nie może być domyślny dla kontenera – najpierw zapisz go pod nową nazwą.</em></summary>
        public const string DefaultNeedsSavedProfile = "BulkCreate.DefaultNeedsSavedProfile";

        /// <summary><em>„{0}” jest teraz domyślnym profilem tego kontenera.</em></summary>
        public const string ProfileIsDefault = "BulkCreate.ProfileIsDefault";

        /// <summary><em>„{0}” nie jest już domyślnym profilem tego kontenera.</em></summary>
        public const string ProfileNotDefault = "BulkCreate.ProfileNotDefault";

        /// <summary><em>Zmiany profilu „{0}” nie zostały zapisane. Odrzucić je i zmienić profil?</em></summary>
        public const string ConfirmDiscardProfileChanges = "BulkCreate.ConfirmDiscardProfileChanges";

        /// <summary><em>Sufiksu UPN z profilu ({0}) nie ma w tym lesie – użyto {1}.</em></summary>
        public const string UpnSuffixMissing = "BulkCreate.UpnSuffixMissing";

        /// <summary><em>Grupy z profilu (liczba: {0}) wybrano w domenie {1} i nie zostaną tu użyte – wybierz grupy ponownie.</em></summary>
        public const string GroupsFromOtherDomain = "BulkCreate.GroupsFromOtherDomain";

        /// <summary><em>Foldery macierzyste nie są tworzone – można ustawić tylko atrybuty homeDirectory i homeDrive.</em></summary>
        public const string HomeFoldersNote = "BulkCreate.HomeFoldersNote";

        /// <summary><em>Numeracja</em></summary>
        public const string Numbering = "BulkCreate.Numbering";

        /// <summary><em>Prefiks</em></summary>
        public const string CounterPrefixLabel = "BulkCreate.CounterPrefixLabel";

        /// <summary><em>Pierwszy numer</em></summary>
        public const string CounterStartLabel = "BulkCreate.CounterStartLabel";

        /// <summary><em>Liczba cyfr (zera z przodu)</em></summary>
        public const string CounterWidthLabel = "BulkCreate.CounterWidthLabel";

        /// <summary><em>Krok</em></summary>
        public const string CounterStepLabel = "BulkCreate.CounterStepLabel";

        /// <summary><em>Wzorce nie używają licznika. Aby numerować konta, wstaw {$nr:201} (Wstaw pole → Kolejny numer), np. kd{$nr:…</em></summary>
        public const string NoCounterHint = "BulkCreate.NoCounterHint";

        /// <summary><em>Pierwszy numer: {0} (bez sprawdzenia w AD)</em></summary>
        public const string FirstNumberUnchecked = "BulkCreate.FirstNumberUnchecked";

        /// <summary><em>Pierwszy wolny numer: {0}</em></summary>
        public const string FirstFree = "BulkCreate.FirstFree";

        /// <summary><em>Pierwszy wolny numer: {0} (najwyższy istniejący: {1})</em></summary>
        public const string FirstFreeWithHighest = "BulkCreate.FirstFreeWithHighest";

        /// <summary><em>Atrybut</em></summary>
        public const string RuleAttributeHeader = "BulkCreate.RuleAttributeHeader";

        /// <summary><em>Wzorzec</em></summary>
        public const string RulePatternHeader = "BulkCreate.RulePatternHeader";

        /// <summary><em>Przykład</em></summary>
        public const string RuleSampleHeader = "BulkCreate.RuleSampleHeader";

        /// <summary><em>Dodaj regułę</em></summary>
        public const string AddRule = "BulkCreate.AddRule";

        /// <summary><em>Usuń regułę</em></summary>
        public const string RemoveRule = "BulkCreate.RemoveRule";

        /// <summary><em>Wstaw pole</em></summary>
        public const string InsertField = "BulkCreate.InsertField";

        /// <summary><em>Reguła zastępuje kolumnę</em></summary>
        public const string OverridesColumn = "BulkCreate.OverridesColumn";

        /// <summary><em>Numeruj</em></summary>
        public const string ApplyCounterFix = "BulkCreate.ApplyCounterFix";

        /// <summary><em>UPN = adres e-mail</em></summary>
        public const string UseMailAsUpn = "BulkCreate.UseMailAsUpn";

        /// <summary><em>Sufiks UPN</em></summary>
        public const string UpnSuffixLabel = "BulkCreate.UpnSuffixLabel";

        /// <summary><em>z wzorca</em></summary>
        public const string UpnFromPattern = "BulkCreate.UpnFromPattern";

        /// <summary><em>Domena adresów e-mail</em></summary>
        public const string MailDomainLabel = "BulkCreate.MailDomainLabel";

        /// <summary><em>Domena {0} nie odbiera poczty z internetu – sprawdź domenę adresów e-mail.</em></summary>
        public const string MailDomainNotRoutable = "BulkCreate.MailDomainNotRoutable";

        /// <summary><em>Wybierz sufiks UPN.</em></summary>
        public const string ChooseUpnSuffix = "BulkCreate.ChooseUpnSuffix";

        /// <summary><em>Wybierz atrybut reguły.</em></summary>
        public const string ChooseRuleAttribute = "BulkCreate.ChooseRuleAttribute";

        /// <summary><em>Numerować kolejno od {0}? → {1}</em></summary>
        public const string CounterFixQuestion = "BulkCreate.CounterFixQuestion";

        /// <summary><em>Kolumna „{0}” ma pierwszeństwo – reguła działa tylko dla pustych komórek</em></summary>
        public const string ColumnPrecedenceNote = "BulkCreate.ColumnPrecedenceNote";

        /// <summary><em>Reguła zastępuje kolumnę „{0}”</em></summary>
        public const string RuleOverridesColumnNote = "BulkCreate.RuleOverridesColumnNote";

        /// <summary><em>bez reguły – wartość z kolumny „{0}”</em></summary>
        public const string ValueFromColumnHint = "BulkCreate.ValueFromColumnHint";

        /// <summary><em>bez reguły – pole pozostanie puste</em></summary>
        public const string NoRuleHint = "BulkCreate.NoRuleHint";

        /// <summary><em>{0}: {1}</em></summary>
        public const string RuleProblem = "BulkCreate.RuleProblem";

        /// <summary><em>{0}: brak reguły i kolumny – dodaj wzorzec albo przypisz kolumnę.</em></summary>
        public const string NameSourceMissing = "BulkCreate.NameSourceMissing";

        /// <summary><em>(zmieniono znaki)</em></summary>
        public const string SampleSanitized = "BulkCreate.SampleSanitized";

        /// <summary><em>(skrócono)</em></summary>
        public const string SampleTruncated = "BulkCreate.SampleTruncated";

        /// <summary><em>najdłuższy: {0}</em></summary>
        public const string SampleLongest = "BulkCreate.SampleLongest";

        /// <summary><em>Pola</em></summary>
        public const string TokenGroupFields = "BulkCreate.TokenGroupFields";

        /// <summary><em>Kolumny</em></summary>
        public const string TokenGroupColumns = "BulkCreate.TokenGroupColumns";

        /// <summary><em>Specjalne</em></summary>
        public const string TokenGroupSpecial = "BulkCreate.TokenGroupSpecial";

        /// <summary><em>Funkcje</em></summary>
        public const string TokenGroupFunctions = "BulkCreate.TokenGroupFunctions";

        /// <summary><em>Kolumna „{0}”</em></summary>
        public const string TokenColumn = "BulkCreate.TokenColumn";

        /// <summary><em>Kolejny numer</em></summary>
        public const string TokenCounter = "BulkCreate.TokenCounter";

        /// <summary><em>Sufiks UPN</em></summary>
        public const string TokenSuffix = "BulkCreate.TokenSuffix";

        /// <summary><em>Domena adresów e-mail</em></summary>
        public const string TokenMailDomain = "BulkCreate.TokenMailDomain";

        /// <summary><em>Domena (nazwa DNS)</em></summary>
        public const string TokenDomain = "BulkCreate.TokenDomain";

        /// <summary><em>Nazwa kontenera</em></summary>
        public const string TokenParentName = "BulkCreate.TokenParentName";

        /// <summary><em>Dzisiejsza data (RRRR-MM-DD)</em></summary>
        public const string TokenDateIso = "BulkCreate.TokenDateIso";

        /// <summary><em>Dzisiejsza data (DD.MM.RRRR)</em></summary>
        public const string TokenDatePolish = "BulkCreate.TokenDatePolish";

        /// <summary><em>tekst</em></summary>
        public const string TokenDefaultText = "BulkCreate.TokenDefaultText";

        /// <summary><em>Hasło początkowe</em></summary>
        public const string PasswordModeLabel = "BulkCreate.PasswordModeLabel";

        /// <summary><em>Długość hasła</em></summary>
        public const string GeneratedLengthLabel = "BulkCreate.GeneratedLengthLabel";

        /// <summary><em>minimum domeny: {0}</em></summary>
        public const string DomainMinimum = "BulkCreate.DomainMinimum";

        /// <summary><em>Hasło</em></summary>
        public const string SharedPasswordLabel = "BulkCreate.SharedPasswordLabel";

        /// <summary><em>Potwierdź hasło</em></summary>
        public const string ConfirmPasswordLabel = "BulkCreate.ConfirmPasswordLabel";

        /// <summary><em>Wszyscy będą znać hasło każdego konta do pierwszej zmiany hasła.</em></summary>
        public const string SharedPasswordEnabledNote = "BulkCreate.SharedPasswordEnabledNote";

        /// <summary><em>Użytkownik musi zmienić hasło przy następnym logowaniu</em></summary>
        public const string MustChangeAtLogon = "BulkCreate.MustChangeAtLogon";

        /// <summary><em>Konto włączone</em></summary>
        public const string Enabled = "BulkCreate.Enabled";

        /// <summary><em>Konto wygasa: koniec dnia</em></summary>
        public const string HasExpiry = "BulkCreate.HasExpiry";

        /// <summary><em>Domena wymaga haseł o długości co najmniej: {0}.</em></summary>
        public const string LengthBelowDomain = "BulkCreate.LengthBelowDomain";

        /// <summary><em>Wybierz datę wygaśnięcia.</em></summary>
        public const string ExpiryDateRequired = "BulkCreate.ExpiryDateRequired";

        /// <summary><em>Data wygaśnięcia jest w przeszłości.</em></summary>
        public const string ExpiryInPast = "BulkCreate.ExpiryInPast";

        /// <summary><em>Zakres grupy</em></summary>
        public const string GroupScopeLabel = "BulkCreate.GroupScopeLabel";

        /// <summary><em>Grupa zabezpieczeń (w przeciwnym razie dystrybucyjna)</em></summary>
        public const string SecurityGroup = "BulkCreate.SecurityGroup";

        /// <summary><em>Użytkownik lub grupa, która może dołączyć komputery do domeny</em></summary>
        public const string JoinerLabel = "BulkCreate.JoinerLabel";

        /// <summary><em>(nie wybrano – Administratorzy domeny)</em></summary>
        public const string NoJoiner = "BulkCreate.NoJoiner";

        /// <summary><em>Nie udało się odczytać wybranego użytkownika lub grupy.</em></summary>
        public const string JoinerNotFound = "BulkCreate.JoinerNotFound";

        /// <summary><em>Kontakty nie mają konta ani hasła.</em></summary>
        public const string ContactNoAccount = "BulkCreate.ContactNoAccount";

        /// <summary><em>Każdy utworzony obiekt zostanie dodany do tych grup.</em></summary>
        public const string GroupsHint = "BulkCreate.GroupsHint";

        /// <summary><em>Dodaj grupy…</em></summary>
        public const string AddGroups = "BulkCreate.AddGroups";

        /// <summary><em>Nie wybrano grup.</em></summary>
        public const string NoGroups = "BulkCreate.NoGroups";

        /// <summary><em>Licznik</em></summary>
        public const string CounterModeLabel = "BulkCreate.CounterModeLabel";

        /// <summary><em>Nie używaj ponownie numerów z Kosza AD</em></summary>
        public const string AvoidRecycleBin = "BulkCreate.AvoidRecycleBin";

        /// <summary><em>Gdy nazwa jest zajęta</em></summary>
        public const string CollisionsLabel = "BulkCreate.CollisionsLabel";

        /// <summary><em>Poprawiaj niedozwolone znaki w identyfikatorach (ą→a, spacje)</em></summary>
        public const string SanitizeIdentifiers = "BulkCreate.SanitizeIdentifiers";

        /// <summary><em>Skracaj za długie nazwy (nazwa logowania 20, cn 64 znaki)</em></summary>
        public const string TruncateNames = "BulkCreate.TruncateNames";

        /// <summary><em>Dodaj główny adres SMTP do proxyAddresses (Microsoft 365 / Exchange)</em></summary>
        public const string AddPrimarySmtp = "BulkCreate.AddPrimarySmtp";

        /// <summary><em>Numery telefonów</em></summary>
        public const string PhonesLabel = "BulkCreate.PhonesLabel";

        /// <summary><em>Szukaj istniejących kont tej osoby</em></summary>
        public const string MatchExisting = "BulkCreate.MatchExisting";

        /// <summary><em>bez numerów z Kosza AD</em></summary>
        public const string SummaryNoRecycleBin = "BulkCreate.SummaryNoRecycleBin";

        /// <summary><em>zajęta nazwa: {0}</em></summary>
        public const string SummaryCollisions = "BulkCreate.SummaryCollisions";

        /// <summary><em>bez poprawiania znaków</em></summary>
        public const string SummaryNoSanitize = "BulkCreate.SummaryNoSanitize";

        /// <summary><em>bez skracania nazw</em></summary>
        public const string SummaryNoTruncate = "BulkCreate.SummaryNoTruncate";

        /// <summary><em>adres SMTP w proxyAddresses</em></summary>
        public const string SummaryPrimarySmtp = "BulkCreate.SummaryPrimarySmtp";

        /// <summary><em>telefony w formacie międzynarodowym</em></summary>
        public const string SummaryInternationalPhones = "BulkCreate.SummaryInternationalPhones";

        /// <summary><em>bez szukania istniejących kont</em></summary>
        public const string SummaryNoMatching = "BulkCreate.SummaryNoMatching";

        /// <summary><em>Wiele naraz…</em></summary>
        public const string SeveralAtOnce = "BulkCreate.SeveralAtOnce";

        /// <summary><em>Otwiera kreator zakładania wielu obiektów tego rodzaju z arkusza wklejonego z Excela</em></summary>
        public const string SeveralAtOnceToolTip = "BulkCreate.SeveralAtOnceToolTip";
    }
}
