using System.Text;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Formatting.Webhooks;

public class StandardWebhookSignerTests
{
    private const string MessageId = "0192f3a45b6c7d8e9f00112233445566";
    private const long Timestamp = 1_792_000_000;
    private static readonly byte[] PolishBody = Encoding.UTF8.GetBytes(
        """{"type":"ad.user.attributes_changed","data":{"object":{"dn":"CN=Łukasz Wiśniewski,OU=Księgowość,DC=contoso,DC=com"}}}""");

    [Fact]
    public void StandardWebhooksSpecVector()
    {
        // Standard Webhooks specification test vector (section 14.5, verified with Python hmac).
        var secret = new SecretValue("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");
        var body = Encoding.UTF8.GetBytes("""{"test": 2432232314}""");

        var signature = StandardWebhookSigner.Sign(secret, "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, body);

        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
        Assert.Equal(signature, StandardWebhookSigner.CreateSignatureHeader(secret, null, "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, body));
        Assert.True(StandardWebhookSigner.Verify(signature, secret, "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, body));
    }

    [Fact]
    public void GitHubStyleVector()
    {
        // GitHub X-Hub-Signature-256 documentation vector (section 14.5, verified).
        var signature = StandardWebhookSigner.SignGitHub(new SecretValue("It's a Secret to Everybody"), Encoding.UTF8.GetBytes("Hello, World!"));

        Assert.Equal("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17", signature);
    }

    [Fact]
    public void Rotation_SendsTwoSignatures_EachVerifiableWithItsKey()
    {
        var current = new SecretValue(StandardWebhookSigner.GenerateSecret());
        var previous = new SecretValue(StandardWebhookSigner.GenerateSecret());

        var header = StandardWebhookSigner.CreateSignatureHeader(current, previous, MessageId, Timestamp, PolishBody);

        var parts = header.Split(' ');
        Assert.Equal(2, parts.Length);
        Assert.Equal(StandardWebhookSigner.Sign(current, MessageId, Timestamp, PolishBody), parts[0]);
        Assert.Equal(StandardWebhookSigner.Sign(previous, MessageId, Timestamp, PolishBody), parts[1]);
        Assert.NotEqual(parts[0], parts[1]);
        Assert.True(StandardWebhookSigner.Verify(header, current, MessageId, Timestamp, PolishBody));
        Assert.True(StandardWebhookSigner.Verify(header, previous, MessageId, Timestamp, PolishBody));
        Assert.False(StandardWebhookSigner.Verify(header, new SecretValue(StandardWebhookSigner.GenerateSecret()), MessageId, Timestamp, PolishBody));
    }

    [Fact]
    public void Signature_CoversIdTimestampAndEveryBodyByte()
    {
        var secret = new SecretValue(StandardWebhookSigner.GenerateSecret());
        var header = StandardWebhookSigner.Sign(secret, MessageId, Timestamp, PolishBody);
        var tampered = (byte[])PolishBody.Clone();
        tampered[^5] ^= 0x01;

        Assert.False(StandardWebhookSigner.Verify(header, secret, MessageId, Timestamp, tampered));
        Assert.False(StandardWebhookSigner.Verify(header, secret, MessageId, Timestamp + 1, PolishBody));
        Assert.False(StandardWebhookSigner.Verify(header, secret, "0192f3a45b6c7d8e9f00112233445567", Timestamp, PolishBody));
        Assert.True(StandardWebhookSigner.Verify(header, secret, MessageId, Timestamp, PolishBody));
    }

    [Fact]
    public void Verify_IgnoresOtherVersionsAndGarbage()
    {
        var secret = new SecretValue(StandardWebhookSigner.GenerateSecret());
        var signature = StandardWebhookSigner.Sign(secret, MessageId, Timestamp, PolishBody);

        Assert.True(StandardWebhookSigner.Verify("v1a,AAAA " + signature, secret, MessageId, Timestamp, PolishBody));
        Assert.False(StandardWebhookSigner.Verify("v1a," + signature[3..], secret, MessageId, Timestamp, PolishBody));
        Assert.False(StandardWebhookSigner.Verify(null, secret, MessageId, Timestamp, PolishBody));
        Assert.False(StandardWebhookSigner.Verify("   ", secret, MessageId, Timestamp, PolishBody));
        Assert.False(StandardWebhookSigner.Verify(signature, new SecretValue("hasło-bez-prefiksu"), MessageId, Timestamp, PolishBody));
        Assert.False(StandardWebhookSigner.Verify(signature, secret, "msg.z.kropką", Timestamp, PolishBody));
    }

    [Fact]
    public void GeneratedSecrets_AreWhsecWith32RandomBytes()
    {
        var first = StandardWebhookSigner.GenerateSecret();
        var second = StandardWebhookSigner.GenerateSecret();

        Assert.StartsWith(StandardWebhookSigner.SecretPrefix, first, StringComparison.Ordinal);
        Assert.Equal(StandardWebhookSigner.SecretPrefix.Length + 44, first.Length);
        Assert.NotEqual(first, second);
        Assert.True(StandardWebhookSigner.TryParseKey(first, out var key));
        Assert.Equal(StandardWebhookSigner.GeneratedKeyBytes, key.Length);
        Assert.Equal(Convert.FromBase64String(first[StandardWebhookSigner.SecretPrefix.Length..]), key);
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(32, true)]
    [InlineData(64, true)]
    [InlineData(23, false)]
    [InlineData(65, false)]
    public void KeyLength_MustBe24To64Bytes(int length, bool valid)
    {
        var secret = StandardWebhookSigner.SecretPrefix + Convert.ToBase64String(Enumerable.Range(1, length).Select(i => (byte)i).ToArray());

        Assert.Equal(valid, StandardWebhookSigner.IsValidSecret(secret));
        Assert.Equal(valid, StandardWebhookSigner.TryParseKey(secret, out var key));
        Assert.Equal(valid ? length : 0, key?.Length ?? 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whsec_")]
    [InlineData("MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")]
    [InlineData("WHSEC_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")]
    [InlineData("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLa$w")]
    [InlineData("whsec_Zażółć-gęślą-jaźń-zażółć-gęślą")]
    [InlineData("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSwA")]
    public void MalformedSecrets_AreRejected(string? secret)
    {
        Assert.False(StandardWebhookSigner.IsValidSecret(secret));
        Assert.False(StandardWebhookSigner.TryParseKey(secret, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void UnpaddedBase64_IsAccepted()
    {
        var padded = "whsec_" + Convert.ToBase64String(new byte[25]);    // 36 chars incl. "=="

        Assert.EndsWith("==", padded, StringComparison.Ordinal);
        Assert.True(StandardWebhookSigner.TryParseKey(padded.TrimEnd('='), out var key));
        Assert.Equal(25, key.Length);
    }

    [Fact]
    public void Sign_RejectsInvalidInput_WithoutRevealingTheSecret()
    {
        const string NotASecret = "Tajne!Hasło#2026";
        var valid = new SecretValue(StandardWebhookSigner.GenerateSecret());

        var error = Assert.Throws<ArgumentException>(() => StandardWebhookSigner.Sign(new SecretValue(NotASecret), MessageId, Timestamp, PolishBody));
        Assert.DoesNotContain(NotASecret, error.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => StandardWebhookSigner.Sign(valid, "msg.1", Timestamp, PolishBody));
        Assert.Throws<ArgumentException>(() => StandardWebhookSigner.Sign(valid, string.Empty, Timestamp, PolishBody));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardWebhookSigner.Sign(valid, MessageId, -1, PolishBody));
        Assert.Throws<ArgumentException>(() => StandardWebhookSigner.CreateSignatureHeader(valid, new SecretValue(NotASecret), MessageId, Timestamp, PolishBody));
    }

    [Fact]
    public void GitHubSignature_UsesTheWholeSecretText()
    {
        var secret = new SecretValue("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");

        var github = StandardWebhookSigner.SignGitHub(secret, PolishBody);

        Assert.Matches("^sha256=[0-9a-f]{64}$", github);
        Assert.Equal(
            "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret.Reveal()), PolishBody)),
            github);
    }
}
