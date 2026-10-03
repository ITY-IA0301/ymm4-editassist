using System.Text.Json;
using System.Text.RegularExpressions;

namespace EditAssist.Core;

// Read only known keys. Never export the whole settings file or its path.
public sealed record HostVideoSettings(IReadOnlyList<string> ReaderOrder, bool? ProxyEnabled,
    int? ProxyMinimumMiB, int? ProxyScalePercent, int? ProxyCacheMiB, bool? DiskFallbackEnabled)
{
    public static HostVideoSettings? Read(string hostDirectory, string hostVersion)
    {
        if (!Regex.IsMatch(hostVersion, @"^\d+\.\d+\.\d+\.\d+$")) return null;
        var directory = Path.Combine(hostDirectory, "user", "setting", hostVersion);
        string[] order = [];
        bool? enabled = null, disk = null;
        int? minimum = null, scale = null, cache = null;
        using (var loader = ReadJson(Path.Combine(directory, "YukkuriMovieMaker.Plugin.PluginLoaderSettings.json")))
        {
            if (loader?.RootElement.TryGetProperty("VideoFileSourcePlugins", out var plugins) == true &&
                plugins.ValueKind == JsonValueKind.Array)
                order = plugins.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetString()!)
                    .Where(p => p.Length <= 256 && Regex.IsMatch(p, @"^[\p{L}\p{N}_.+`]+$"))
                    .Take(128).ToArray();
        }
        // ProxyForge 1.x only. Different layouts/versions must not be guessed.
        using (var proxy = ReadJson(Path.Combine(directory, "ProxyForge.Settings.ProxyForgeSettings.json")))
        {
            if (proxy is not null)
            {
                var root = proxy.RootElement;
                enabled = Boolean(root, "UseProxy");
                disk = Boolean(root, "EnableDiskFallback");
                minimum = Integer(root, "MinFileSizeForProxy", 1, 10000);
                scale = Integer(root, "Scale", 10, 100);
                cache = Integer(root, "MaxCacheMemoryMb", 256, 16384);
            }
        }
        return order.Length == 0 && enabled is null && minimum is null && scale is null && cache is null && disk is null
            ? null : new(order, enabled, minimum, scale, cache, disk);
    }

    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1_048_576) return null;
            var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }
    private static bool? Boolean(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    private static int? Integer(JsonElement root, string key, int min, int max) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number >= min && number <= max ? number : null;
}
