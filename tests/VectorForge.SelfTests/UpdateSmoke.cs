using System.Net.Http;
using VectorForge.Services;

static class UpdateSmoke
{
    public static int Run()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "update-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VECTORFORGE_DATA_DIR", root);
        var envelope = new HttpClient().GetStringAsync("https://mootop.top/wp-content/uploads/vectorforge/update.json").GetAwaiter().GetResult();
        var offer = SoftwareUpdate.Verify(envelope, File.ReadAllText("src/VectorForge/Assets/UpdatePublicKey.pem"));
        if (SoftwareUpdate.CheckAsync().GetAwaiter().GetResult() is not null) throw new Exception("当前版本被错误识别为旧版");
        var path = SoftwareUpdate.DownloadAsync(offer).GetAwaiter().GetResult();
        Console.WriteLine("线上签名有效，当前无新版；下载、解压和安装包摘要校验通过（未执行安装）：" + path);
        return 0;
    }
}
