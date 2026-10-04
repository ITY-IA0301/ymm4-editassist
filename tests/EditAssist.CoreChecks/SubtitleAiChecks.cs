using System.Text.Json;
using EditAssist.Core;

internal static class SubtitleAiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        TextExtent Measure(string text, double size) => new(text.Split('\n').Max(l => SubtitleLayout.Elements(l).Length) * size, text.Split('\n').Length * size);
        void Refused(Action action, string label)
        { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException) { check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        var settings = new SubtitleOptions(Width: 100, Height: 50, Padding: 0);
        var off = SubtitleLayout.Fit("長い字幕をそのまま保持", 42, settings, Measure);
        check(off.Pages.Count == 1 && off.Pages[0].Text == "長い字幕をそのまま保持" && off.Pages[0].FontSize == 42, "all subtitle toggles OFF preserve text and font");
        var wrap = SubtitleLayout.Fit("あいうえおかきくけこ", 10, settings with { Wrap = true, MaxLines = 3, Width = 40 }, Measure);
        check(wrap.Pages.Count == 1 && wrap.Pages[0].Text.Replace("\n", "") == "あいうえおかきくけこ" && wrap.Pages[0].Text.Contains('\n') && wrap.Pages[0].FontSize == 10, "wrap-only preserves speech and size");
        var resize = SubtitleLayout.Fit("あいうえおかきくけこ", 40, settings with { Resize = true, MinFontSize = 8, MaxFontSize = 40 }, Measure);
        check(resize.Pages.Count == 1 && resize.Pages[0].Text == "あいうえおかきくけこ" && resize.Pages[0].FontSize <= 10 && resize.Pages[0].Fits, "resize-only fits without splitting or newlines");
        var split = SubtitleLayout.Fit("あいうえおかきくけこ", 10, settings with { Split = true, MaxCharacters = 3, Width = 1000, Height = 1000 }, Measure);
        check(split.Pages.Count == 4 && string.Concat(split.Pages.Select(x => x.Text)) == "あいうえおかきくけこ" && split.Pages.All(x => x.FontSize == 10), "split-only preserves every character and original size");
        foreach (bool splitOn in new[] { false, true }) foreach (bool resizeOn in new[] { false, true }) foreach (bool wrapOn in new[] { false, true })
        {
            var layout = SubtitleLayout.Fit("本文😀「確認」、次です。", 20, settings with { Split = splitOn, Resize = resizeOn, Wrap = wrapOn, MaxCharacters = 5, MinFontSize = 8, MaxFontSize = 20, MaxLines = 3 }, Measure);
            check(string.Concat(layout.Pages.Select(x => x.Text.Replace("\n", ""))) == "本文😀「確認」、次です。", $"subtitle toggle combination {splitOn}/{resizeOn}/{wrapOn} never drops text");
        }
        check(SubtitleLayout.Elements("👨‍👩‍👧‍👦か\u3099😀").Length == 3, "subtitle splitting keeps grapheme clusters and emoji intact");
        var punctuation = SubtitleLayout.Fit("あいう「えお」、かき。", 10, settings with { Wrap = true, MaxLines = 10, Width = 40, Height = 1000 }, Measure).Pages[0].Text.Split('\n');
        check(punctuation.All(x => !x.StartsWith('、') && !x.EndsWith('「')), "Japanese line breaks respect opening and closing punctuation");
        var impossible = SubtitleLayout.Fit("あ", 48, settings with { Resize = true, MinFontSize = 24, MaxFontSize = 48, Width = 10, Height = 10 }, Measure);
        check(!impossible.Pages[0].Fits && impossible.Pages[0].FontSize == 24 && impossible.Warnings.Count > 0, "unfittable subtitles warn and never shrink below configured minimum");
        var timings = SubtitleLayout.Allocate(split.Pages, 100);
        check(timings.Sum(x => x.Length) == 100 && timings[0].Offset == 0 && timings.Zip(timings.Skip(1)).All(x => x.First.Offset + x.First.Length == x.Second.Offset), "split subtitle timings cover original duration without gaps");
        check(SubtitleLayout.Allocate(split.Pages, 4).All(x => x.Length == 1), "minimum one-frame pages cover exact clip length");
        Refused(() => SubtitleLayout.Allocate(split.Pages, 3), "more subtitle pages than frames refused");
        Refused(() => SubtitleLayout.Fit("abc", 10, settings with { Width = double.NaN }, Measure), "nonfinite subtitle area refused");
        Refused(() => SubtitleLayout.Fit("abc", 10, settings with { MinFontSize = 40, MaxFontSize = 20 }, Measure), "reversed font bounds refused");
        var id = Guid.NewGuid();
        var request = new AiRequest("短い台本", "案内：丁寧", "下書き", [new(id, "成功の音", "音声", "成功", "説明")]);
        string json = $$"""{"version":1,"lines":[{"character":"案内","text":"完成です。"}],"materials":[{"materialId":"{{id}}","line":1,"reason":"完成の場面"}],"notes":"提案"}""";
        var result = AiAssist.Parse(json, request);
        check(result.Lines.Single().Text == "完成です。" && result.Materials.Single().MaterialId == id.ToString(), "AI reply parses only registered material IDs");
        check(AiAssist.Parse("```json\n" + json + "\n```", request).Version == 1, "ChatGPT fenced JSON import supported");
        check(ScriptImport.Parse(AiAssist.Csv(result)).Single().Text == "完成です。", "AI script transfers to existing CSV importer");
        var quotes = result with { Lines = [new("案内", "引用\"と,改行\n続き")] };
        check(ScriptImport.Parse(AiAssist.Csv(quotes)).Single().Text == quotes.Lines[0].Text, "AI CSV preserves quotes commas and multiline dialogue");
        Refused(() => AiAssist.Parse(json.Replace(id.ToString(), Guid.NewGuid().ToString()), request), "AI hallucinated material ID refused");
        Refused(() => AiAssist.Parse(json.Replace("\"line\":1", "\"line\":2"), request), "AI out-of-range material timing reference refused");
        Refused(() => AiAssist.Parse(json.Replace("\"version\":1", "\"version\":2"), request), "unsupported AI format refused");
        Refused(() => AiAssist.Parse(json.Replace("\"notes\":\"提案\"", "\"notes\":\"提案\",\"command\":\"run\""), request), "AI commands outside the schema refused");
        Refused(() => AiAssist.Parse("{\"version\":1,\"lines\":[],\"materials\":[]}", request), "missing AI envelope fields refused");
        Refused(() => AiAssist.Parse(json.Replace("完成です。", ""), request), "empty AI speech refused");
        string prompt = AiAssist.Prompt(request);
        check(prompt.Contains(id.ToString()) && prompt.Contains("ファイルパスや外部URLを作らない") && !prompt.Contains("filePath"), "AI prompt includes supplied metadata without local file paths");
    }
}
