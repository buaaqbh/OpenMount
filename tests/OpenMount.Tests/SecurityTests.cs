using OpenMount.Core.Security;
using Xunit;

namespace OpenMount.Tests;

public class ObscureTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("p@ss word with spaces and 中文 和 emoji 🙂")]
    [InlineData("exactly16bytes!!")]
    [InlineData("this is a longer secret that spans several aes blocks to exercise the counter increment logic")]
    public void RoundTrips(string plain)
    {
        var obscured = Obscure.Encode(plain);
        Assert.DoesNotContain("=", obscured);
        Assert.DoesNotContain("+", obscured);
        Assert.DoesNotContain("/", obscured);
        Assert.Equal(plain, Obscure.Decode(obscured));
    }

    [Fact]
    public void IsDeterministicPerSecret_SoRcloneCacheDirsStayStable()
    {
        Assert.Equal(Obscure.Encode("same"), Obscure.Encode("same"));
        Assert.NotEqual(Obscure.Encode("same"), Obscure.Encode("same2"));
        Assert.NotEqual(Obscure.EncodeRandom("same"), Obscure.EncodeRandom("same"));
        Assert.Equal("same", Obscure.Decode(Obscure.EncodeRandom("same")));
    }

    [Fact]
    public void CounterCarriesAcrossBlocks()
    {
        var iv = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var plain = new string('x', 40);
        var encoded = Obscure.EncodeWithIv(plain, iv);
        Assert.Equal(plain, Obscure.Decode(encoded));
    }
}

public class RedactorTests
{
    private readonly Redactor _r = new();

    [Theory]
    [InlineData("Authorization: Bearer REVIEW_TEST_TOKEN", "REVIEW_TEST_TOKEN")]
    [InlineData("{\"password\":\"REVIEW_TEST_SECRET\"}", "REVIEW_TEST_SECRET")]
    [InlineData("password=hello world", "hello world")]
    [InlineData("https://user:REVIEW_PW@host/path", "REVIEW_PW")]
    [InlineData("RCLONE_CONFIG_OMX_PASS=abcdef", "abcdef")]
    [InlineData("RCLONE_RC_PASS=deadbeef", "deadbeef")]
    [InlineData("HTTPS_PROXY=http://u:PROXYPW@proxy:3128", "PROXYPW")]
    [InlineData("line1\n  \"pass\": \"a b c\",\nline3", "a b c")]
    [InlineData("{\"a\":{\"token\":\"T0K3N-nested\"}}", "T0K3N-nested")]
    [InlineData("key_file_pass = my passphrase here", "my passphrase here")]
    [InlineData("Proxy-Authorization: Basic QUJDREVGRw==", "QUJDREVGRw==")]
    [InlineData("{'password':'single quoted'}", "single quoted")]
    public void ReviewSamplesAreRedacted(string input, string secret)
    {
        var output = _r.Redact(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains(Redactor.Mask, output);
    }

    [Fact]
    public void KnownSecretsAreReplacedEverywhere_IncludingEncodedForms()
    {
        const string secret = "we!rd\"pa%ss\\word";
        _r.RegisterSecret(secret);
        var obscured = Obscure.Encode(secret);
        _r.RegisterObscuredForm(obscured);
        var input = $"msg: failed for {secret} and url https://h/?p={Uri.EscapeDataString(secret)} env={obscured}";
        var output = _r.Redact(input);
        Assert.DoesNotContain(secret, output);
        Assert.DoesNotContain(Uri.EscapeDataString(secret), output);
        Assert.DoesNotContain(obscured, output);
    }

    [Fact]
    public void LeavesOrdinaryLogLinesAlone()
    {
        const string line = "{\"level\":\"error\",\"msg\":\"failed to list: 401 Unauthorized\",\"source\":\"webdav/webdav.go:123\"}";
        Assert.Equal(line, _r.Redact(line));
    }

    [Fact]
    public void ShortSecretsAreNotRegisteredGlobally()
    {
        _r.RegisterSecret("ab");
        Assert.Equal("table", _r.Redact("table"));
    }
}

public class ExportCryptoTests
{
    [Fact]
    public void RoundTrips()
    {
        var env = ExportCrypto.Encrypt("{\"hello\":\"世界\"}", "p@ss");
        Assert.Contains("\"encrypted\": true", env);
        Assert.Equal("{\"hello\":\"世界\"}", ExportCrypto.Decrypt(env, "p@ss"));
    }

    [Fact]
    public void WrongPasswordFails()
    {
        var env = ExportCrypto.Encrypt("{}", "right");
        Assert.Throws<ExportFormatException>(() => ExportCrypto.Decrypt(env, "wrong"));
    }

    [Fact]
    public void TamperedCiphertextFails()
    {
        var env = ExportCrypto.Encrypt("{\"x\":1}", "pw");
        var tampered = env.Replace("\"ciphertext\": \"", "\"ciphertext\": \"AAAA");
        Assert.Throws<ExportFormatException>(() => ExportCrypto.Decrypt(tampered, "pw"));
    }
}
