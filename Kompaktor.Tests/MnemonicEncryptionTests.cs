using Kompaktor.Wallet;
using Xunit;

namespace Kompaktor.Tests;

public class MnemonicEncryptionTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrip()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var passphrase = "test-passphrase-123";

        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, passphrase);
        var decrypted = MnemonicEncryption.Decrypt(encrypted, salt, passphrase);

        Assert.Equal(mnemonic, decrypted);
    }

    [Fact]
    public void Decrypt_WrongPassphrase_Throws()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, "correct-password");

        Assert.ThrowsAny<Exception>(() => MnemonicEncryption.Decrypt(encrypted, salt, "wrong-password"));
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var passphrase = "password";

        var (enc1, _) = MnemonicEncryption.Encrypt(mnemonic, passphrase);
        var (enc2, _) = MnemonicEncryption.Encrypt(mnemonic, passphrase);

        // Different salts and nonces produce different ciphertext
        Assert.False(enc1.SequenceEqual(enc2));
    }

    [Fact]
    public void EncryptDecrypt_24WordMnemonic()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art";
        var passphrase = "long-passphrase";

        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, passphrase);
        var decrypted = MnemonicEncryption.Decrypt(encrypted, salt, passphrase);

        Assert.Equal(mnemonic, decrypted);
    }
}
