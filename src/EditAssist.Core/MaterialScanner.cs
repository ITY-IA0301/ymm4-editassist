namespace EditAssist.Core;

public sealed record ScanResult(IReadOnlyList<MaterialEntry> Entries,
    int UnreadableItems, bool LimitReached);

public static class MaterialScanner
{
    public const int DefaultLimit = 20_000;
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v" };

    public static MaterialKind? GetKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (Audio.Contains(extension)) return MaterialKind.Audio;
        if (Images.Contains(extension)) return MaterialKind.Image;
        if (Videos.Contains(extension)) return MaterialKind.Video;
        return null;
    }

    // This method does no decoding and must be called off the UI thread.
    // Reparse points are skipped so directory links cannot create recursion loops.
    public static ScanResult Scan(IEnumerable<string> paths, CancellationToken token,
        IProgress<int>? progress = null, int limit = DefaultLimit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var entries = new List<MaterialEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unreadable = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                IEnumerable<string> files;
                if (File.Exists(path)) files = [path];
                else if (Directory.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    files = Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true, IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        ReturnSpecialDirectories = false
                    });
                }
                else { unreadable++; continue; }
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var kind = GetKind(file);
                    if (kind is null) continue;
                    var fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath)) continue;
                    // Detect truncation only on an additional supported file.
                    if (entries.Count >= limit) return new(entries, unreadable, true);
                    try
                    {
                        var info = new FileInfo(fullPath);
                        entries.Add(new MaterialEntry
                        {
                            FilePath = fullPath, Title = string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fullPath))
                                ? Path.GetFileName(fullPath) : Path.GetFileNameWithoutExtension(fullPath),
                            Kind = kind.Value, SizeBytes = info.Length,
                            LastModifiedUtc = info.LastWriteTimeUtc
                        });
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { unreadable++; }
                    if (entries.Count % 100 == 0) progress?.Report(entries.Count);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { unreadable++; }
        }
        progress?.Report(entries.Count);
        return new(entries, unreadable, false);
    }

    // Preserve human labels and IDs when scanning again. Never delete missing files.
    public static void Merge(MaterialCatalog catalog, ScanResult result)
    {
        var existing = catalog.Entries.ToDictionary(e => e.FilePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in result.Entries)
        {
            if (existing.TryGetValue(incoming.FilePath, out var saved))
            {
                saved.SizeBytes = incoming.SizeBytes;
                saved.LastModifiedUtc = incoming.LastModifiedUtc;
                saved.IsMissing = false;
            }
            else
            {
                var added = incoming.Clone();
                catalog.Entries.Add(added);
                existing.Add(added.FilePath, added);
            }
        }
    }
}

