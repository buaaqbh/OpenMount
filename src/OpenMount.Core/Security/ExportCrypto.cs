using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMount.Core.Security;

/// <summary>Encrypted export envelope "openmount-export" v1: PBKDF2-HMAC-SHA256 + AES-256-GCM (design §8.6).</summary>
public static class ExportCrypto
{
    public const string Format = "openmount-export";
    public const int Version = 1;
    public const int Iterations = 600_000;

    public sealed class Envelope
    {
        [JsonPropertyName("format")] public string Format { get; set; } = ExportCrypto.Format;
        [JsonPropertyName("version")] public int Version { get; set; } = ExportCrypto.Version;
        [JsonPropertyName("encrypted")] public bool Encrypted { get; set; } = true;
        [JsonPropertyName("kdf")] public KdfInfo Kdf { get; set; } = new();
        [JsonPropertyName("cipher")] public CipherInfo Cipher { get; set; } = new();
        [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = string.Empty;
    }

    public sealed class KdfInfo
    {
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "PBKDF2-HMAC-SHA256";
        [JsonPropertyName("iterations")] public int Iterations { get; set; } = ExportCrypto.Iterations;
        [JsonPropertyName("salt")] public string Salt { get; set; } = string.Empty;
    }

    public sealed class CipherInfo
    {
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "AES-256-GCM";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
        [JsonPropertyName("tag")] public string Tag { get; set; } = string.Empty;
    }

    public static string Encrypt(string plaintextJson, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = Encoding.UTF8.GetBytes(plaintextJson);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var aad = Aad(Iterations, salt, nonce);
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        CryptographicOperations.ZeroMemory(key);
        var env = new Envelope
        {
            Kdf = new KdfInfo { Salt = Convert.ToBase64String(salt), Iterations = Iterations },
            Cipher = new CipherInfo { Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag) },
            Ciphertext = Convert.ToBase64String(ciphertext),
        };
        return JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Decrypt(string envelopeJson, string password)
    {
        Envelope env;
        try { env = JsonSerializer.Deserialize<Envelope>(envelopeJson) ?? throw new ExportFormatException("口令错误或文件已损坏"); }
        catch (JsonException) { throw new ExportFormatException("口令错误或文件已损坏"); }
        if (env.Format != Format || !env.Encrypted) throw new ExportFormatException("不是加密的 OpenMount 导出文件");
        if (env.Version != Version) throw new ExportFormatException($"不支持的导出文件版本 {env.Version}，请升级 OpenMount");
        if (env.Kdf.Algorithm != "PBKDF2-HMAC-SHA256" || env.Cipher.Algorithm != "AES-256-GCM") throw new ExportFormatException("不支持的加密算法");
        try
        {
            var salt = Convert.FromBase64String(env.Kdf.Salt);
            var nonce = Convert.FromBase64String(env.Cipher.Nonce);
            var tag = Convert.FromBase64String(env.Cipher.Tag);
            var ciphertext = Convert.FromBase64String(env.Ciphertext);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, env.Kdf.Iterations, HashAlgorithmName.SHA256, 32);
            var plaintext = new byte[ciphertext.Length];
            using (var gcm = new AesGcm(key, 16))
                gcm.Decrypt(nonce, ciphertext, tag, plaintext, Aad(env.Kdf.Iterations, salt, nonce));
            CryptographicOperations.ZeroMemory(key);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new ExportFormatException("口令错误或文件已损坏");
        }
    }

    private static byte[] Aad(int iterations, byte[] salt, byte[] nonce) =>
        Encoding.UTF8.GetBytes($"{Format}|{Version}|PBKDF2-HMAC-SHA256|{iterations}|{Convert.ToBase64String(salt)}|AES-256-GCM|{Convert.ToBase64String(nonce)}");
}

public sealed class ExportFormatException : Exception
{
    public ExportFormatException(string message) : base(message) { }
}
