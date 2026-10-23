using System.Security.Cryptography;
using System.Text;

namespace OpenMount.Core.Security;

/// <summary>
/// rclone "obscure" encoding: AES-256-CTR with a fixed, public key and a 16-byte IV prefix, base64url without padding.
/// This is an encoding, not encryption. Secrets at rest are protected by the Windows Credential Manager instead.
/// <para>
/// The IV is derived from the secret rather than random: rclone appends a hash of the remote's configuration
/// (including the obscured password) to the remote name and uses that name for the VFS cache directory, so the
/// encoding must be stable across runs or dirty cache entries would never be found again after a restart (FR-22.6).
/// </para>
/// </summary>
public static class Obscure
{
    private static readonly byte[] Key = Convert.FromHexString("9c935b48730a554d6bfd7c63c886a92bd390198eb8128afbf4de162b8b95f638");
    private static readonly byte[] IvDomain = Encoding.UTF8.GetBytes("openmount-obscure-iv-v1|");

    public static string Encode(string plain)
    {
        var iv = SHA256.HashData(IvDomain.Concat(Encoding.UTF8.GetBytes(plain)).ToArray()).AsSpan(0, 16).ToArray();
        return EncodeWithIv(plain, iv);
    }

    /// <summary>Random-IV variant matching the upstream tool byte for byte in spirit; not used for mounts.</summary>
    public static string EncodeRandom(string plain) => EncodeWithIv(plain, RandomNumberGenerator.GetBytes(16));

    internal static string EncodeWithIv(string plain, byte[] iv)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var output = new byte[16 + data.Length];
        iv.CopyTo(output, 0);
        Ctr(iv, data, output.AsSpan(16));
        return Base64UrlNoPad(output);
    }

    public static string Decode(string obscured)
    {
        var bytes = FromBase64UrlNoPad(obscured);
        if (bytes.Length < 16) throw new FormatException("obscured value too short");
        var iv = bytes[..16];
        var plain = new byte[bytes.Length - 16];
        Ctr(iv, bytes.AsSpan(16).ToArray(), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static void Ctr(byte[] iv, byte[] input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        var counter = (byte[])iv.Clone();
        var block = new byte[16];
        for (var i = 0; i < input.Length; i += 16)
        {
            enc.TransformBlock(counter, 0, 16, block, 0);
            for (var j = 0; j < 16 && i + j < input.Length; j++)
                output[i + j] = (byte)(input[i + j] ^ block[j]);
            for (var k = 15; k >= 0 && ++counter[k] == 0; k--) { }
        }
    }

    private static string Base64UrlNoPad(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64UrlNoPad(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
        }
        return Convert.FromBase64String(b);
    }
}
