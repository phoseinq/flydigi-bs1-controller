using System;
using System.Security.Cryptography;
using System.Text;

namespace Bs1Cooler.App;

/// The firmware images contain Flydigi's code, so the repo only carries them sealed (AES-GCM): not readable
/// or usable as plain files. The app opens them in memory right before flashing.
/// ponytail: the key is derived in the app, so this keeps casual hands off, not a determined reverser.
public static class FirmwareBox
{
    private static readonly byte[] Magic = "BS1F"u8.ToArray();

    private static byte[] Key() => SHA256.HashData(Encoding.UTF8.GetBytes("bs1-cooler/" + nameof(FirmwareBox) + "/" + 0x5AA5.ToString("X")));

    public static byte[] Seal(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    public static byte[] Open(byte[] sealedData)
    {
        if (sealedData.Length < 32 || !sealedData.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidOperationException("not a sealed firmware image");
        var plain = new byte[sealedData.Length - 32];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(sealedData.AsSpan(4, 12), sealedData.AsSpan(32), sealedData.AsSpan(16, 16), plain);  // throws if altered
        return plain;
    }
}
