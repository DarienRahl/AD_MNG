using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Testing.Pki;

/// <summary>
/// The reflection checks of the P1 contract tests (docs/dev/p-certificate-services.md 2.7, 2.10): every record and record
/// struct of a namespace declares its own <c>ToString</c> (not the compiler-generated one that prints every member), and a
/// populated instance – every string a Polish sample value of <see cref="PkiSamples.SampleTexts"/>, every list one element,
/// nested records populated the same way – prints none of them. New records an implementer adds are covered automatically;
/// a member type this builder cannot fill fails the test with its name (add it to <see cref="Value"/>).
/// </summary>
public static class PkiRecordSamples
{
    /// <summary>The finding-code style members whose string is a constant of the catalogue, not directory data.</summary>
    private static readonly HashSet<string> ConstantMembers = new(StringComparer.Ordinal) { "Code" };

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

    /// <summary>The sample values a record's <c>ToString</c> printed (empty: value-free).</summary>
    public static IReadOnlyList<string> LeakedValues(Type type)
    {
        var text = Create(type).ToString() ?? string.Empty;
        return [.. PkiSamples.SampleTexts.Where(s => text.Contains(s, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>A populated instance of <paramref name="type"/>: the widest public constructor, then every init/set property.</summary>
    public static object Create(Type type) => Create(type, depth: 0);

    private static object Create(Type type, int depth)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (depth > 5)
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
            instance = constructor.Invoke([.. constructor.GetParameters().Select(p => Value(p.ParameterType, p.Name ?? string.Empty, depth + 1))]);
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

    private static int _next;

    private static string NextText()
    {
        var texts = PkiSamples.SampleTexts;
        return texts[(int)((uint)Interlocked.Increment(ref _next) % (uint)texts.Count)];
    }

    /// <summary>A populated value of <paramref name="type"/> for the member <paramref name="name"/>.</summary>
    private static object? Value(Type type, string name, int depth)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Value(underlying, name, depth);
        }

        if (type == typeof(string))
        {
            return ConstantMembers.Contains(name) ? PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth : NextText();
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

        if (type == typeof(uint))
        {
            return 7u;
        }

        if (type == typeof(byte))
        {
            return (byte)7;
        }

        if (type == typeof(double))
        {
            return 1.5;
        }

        if (type == typeof(Guid))
        {
            return new Guid("8f3c1a2b-4d5e-4f60-8a71-92b3c4d5e6f7");
        }

        if (type == typeof(DateTimeOffset))
        {
            return PkiSamples.Now;
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (type == typeof(BigInteger))
        {
            return new BigInteger(42);
        }

        if (type == typeof(byte[]))
        {
            return Encoding.UTF8.GetBytes(NextText());
        }

        if (type == typeof(TimeProvider))
        {
            return TimeProvider.System;
        }

        if (type == typeof(GroupGraph))
        {
            return new GroupGraph([]);
        }

        if (type == typeof(PrivilegedAccessOptions))
        {
            return new PrivilegedAccessOptions { DomainSid = "S-1-5-21-3623811015-3361044348-30300820" };
        }

        if (type == typeof(AclObjectTypes))
        {
            return new AclObjectTypes(null, null);
        }

        if (type == typeof(PrincipalClassifier))
        {
            return new PrincipalClassifier("S-1-5-21-3623811015-3361044348-30300820", null, new PrivilegedAccessOptions(), null, []);
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

        throw new InvalidOperationException($"The sample builder cannot fill a {type.Name} ('{name}'); add it to PkiRecordSamples.Value.");
    }
}
