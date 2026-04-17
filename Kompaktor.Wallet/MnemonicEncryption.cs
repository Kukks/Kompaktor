using System.Security.Cryptography;
using System.Text;

namespace Kompaktor.Wallet;

/// <summary>
/// AES-256-GCM encryption for BIP-39 mnemonics, keyed via PBKDF2 from a user passphrase.
/// </summary>
public static class MnemonicEncryption
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;   // AES-GCM standard
    private const int KeySize = 32;   // AES-256
    private const int Iterations = 100_000;

    /// <summary>
    /// Encrypts a mnemonic string with AES-256-GCM.
    /// Returns (ciphertext_with_nonce_and_tag, salt).
    /// Layout: [12-byte nonce][ciphertext][16-byte tag]
    /// </summary>
    public static (byte[] Encrypted, byte[] Salt) Encrypt(string mnemonic, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(passphrase, salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(mnemonic);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Pack as [nonce][ciphertext][tag]
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return (result, salt);
    }

    /// <summary>
    /// Decrypts a mnemonic. Throws CryptographicException on wrong passphrase / tampered data.
    /// </summary>
    public static string Decrypt(byte[] encrypted, byte[] salt, string passphrase)
    {
        var key = DeriveKey(passphrase, salt);

        var nonce = encrypted.AsSpan(0, NonceSize);
        var tag = encrypted.AsSpan(encrypted.Length - TagSize);
        var ciphertext = encrypted.AsSpan(NonceSize, encrypted.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
