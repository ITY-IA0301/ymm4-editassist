using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EditAssist.Plugin;
using YukkuriMovieMaker.Plugin;

internal static class ReadOnlyProjectProbe
{
    internal static async Task RunAsync(string path)
    {
        byte[] before = File.ReadAllBytes(path);
        int bom = before.Length >= 3 && before[0] == 0xef && before[1] == 0xbb && before[2] == 0xbf ? 3 : 0;
        using var json = JsonDocument.Parse(before.AsMemory(bom));
        var references = new List<VideoReference>();
        foreach (var timeline in json.RootElement.GetProperty("Timelines").EnumerateArray())
            foreach (var item in timeline.GetProperty("Items").EnumerateArray())
                if (item.GetProperty("$type").GetString() == "YukkuriMovieMaker.Project.Items.VideoItem, YukkuriMovieMaker")
                    references.Add(new(timeline.GetProperty("Name").GetString() ?? "", item.GetProperty("FilePath").GetString() ?? "", [0]));
        var rows = MediaCatalog.Inspect(references, null, CancellationToken.None);
        Console.WriteLine($"READONLY project: {references.Count} video clips / {rows.Count} unique paths / {rows.Count(x => x.HasWarning)} basic warnings.");
        foreach (var row in rows.Where(x => x.HasWarning)) Console.WriteLine("READONLY warning: " + row.Filename + " - " + row.Detail);
        var tools = MediaDecoderProbe.Locate(Path.GetDirectoryName(typeof(IPlugin).Assembly.Location)!);
        foreach (string root in new[] { "D:\\MinecraftArchive\\", "D:\\Minecraftreplay\\" })
        {
            var sample = rows.FirstOrDefault(x => x.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && x.Stamp is not null);
            if (sample is null) continue;
            var result = await MediaDecoderProbe.CheckAsync(sample.Path, [0], tools.Probe, tools.Decoder, CancellationToken.None);
            Console.WriteLine($"READONLY sample: {sample.Filename} - {result.Success} - {result.Detail}");
            if (result.Success != true) throw new Exception("Actual video sample check not successful: " + result.Detail);
        }
        if (!SHA256.HashData(before).SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
            throw new Exception("Project changed during read-only validation; no writes are performed by this check.");
        Console.WriteLine("READONLY verification: project bytes unchanged; no baseline written and no video/proxy outputs created.");
    }
}
