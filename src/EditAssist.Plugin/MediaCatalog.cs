using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EditAssist.Plugin;

internal sealed record VideoReference(string Scene, string Path, double[] SampleSeconds);
internal sealed record FileStamp(long Length, long WriteUtcTicks)
{
    internal static FileStamp Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("参照先にファイルがありません。", path);
        if (info.Length == 0) throw new IOException("ファイルが空（0バイト）です。");
        if ((info.Attributes & FileAttributes.Offline) != 0) throw new IOException("クラウド上のみ／オフラインのファイルです。先にローカルへ取得してください。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.ReadByte() < 0) throw new IOException("ファイルを読み取れません。");
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }
}

internal sealed record BaselineEntry(string Path, FileStamp? Stamp);
internal sealed record ReferenceBaseline(int Version, string ProjectKey, BaselineEntry[] Entries);
internal sealed record MediaRow(string Filename, int Clips, string Path, string Status, string Detail, FileStamp? Stamp)
{
    public string FileName => Filename;
    public int ClipCount => Clips;
    public string FilePath => Path;
    public string Result => Status;
    public string Warning => Detail;
    public bool IsWarning => HasWarning;
    internal bool HasWarning => Status != "確認済み";
}

internal static class MediaCatalog
{
    internal static string ProjectKey(IEnumerable<Guid> sceneIds) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("|", sceneIds.Order().Select(x => x.ToString("D"))))));

    internal static string[] FindCandidates(string filename, string directory, bool recursive, CancellationToken token)
    {
        filename = filename.Trim().Trim('"');
        directory = directory.Trim().Trim('"');
        if (filename.Length == 0 || filename != System.IO.Path.GetFileName(filename) || filename.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("拡張子を含むファイル名1つを入力してください。フォルダーやワイルドカードは指定できません。");
        string root = System.IO.Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("検索先フォルダーがありません。");
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive };
        var found = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*", options))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(System.IO.Path.GetFileName(path), filename, StringComparison.OrdinalIgnoreCase)) found.Add(System.IO.Path.GetFullPath(path));
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<MediaRow> Inspect(IReadOnlyList<VideoReference> references, ReferenceBaseline? baseline, CancellationToken token)
    {
        var rows = new List<MediaRow>();
        foreach (var group in references.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string name;
            try { name = System.IO.Path.GetFileName(group.Key); } catch { name = group.Key; }
            FileStamp? stamp = null;
            var warnings = new List<string>();
            string status = "確認済み";
            try { stamp = FileStamp.Read(group.Key); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { status = "警告"; warnings.Add(ex.Message); }
            if (baseline is not null)
            {
                var sameName = baseline.Entries.Where(x => string.Equals(System.IO.Path.GetFileName(x.Path), name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (sameName.Length > 0 && !sameName.Any(x => string.Equals(x.Path, group.Key, StringComparison.OrdinalIgnoreCase)))
                { status = "変更を検出"; warnings.Add("以前と参照先が異なります。以前：" + string.Join(" / ", sameName.Select(x => x.Path))); }
                var old = sameName.FirstOrDefault(x => string.Equals(x.Path, group.Key, StringComparison.OrdinalIgnoreCase));
                if (old?.Stamp is { } previous && stamp is { } current && previous != current)
                { status = "変更を検出"; warnings.Add("以前とサイズまたは更新日時が異なります。同名動画の置換・更新の可能性があります。"); }
            }
            if (warnings.Count == 0) warnings.Add("参照先の存在・サイズ・読み取りを確認。");
            warnings.Add("映像デコードは未確認。");
            rows.Add(new(name, group.Count(), group.Key, status, string.Join("\n", warnings), stamp));
        }
        return rows.OrderByDescending(x => x.HasWarning).ThenBy(x => x.Filename, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static ReferenceBaseline Capture(string key, IEnumerable<MediaRow> rows) => new(1, key,
        rows.Select(x => new BaselineEntry(x.Path, x.Stamp)).ToArray());

    internal static ReferenceBaseline? LoadBaseline(string directory, string key)
    {
        string path = System.IO.Path.Combine(directory, key + ".json");
        if (!File.Exists(path)) return null;
        var baseline = JsonSerializer.Deserialize<ReferenceBaseline>(File.ReadAllText(path));
        if (baseline is null || baseline.Version != 1 || baseline.ProjectKey != key || baseline.Entries is null)
            throw new IOException("比較用記録を読み込めません。基準を保存し直してください。");
        return baseline;
    }

    internal static ReferenceBaseline? LoadWithLegacy(string directory, string key, string? legacyDirectory)
    {
        var current = LoadBaseline(directory, key);
        if (current is not null || legacyDirectory is null) return current;
        var legacy = LoadBaseline(legacyDirectory, key);
        if (legacy is not null) SaveBaseline(directory, legacy);
        return legacy;
    }

    internal static void SaveBaseline(string directory, ReferenceBaseline baseline)
    {
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, baseline.ProjectKey + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(baseline), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
