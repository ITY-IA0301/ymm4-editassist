using System.Globalization;
using System.Text;

namespace EditAssist.Core;

public sealed record SubtitleOptions(
    bool Split = false, bool Resize = false, bool Wrap = false,
    int MaxCharacters = 40, int MaxLines = 2,
    double Width = 1600, double Height = 220,
    double MinFontSize = 24, double MaxFontSize = 48, double Padding = 12)
{
    public bool Enabled => Split || Resize || Wrap;
    public void Validate()
    {
        if (MaxCharacters is < 1 or > 2000 || MaxLines is < 1 or > 20 ||
            !double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0 ||
            !double.IsFinite(Padding) || Padding < 0 || Width <= 2 * Padding || Height <= 2 * Padding ||
            !double.IsFinite(MinFontSize) || !double.IsFinite(MaxFontSize) ||
            MinFontSize < 8 || MaxFontSize > 500 || MinFontSize > MaxFontSize)
            throw new ArgumentException("字幕の領域・文字数・行数・文字サイズを確認してください。");
    }
}
public sealed record TextExtent(double Width, double Height);
public sealed record SubtitlePage(string Text, double FontSize, bool Fits);
public sealed record SubtitleLayoutResult(IReadOnlyList<SubtitlePage> Pages, IReadOnlyList<string> Warnings);
public sealed record SubtitleTiming(int Offset, int Length);

public static class SubtitleLayout
{
    private const string NoStart = "、。，．！？!?：；:;)]）］｝」』】〉》ーぁぃぅぇぉっゃゅょァィゥェォッャュョ";
    private const string NoEnd = "([（［｛「『【〈《";
    public static string[] Elements(string text) => StringInfo.GetTextElementEnumerator(text).AsEnumerable().ToArray();
    private static IEnumerable<string> AsEnumerable(this TextElementEnumerator reader)
    { while (reader.MoveNext()) yield return reader.GetTextElement(); }

    public static SubtitleLayoutResult Fit(string text, double originalSize, SubtitleOptions options,
        Func<string, double, TextExtent> measure)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(measure);
        options.Validate();
        if (text.Length > 10000 || !double.IsFinite(originalSize) || originalSize <= 0 || originalSize > 1000)
            throw new ArgumentException("字幕は1万文字以内、文字サイズは有効な値にしてください。");
        if (!options.Enabled) return new([new(text, originalSize, true)], []);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        double startingSize = options.Resize ? Math.Clamp(originalSize, options.MinFontSize, options.MaxFontSize) : originalSize;
        var sourcePages = options.Split ? Split(text, options.MaxCharacters) : new List<string> { text };
        var pages = new List<SubtitlePage>();
        foreach (string source in sourcePages)
        {
            var page = FitPage(source, startingSize, options, measure);
            // When splitting is enabled, also limit lines/area, even if character count is small.
            if (options.Split && !page.Fits)
            {
                var elements = Elements(source);
                int at = 0;
                while (at < elements.Length)
                {
                    int best = 0;
                    for (int count = 1; at + count <= elements.Length; count++)
                    {
                        var candidate = FitPage(string.Concat(elements.Skip(at).Take(count)), startingSize, options, measure);
                        if (!candidate.Fits) break;
                        best = count;
                    }
                    best = Math.Max(1, best);
                    int boundary = PreferredBoundary(elements, at, best);
                    pages.Add(FitPage(string.Concat(elements.Skip(at).Take(boundary)), startingSize, options, measure));
                    at += boundary;
                    if (pages.Count > 2000) throw new ArgumentException("字幕の分割数が多すぎます。領域を広げてください。");
                }
            }
            else pages.Add(page);
        }
        if (pages.Count == 0) pages.Add(FitPage("", startingSize, options, measure));
        var warnings = new List<string>();
        if (pages.Any(p => !p.Fits)) warnings.Add("領域または最大行数に収まらない字幕があります。設定を広げるか、分割・サイズ調整を有効にしてください。");
        if (pages.Count > 1) warnings.Add("字幕の切替時刻は文章量による配分です。音声を聞いて調整してください。");
        return new(pages, warnings);
    }
    private static SubtitlePage FitPage(string text, double size, SubtitleOptions o, Func<string, double, TextExtent> measure)
    {
        SubtitlePage At(double s)
        {
            string display = o.Wrap ? WrapText(text, o.Width - 2 * o.Padding, s, measure) : text;
            var extent = measure(display, s);
            bool fits = double.IsFinite(extent.Width) && double.IsFinite(extent.Height) &&
                extent.Width <= o.Width - 2 * o.Padding + .01 && extent.Height <= o.Height - 2 * o.Padding + .01 &&
                display.Split('\n').Length <= o.MaxLines;
            return new(display, s, fits);
        }
        var current = At(size);
        if (current.Fits || !o.Resize) return current;
        var minimum = At(o.MinFontSize);
        if (!minimum.Fits) return minimum;
        double low = o.MinFontSize, high = size;
        for (int i = 0; i < 16; i++) { double mid = (low + high) / 2; if (At(mid).Fits) low = mid; else high = mid; }
        return At(Math.Floor(low * 10) / 10);
    }
    private static List<string> Split(string text, int limit)
    {
        var elements = Elements(text); var pages = new List<string>(); int at = 0;
        while (at < elements.Length)
        {
            int count = Math.Min(limit, elements.Length - at);
            count = PreferredBoundary(elements, at, count);
            pages.Add(string.Concat(elements.Skip(at).Take(count))); at += count;
        }
        return pages;
    }
    private static int PreferredBoundary(string[] elements, int start, int count)
    {
        if (start + count == elements.Length) return count;
        int floor = Math.Max(1, count / 2);
        for (int n = count; n >= floor; n--)
            if (LegalBreak(elements, start + n) && "。！？!?、, \n".Contains(elements[start + n - 1], StringComparison.Ordinal)) return n;
        for (int n = count; n >= 1; n--) if (LegalBreak(elements, start + n)) return n;
        return count; // Progress is mandatory for pathological punctuation or tiny regions.
    }
    private static bool LegalBreak(string[] elements, int at) =>
        at >= elements.Length || (!NoStart.Contains(elements[at], StringComparison.Ordinal) && !NoEnd.Contains(elements[at - 1], StringComparison.Ordinal));
    private static string WrapText(string text, double width, double size, Func<string, double, TextExtent> measure)
    {
        var output = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            var elements = Elements(line); int at = 0;
            if (elements.Length == 0) { output.Add(""); continue; }
            while (at < elements.Length)
            {
                int count = 0;
                while (at + count < elements.Length && measure(string.Concat(elements.Skip(at).Take(count + 1)), size).Width <= width) count++;
                count = Math.Max(1, count); count = PreferredBoundary(elements, at, count);
                output.Add(string.Concat(elements.Skip(at).Take(count))); at += count;
            }
        }
        return string.Join("\n", output);
    }
    public static IReadOnlyList<SubtitleTiming> Allocate(IReadOnlyList<SubtitlePage> pages, int frames)
    {
        if (pages.Count == 0 || frames < pages.Count) throw new ArgumentException("字幕の分割数がフレーム数を超えています。分割を減らしてください。");
        var weights = pages.Select(p => Math.Max(1, Elements(p.Text.Replace("\n", "")).Length)).ToArray();
        long total = weights.Sum(x => (long)x), cumulative = 0; int at = 0;
        var result = new List<SubtitleTiming>();
        for (int i = 0; i < pages.Count; i++)
        {
            cumulative += weights[i];
            int end = i == pages.Count - 1 ? frames : (int)(frames * cumulative / total);
            end = Math.Clamp(end, at + 1, frames - (pages.Count - i - 1));
            result.Add(new(at, end - at)); at = end;
        }
        return result;
    }
}
