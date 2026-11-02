using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VectorForge.Services;

static class ReleaseTests
{
    public static void SignedUpdates()
    {
        using var rsa = RSA.Create(2048);
        var offer = new UpdateOffer("vectorforge", "win-x64", "18.0.2", "https://mootop.top/wp-content/uploads/VectorForge-v18.0.2.zip", 100,
            new string('a', 64), "VectorForge-Setup-v18.0.2.exe", new string('b', 64));
        string Envelope(byte[] payload, byte[] signature) => JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(offer);
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (SoftwareUpdate.Verify(Envelope(bytes, signature), rsa.ExportSubjectPublicKeyInfoPem()) != offer) throw new Exception("合法清单被拒绝");
        var bad = bytes.ToArray(); bad[0] ^= 1;
        try { SoftwareUpdate.Verify(Envelope(bad, signature), rsa.ExportSubjectPublicKeyInfoPem()); throw new Exception("篡改签名被接受"); } catch (IOException) { }
        var root = Path.Combine(Path.GetTempPath(), "VectorForgeUpdate", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "fixture.bin"); File.WriteAllText(path, "test");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            SoftwareUpdate.VerifyFile(path, new FileInfo(path).Length, hash);
            try { SoftwareUpdate.VerifyFile(path, 99, hash); throw new Exception("错误长度未拒绝"); } catch (IOException) { }
            try { SoftwareUpdate.VerifyFile(path, new FileInfo(path).Length, new string('0',64)); throw new Exception("错误摘要未拒绝"); } catch (IOException) { }
            if (File.ReadAllText(path) != "test") throw new Exception("验证失败修改文件");
        }
        finally { Directory.Delete(root, true); }
    }
}
