using System.Text.Json;
using System.Text.RegularExpressions;

namespace EditAssist.Core;
public sealed record EditPreset(int SchemaVersion = 1, string Name = "プリセット", string? Font = null,
    double? FontSize = null, string? FontColor = null, bool? Bold = null, double? Volume = null,
    double? X = null, double? Y = null)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 200 ||
            Font is { Length: > 200 } || Font is not null && string.IsNullOrWhiteSpace(Font) ||
            FontSize.HasValue && (!double.IsFinite(FontSize.Value) || FontSize < 1 || FontSize > 1000) ||
            Volume.HasValue && (!double.IsFinite(Volume.Value) || Volume < 0 || Volume > 1000) ||
            X.HasValue && (!double.IsFinite(X.Value) || Math.Abs(X.Value) > 100000) ||
            Y.HasValue && (!double.IsFinite(Y.Value) || Math.Abs(Y.Value) > 100000) ||
            FontColor is not null && !Regex.IsMatch(FontColor, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$"))
            throw new InvalidDataException("プリセットの版・名前・値が不正です。");
    }
    public static EditPreset Load(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("プリセットが大きすぎます。");
        var preset = JsonSerializer.Deserialize<EditPreset>(File.ReadAllText(path)) ?? throw new InvalidDataException("プリセットが空です。");
        preset.Validate(); return preset;
    }
    public void Save(string path)
    {
        Validate();
        var temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, this, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
