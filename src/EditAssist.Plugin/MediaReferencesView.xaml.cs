using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EditAssist.Plugin;

public partial class MediaReferencesView : UserControl
{
    private EditAssistViewModel? model;
    private MediaReferenceSession? session;
    private ReferenceBaseline? baseline;
    private IReadOnlyList<MediaRow> rows = [];
    private RelinkPlan? plan;
    private CancellationTokenSource? cancellation;
    private bool busy, active, publishingRows;
    private readonly Dictionary<string, (FileStamp Stamp, string Samples, DecodeResult Result)> decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromSeconds(15) };
    internal string HistoryDirectory { get; set; } = Path.Combine(MediaReferenceSession.StorageRoot, "reference-history");
    internal string? LegacyHistoryDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4PreviewLite", "reference-history");

    public MediaReferencesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { active = true; Attach(); watch.Start(); };
        Unloaded += (_, _) => { active = false; watch.Stop(); cancellation?.Cancel(); Detach(); };
        watch.Tick += async (_, _) => { if (WatchBox.IsChecked == true && !busy && session is not null) await InspectAsync(false, true); };
        FilenameBox.TextChanged += (_, _) => InvalidatePlan(true);
        DirectoryBox.TextChanged += (_, _) => InvalidatePlan(true);
        RecursiveBox.Checked += (_, _) => InvalidatePlan(true);
        RecursiveBox.Unchecked += (_, _) => InvalidatePlan(true);
    }

    private void Detach()
    {
        if (model is not null) model.PropertyChanged -= ModelChanged;
        model = null;
    }
    private void Attach()
    {
        Detach();
        model = DataContext as EditAssistViewModel;
        if (model is not null) model.PropertyChanged += ModelChanged;
        Connect();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditAssistViewModel.TimelineInfo)) Connect();
    }
    private void Connect()
    {
        cancellation?.Cancel();
        InvalidatePlan();
        session = model?.TimelineInfo is { } info ? new(info) : null;
        baseline = null;
        rows = [];
        PublishRows();
        MediaConnectionText.Text = session is null ? "プロジェクトを開いてください。" : "接続：" + session.Info.Timeline.Name + "（全シーンの動画を対象）";
        SetBusy(busy);
        if (active && session is not null && !busy) _ = InspectAsync(false, true);
    }

    private void InvalidatePlan(bool clearMessage = false)
    {
        plan = null;
        if (ApplyRelinkButton is not null) ApplyRelinkButton.IsEnabled = false;
        if (clearMessage && RelinkPlanText is not null) RelinkPlanText.Text = "ファイル名と移動先を指定し、「変更内容を確認」を押してください。";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        foreach (var button in new[] { InspectButton, DecodeButton, BaselineButton, PrepareRelinkButton, FolderButton, VideoButton }) button.IsEnabled = !value && session is not null;
        FilenameBox.IsEnabled = DirectoryBox.IsEnabled = RecursiveBox.IsEnabled = !value;
        CancelMediaButton.IsEnabled = value;
        ApplyRelinkButton.IsEnabled = !value && plan is not null;
    }
    private MediaReferenceSession RequireSession() => session ?? throw new InvalidOperationException("プロジェクトを開いてください。");
    private async void InspectClick(object sender, RoutedEventArgs e) => await InspectAsync(false, false);
    private async void DecodeClick(object sender, RoutedEventArgs e) => await InspectAsync(true, false);
    private void CancelClick(object sender, RoutedEventArgs e) => cancellation?.Cancel();

    private async Task InspectAsync(bool decode, bool automatic)
    {
        if (busy || session is null) return;
        SetBusy(true);
        cancellation = new();
        var token = cancellation.Token;
        var connected = session;
        try
        {
            var references = connected.CaptureReferences();
            string key = connected.ProjectKey;
            baseline = MediaCatalog.LoadWithLegacy(HistoryDirectory, key, LegacyHistoryDirectory);
            var inspected = await Task.Run(() => MediaCatalog.Inspect(references, baseline, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(connected, session) || key != connected.ProjectKey) return;
            bool first = baseline is null;
            if (first)
            {
                baseline = MediaCatalog.Capture(key, inspected);
                MediaCatalog.SaveBaseline(HistoryDirectory, baseline);
            }
            var working = inspected.ToList();
            if (decode)
            {
                var host = Path.GetDirectoryName(typeof(YukkuriMovieMaker.Plugin.IPlugin).Assembly.Location)!;
                var tools = MediaDecoderProbe.Locate(host);
                for (int i = 0; i < working.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = working[i];
                    MediaStatusText.Text = $"映像確認 {i + 1}/{working.Count}：{row.Filename}（中止できます）";
                    if (row.Stamp is null) continue;
                    DecodeResult result;
                    try
                    {
                        var samples = references.Where(x => string.Equals(x.Path, row.Path, StringComparison.OrdinalIgnoreCase)).SelectMany(x => x.SampleSeconds).ToArray();
                        result = await MediaDecoderProbe.CheckAsync(row.Path, samples, tools.Probe, tools.Decoder, token);
                        if (FileStamp.Read(row.Path) != row.Stamp) result = new(null, "検査中に動画が更新されました。もう一度検査してください。");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result = new(false, ex.Message); }
                    decoded[row.Path] = (row.Stamp, SampleKey(row.Path, references), result);
                }
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(connected, session) || key != connected.ProjectKey) return;
            if (ReferenceKey(references) != ReferenceKey(connected.CaptureReferences()))
                throw new InvalidOperationException("検査中に動画参照や使用位置が変わりました。もう一度確認してください。");
            rows = working.Select(row => WithDecode(row, references)).OrderByDescending(x => x.HasWarning).ToArray();
            PublishRows();
            int warnings = rows.Count(x => x.HasWarning);
            MediaStatusText.Text = $"動画{rows.Count}本／{references.Count}クリップ：警告・未確認 {warnings}件。" +
                (first ? " 初回の比較基準を記録しました。" : " 変更の警告は「比較基準に保存」するまで残ります。") +
                (decode ? " 映像確認は最大4か所だけです。" : " 存在・サイズ・読み取りの簡易確認です。");
        }
        catch (OperationCanceledException) { if (active) MediaStatusText.Text = "確認を中止しました。未検査の動画を正常とは判定していません。"; }
        catch (Exception ex) { if (active) MediaStatusText.Text = (automatic ? "簡易確認を保留：" : "確認できません：") + ex.GetBaseException().Message; }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }

    private static string SampleKey(string path, IReadOnlyList<VideoReference> references) => string.Join(",",
        MediaDecoderProbe.SamplePositions(references.Where(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)).SelectMany(x => x.SampleSeconds))
            .Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    private static string ReferenceKey(IReadOnlyList<VideoReference> references) => System.Text.Json.JsonSerializer.Serialize(references);

    private MediaRow WithDecode(MediaRow row, IReadOnlyList<VideoReference> references)
    {
        if (row.Stamp is null || !decoded.TryGetValue(row.Path, out var cached) || cached.Stamp != row.Stamp || cached.Samples != SampleKey(row.Path, references)) return row;
        string detail = row.Detail.Replace("映像デコードは未確認。", "").Trim() + "\n" + cached.Result.Detail;
        return row with { Status = cached.Result.Success == true ? row.Status : cached.Result.Success == false ? "映像読み込み警告" : "映像は未確認", Detail = detail };
    }

    private void BaselineClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var connected = RequireSession();
            if (rows.Count == 0) throw new InvalidOperationException("先に参照先を確認してください。");
            if (MessageBox.Show("現在の参照先を比較基準として記録し直します。パス・サイズ・更新日時の変更警告がリセットされます。\n読み込み異常の警告は解除しません。", "比較基準の更新", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var references = connected.CaptureReferences();
            var fresh = MediaCatalog.Inspect(references, null, CancellationToken.None);
            baseline = MediaCatalog.Capture(connected.ProjectKey, fresh);
            MediaCatalog.SaveBaseline(HistoryDirectory, baseline);
            rows = fresh.Select(row => WithDecode(row, references)).ToArray();
            PublishRows();
            MediaStatusText.Text = "現在の参照を比較基準に記録しました。映像読み込み警告は引き続き確認してください。";
        }
        catch (Exception ex) { MediaStatusText.Text = ex.GetBaseException().Message; }
    }

    private void MediaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (publishingRows) return;
        if (MediaList.SelectedItem is not MediaRow row) { MediaDetailText.Text = "一覧を選択すると詳細とファイル名を表示します。"; return; }
        FilenameBox.Text = row.Filename;
        MediaDetailText.Text = row.Path + "\n" + row.Detail;
    }
    private void PublishRows()
    {
        string? selected = (MediaList.SelectedItem as MediaRow)?.Path;
        publishingRows = true;
        try
        {
            MediaList.ItemsSource = rows;
            MediaList.SelectedItem = rows.FirstOrDefault(x => string.Equals(x.Path, selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { publishingRows = false; }
        MediaDetailText.Text = MediaList.SelectedItem is MediaRow row ? row.Path + "\n" + row.Detail : "一覧を選択すると詳細とファイル名を表示します。";
    }
    private void FolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "移動先動画があるフォルダーを指定", Multiselect = false };
        if (dialog.ShowDialog() == true) DirectoryBox.Text = dialog.FolderName;
    }
    private void VideoClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "同じ名前の移動先動画を指定", CheckFileExists = true, Multiselect = false,
            Filter = "動画|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|すべてのファイル|*.*" };
        if (dialog.ShowDialog() == true) { FilenameBox.Text = Path.GetFileName(dialog.FileName); DirectoryBox.Text = Path.GetDirectoryName(dialog.FileName)!; RecursiveBox.IsChecked = false; }
    }

    private async void PrepareClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        InvalidatePlan();
        SetBusy(true);
        cancellation = new();
        try
        {
            var connected = RequireSession();
            string name = FilenameBox.Text.Trim().Trim('"'), directory = DirectoryBox.Text;
            bool recursive = RecursiveBox.IsChecked == true;
            var candidates = await Task.Run(() => MediaCatalog.FindCandidates(name, directory, recursive, cancellation.Token), cancellation.Token);
            if (!ReferenceEquals(connected, session)) throw new InvalidOperationException("プロジェクトが変わりました。");
            if (candidates.Length == 0) throw new FileNotFoundException("指定フォルダーに同名の動画がありません。");
            if (candidates.Length > 1) throw new InvalidOperationException("同名候補が複数あります。検索先を絞るか「移動先動画から指定」を使ってください。\n" + string.Join("\n", candidates));
            if (name != FilenameBox.Text.Trim().Trim('"') || directory != DirectoryBox.Text || recursive != (RecursiveBox.IsChecked == true))
                throw new InvalidOperationException("検索中に入力が変わりました。もう一度確認してください。");
            plan = connected.Prepare(name, candidates[0]);
            RelinkPlanText.Text = plan.Summary + "\n同じファイル名でも内容が同じとは限りません。移動先と全件変更することを確認してください。";
        }
        catch (Exception ex) { RelinkPlanText.Text = ex is OperationCanceledException ? "検索を中止しました。" : ex.GetBaseException().Message; }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (busy || plan is null) return;
        var prepared = plan;
        if (MessageBox.Show(prepared.Summary + "\n\n変更前のバックアップを保存し、参照先だけ変更します。実行しますか？", "再参照の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SetBusy(true);
        try
        {
            CancelMediaButton.IsEnabled = false;
            var connected = RequireSession();
            await connected.ApplyAsync(prepared);
            RelinkPlanText.Text = $"{prepared.Targets.Count}クリップを再参照しました。プロジェクトを保存してください。\nバックアップ：{connected.LastBackupPath}";
            InvalidatePlan();
        }
        catch (Exception ex) { InvalidatePlan(); RelinkPlanText.Text = "変更停止：" + ex.GetBaseException().Message; }
        finally { SetBusy(false); }
        await InspectAsync(false, false);
    }
}
