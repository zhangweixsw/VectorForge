using System.Text.Json;

namespace VectorForge.Services;

public static class AtomicJsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        return TryLoad<T>(path, out var value) ? value! : fallback();
    }

    public static bool TryLoad<T>(string path, out T? value)
    {
        try
        {
            if (!File.Exists(path)) { value = default; return false; }
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            return value is not null;
        }
        catch { value = default; return false; }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
}
