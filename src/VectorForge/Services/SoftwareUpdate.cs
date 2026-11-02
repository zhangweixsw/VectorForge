using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace VectorForge.Services;

public sealed record UpdateOffer(string Product, string Platform, string Version, string Url, long Bytes, string Sha256, string Entry, string InstallerSha256);

public static class SoftwareUpdate
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    public static UpdateOffer Verify(string envelope, string publicKey)
    {
        using var json = JsonDocument.Parse(envelope);
        var data = Convert.FromBase64String(json.RootElement.GetProperty("payload").GetString()!);
        var signature = Convert.FromBase64String(json.RootElement.GetProperty("signature").GetString()!);
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new IOException("更新签名验证失败");
        var offer = JsonSerializer.Deserialize<UpdateOffer>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("清单为空");
        var uri = new Uri(offer.Url);
        if (offer.Product != "vectorforge" || offer.Platform != "win-x64" || !Version.TryParse(offer.Version, out _) ||
            uri.Scheme != "https" || uri.Host != "mootop.top" || offer.Bytes <= 0 || offer.Bytes > 536870912 ||
            offer.Sha256.Length != 64 || offer.InstallerSha256.Length != 64 || Path.GetFileName(offer.Entry) != offer.Entry || !offer.Entry.EndsWith(".exe"))
            throw new IOException("更新清单不符合产品要求");
        return offer;
    }
    public static async Task<UpdateOffer?> CheckAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await Client.GetAsync("https://mootop.top/wp-content/uploads/vectorforge/update.json", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var buffer = new MemoryStream(); var bytes = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(bytes, cancellation.Token)) > 0)
        { if (buffer.Length + count > 65536) throw new IOException("更新清单过大"); buffer.Write(bytes, 0, count); }
        using var keyStream = typeof(SoftwareUpdate).Assembly.GetManifestResourceStream("VectorForge.UpdatePublicKey.pem")!;
        using var keyReader = new StreamReader(keyStream);
        var offer = Verify(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), keyReader.ReadToEnd());
        return new Version(offer.Version) > typeof(SoftwareUpdate).Assembly.GetName().Version! ? offer : null;
    }
    public static void VerifyFile(string path, long bytes, string sha256)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新文件大小或 SHA-256 不符");
    }
    public static async Task<string> DownloadAsync(UpdateOffer offer)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "download.zip");
        using var response = await Client.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920]; int count; long total = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            while ((count = await input.ReadAsync(buffer, cancellation.Token)) > 0)
            { total += count; if (total > offer.Bytes) throw new IOException("下载超过声明大小"); await output.WriteAsync(buffer.AsMemory(0, count), cancellation.Token); }
        }
        VerifyFile(archive, offer.Bytes, offer.Sha256);
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.GetEntry(offer.Entry) ?? throw new IOException("缺少安装包");
        if (entry.Length > 536870912 || entry.Length < 1000000) throw new IOException("安装包大小异常");
        var installer = Path.Combine(directory, offer.Entry);
        using (var input = entry.Open()) using (var output = new FileStream(installer, FileMode.CreateNew)) input.CopyTo(output);
        VerifyFile(installer, entry.Length, offer.InstallerSha256);
        return installer;
    }
}
