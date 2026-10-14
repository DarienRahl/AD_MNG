using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>Move up/down buttons are re-evaluated whenever the selected column list changes, not only on selection.</summary>
public class ColumnChooserCommandTests
{
    [Fact]
    public void AddRemoveAndReset_ReevaluateMoveUpAndMoveDown()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var chooser = new ColumnChooserViewModel(localizer, TestSchema.Create(), ["name", "objectClass", "description"], ColumnSets.Container);
        chooser.SelectedColumn = chooser.Selected[^1];
        Assert.False(chooser.MoveDownCommand.CanExecute(null));
        var moveUp = 0;
        var moveDown = 0;
        chooser.MoveUpCommand.CanExecuteChanged += (_, _) => moveUp++;
        chooser.MoveDownCommand.CanExecuteChanged += (_, _) => moveDown++;

        // The selected (last) column is no longer last: Move down becomes possible.
        chooser.AddCommand.Execute(chooser.Available.First(c => c.Attribute == Attr.Department));
        Assert.True(moveDown > 0);
        Assert.True(chooser.MoveDownCommand.CanExecute(null));

        moveDown = 0;
        chooser.RemoveCommand.Execute(chooser.Selected.Single(c => c.Attribute == Attr.Department));
        Assert.True(moveDown > 0);
        Assert.False(chooser.MoveDownCommand.CanExecute(null));

        // Reset puts the defaults back: the selected column may now be first or last.
        chooser.SelectedColumn = chooser.Selected[0];
        chooser.MoveDownCommand.Execute(null);
        Assert.True(chooser.MoveUpCommand.CanExecute(null));
        moveUp = 0;
        moveDown = 0;
        chooser.ResetToDefaultCommand.Execute(null);
        Assert.True(moveUp > 0);
        Assert.True(moveDown > 0);
        Assert.False(chooser.MoveUpCommand.CanExecute(null));
    }
}
