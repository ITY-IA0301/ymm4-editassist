using System.Text;

namespace EditAssist.Core;
public sealed record ScriptLine(int Number, string Character, string Text);
public static class ScriptImport
{
    public static IReadOnlyList<ScriptLine> Parse(string text)
    {
        if (text.Length > 2_000_000) throw new InvalidDataException("台本は200万文字以内に分けてください。");
        text = text.TrimStart('\uFEFF');
        char delimiter = Delimiter(text);
        var records = new List<List<string>>();
        var row = new List<string>(); var field = new StringBuilder();
        bool quoted = false, closedQuote = false;
        void Field() { row.Add(field.ToString()); field.Clear(); closedQuote = false; }
        void Row() { Field(); if (row.Any(s => !string.IsNullOrWhiteSpace(s))) records.Add(row); row = []; }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') { quoted = false; closedQuote = true; }
                else field.Append(c);
            }
            else if (c == delimiter) Field();
            else if (c == '\r' || c == '\n') { Row(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '"' && field.Length == 0 && !closedQuote) quoted = true;
            else if (c == '"' || closedQuote && !char.IsWhiteSpace(c)) throw new InvalidDataException("台本の引用符の位置が不正です。");
            else if (!closedQuote) field.Append(c);
        }
        if (quoted) throw new InvalidDataException("台本の引用符が閉じていません。");
        if (field.Length > 0 || row.Count > 0 || closedQuote) Row();
        var lines = new List<ScriptLine>();
        foreach (var r in records)
        {
            int number = lines.Count + 1;
            if (r.Count != 2) throw new InvalidDataException($"台本の行は「キャラ名,セリフ」の2列にしてください（記録 {records.IndexOf(r) + 1}）。");
            string name = r[0].Trim(), speech = r[1].Trim();
            if (lines.Count == 0 && (name is "キャラ" or "キャラクター" or "Character" or "character") &&
                (speech is "セリフ" or "台詞" or "Text" or "text")) continue;
            if (name.Length == 0 || speech.Length == 0) throw new InvalidDataException("キャラ名とセリフの両方を入力してください。");
            if (name.Length > 200 || speech.Length > 10000 || lines.Count >= 2000)
                throw new InvalidDataException("台本は2000行以内、キャラ名200文字、セリフ1万文字以内に分けてください。");
            lines.Add(new(number, name, speech));
        }
        return lines;
    }
    private static char Delimiter(string text)
    {
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is ',' or '\t') return c;
        }
        return ',';
    }
}
