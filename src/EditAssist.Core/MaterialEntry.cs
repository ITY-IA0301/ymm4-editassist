using System.Text.Json.Serialization;

namespace EditAssist.Core;

public enum MaterialKind { Audio, Image, Video }

public sealed class MaterialEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public MaterialKind Kind { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Notes { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string LicenseNote { get; set; } = "";
    public bool Favorite { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    [JsonIgnore] public bool IsMissing { get; set; }
    [JsonIgnore] public string FavoriteMark => Favorite ? "★" : "";
    [JsonIgnore] public string TagText => string.Join("、", Tags);
    [JsonIgnore] public string KindText => Kind switch
    { MaterialKind.Audio => "音声", MaterialKind.Image => "画像", _ => "動画" };
    [JsonIgnore] public string Availability => IsMissing ? "見つからない" : "登録済み";

    public MaterialEntry Clone() => new()
    {
        Id = Id, FilePath = FilePath, Title = Title, Kind = Kind,
        Tags = [.. Tags], Notes = Notes, SourceUrl = SourceUrl,
        LicenseNote = LicenseNote, Favorite = Favorite, SizeBytes = SizeBytes,
        LastModifiedUtc = LastModifiedUtc, IsMissing = IsMissing
    };
}

public sealed class MaterialCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Roots { get; set; } = [];
    public List<MaterialEntry> Entries { get; set; } = [];
    public MaterialCatalog Clone() => new()
    {
        SchemaVersion = SchemaVersion, Roots = [.. Roots],
        Entries = Entries.Select(e => e.Clone()).ToList()
    };
}

