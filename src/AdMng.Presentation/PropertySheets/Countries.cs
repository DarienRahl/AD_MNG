namespace AdMng.Presentation.PropertySheets;

/// <summary>A country/region of ISO 3166-1 as Active Directory stores it.</summary>
/// <param name="Code">Alpha-2 code written to <c>c</c>, e.g. PL.</param>
/// <param name="NumericCode">Numeric code written to <c>countryCode</c>, e.g. 616.</param>
/// <remarks>The name written to <c>co</c> is the localized resource <c>Country.{Code}</c>.</remarks>
public sealed record CountryInfo(string Code, int NumericCode);

/// <summary>ISO 3166-1 country list (codes from iso-codes; names live in the resources, Country.XX).</summary>
public static class Countries
{
    /// <summary>All countries/regions ordered by code.</summary>
    public static IReadOnlyList<CountryInfo> All { get; } =
    [
        new("AD", 20), new("AE", 784), new("AF", 4), new("AG", 28), new("AI", 660), new("AL", 8), new("AM", 51),
        new("AO", 24), new("AQ", 10), new("AR", 32), new("AS", 16), new("AT", 40), new("AU", 36), new("AW", 533),
        new("AX", 248), new("AZ", 31), new("BA", 70), new("BB", 52), new("BD", 50), new("BE", 56), new("BF", 854),
        new("BG", 100), new("BH", 48), new("BI", 108), new("BJ", 204), new("BL", 652), new("BM", 60), new("BN", 96),
        new("BO", 68), new("BQ", 535), new("BR", 76), new("BS", 44), new("BT", 64), new("BV", 74), new("BW", 72),
        new("BY", 112), new("BZ", 84), new("CA", 124), new("CC", 166), new("CD", 180), new("CF", 140), new("CG", 178),
        new("CH", 756), new("CI", 384), new("CK", 184), new("CL", 152), new("CM", 120), new("CN", 156), new("CO", 170),
        new("CR", 188), new("CU", 192), new("CV", 132), new("CW", 531), new("CX", 162), new("CY", 196), new("CZ", 203),
        new("DE", 276), new("DJ", 262), new("DK", 208), new("DM", 212), new("DO", 214), new("DZ", 12), new("EC", 218),
        new("EE", 233), new("EG", 818), new("EH", 732), new("ER", 232), new("ES", 724), new("ET", 231), new("FI", 246),
        new("FJ", 242), new("FK", 238), new("FM", 583), new("FO", 234), new("FR", 250), new("GA", 266), new("GB", 826),
        new("GD", 308), new("GE", 268), new("GF", 254), new("GG", 831), new("GH", 288), new("GI", 292), new("GL", 304),
        new("GM", 270), new("GN", 324), new("GP", 312), new("GQ", 226), new("GR", 300), new("GS", 239), new("GT", 320),
        new("GU", 316), new("GW", 624), new("GY", 328), new("HK", 344), new("HM", 334), new("HN", 340), new("HR", 191),
        new("HT", 332), new("HU", 348), new("ID", 360), new("IE", 372), new("IL", 376), new("IM", 833), new("IN", 356),
        new("IO", 86), new("IQ", 368), new("IR", 364), new("IS", 352), new("IT", 380), new("JE", 832), new("JM", 388),
        new("JO", 400), new("JP", 392), new("KE", 404), new("KG", 417), new("KH", 116), new("KI", 296), new("KM", 174),
        new("KN", 659), new("KP", 408), new("KR", 410), new("KW", 414), new("KY", 136), new("KZ", 398), new("LA", 418),
        new("LB", 422), new("LC", 662), new("LI", 438), new("LK", 144), new("LR", 430), new("LS", 426), new("LT", 440),
        new("LU", 442), new("LV", 428), new("LY", 434), new("MA", 504), new("MC", 492), new("MD", 498), new("ME", 499),
        new("MF", 663), new("MG", 450), new("MH", 584), new("MK", 807), new("ML", 466), new("MM", 104), new("MN", 496),
        new("MO", 446), new("MP", 580), new("MQ", 474), new("MR", 478), new("MS", 500), new("MT", 470), new("MU", 480),
        new("MV", 462), new("MW", 454), new("MX", 484), new("MY", 458), new("MZ", 508), new("NA", 516), new("NC", 540),
        new("NE", 562), new("NF", 574), new("NG", 566), new("NI", 558), new("NL", 528), new("NO", 578), new("NP", 524),
        new("NR", 520), new("NU", 570), new("NZ", 554), new("OM", 512), new("PA", 591), new("PE", 604), new("PF", 258),
        new("PG", 598), new("PH", 608), new("PK", 586), new("PL", 616), new("PM", 666), new("PN", 612), new("PR", 630),
        new("PS", 275), new("PT", 620), new("PW", 585), new("PY", 600), new("QA", 634), new("RE", 638), new("RO", 642),
        new("RS", 688), new("RU", 643), new("RW", 646), new("SA", 682), new("SB", 90), new("SC", 690), new("SD", 729),
        new("SE", 752), new("SG", 702), new("SH", 654), new("SI", 705), new("SJ", 744), new("SK", 703), new("SL", 694),
        new("SM", 674), new("SN", 686), new("SO", 706), new("SR", 740), new("SS", 728), new("ST", 678), new("SV", 222),
        new("SX", 534), new("SY", 760), new("SZ", 748), new("TC", 796), new("TD", 148), new("TF", 260), new("TG", 768),
        new("TH", 764), new("TJ", 762), new("TK", 772), new("TL", 626), new("TM", 795), new("TN", 788), new("TO", 776),
        new("TR", 792), new("TT", 780), new("TV", 798), new("TW", 158), new("TZ", 834), new("UA", 804), new("UG", 800),
        new("UM", 581), new("US", 840), new("UY", 858), new("UZ", 860), new("VA", 336), new("VC", 670), new("VE", 862),
        new("VG", 92), new("VI", 850), new("VN", 704), new("VU", 548), new("WF", 876), new("WS", 882), new("YE", 887),
        new("YT", 175), new("ZA", 710), new("ZM", 894), new("ZW", 716),
    ];

    /// <summary>Finds a country by its alpha-2 code (case-insensitive).</summary>
    public static CountryInfo? FindByCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(c => string.Equals(c.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a country by its numeric code (0 = none).</summary>
    public static CountryInfo? FindByNumericCode(int? numericCode) =>
        numericCode is null or 0 ? null : All.FirstOrDefault(c => c.NumericCode == numericCode);
}
