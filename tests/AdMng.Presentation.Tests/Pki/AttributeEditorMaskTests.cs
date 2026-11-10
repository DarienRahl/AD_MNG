using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The attribute-editor mask of P1 decision 7 (docs/dev/p-certificate-services.md 2.6): key material
/// (<see cref="SecretAttributes.IsNeverShown"/>) shows <see cref="SecretAttributes.Mask"/>, is read only with the reason
/// <i>wartość poufna</i>, cannot be edited or copied and stages nothing; operator-readable secrets (LAPS) and ordinary
/// binary attributes keep today's behaviour.
/// </summary>
public sealed class AttributeEditorMaskTests
{
    private const string Pkcs12 = "userPKCS12";

    [Fact]
    public async Task UserPkcs12_ShowsTheMask_IsReadOnly_AndCannotBeEditedOrCopied()
    {
        var harness = await PkiHarness.CreateAsync();
        var (sheet, editor) = await OpenAsync(harness.B2, harness.Company.JanKowalski);

        var row = Assert.Single(editor.Rows, r => r.Attribute == Pkcs12);
        Assert.True(row.IsMasked);
        Assert.True(row.HasValue);
        Assert.Equal(SecretAttributes.Mask, row.ValueText);
        Assert.True(row.IsReadOnly);
        Assert.Equal("Wartość poufna (materiał klucza): nie jest wyświetlana, kopiowana ani edytowana", row.ReadOnlyReason);
        Assert.Equal(AttributeEditorKind.Binary, row.EditorKind);

        Assert.False(editor.EditCommand.CanExecute(row));
        Assert.False(editor.CopyValueCommand.CanExecute(row));
        editor.SelectedRow = row;
        Assert.False(editor.EditCommand.CanExecute(null));
        Assert.False(editor.CopyValueCommand.CanExecute(null));

        // Even when invoked directly (a double click, a key binding), nothing opens and nothing reaches the clipboard.
        editor.EditCommand.Execute(row);
        Assert.Null(editor.Editor);
        editor.CopyValueCommand.Execute(row);
        Assert.Null(harness.B2.Clipboard.Text);
        Assert.Empty(harness.B2.Notifications.Items);

        editor.ShowOnlyWritable = true;
        Assert.DoesNotContain(editor.Rows, r => r.Attribute == Pkcs12);
        Assert.False(sheet.HasChanges);
    }

    [Fact]
    public async Task AMaskedRow_NeverProducesAnOperation_NotEvenFromAStagedValue()
    {
        var harness = await PkiHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var before = harness.Session.GetValues(jan, Pkcs12);
        var (sheet, editor) = await OpenAsync(harness.B2, jan);
        var row = Assert.Single(editor.Rows, r => r.Attribute == Pkcs12);

        // A value staged behind the editor's back (the editor itself never opens for the row).
        row.PendingValues = [SecretAttributes.Mask];

        Assert.DoesNotContain(editor.GetOperations(), o => o is SetValuesOperation { Attribute: Pkcs12 });
        Assert.Empty(editor.GetOperations());
        Assert.Equal(SecretAttributes.Mask, row.ValueText);
        if (sheet.HasChanges)
        {
            await sheet.ApplyChangesAsync();
        }

        Assert.Equal(before, harness.Session.GetValues(jan, Pkcs12));
        Assert.DoesNotContain(harness.Session.Operations, o => o.IsWrite && o.Modifications.Any(m => string.Equals(m.Attribute, Pkcs12, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AMaskedRowWithoutAValue_SaysNotSet_AndStaysMasked()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        var (_, editor) = await OpenAsync(harness.B2, harness.Company.LukaszWisniewski);

        var row = Assert.Single(editor.Rows, r => r.Attribute == Pkcs12);
        Assert.True(row.IsMasked);
        Assert.False(row.HasValue);
        Assert.Equal(harness.B2.Localizer[L.AttributeEditor.NotSet], row.ValueText);
        Assert.Equal("Confidential value (key material): never shown, copied or edited", row.ReadOnlyReason);
        Assert.False(editor.EditCommand.CanExecute(row));
    }

    [Fact]
    public async Task ALapsPassword_KeepsTodaysDisplay_AndItsSecureCopy()
    {
        var harness = await B2Harness.CreateAsync();
        var computer = harness.Company.Computer1;
        harness.Session.SetValues(computer, "ms-Mcs-AdmPwd", "Tajne!Hasło-Ąę");
        var (_, editor) = await OpenAsync(harness, computer);

        var row = Assert.Single(editor.Rows, r => r.Attribute == "ms-Mcs-AdmPwd");
        Assert.False(row.IsMasked);
        Assert.Equal("Tajne!Hasło-Ąę", row.ValueText);
        Assert.True(editor.CopyValueCommand.CanExecute(row));
        Assert.True(editor.EditCommand.CanExecute(row));

        editor.CopyValueCommand.Execute(row);
        Assert.Equal("Tajne!Hasło-Ąę", harness.Clipboard.Text);
        Assert.True(harness.Clipboard.IsSecret);
    }

    [Fact]
    public async Task AnOrdinaryBinaryAttribute_IsNotMasked()
    {
        var harness = await PkiHarness.CreateAsync();
        var (_, editor) = await OpenAsync(harness.B2, harness.Company.JanKowalski);

        var certificate = Assert.Single(editor.Rows, r => r.Attribute == "userCertificate");
        Assert.False(certificate.IsMasked);
        Assert.NotEqual(SecretAttributes.Mask, certificate.ValueText);
        Assert.True(editor.EditCommand.CanExecute(certificate));
        editor.EditCommand.Execute(certificate);
        Assert.NotNull(editor.Editor);
        Assert.True(editor.Editor!.IsReadOnly);
        Assert.NotEmpty(editor.Editor.Value);
    }

    /// <summary>
    /// The UI test that visits every tab opens the value editor of the first row of each editor kind: on every sheet of the
    /// sample (with and without the PKI sample) that row is never a masked one, so the editor opens.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheFirstRowOfEachEditorKind_StillOpensItsEditor(bool publicKeySample)
    {
        var harness = await B2Harness.CreateAsync();
        if (publicKeySample)
        {
            harness.Builder.AddPublicKeySample(harness.Company);
        }

        var contact = harness.Builder.AddContact("Anna", "Zając", harness.Company.SalesOu);
        foreach (var dn in new[] { harness.Company.JanKowalski, harness.Company.Computer1, harness.Company.ItOu, harness.Company.GroupIt, contact })
        {
            var (_, editor) = await OpenAsync(harness, dn);
            if (dn == harness.Company.JanKowalski)
            {
                Assert.Contains(editor.Rows, r => r.IsMasked);
            }

            foreach (var kind in Enum.GetValues<AttributeEditorKind>())
            {
                if (editor.Rows.FirstOrDefault(r => r.EditorKind == kind) is not { } row)
                {
                    continue;
                }

                editor.EditCommand.Execute(row);
                Assert.True(editor.Editor is not null, $"{dn}: {kind} ({row.Attribute})");
                editor.Editor!.CancelCommand.Execute(null);
            }
        }
    }

    private static async Task<(PropertySheetViewModel Sheet, AttributeEditorPageViewModel Editor)> OpenAsync(B2Harness harness, string dn)
    {
        var sheet = harness.Factory.CreatePropertySheet(dn);
        await sheet.InitializeAsync();
        var editor = Assert.Single(sheet.Pages.OfType<AttributeEditorPageViewModel>());
        await editor.ActivateAsync();
        Assert.False(editor.IsLoading);
        Assert.NotEmpty(editor.Rows);
        return (sheet, editor);
    }
}
