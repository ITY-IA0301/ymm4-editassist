using System.Windows.Media;
using EditAssist.Core;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;

namespace EditAssist.Plugin;

internal sealed record SubtitlePreviewRow(int SourceKey, int Page, int Frame, int Length, string Text, double FontSize, string Status);
internal sealed record PreparedSubtitles(PreparedEdit Edit, IReadOnlyList<SubtitlePreviewRow> Rows);
public sealed partial class TimelineSession
{
    internal PreparedSubtitles PrepareSubtitles(IEnumerable<int> keys, SubtitleOptions options, int destinationLayer, double x, double y)
    {
        if (!options.Enabled) return new(Prepare(_ => new EditPlan { Title = "字幕調整はOFF" }), []);
        options.Validate();
        if (!double.IsFinite(x) || !double.IsFinite(y) || destinationLayer < 0 || destinationLayer >= Timeline.LayerLimit)
            throw new ArgumentException("字幕の位置・レイヤーを確認してください。");
        if (Math.Abs(x) + options.Width / 2 > Info.Timeline.VideoInfo.Width / 2d ||
            Math.Abs(y) + options.Height / 2 > Info.Timeline.VideoInfo.Height / 2d)
            throw new ArgumentException("字幕領域が動画画面の外です。領域の大きさ・中心位置を調整してください。");
        var targets = keys.ToHashSet();
        var edit = Prepare(_ => new EditPlan { Title = "字幕の文章量・文字サイズ・改行調整" });
        var additions = new List<IItem>(); var removals = new List<IItem>(); var hidden = new List<int>(); var rows = new List<SubtitlePreviewRow>();
        foreach (int key in targets.Order())
        {
            if (key < 0 || key >= edit.OriginalItems.Count) throw new InvalidOperationException("字幕対象を選び直してください。");
            var source = edit.OriginalItems[key];
            if (source is not (VoiceItem or TextItem)) continue;
            if (source.IsLocked || source.Group != 0 || source.IsHidden || source.Length <= 0)
                throw new InvalidOperationException($"#{key}はロック・グループ・非表示または無効な長さです。対象を確認してください。");
            if (source is VoiceItem voice && (voice.JimakuVisibility == JimakuVisibility.Hidden ||
                voice.ContentOffset != TimeSpan.Zero || !edit.Snapshot[key].CanFitVoice))
                throw new InvalidOperationException($"#{key}は字幕非表示・分割音声または速度アニメーションがあります。字幕をテキスト化して調整してください。");
            object appearance = source is VoiceItem v && v.JimakuVisibility == JimakuVisibility.UseCharacterSetting ? v.Character : source;
            object? Read(string name) => appearance.GetType().GetProperty(name)?.GetValue(appearance);
            double Value(string name, double fallback) => Read(name) is Animation a ? a.GetFirstValue() : fallback;
            foreach (string name in new[] { "FontSize", "LineHeight2", "LetterSpacing2" })
                if (Read(name) is Animation a && a.AnimationType != AnimationType.なし)
                    throw new InvalidOperationException($"#{key}の{name}にアニメーションがあります。固定値の字幕で試してください。");
            if (Read("IsDevidedPerCharacter") is true)
                throw new InvalidOperationException($"#{key}は一文字ずつ表示する字幕です。表示方式を固定してから調整してください。");
            string font = Read("Font") as string ?? "Yu Gothic";
            bool bold = Read("Bold") is true, italic = Read("Italic") is true;
            string text = source is VoiceItem speech ? speech.Serif : ((TextItem)source).Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (source is VoiceItem sv && sv.Decorations.Count > 0 || source is TextItem st && st.Decorations.Count > 0)
                throw new InvalidOperationException($"#{key}には部分装飾があります。装飾位置を保つため、自動調整の対象から外してください。");
            double lineHeight = Value("LineHeight2", 100), letterSpacing = Value("LetterSpacing2", 0);
            var layout = SubtitleLayout.Fit(text, Value("FontSize", 48), options,
                (s, size) => SubtitleMeasure.Measure(s, size, font, bold, italic, lineHeight, letterSpacing));
            foreach (string warning in layout.Warnings) edit.Plan.Issues.Add(new("注意", key, warning));
            var timing = SubtitleLayout.Allocate(layout.Pages, source.Length);
            for (int i = 0; i < layout.Pages.Count; i++)
            {
                var page = layout.Pages[i]; var time = timing[i];
                var subtitle = CopyAppearance(source, appearance);
                subtitle.Text = page.Text; subtitle.Frame = checked(source.Frame + time.Offset); subtitle.Length = time.Length;
                subtitle.Layer = destinationLayer; subtitle.Group = 0; subtitle.ContentOffset = TimeSpan.Zero;
                subtitle.FontSize.AnimationType = AnimationType.なし; subtitle.FontSize.SetFirstValue(page.FontSize);
                subtitle.WordWrap = WordWrap.NoWrap; // Explicit line breaks are already measured.
                subtitle.X.AnimationType = AnimationType.なし; subtitle.X.SetFirstValue(x);
                subtitle.Y.AnimationType = AnimationType.なし; subtitle.Y.SetFirstValue(y);
                subtitle.Zoom.AnimationType = AnimationType.なし; subtitle.Zoom.SetFirstValue(100);
                subtitle.Rotation.AnimationType = AnimationType.なし; subtitle.Rotation.SetFirstValue(0);
                subtitle.BasePoint = BasePoint.CenterCenter;
                subtitle.Remark = "EditAssist字幕（切替時刻は文章量配分）";
                subtitle.SetFPS(Info.Timeline.VideoInfo.FPS);
                additions.Add(subtitle);
                rows.Add(new(key, i + 1, subtitle.Frame, subtitle.Length, page.Text, page.FontSize, page.Fits ? "目安内" : "要確認"));
            }
            edit.Plan.Changes.Add(new(key));
            if (source is VoiceItem) hidden.Add(key); else removals.Add(source);
        }
        if (additions.Count == 0) throw new InvalidOperationException("対象のボイスまたはテキストをYMM4で選択してください。");
        foreach (var added in additions)
        {
            if (edit.OriginalItems.Except(removals).Any(item => item.Layer == destinationLayer &&
                (long)item.Frame + item.Length > added.Frame && item.Frame < (long)added.Frame + added.Length))
                edit.Plan.Issues.Add(new("エラー", null, "字幕の移動先レイヤーに既存素材があります。空いたレイヤーを指定してください。"));
            if (Info.Timeline.Items.OfType<GroupItem>().Any(g => OverlapsGroup(added.Frame, added.Length, added.Layer, g)))
                edit.Plan.Issues.Add(new("エラー", null, "字幕の配置先がグループ制御範囲です。レイヤーを変更してください。"));
        }
        if (additions.Where((a, i) => additions.Skip(i + 1).Any(b => (long)a.Frame + a.Length > b.Frame && a.Frame < (long)b.Frame + b.Length)).Any())
            edit.Plan.Issues.Add(new("エラー", null, "同時に表示する字幕が重なります。キャラごとに分けて調整してください。"));
        edit.Plan.Issues.Add(new("注意", null, "元ボイスの字幕を非表示にし、指定領域の独立テキストへ置き換えます。音声・セリフは保持します。縁取り・効果によるはみ出しはYMM4で確認してください。"));
        return new(new PreparedEdit { Plan = edit.Plan, Fingerprint = edit.Fingerprint, OriginalItems = edit.OriginalItems,
            Snapshot = edit.Snapshot, Additions = additions, RemoveItems = removals, HideVoiceSubtitles = hidden }, rows);
    }
    private static TextItem CopyAppearance(IItem source, object appearance)
    {
        if (source is TextItem text) return Json.LoadFromText<TextItem>(Json.GetJsonText(text))!;
        var result = new TextItem();
        string[] shared = ["Font", "FontSize", "LineHeight2", "LetterSpacing2", "WordWrap", "MaxWidth", "FontColor", "Style", "StyleColor",
            "Bold", "Italic", "Underline", "Strikethrough", "IsTrimEndSpace", "IsDevidedPerCharacter", "DisplayInterval", "DisplayDirection", "HideInterval", "HideDirection"];
        foreach (string name in shared)
        {
            var from = appearance.GetType().GetProperty(name)?.GetValue(appearance);
            var property = typeof(TextItem).GetProperty(name);
            if (from is Animation original && property?.GetValue(result) is Animation target) target.CopyFrom(original);
            else if (from is not null && property?.CanWrite == true && property.PropertyType.IsInstanceOfType(from)) property.SetValue(result, from);
        }
        foreach (var (destination, origin) in new[] { ("Opacity", "JimakuOpacity"), ("FadeIn", "JimakuFadeIn"), ("FadeOut", "JimakuFadeOut"),
            ("Blend", "JimakuBlend"), ("IsInverted", "JimakuIsInverted"), ("VideoEffects", "JimakuVideoEffects") })
        {
            string name = appearance is Character && origin is not ("JimakuFadeIn" or "JimakuFadeOut" or "JimakuVideoEffects") ? origin[6..] : origin;
            var from = appearance.GetType().GetProperty(name)?.GetValue(appearance);
            var property = typeof(TextItem).GetProperty(destination);
            if (from is Animation original && property?.GetValue(result) is Animation target) target.CopyFrom(original);
            else if (from is not null && property?.CanWrite == true && property.PropertyType.IsInstanceOfType(from)) property.SetValue(result, from);
        }
        // Clone the completed text to detach nested appearance objects from the character.
        return Json.LoadFromText<TextItem>(Json.GetJsonText(result))!;
    }
}
