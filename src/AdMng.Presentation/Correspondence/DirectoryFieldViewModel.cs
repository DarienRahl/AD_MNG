using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>An editable <see cref="DirectoryFieldDefinition"/>: name, U1 expression, target and its validation error.</summary>
public sealed partial class DirectoryFieldViewModel : ObservableObject
{
    public DirectoryFieldViewModel(DirectoryFieldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Name = definition.Name;
        Expression = definition.Expression;
        Target = definition.Target;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>A U1 template expression (<c>{givenName}</c>, <c>{$honorific:biernik}</c> …).</summary>
    [ObservableProperty]
    public partial string Expression { get; set; }

    [ObservableProperty]
    public partial DirectoryFieldTarget Target { get; set; }

    /// <summary>Why the definition cannot be used (invalid expression, forbidden attribute), or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The definition as edited.</summary>
    public DirectoryFieldDefinition ToDefinition() => new(Name, Expression, Target);

    public override string ToString() => $"{nameof(DirectoryFieldViewModel)} {Name}";
}
