using System.Text;
using System.Text.RegularExpressions;

namespace EditAssist.Core;

/// <summary>Extracts reference examples and surface endings locally; does not train model weights.</summary>
public static class SpeakingStyle
{
    public static string Extract(string script)
    {
        var lines = ScriptImport.Parse(script);
        if (lines.Count == 0) throw new InvalidDataException("参考台本にセリフがありません。");
        var groups = lines.GroupBy(x => x.Character, StringComparer.Ordinal).ToArray();
        if (groups.Length > 20) throw new InvalidDataException("参考台本は20キャラ以内に分けてください。");
        var output = new StringBuilder();
        foreach (var group in groups)
        {
            output.AppendLine($"【{group.Key}】参考セリフ {group.Count()}件");
            var endings = group.SelectMany(x => Regex.Split(x.Text, @"[。！？!?\r\n]+"))
                .Select(x => x.Trim().TrimEnd('」', '』', '”', '"', '）', ')'))
                .Where(x => x.Length > 0)
                .Select(x => x.Length > 4 ? x[^4..] : x)
                .GroupBy(x => x, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Take(6).Select(x => $"「{x.Key}」({x.Count()}回)");
            output.AppendLine("文末の参考（末尾最大4文字・語尾の断定ではありません）：" + string.Join("、", endings));
            output.AppendLine("話し方の参考例文：");
            foreach (var line in group.DistinctBy(x => x.Text).Take(6))
                output.AppendLine("・" + (line.Text.Length > 160 ? line.Text[..160] + "…" : line.Text));
            if (group.Count() < 5) output.AppendLine("参考数が少ないため傾向の確認が必要です。");
            output.AppendLine();
        }
        if (output.Length > 8000) throw new InvalidDataException("抽出結果が長すぎます。キャラを分けて読み込んでください。");
        return output.ToString();
    }

    public static string Settings(string existing, string manual, bool useManual, string learned, bool useLearned)
    {
        string value = existing.Trim();
        if (useManual && !string.IsNullOrWhiteSpace(manual)) value += "\n【手動の口調指定・最優先】\n" + manual.Trim();
        if (useLearned && !string.IsNullOrWhiteSpace(learned)) value += "\n【参考台本から抽出した話し方】\n" + learned.Trim();
        if (value.Length > 10000) throw new ArgumentException("キャラ設定と口調の合計を1万文字以内にしてください。");
        return value;
    }
}
