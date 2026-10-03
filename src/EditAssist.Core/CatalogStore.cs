using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditAssist.Core;

public sealed class CatalogStore : IDisposable
{
    private readonly string path;
    private FileStream? writerLock;
    private bool loadedSuccessfully;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public CatalogStore(string path) => this.path = Path.GetFullPath(path);
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YMM4EditAssist", "catalog.json");

    public MaterialCatalog Load()
    {
        loadedSuccessfully = false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writerLock ??= new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var catalog = File.Exists(path)
            ? JsonSerializer.Deserialize<MaterialCatalog>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("素材データが空です。")
            : new MaterialCatalog();
        Validate(catalog);
        loadedSuccessfully = true;
        return catalog;
    }

    // A failed load never grants permission to overwrite the user's catalog.
    // Atomic replacement retains the previous successful file as .bak.
    public void Save(MaterialCatalog catalog)
    {
        if (!loadedSuccessfully || writerLock is null)
            throw new InvalidOperationException("読み込み未完了のため、素材データを保存できません。");
        Validate(catalog);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, catalog, JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Validate(MaterialCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Entries is null || catalog.Roots is null)
            throw new InvalidDataException("未対応、または破損した素材データです。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        foreach (var entry in catalog.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath) ||
                !Path.IsPathFullyQualified(entry.FilePath) || string.IsNullOrWhiteSpace(entry.Title) ||
                entry.Tags is null || entry.Tags.Any(t => t is null) ||
                entry.Notes is null || entry.SourceUrl is null || entry.LicenseNote is null ||
                !Enum.IsDefined(entry.Kind) || entry.Id == Guid.Empty || !ids.Add(entry.Id) ||
                entry.SizeBytes < 0 || !paths.Add(Path.GetFullPath(entry.FilePath)))
                throw new InvalidDataException("素材データの項目が破損しています。バックアップを確認してください。");
        }
        if (catalog.Roots.Any(r => string.IsNullOrWhiteSpace(r) || !Path.IsPathFullyQualified(r)))
            throw new InvalidDataException("素材フォルダの記録が破損しています。");
    }

    public void Dispose()
    {
        loadedSuccessfully = false;
        writerLock?.Dispose();
        writerLock = null;
    }
}

