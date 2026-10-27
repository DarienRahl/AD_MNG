using System.Collections.Frozen;

namespace AdMng.Core.Messaging.Salutations;

/// <summary>
/// Built-in given-name lists of the gender guess (U1d): exceptions to the Polish rule "a name ending in -a is feminine"
/// and names that give no signal. Names are stored as keys (lower case, no diacritics). Minimum content – extend, never
/// shrink.
/// </summary>
public static class GivenNameLists
{
    /// <summary>Masculine names ending in -a (Kuba, Barnaba, Mykoła …).</summary>
    public static IReadOnlySet<string> MasculineEndingInA { get; } = Keys(
        "barnaba", "bonawentura", "dyzma", "jarema", "kosma", "kuba", "zawisza", "juda", "jona", "sawa", "nikita", "mykyta",
        "mykola", "mykoła", "danila", "gawrila", "kuzma", "foma", "ilja", "ilia", "illia", "misza", "grisza", "kola", "wania",
        "kostia", "petia", "fedia", "tolia", "wolodia", "wasia", "dima", "luca", "joshua", "ezra", "elia", "mustafa", "hamza",
        "osama", "isa", "musa", "ilya", "misha", "grisha", "kostya", "vanya", "pasha", "kolya", "tolya", "volodya", "vasya", "petya",
        "fedya", "mattia", "luka");

    /// <summary>Feminine names that do not end in -a (Beatrycze, Noemi, Emily …).</summary>
    public static IReadOnlySet<string> FeminineNotEndingInA { get; } = Keys(
        "beatrycze", "noemi", "miriam", "ingrid", "nel", "abigail", "rut", "ruth", "karmen", "carmen", "inez", "ines", "mercedes",
        "nikol", "nicole", "dagmar", "astrid", "sigrid", "agnes", "rachel", "sarah", "hannah", "deborah", "esther", "judith",
        "lilian", "vivien", "michelle", "emily", "amy", "lily", "lili", "ann", "anne", "mary", "marie", "sophie", "chloe", "zoe",
        "julie", "claire", "jeanne", "louise", "elizabeth", "catherine", "kathleen", "margaret", "irene", "doris", "alice",
        "charlotte", "isabel", "heike", "elke", "silke", "ute", "gudrun", "karin", "birgit", "ellen", "helen", "kathrin", "jasmin",
        "lubow", "liubov", "jennifer", "karen", "susan", "megan", "sabine", "yvonne", "christine", "kerstin", "iris", "kirsten",
        "kristen", "caroline", "jacqueline", "madeleine", "simone", "nadine", "janine", "isabelle", "nathalie", "natalie",
        "elisabeth", "allison", "alison", "kimberly", "shirley", "dorothy", "nancy", "lucy", "lauren", "erin", "colleen",
        "maureen", "eileen", "carol", "carolyn", "evelyn", "marilyn", "sharon", "edith", "brigitte", "monique", "sylvie",
        "chantal", "yvette", "inge", "anke", "frauke", "ilse", "gisele", "beatriz", "raquel", "pilar", "dolores");

    /// <summary>Names used for both genders (no signal from the name; the surname may decide).</summary>
    public static IReadOnlySet<string> Unisex { get; } = Keys(
        "sasza", "sasha", "zhenya", "andrea", "nicola", "nikola", "kim", "alex", "robin", "jean", "dominique", "toni", "kai", "noa",
        "maxime", "akira");

    private static FrozenSet<string> Keys(params string[] names) =>
        names.Select(SalutationKey.Of).ToFrozenSet(StringComparer.Ordinal);
}
