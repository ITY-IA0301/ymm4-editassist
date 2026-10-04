using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EditAssist.Core;

namespace EditAssist.Plugin;

internal static class SubtitleMeasure
{
    internal static TextExtent Measure(string text, double size, string font, bool bold, bool italic,
        double lineHeight = 100, double letterSpacing = 0)
    {
        var typeface = new Typeface(new FontFamily(font), italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var lines = text.Split('\n'); double width = 0, height = 0;
        foreach (string line in lines)
        {
            var formatted = new FormattedText(line.Length == 0 ? " " : line, CultureInfo.GetCultureInfo("ja-JP"),
                FlowDirection.LeftToRight, typeface, size, Brushes.White, 1);
            width = Math.Max(width, formatted.WidthIncludingTrailingWhitespace +
                Math.Max(0, SubtitleLayout.Elements(line).Length - 1) * letterSpacing);
            height += formatted.Height * Math.Max(.1, lineHeight / 100d);
        }
        return new(Math.Max(0, width), height);
    }
}
