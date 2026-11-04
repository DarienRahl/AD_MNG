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
        "fedya", "mattia", "luka", "savva", "attila", "aditya", "krishna", "chaitanya", "surya", "rama", "shiva", "ravindra",
        "rajendra", "narendra", "mahendra", "surendra", "jitendra", "devendra", "upendra", "virendra", "yahya", "zakaria",
        "taha", "takuya", "kazuya", "tetsuya", "kenta", "yuta", "ryota", "shota");

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
        "chantal", "yvette", "inge", "anke", "frauke", "ilse", "gisele", "beatriz", "raquel", "pilar", "dolores", "swati",
        "jyoti", "preeti", "priti", "shruti", "lakshmi", "gauri", "aarti", "arti", "bharti", "shanti", "parvati", "devi",
        "pallavi", "shalini", "nalini", "malini", "kirti", "deepti", "smriti", "aditi", "nandini", "sakshi", "ayse", "elif",
        "zeynep", "merve", "katherine", "kathryn", "kate", "katie", "jane", "janet", "grace", "rose", "stephanie", "diane",
        "dianne", "leah", "amelie", "danielle", "gabrielle", "joanne", "suzanne", "valerie", "melanie", "elaine", "eleanor",
        "josephine", "pauline", "heather", "holly", "wendy", "judy", "betty", "sally", "ivy", "hazel", "ljubow", "lyubov",
        "ljubov");

    /// <summary>
    /// Frequent Polish given names: in English messages the "-a" rule decides for these (and for names with Polish
    /// letters) only – "Jan Nowak" with preferredLanguage=en is still "Dear Mr Nowak". No name used for the other gender
    /// in English ("Marian").
    /// </summary>
    public static IReadOnlySet<string> Polish { get; } = Keys(
        "jan", "piotr", "krzysztof", "andrzej", "tomasz", "paweł", "michał", "marcin", "grzegorz", "józef", "łukasz", "adam",
        "zbigniew", "jerzy", "tadeusz", "mateusz", "dariusz", "mariusz", "wojciech", "ryszard", "jakub", "henryk", "robert",
        "rafał", "kazimierz", "jacek", "maciej", "kamil", "janusz", "marek", "władysław", "stanisław", "mirosław", "sławomir",
        "bogdan", "roman", "zdzisław", "edward", "mieczysław", "damian", "wiesław", "czesław", "leszek", "daniel", "karol",
        "przemysław", "bartosz", "sebastian", "artur", "patryk", "dawid", "szymon", "kacper", "filip", "wiktor", "igor",
        "hubert", "dominik", "oskar", "antoni", "franciszek", "aleksander", "stefan", "zenon", "waldemar", "bogusław",
        "eugeniusz", "lech", "witold", "ireneusz", "emil", "adrian", "konrad", "krystian", "norbert", "arkadiusz", "jarosław",
        "radosław", "bolesław", "gabriel", "julian", "ludwik", "leon", "feliks", "ignacy", "wacław", "zygmunt", "bronisław",
        "edmund", "kornel", "cezary", "remigiusz", "tymoteusz", "bartłomiej", "miłosz", "oliwer", "nikodem", "borys",
        "ksawery", "jędrzej", "maksymilian", "fabian", "benedykt", "wincenty", "bernard", "tymon", "leonard", "olaf", "eryk",
        "marcel", "iwo", "kajetan", "gustaw", "seweryn", "sylwester", "teodor", "tobiasz", "mikołaj", "lucjan", "florian",
        "juliusz", "klemens", "konstanty", "leopold", "maurycy", "walenty", "wawrzyniec", "włodzimierz", "bogumił", "anna",
        "maria", "katarzyna", "małgorzata", "agnieszka", "krystyna", "barbara", "ewa", "elżbieta", "zofia", "janina", "teresa",
        "joanna", "magdalena", "monika", "jadwiga", "danuta", "irena", "halina", "helena", "beata", "aleksandra", "marta",
        "dorota", "marianna", "grażyna", "jolanta", "stanisława", "iwona", "karolina", "bożena", "urszula", "justyna",
        "renata", "alicja", "paulina", "sylwia", "natalia", "wanda", "agata", "aneta", "izabela", "ewelina", "marzena",
        "wiesława", "genowefa", "patrycja", "kazimiera", "edyta", "stefania", "julia", "zuzanna", "wiktoria", "oliwia", "maja",
        "lena", "hanna", "amelia", "gabriela", "kinga", "emilia", "weronika", "klaudia", "dominika", "angelika", "martyna",
        "kamila", "daria", "nina", "anita", "lucyna", "mirosława", "bogumiła", "czesława", "władysława", "ilona", "lidia",
        "kornelia", "regina", "róża", "celina", "leokadia", "zdzisława", "bogusława", "henryka", "józefa", "zyta", "aniela",
        "franciszka", "antonina", "apolonia", "bronisława", "eugenia", "felicja", "honorata", "ludmiła", "rozalia", "sabina",
        "wioletta", "żaneta", "adrianna", "aurelia", "blanka", "elwira", "eliza", "jagoda", "jowita", "judyta", "klara",
        "liliana", "malwina", "marcelina", "marlena", "matylda", "milena", "roksana", "alina", "cecylia", "faustyna", "kalina",
        "karina", "łucja", "michalina", "dagmara", "magda", "zenona", "teodora");

    /// <summary>Names used for both genders (no signal from the name; the surname may decide).</summary>
    public static IReadOnlySet<string> Unisex { get; } = Keys(
        "sasza", "sasha", "saša", "zhenya", "andrea", "nicola", "nikola", "kim", "alex", "robin", "jean", "dominique", "toni", "kai",
        "noa", "maxime", "akira");

    private static FrozenSet<string> Keys(params string[] names) =>
        names.Select(SalutationKey.Of).ToFrozenSet(StringComparer.Ordinal);
}
