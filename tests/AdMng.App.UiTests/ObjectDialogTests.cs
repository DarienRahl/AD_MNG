using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.PropertySheets;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Object dialogs (wave C2a) over the sample company: property sheets of every object kind with every page and field
/// template rendered, edits applied through the change pipeline, New Object, Reset Password, the pickers, Find and the
/// DialogService wiring. Every test ends with <see cref="UiTestContext.AssertHealthy"/> (no binding errors).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ObjectDialogTests
{
    /// <summary>Field view model types of a user's sheet (with proxy addresses and "Log On To" values).</summary>
    private static readonly Type[] UserFieldTypes =
    [
        typeof(FieldViewModel), typeof(MultiValueFieldViewModel), typeof(CheckFieldViewModel), typeof(FlagFieldViewModel),
        typeof(ExpiryFieldViewModel), typeof(DnFieldViewModel), typeof(ReferenceListFieldViewModel), typeof(CountryFieldViewModel),
        typeof(HomeFolderFieldViewModel), typeof(UpnFieldViewModel), typeof(WorkstationsFieldViewModel),
        typeof(EncryptionTypesFieldViewModel), typeof(ProxyAddressesFieldViewModel),
    ];

    // ---- Property sheets ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public Task PropertySheet_User_RendersEveryPageAndFieldType(string language) => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync(language);
        await using var context = created;
        var jan = context.Company.JanKowalski;
        context.Directory.Session.SetValues(jan, Attr.ProxyAddresses, "SMTP:jan.kowalski@contoso.test", "smtp:jk@contoso.test");
        context.Directory.Session.SetValues(jan, Attr.UserWorkstations, "WAW-PC001,WAW-PC002");
        context.Directory.Session.SetValues(jan, Attr.OtherTelephone, "+48 22 123 45 67");
        await context.ConnectAsync();

        var window = await OpenSheetAsync(context, jan);
        Assert.Contains(window.ViewModel.Pages, p => p.Key == L.Pages.EmailAddresses);
        var rendered = await VisitAllPagesAsync(window);

        Assert.Equal([], UserFieldTypes.Except(rendered).Select(t => t.Name));
        Assert.False(window.ViewModel.HasChanges);
        context.AssertHealthy();
    });

    [Theory]
    [InlineData("computer")]
    [InlineData("ou")]
    [InlineData("contact")]
    public Task PropertySheet_OtherKinds_RenderEveryPage(string target) => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        var dn = target switch
        {
            "computer" => context.Company.Computer1,
            "ou" => context.Company.ItOu,
            _ => context.Directory.AddContact("Anna", "Zając", context.Company.SalesOu, new Dictionary<string, object?>
            {
                [Attr.Mail] = "anna.zajac@example.com",
                [Attr.Company] = "Dostawca Sp. z o.o.",
            }),
        };
        await context.ConnectAsync();

        var window = await OpenSheetAsync(context, dn);
        var rendered = await VisitAllPagesAsync(window);

        Assert.Contains(typeof(FieldViewModel), rendered);
        Assert.Contains(typeof(DnFieldViewModel), rendered);
        if (target == "ou")
        {
            Assert.Contains(typeof(CountryFieldViewModel), rendered);
        }

        if (target == "contact")
        {
            Assert.Contains(window.ViewModel.Pages, p => p is MemberOfPageViewModel);
        }

        context.AssertHealthy();
    });

    [Fact]
    public Task PropertySheet_Group_StagesMembershipChanges_AndAppliesThem() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var group = context.Company.GroupIt;

        var window = await OpenSheetAsync(context, group);
        var rendered = await VisitAllPagesAsync(window);
        Assert.Contains(typeof(GroupTypeFieldViewModel), rendered);
        Assert.Contains(typeof(DnFieldViewModel), rendered);

        var sheet = window.ViewModel;
        var members = sheet.Pages.OfType<MembersPageViewModel>().Single();
        sheet.SelectedPage = members;
        await UiTestSupport.WaitForIdleAsync();
        await UiWait.UntilAsync(() => members.Items.Count == 2, "the two members of GG_IT");

        // Multi-selection of the list drives Remove; Add goes through the object picker (scripted here).
        var list = UiWait.Descendants<ListView>(window).Single(l => l.Name == "MembershipList");
        var lukasz = members.Items.Single(i => string.Equals(i.Dn, context.Company.LukaszWisniewski, StringComparison.OrdinalIgnoreCase));
        list.SelectedItems.Add(lukasz);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(list.SelectedItems, members.SelectedItems);
        Assert.True(members.RemoveCommand.CanExecute(null));
        members.RemoveCommand.Execute(null);
        Assert.True(lukasz.IsPendingRemove);

        dialogs.PickedObjects.Enqueue([context.Company.MalgorzataWojcik]);
        await members.AddCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains(members.Items, i => i.IsPendingAdd && i.Name == "Małgorzata Wójcik");
        Assert.True(sheet.HasChanges);

        await sheet.ApplyCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Null(sheet.ApplyError);
        Assert.False(sheet.HasChanges);
        var values = context.Directory.Session.GetValues(group, Attr.Member);
        Assert.Contains(context.Company.MalgorzataWojcik, values, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(context.Company.JanKowalski, values, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(context.Company.LukaszWisniewski, values, StringComparer.OrdinalIgnoreCase);
        context.AssertHealthy();
    });

    [Fact]
    public Task PropertySheet_TypedValue_IsValidated_AppliedThroughThePipeline_AndOkCloses() => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var jan = context.Company.JanKowalski;
        var window = await OpenSheetAsync(context, jan);
        var sheet = window.ViewModel;

        // Type into the Job Title box like the operator would.
        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.Organization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var titleBox = UiWait.Descendants<TextBox>(window).Single(t => t.DataContext is FieldViewModel { Attribute: Attr.Title });
        titleBox.Text = "Kierownik działu IT";
        var title = (FieldViewModel)titleBox.DataContext;
        Assert.True(title.IsDirty);
        Assert.True(sheet.HasChanges);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(sheet.ApplyCommand.CanExecute(null));

        await sheet.ApplyCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(sheet.ApplyError);
        Assert.False(sheet.HasChanges);
        Assert.Equal(["Kierownik działu IT"], context.Directory.Session.GetValues(jan, Attr.Title));

        // Live validation: a too long first name shows the error under the field and blocks Apply.
        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.General);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var givenNameBox = UiWait.Descendants<TextBox>(window).Single(t => t.DataContext is FieldViewModel { Attribute: Attr.GivenName });
        givenNameBox.Text = new string('x', 65);
        await UiTestSupport.WaitForIdleAsync();
        var givenName = (FieldViewModel)givenNameBox.DataContext;
        Assert.NotNull(givenName.Error);
        Assert.True(sheet.HasErrors);
        Assert.False(sheet.ApplyCommand.CanExecute(null));
        Assert.Contains(UiWait.Descendants<TextBlock>(window), t => t.IsVisible && t.Text == givenName.Error);
        givenNameBox.Text = "Jan";
        Assert.False(sheet.HasChanges);

        // OK with nothing pending closes the window with a positive result.
        await sheet.OkCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.False(window.IsVisible);
        Assert.True(sheet.IsDisposed);
        context.AssertHealthy();
    });

    [Fact]
    public Task AttributeEditor_DoubleClickEditsAValue_AndApplyWritesIt() => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var jan = context.Company.JanKowalski;
        var window = await OpenSheetAsync(context, jan);
        var sheet = window.ViewModel;
        var editor = sheet.Pages.OfType<AttributeEditorPageViewModel>().Single();

        sheet.SelectedPage = editor;
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.Rows.Count > 0, "attributes read");
        editor.FilterText = "department";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var row = editor.Rows.Single(r => r.Attribute == Attr.Department);
        var list = UiWait.Descendants<ListView>(window).Single(l => l.Name == "AttributeList");
        list.UpdateLayout();
        var container = Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromItem(row));
        container.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotNull(editor.Editor);
        Assert.Equal(AttributeEditorKind.Text, editor.Editor!.Kind);

        var valueBox = UiWait.Descendants<TextBox>(window)
            .Single(t => t.DataContext is AttributeValueEditorViewModel && t.IsVisible && !t.IsReadOnly);
        valueBox.Text = "Dział IT – administracja";
        editor.Editor.OkCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(editor.Editor);
        Assert.True(row.IsDirty);
        Assert.True(sheet.HasChanges);

        await sheet.ApplyCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(sheet.ApplyError);
        Assert.Equal(["Dział IT – administracja"], context.Directory.Session.GetValues(jan, Attr.Department));
        context.AssertHealthy();
    });

    [Fact]
    public Task ShowProperties_OpensOneWindowPerObject_AndAsksBeforeDiscardingChanges() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var jan = context.Company.JanKowalski;

        await dialogs.Inner.ShowPropertiesAsync(jan);
        await dialogs.Inner.ShowPropertiesAsync(jan.ToUpperInvariant());
        var window = Assert.Single(Application.Current.Windows.OfType<PropertiesWindow>());
        window.Left = -10000;
        window.Top = -10000;
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var sheet = window.ViewModel;
        var description = sheet.Pages.OfType<FieldsPageViewModel>().First().Fields.OfType<FieldViewModel>().Single(f => f.Attribute == Attr.Description);
        description.Value = "Zmiana, której nie zapisano";
        Assert.True(sheet.HasChanges);

        // Title bar close with pending changes: the question comes after the Closing event; declined = stays open.
        dialogs.ConfirmAnswer = false;
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.IsVisible);
        Assert.Single(dialogs.Confirmations);

        dialogs.ConfirmAnswer = true;
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.IsVisible);
        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.True(sheet.IsDisposed);
        Assert.Null(window.Result);
        Assert.Empty(context.Directory.Session.GetValues(jan, Attr.Description));
        context.AssertHealthy();
    });

    // ---- New Object ---------------------------------------------------------------------------------------------

    [Fact]
    public Task NewObject_User_DerivesNames_AndCreatesTheAccount() => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync("pl");
        await using var context = created;
        await context.ConnectAsync();
        var dialog = Factory(context).CreateNewObject(ObjectKind.User, context.Company.ItOu);
        var window = (NewObjectWindow)Windows(context).CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);

        window.FirstNameBox.Text = "Łucja";
        dialog.LastName = "Żak-Wąsowska";
        await UiWait.UntilAsync(() => dialog.SamAvailability == NameAvailability.Available, "logon name checked");
        Assert.Equal("Łucja Żak-Wąsowska", dialog.FullName);
        Assert.Equal("lucja.zak-wasowska", dialog.SamAccountName);
        Assert.Equal(Visibility.Visible, window.SamSection.Visibility);
        Assert.Equal(Visibility.Collapsed, window.DescriptionSection.Visibility);

        window.PasswordBox.Password = "Start!Haslo2026";
        window.ConfirmPasswordBox.Password = "Start!Haslo2026";
        await dialog.CreateCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Null(dialog.Error);
        Assert.True(window.Result);
        Assert.False(window.IsVisible);
        Assert.NotNull(dialog.CreatedDn);
        Assert.True(context.Directory.Session.Exists(dialog.CreatedDn!));
        Assert.Equal(dialog.CreatedDn, Assert.Single(context.Directory.Session.PasswordResets).DistinguishedName, ignoreCase: true);
        Assert.Equal(string.Empty, window.PasswordBox.Password);
        context.AssertHealthy();
    });

    [Theory]
    [InlineData(ObjectKind.Group, "GG_Testerzy")]
    [InlineData(ObjectKind.OrganizationalUnit, "Projekty")]
    [InlineData(ObjectKind.Computer, "WAW-PC003")]
    [InlineData(ObjectKind.Contact, "Anna")]
    public Task NewObject_OtherKinds_RenderAndCreate(ObjectKind kind, string name) => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var parent = kind switch
        {
            ObjectKind.Group => context.Company.GroupsOu,
            ObjectKind.Computer => context.Company.ComputersOu,
            _ => context.Company.CompanyOu,
        };
        var dialog = Factory(context).CreateNewObject(kind, parent);
        var window = (NewObjectWindow)Windows(context).CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);

        if (kind == ObjectKind.Contact)
        {
            dialog.FirstName = name;
            dialog.LastName = "Zając";
        }
        else
        {
            dialog.Name = name;
            dialog.Description = "Utworzono w teście interfejsu – zażółć gęślą jaźń";
        }

        if (kind is ObjectKind.Group or ObjectKind.Computer)
        {
            await UiWait.UntilAsync(() => dialog.SamAvailability == NameAvailability.Available, "pre-Windows 2000 name checked");
        }

        await UiTestSupport.WaitForIdleAsync();
        await dialog.CreateCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Null(dialog.Error);
        Assert.True(window.Result);
        Assert.True(context.Directory.Session.Exists(dialog.CreatedDn!));
        context.AssertHealthy();
    });

    // ---- Reset Password -----------------------------------------------------------------------------------------

    [Fact]
    public Task ResetPassword_OneAccount_UsesThePasswordBoxes() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var zaneta = context.Company.ZanetaSzymanska;
        var dialog = Factory(context).CreateResetPassword([zaneta]);
        var window = (ResetPasswordWindow)Windows(context).CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(dialog.IsSingle);
        Assert.True(dialog.IsAnyLocked);
        Assert.Contains("Żaneta", dialog.WindowTitle, StringComparison.Ordinal);

        window.PasswordBox.Password = "Nowe!Haslo2026";
        window.ConfirmPasswordBox.Password = "Inne!Haslo2026";
        await dialog.ResetCommand.ExecuteAsync(null);
        Assert.NotNull(dialog.Error);
        Assert.Empty(context.Directory.Session.PasswordResets);

        window.ConfirmPasswordBox.Password = "Nowe!Haslo2026";
        await dialog.ResetCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(dialog.Error);
        Assert.True(dialog.IsCompleted);
        Assert.Single(context.Directory.Session.PasswordResets);
        Assert.Equal(string.Empty, window.PasswordBox.Password);
        Assert.Equal(Visibility.Collapsed, window.ResetButton.Visibility);

        // Nothing generated: closing does not ask.
        await dialog.CancelCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.False(window.IsVisible);
        Assert.Empty(dialogs.Confirmations);
        context.AssertHealthy();
    });

    [Fact]
    public Task ResetPassword_ManyAccounts_GeneratedPasswords_GuardTheTitleBarClose() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        await context.ConnectAsync();
        var dialog = Factory(context).CreateResetPassword([context.Company.JanKowalski, context.Company.LukaszWisniewski]);
        var window = (ResetPasswordWindow)Windows(context).CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        Assert.True(dialog.GeneratePassword);
        Assert.False(dialog.CanTypePassword);

        await dialog.ResetCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(dialog.IsCompleted);
        Assert.Equal(2, dialog.Results.Count);
        Assert.Equal(2, context.Directory.Session.PasswordResets.Count);
        Assert.All(dialog.Results, r => Assert.Equal("••••••••", r.DisplayedPassword));
        var asked = dialogs.Confirmations.Count;

        // The passwords exist only in this window: a title bar close asks first (declined = stays open).
        dialogs.ConfirmAnswer = false;
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.IsVisible);
        Assert.Equal(asked + 1, dialogs.Confirmations.Count);

        // Revealing hands them out: closing no longer asks.
        foreach (var row in dialog.Results)
        {
            row.IsRevealed = true;
        }

        await UiTestSupport.WaitForIdleAsync();
        Assert.All(dialog.Results, r => Assert.NotEqual("••••••••", r.DisplayedPassword));
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.IsVisible);
        Assert.Equal(asked + 1, dialogs.Confirmations.Count);
        Assert.Empty(dialog.Results);
        context.AssertHealthy();
    });

    // ---- Pickers ------------------------------------------------------------------------------------------------

    [Fact]
    public Task ObjectPicker_ChecksNames_FindsAndAddsAMultiSelection() => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        var session = await context.ConnectAsync();
        var picker = Factory(context).CreateObjectPicker(session, [ObjectKind.User, ObjectKind.Group], multiSelect: true);
        var window = (ObjectPickerWindow)Windows(context).CreateWindow(picker);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        Assert.Equal(System.Windows.Controls.SelectionMode.Extended, window.ResultsList.SelectionMode);

        window.SearchBox.Text = "Kowalski";
        await picker.CheckNamesCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains(picker.Chosen, c => c.Name == "Jan Kowalski");

        await picker.FindNowCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var group = picker.Results.First(r => r.Kind == ObjectKind.Group);
        var lukasz = picker.Results.First(r => string.Equals(r.Dn, context.Company.LukaszWisniewski, StringComparison.OrdinalIgnoreCase));
        window.ResultsList.SelectedItems.Add(group);
        window.ResultsList.SelectedItems.Add(lukasz);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(picker.AddSelectedCommand.CanExecute(null));
        picker.AddSelectedCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(3, picker.Chosen.Count);

        await picker.OkCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.Equal(3, picker.Result.Count);
        Assert.Contains(context.Company.LukaszWisniewski, picker.Result, StringComparer.OrdinalIgnoreCase);
        context.AssertHealthy();
    });

    [Fact]
    public Task ContainerPicker_ExpandsToTheStartContainer_AndReturnsTheSelection() => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        var session = await context.ConnectAsync();
        var picker = Factory(context).CreateContainerPicker(session, context.Company.ItOu);
        var window = (ContainerPickerWindow)Windows(context).CreateWindow(picker);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(context.Company.ItOu, picker.SelectedContainer?.Dn, ignoreCase: true);
        var selected = Assert.Single(UiWait.Descendants<TreeViewItem>(window), i => i.IsSelected);
        Assert.Equal("IT", Assert.IsAssignableFrom<TreeNodeViewModel>(selected.DataContext).Name);

        picker.OkCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.Equal(context.Company.ItOu, picker.Result, ignoreCase: true);
        context.AssertHealthy();
    });

    // ---- Find ---------------------------------------------------------------------------------------------------

    [Fact]
    public Task Find_ListsResults_AndActivatingARowOpensItsProperties() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        dialogs.RecordPropertiesOnly = true;
        await context.ConnectAsync();
        var find = Factory(context).CreateFind(context.Company.EmployeesOu);
        var window = (FindWindow)Windows(context).CreateWindow(find);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();

        window.NameBox.Text = "Kowalski";
        Assert.Contains("anr=", find.FilterText, StringComparison.OrdinalIgnoreCase);
        find.AddConditionCommand.Execute(null);
        window.AdvancedExpander.IsExpanded = true;
        await UiTestSupport.WaitForIdleAsync();
        var condition = Assert.Single(find.Conditions);
        condition.Field = condition.Fields.Single(f => f.Value == Attr.Department);
        condition.Value = "IT";

        await find.FindNowCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !find.Results.IsLoading && find.Results.Items.Count > 0, "search results");
        await UiTestSupport.WaitForIdleAsync();
        var row = Assert.Single(find.Results.Items);
        Assert.Equal("Jan Kowalski", row.Name);

        // Double click (ObjectListView.ItemActivated) opens the properties through IDialogService.
        var list = window.ResultsView.ListControl;
        list.UpdateLayout();
        var container = Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromItem(row));
        container.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(context.Company.JanKowalski, Assert.Single(dialogs.OpenedProperties), ignoreCase: true);

        // Custom search: the LDAP filter is edited by hand and validated.
        find.Kind = find.Kinds.Single(k => k.Value == FindKind.Custom);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(find.IsRawFilter);
        window.FilterBox.Text = "(&(objectClass=user)(l=Kraków)";
        await UiWait.UntilAsync(() => find.FilterError is not null, "filter validated");
        Assert.False(find.FindNowCommand.CanExecute(null));

        find.ClearAllCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(find.IsRawFilter);
        Assert.Null(find.FilterError);
        context.AssertHealthy();
    });

    // ---- DialogService wiring -----------------------------------------------------------------------------------

    [Fact]
    public Task DialogService_HostsTheModalDialogs_AndReturnsTheirResults() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync();
        await using var context = created;
        var session = await context.ConnectAsync();
        var service = dialogs.Inner;

        var pickContainer = UiWait.DriveModalAsync<ContainerPickerWindow>(async w =>
        {
            await UiWait.InitializedAsync(w.Initialization);
            w.ViewModel.OkCommand.Execute(null);
        });
        var container = await service.PickContainerAsync(session, context.Company.SalesOu);
        await pickContainer;
        Assert.Equal(context.Company.SalesOu, container, ignoreCase: true);

        var pickObjects = UiWait.DriveModalAsync<ObjectPickerWindow>(async w =>
        {
            w.ViewModel.SearchText = "Wójcik";
            await w.ViewModel.OkCommand.ExecuteAsync(null);
        });
        var picked = await service.PickObjectsAsync(session, [ObjectKind.User], multiSelect: false);
        await pickObjects;
        Assert.Equal([context.Company.MalgorzataWojcik], picked, StringComparer.OrdinalIgnoreCase);

        var newOu = UiWait.DriveModalAsync<NewObjectWindow>(async w =>
        {
            await UiWait.InitializedAsync(w.Initialization);
            w.ViewModel.Name = "Projekty";
            await w.ViewModel.CreateCommand.ExecuteAsync(null);
        });
        var createdDn = await service.ShowNewObjectAsync(ObjectKind.OrganizationalUnit, context.Company.CompanyOu);
        await newOu;
        Assert.NotNull(createdDn);
        Assert.True(context.Directory.Session.Exists(createdDn!));

        var reset = UiWait.DriveModalAsync<ResetPasswordWindow>(async w =>
        {
            await UiWait.InitializedAsync(w.Initialization);
            w.PasswordBox.Password = "Nowe!Haslo2026";
            w.ConfirmPasswordBox.Password = "Nowe!Haslo2026";
            await w.ViewModel.ResetCommand.ExecuteAsync(null);
            await w.ViewModel.CancelCommand.ExecuteAsync(null);
        });
        await service.ShowResetPasswordAsync([context.Company.BartlomiejKozlowski]);
        await reset;
        Assert.Equal(context.Company.BartlomiejKozlowski, Assert.Single(context.Directory.Session.PasswordResets).DistinguishedName, ignoreCase: true);

        // Find is modeless: the call returns while the window stays open.
        await service.ShowFindAsync(context.Company.EmployeesOu);
        var findWindow = Assert.Single(Application.Current.Windows.OfType<FindWindow>());
        findWindow.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(findWindow.IsVisible);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static IDialogViewModelFactory Factory(UiTestContext context) => context.Services.GetRequiredService<IDialogViewModelFactory>();

    private static IWindowService Windows(UiTestContext context) => context.Services.GetRequiredService<IWindowService>();

    private static async Task<PropertiesWindow> OpenSheetAsync(UiTestContext context, string distinguishedName)
    {
        var window = (PropertiesWindow)Windows(context).CreateWindow(Factory(context).CreatePropertySheet(distinguishedName));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(window.ViewModel.LoadError);
        Assert.NotEmpty(window.ViewModel.Pages);
        return window;
    }

    /// <summary>
    /// Shows every tab (with what each page loads lazily: "Other..." lists, the nested membership list, the value editor
    /// of every attribute syntax) and returns the field view model types rendered with the field chrome.
    /// </summary>
    private static async Task<HashSet<Type>> VisitAllPagesAsync(PropertiesWindow window)
    {
        var sheet = window.ViewModel;
        var chrome = (ControlTemplate)window.FindResource("FieldChrome");
        var rendered = new HashSet<Type>();
        foreach (var page in sheet.Pages.ToList())
        {
            sheet.SelectedPage = page;
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Same(page, window.PageTabs.SelectedItem);
            switch (page)
            {
                case FieldsPageViewModel:
                    foreach (var toggle in UiWait.Descendants<ToggleButton>(window).Where(t => t.Name == "OthersToggle" && t.IsVisible).ToList())
                    {
                        toggle.IsChecked = true;
                    }

                    await UiTestSupport.WaitForIdleAsync(rounds: 2);
                    break;
                case MembershipPageViewModel membership:
                    await UiWait.UntilAsync(() => !membership.IsLoading, $"{page.Key} loaded");
                    var tabs = UiWait.Descendants<TabControl>(window).Single(t => t.Name == "MembershipTabs");
                    tabs.SelectedIndex = 1;
                    await UiWait.UntilAsync(() => membership.IsNestedLoaded || membership.NestedError is not null, $"{page.Key} nested list");
                    await UiTestSupport.WaitForIdleAsync(rounds: 2);
                    Assert.Null(membership.NestedError);
                    tabs.SelectedIndex = 0;
                    await UiTestSupport.WaitForIdleAsync(rounds: 2);
                    break;
                case AttributeEditorPageViewModel editor:
                    await UiWait.UntilAsync(() => !editor.IsLoading && editor.Rows.Count > 0, "attributes read");
                    foreach (var kind in Enum.GetValues<AttributeEditorKind>())
                    {
                        if (editor.Rows.FirstOrDefault(r => r.EditorKind == kind) is not { } row)
                        {
                            continue;
                        }

                        editor.EditCommand.Execute(row);
                        await UiTestSupport.WaitForIdleAsync(rounds: 2);
                        Assert.NotNull(editor.Editor);
                        editor.Editor!.CancelCommand.Execute(null);
                        await UiTestSupport.WaitForIdleAsync(rounds: 1);
                        Assert.Null(editor.Editor);
                    }

                    break;
            }

            rendered.UnionWith(UiWait.Descendants<ContentControl>(window)
                .Where(c => ReferenceEquals(c.Template, chrome))
                .Select(c => c.DataContext?.GetType())
                .OfType<Type>());
        }

        return rendered;
    }
}
