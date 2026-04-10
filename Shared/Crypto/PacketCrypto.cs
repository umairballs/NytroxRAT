using System.Security.Cryptography;
using System.Text;

namespace NytroxRAT.Shared.Crypto;

/// <summary>
/// AES-256-GCM encryption for all WebSocket traffic.
/// Key is derived from the shared secret using PBKDF2.
/// </summary>
public static class PacketCrypto
{
    private const int KeySize   = 32; // 256-bit
    private const int NonceSize = 12; // 96-bit GCM nonce
    private const int TagSize   = 16; // 128-bit GCM tag
    private static readonly byte[] Salt = "NytroxRAT.Salt.v1"u8.ToArray();

    public static byte[] DeriveKey(string sharedSecret)
    {
        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(sharedSecret), Salt,
            100_000, HashAlgorithmName.SHA256);
        return kdf.GetBytes(KeySize);
    }

    /// <summary>Returns nonce (12) + ciphertext + tag (16)</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);
        return result;
    }

    /// <summary>Accepts the format produced by Encrypt.</summary>
    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        var nonce      = data[..NonceSize];
        var tag        = data[^TagSize..];
        var ciphertext = data[NonceSize..^TagSize];
        var plaintext  = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static string EncryptJson(string json, byte[] key)
        => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(json), key));

    public static string DecryptJson(string base64, byte[] key)
        => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64), key));
}
