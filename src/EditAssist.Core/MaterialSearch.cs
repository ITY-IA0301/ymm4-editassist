using System.Globalization;
using System.Text;

namespace EditAssist.Core;

public sealed record SearchOptions(string Query = "", string Scene = "",
    MaterialKind? Kind = null, bool FavoritesOnly = false);

public static class MaterialSearch
{
    // Local vocabulary, not AI inference or identification of another video's sound.
    public static IReadOnlyDictionary<string, string[]> Scenes { get; } =
        new Dictionary<string, string[]>
        {
            ["驚き"] = ["驚き", "びっくり", "驚愕", "衝撃", "ドン", "バーン"],
            ["失敗"] = ["失敗", "がっかり", "ずっこけ", "落胆", "残念", "チーン"],
            ["成功"] = ["成功", "達成", "完成", "勝利", "ファンファーレ", "テッテレー"],
            ["登場"] = ["登場", "出現", "紹介", "ジャーン", "決め"],
            ["緊張"] = ["緊張", "不安", "恐怖", "ホラー", "ドキドキ"],
            ["場面転換"] = ["場面転換", "切り替え", "トランジション", "スイッシュ", "シュッ"],
            ["説明"] = ["説明", "注目", "通知", "ポン", "ピンポン"]
        };

    public static string Normalize(string text)
    {
        var value = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        return string.Concat(value.Select(c => c >= '\u30a1' && c <= '\u30f6'
            ? (char)(c - 0x60) : c));
    }

    private static string[] Expand(string token)
    {
        var normalized = Normalize(token);
        foreach (var pair in Scenes)
            if (pair.Value.Any(alias => Normalize(alias) == normalized))
                return pair.Value.Select(Normalize).Distinct().ToArray();
        return [normalized];
    }

    public static IReadOnlyList<MaterialEntry> Find(IEnumerable<MaterialEntry> entries,
        SearchOptions options)
    {
        var terms = options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Expand).ToList();
        if (!string.IsNullOrWhiteSpace(options.Scene)) terms.Add(Expand(options.Scene));
        var ranked = new List<(MaterialEntry Entry, int Score)>();
        foreach (var entry in entries)
        {
            if (options.Kind is not null && entry.Kind != options.Kind) continue;
            if (options.FavoritesOnly && !entry.Favorite) continue;
            var title = Normalize(entry.Title);
            var filename = Normalize(Path.GetFileNameWithoutExtension(entry.FilePath));
            var tags = entry.Tags.Select(Normalize).ToArray();
            var notes = Normalize(entry.Notes);
            int score = entry.Favorite ? 2 : 0;
            bool allTerms = true;
            foreach (var aliases in terms)
            {
                int best = 0;
                foreach (var alias in aliases)
                {
                    if (title == alias || filename == alias) best = Math.Max(best, 20);
                    else if (title.Contains(alias) || filename.Contains(alias)) best = Math.Max(best, 12);
                    if (tags.Any(t => t.Contains(alias))) best = Math.Max(best, 16);
                    if (notes.Contains(alias)) best = Math.Max(best, 5);
                }
                if (best == 0) { allTerms = false; break; }
                score += best;
            }
            if (allTerms) ranked.Add((entry, score));
        }
        return ranked.OrderByDescending(r => r.Score)
            .ThenBy(r => r.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => r.Entry).ToArray();
    }

    public static List<string> ParseTags(string text) => text
        .Split([',', '、', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Trim()).Where(t => t.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

