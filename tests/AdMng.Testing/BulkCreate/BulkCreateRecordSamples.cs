using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AdMng.Core.Schema;

namespace AdMng.Testing.BulkCreate;

/// <summary>
/// The reflection checks of the F3 contract tests (docs/dev/phase3-bulk-create.md 2.8, 2.11): every record and record struct
/// of a namespace declares its own <c>ToString</c> (not the compiler-generated one that prints every member), and an instance
/// filled with <see cref="Sentinels"/> – Polish names, an address, a phone number, a DN and a password – prints none of them.
/// Members named <c>Attribute</c>, <c>Code</c> or <c>Letter</c> get an LDAP name, a code or an Excel letter (not directory
/// data). New records an implementer adds are covered automatically; a member type this builder cannot fill fails the test
/// with its name (add it to <see cref="Value"/>).
/// </summary>
public static class BulkCreateRecordSamples
{
    /// <summary>The values a <c>ToString</c> must never print (rule 1, rule 2).</summary>
    public static IReadOnlyList<string> Sentinels { get; } =
    [
        "Łucja Żak-Sentinel", "lucja.zak@sentinel.firma.pl", "+48 600 999 111", "OU=Nowi pracownicy Sentinel,DC=contoso,DC=test",
        "Księgowość Sentinel", "Hasło!Sentinel2026", "kd999sentinel",
    ];

    private static readonly Dictionary<string, object> ConstantMembers = new(StringComparer.Ordinal)
    {
        ["Attribute"] = "department",
        ["Code"] = "DuplicateRow",
        ["Letter"] = "C",
    };

    private static int _next;

    /// <summary>The records and record structs of <paramref name="assembly"/> in <paramref name="namespacePrefix"/> (and below).</summary>
    public static IReadOnlyList<Type> Records(Assembly assembly, string namespacePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePrefix);
        return [.. assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && (ns == namespacePrefix || ns.StartsWith(namespacePrefix + ".", StringComparison.Ordinal)))
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && !t.IsDefined(typeof(CompilerGeneratedAttribute), false) && IsRecord(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>A record or record struct: the compiler synthesizes <c>PrintMembers(StringBuilder)</c> for both.</summary>
    public static bool IsRecord(Type type) =>
        type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(StringBuilder)]) is not null;

    /// <summary>The type declares a hand-written <c>ToString()</c> (not the synthesized one that calls <c>PrintMembers</c>).</summary>
    public static bool DeclaresOwnToString(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var method = type.GetMethod(nameof(ToString), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, Type.EmptyTypes);
        return method is not null && !method.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }

    /// <summary>The sentinels the <c>ToString</c> of a populated instance of <paramref name="type"/> printed (empty: value-free).</summary>
    public static IReadOnlyList<string> LeakedValues(Type type) => LeakedValues(Create(type));

    /// <summary>The sentinels <paramref name="instance"/>'s <c>ToString</c> printed.</summary>
    public static IReadOnlyList<string> LeakedValues(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var text = instance.ToString() ?? string.Empty;
        return [.. Sentinels.Where(s => text.Contains(s, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>A populated instance of <paramref name="type"/>: the widest public constructor, then every public init/set property.</summary>
    public static object Create(Type type) => Create(type, depth: 0);

    private static object Create(Type type, int depth)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (depth > 6)
        {
            throw new InvalidOperationException($"{type.Name}: records nest too deep for the sample builder.");
        }

        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        object instance;
        if (constructor is null)
        {
            instance = type.IsValueType
                ? Activator.CreateInstance(type)!
                : throw new InvalidOperationException($"{type.Name} has no public constructor.");
        }
        else
        {
            instance = constructor.Invoke([.. constructor.GetParameters().Select(p => Value(p.ParameterType, ToMemberName(p.Name), depth + 1))]);
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0 && property.Name != "EqualityContract")
            {
                property.SetValue(instance, Value(property.PropertyType, property.Name, depth + 1));
            }
        }

        return instance;
    }

    private static string ToMemberName(string? parameter) =>
        string.IsNullOrEmpty(parameter) ? string.Empty : char.ToUpperInvariant(parameter[0]) + parameter[1..];

    private static string NextSentinel() => Sentinels[(int)((uint)Interlocked.Increment(ref _next) % (uint)Sentinels.Count)];

    /// <summary>A populated value of <paramref name="type"/> for the member <paramref name="name"/>.</summary>
    private static object? Value(Type type, string name, int depth)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Value(underlying, name, depth);
        }

        if (type == typeof(string))
        {
            return ConstantMembers.TryGetValue(name, out var constant) ? constant : NextSentinel();
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1);
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(int))
        {
            return 7;
        }

        if (type == typeof(long))
        {
            return 7L;
        }

        if (type == typeof(char))
        {
            return 'x';
        }

        if (type == typeof(Guid))
        {
            return new Guid("8f3c1a2b-4d5e-4f60-8a71-92b3c4d5e6f7");
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(1));
        }

        if (type == typeof(DateTime))
        {
            return new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Local);
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (type == typeof(ISchemaCatalog))
        {
            return FakeSchema.Create(bulkCreate: true);
        }

        if (type == typeof(Func<string, string>))
        {
            return (Func<string, string>)(_ => NextSentinel());
        }

        if (type == typeof(Func<string, IReadOnlyList<string>>))
        {
            return (Func<string, IReadOnlyList<string>>)(_ => [NextSentinel()]);
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IEnumerable<>)
                || definition == typeof(IList<>) || definition == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments[0]))!;
                list.Add(Value(arguments[0], name, depth));
                return list;
            }

            if (definition == typeof(IReadOnlySet<>) || definition == typeof(ISet<>) || definition == typeof(HashSet<>))
            {
                var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(arguments[0]))!;
                set.GetType().GetMethod("Add")!.Invoke(set, [Value(arguments[0], name, depth)]);
                return set;
            }

            if (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(Dictionary<,>))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!;
                dictionary.Add(Value(arguments[0], name, depth)!, Value(arguments[1], name, depth));
                return dictionary;
            }
        }

        if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        {
            return Create(type, depth);
        }

        throw new InvalidOperationException($"The sample builder cannot fill a {type.Name} ('{name}'); add it to BulkCreateRecordSamples.Value.");
    }
}
