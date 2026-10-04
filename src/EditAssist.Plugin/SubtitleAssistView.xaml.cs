using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EditAssist.Core;
using Microsoft.Win32;

namespace EditAssist.Plugin;

public partial class SubtitleAssistView : UserControl
{
    private TimelineSession? session;
    private PreparedSubtitles? pending;
    private bool busy;
    public SubtitleAssistView() { InitializeComponent(); }
    private TimelineSession Session
    {
        get
        {
            var info = (DataContext as EditAssistViewModel)?.TimelineInfo ?? throw new InvalidOperationException("プロジェクトを開いてください。");
            if (!ReferenceEquals(session?.Info, info)) { session = new(info); pending = null; }
            return session;
        }
    }
    private static double Number(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n)
        ? n : throw new ArgumentException("数値を確認してください。");
    private static int Integer(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
        ? n : throw new ArgumentException("文字数・行数・レイヤーには整数を指定してください。");
    private SubtitleOptions Options => new(SplitBox.IsChecked == true, ResizeBox.IsChecked == true, WrapBox.IsChecked == true,
        Integer(CharactersBox), Integer(LinesBox), Number(WidthBox), Number(HeightBox), Number(MinSizeBox), Number(MaxSizeBox), Number(PaddingBox));
    private void OnChanged(object sender, RoutedEventArgs e)
    {
        pending = null;
        if (ConfirmBox is not null) ConfirmBox.IsChecked = false;
        if (PreviewGrid is not null) PreviewGrid.ItemsSource = null;
        if (WarningsBox is not null) WarningsBox.Text = "";
    }
    private void Safe(Action action) { try { if (!busy) action(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void OnPrepare(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var current = Session;
        var selected = current.Info.Timeline.SelectedItems;
        int[] keys = current.Info.Timeline.Items.Select((item, key) => (item, key)).Where(x => selected.Contains(x.item)).Select(x => x.key).ToArray();
        var result = current.PrepareSubtitles(keys, Options, Integer(LayerBox), Number(XBox), Number(YBox));
        pending = result; ConfirmBox.IsChecked = false; PreviewGrid.ItemsSource = result.Rows;
        WarningsBox.Text = string.Join("\n", result.Edit.Plan.Issues.Select(x => $"[{x.Severity}] #{x.Key} {x.Message}"));
        StatusText.Text = result.Rows.Count == 0 ? "すべてOFFなので変更しません。" : $"字幕{result.Rows.Count}個。まだ変更していません。";
    });
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var current = Session; var prepared = pending ?? throw new InvalidOperationException("先に変更プレビューを作ってください。");
            if (ConfirmBox.IsChecked != true) throw new InvalidOperationException("変更内容を確認してください。");
            busy = true; IsEnabled = false;
            await current.ApplyAsync(prepared.Edit, true);
            pending = null; ConfirmBox.IsChecked = false;
            StatusText.Text = "適用しました。YMM4のUndoで戻せます。バックアップ：" + current.LastBackupPath;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { busy = false; IsEnabled = true; }
    }
    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { var current = Session; busy = true; IsEnabled = false; await current.RestoreLastAsync(); pending = null; PreviewGrid.ItemsSource = null; StatusText.Text = "直前の字幕調整を復元しました。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { busy = false; IsEnabled = true; }
    }
    private sealed record Settings(SubtitleOptions Options, int Layer, double X, double Y);
    private void OnSave(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var settings = new Settings(Options, Integer(LayerBox), Number(XBox), Number(YBox)); settings.Options.Validate();
        var dialog = new SaveFileDialog { Filter = "字幕設定|*.json", FileName = "EditAssist-subtitles.json" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    });
    private void OnLoad(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var dialog = new OpenFileDialog { Filter = "字幕設定|*.json" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 20000) throw new InvalidDataException("字幕設定が大きすぎます。");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("字幕設定が空です。");
        settings.Options.Validate();
        SplitBox.IsChecked = settings.Options.Split; ResizeBox.IsChecked = settings.Options.Resize; WrapBox.IsChecked = settings.Options.Wrap;
        void Put(TextBox box, double n) => box.Text = n.ToString(CultureInfo.InvariantCulture);
        Put(CharactersBox, settings.Options.MaxCharacters); Put(LinesBox, settings.Options.MaxLines); Put(WidthBox, settings.Options.Width); Put(HeightBox, settings.Options.Height);
        Put(MinSizeBox, settings.Options.MinFontSize); Put(MaxSizeBox, settings.Options.MaxFontSize); Put(PaddingBox, settings.Options.Padding);
        Put(LayerBox, settings.Layer); Put(XBox, settings.X); Put(YBox, settings.Y);
        StatusText.Text = "設定を読み込みました。字幕を選択してプレビューしてください。";
    });
    private void OnUnloaded(object sender, RoutedEventArgs e) { pending = null; }
}
