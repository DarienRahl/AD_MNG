using System.Reflection;
using System.Runtime.InteropServices;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook.Tests;

/// <summary>
/// Every code AdMng.Outlook can produce is one of <see cref="OutlookFailureCodes"/> (the window and the U1 messages localize
/// exactly that list, so a code outside it would be shown raw).
/// </summary>
public class OutlookFailureCodesTests
{
    private static readonly uint[] HResults =
    [
        0x80080005, 0x800401E3, 0x8001010A, 0x80010001, 0x800706BE, 0x80010105, 0x80004004, 0x80010108, 0x800706BA, 0x800706BF,
        0x8004060C, 0x80040305, 0x80040116, 0x80040154, 0x800401F3, 0x80004005, 0x80070005,
    ];

    private static readonly string[] Steps =
    [
        "connect", "create", "account", "onBehalf", "recipients", "replyTo", "subject", "body", "receipt", "defer", "voting",
        "importance", "properties", "images", "attachments", "save", "inline", "send", "render", "delete", "diagnose",
        "testItem", "campaigns", "responses",
    ];

    [Fact]
    public void Codes_AreOnePerKind_DistinctAndInTheOrderOfTheEnum()
    {
        var kinds = Enum.GetValues<OutlookFailureKind>();

        Assert.Equal(kinds.Length, OutlookFailureCodes.Failures.Count);
        Assert.Equal(OutlookFailureCodes.Failures.Count, OutlookFailureCodes.Failures.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([.. kinds.Select(OutlookFailureCodes.Of)], OutlookFailureCodes.Failures);
        Assert.Equal([.. OutlookFailureCodes.Failures, OutlookFailureCodes.Sent], OutlookFailureCodes.All);
        Assert.Equal(OutlookFailureCodes.Unknown, OutlookFailureCodes.Of((OutlookFailureKind)999));
        Assert.All(OutlookFailureCodes.All, code => Assert.StartsWith("outlook.", code, StringComparison.Ordinal));
        Assert.True(OutlookFailureCodes.IsKnown("outlook.denied"));
        Assert.False(OutlookFailureCodes.IsKnown("outlook.unknownThing"));
        Assert.False(OutlookFailureCodes.IsKnown(null));
    }

    [Fact]
    public void TheEngine_UsesTheList()
    {
        Assert.Equal(OutlookFailureCodes.Failures, OutlookErrors.AllCodes);
        Assert.Equal(OutlookFailureCodes.Sent, OutlookUserMessageTransport.SentCode);
        foreach (var kind in Enum.GetValues<OutlookFailureKind>().Append((OutlookFailureKind)999))
        {
            Assert.Contains(OutlookErrors.Code(kind), OutlookFailureCodes.All);
            Assert.Contains(OutlookErrors.Failure(kind, "send").Code, OutlookFailureCodes.All);
        }
    }

    [Fact]
    public void EveryClassification_GivesAListedCode()
    {
        var exceptions = new List<Exception>
        {
            new OperationCanceledException(),
            new PlatformNotSupportedException(),
            new FileNotFoundException("brak pliku Łódź.pdf"),
            new DirectoryNotFoundException(),
            new InvalidOperationException("adres jan.kowalski@contoso.test"),
            new TargetInvocationException(new COMException("x", unchecked((int)0x8001010A))),
            new AggregateException(new COMException("x", unchecked((int)0x80010108))),
        };
        exceptions.AddRange(HResults.Select(hresult => (Exception)new COMException("Błąd dla małgorzata.wójcik@contoso.test", unchecked((int)hresult))));

        foreach (var exception in exceptions)
        {
            foreach (var step in Steps.Append<string?>(null))
            {
                foreach (var elevated in new[] { false, true })
                {
                    var failure = OutlookErrors.Classify(exception, step, elevated);
                    Assert.Contains(failure.Code, OutlookFailureCodes.All);
                    Assert.Equal(OutlookFailureCodes.Of(failure.Kind), failure.Code);
                }
            }
        }
    }

    [Fact]
    public void EveryDelivery_GivesAListedCode()
    {
        var results = new List<OutlookItemResult> { OutlookItemResult.Sent, OutlookItemResult.Draft, new(false, false, null) };
        results.AddRange(Enum.GetValues<OutlookFailureKind>().Select(kind => new OutlookItemResult(false, kind == OutlookFailureKind.Unknown, OutlookErrors.Failure(kind))));

        foreach (var result in results)
        {
            Assert.Contains(OutlookUserMessageTransport.ToDelivery(result).Code, OutlookFailureCodes.All);
        }
    }

    [Fact]
    public void EveryCodeConstantOfTheAssembly_IsListed()
    {
        var constants = typeof(OutlookClient).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.StartsWith("outlook.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(constants);
        Assert.All(constants, value => Assert.Contains(value, OutlookFailureCodes.All));
    }
}
