using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EditAssist.Core;
using Microsoft.Win32;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace EditAssist.Plugin;
public sealed class CharacterAssignment
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}
public partial class TimelineWorkbench : UserControl
{
    private EditAssistViewModel? vm;
    private TimelineSession? session;
    private PreparedEdit? pending;
    private IReadOnlyList<ScriptLine> script = [];
    private string parsedText = "";
    private bool busy;
    private CancellationTokenSource? cancellation;
    private CheckedTimelineState? checkedState;
    private TimelineSession? checkedSession;
    private bool refreshing, connected;
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public TimelineWorkbench()
    {
        InitializeComponent();
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); if (IsLoaded && session is not null) Safe(Refresh); };
    }
    private void OnLoaded(object sender, RoutedEventArgs e) { connected = true; Attach(); }
    private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e) { if (IsLoaded) Attach(); }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    { connected = false; filterTimer.Stop(); ClearCheck(); cancellation?.Cancel(); if (vm is not null) vm.PropertyChanged -= OnVmChanged; vm = null; }
    private void Attach()
    {
        if (vm is not null) vm.PropertyChanged -= OnVmChanged;
        vm = DataContext as EditAssistViewModel;
        if (vm is not null) vm.PropertyChanged += OnVmChanged;
        BindTimeline();
    }
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(EditAssistViewModel.TimelineInfo)) BindTimeline(); }
    private void BindTimeline()
    {
        if (vm?.TimelineInfo is not { } info)
        { session = null; pending = null; ClearCheck(); CharacterLayersGrid.ItemsSource = null; ConnectionText.Text = "タイムラインに未接続です。プロジェクトを開き、ツールを開き直してください。"; return; }
        if (!ReferenceEquals(session?.Info, info))
        { cancellation?.Cancel(); session = new(info); pending = null; ClearCheck(); CharacterLayersGrid.ItemsSource = null; }
        ConnectionText.Text = $"接続：{info.Timeline.Name} / {info.Timeline.VideoInfo.FPS} fps / {info.Timeline.Items.Count:N0}個。変更は現在のタイムラインだけに適用します。";
        Safe(Refresh);
    }
    private TimelineSession Session => session ?? throw new InvalidOperationException("YMM4のタイムラインに接続できていません。");
    private static int? Integer(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null :
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n :
        throw new InvalidOperationException("フレーム／レイヤーには整数を入力してください。");
    private static double? Number(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null :
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n :
        throw new InvalidOperationException("サイズ・位置・音量には有限の数値を入力してください。");
    private ItemFilter Filter => new(KindFilter.SelectedIndex switch { 0 => "Voice", 1 => "Text", 2 => "Image", 3 => "Video", 4 => "Audio", _ => "All" },
        CharacterFilter.Text.Trim(), Integer(FromFrameBox), Integer(ToFrameBox), Integer(FromLayerBox), Integer(ToLayerBox), Query: ContentQueryBox.Text);
    private void Refresh()
    {
        refreshing = true;
        try
        {
            var current = Session;
            var all = current.Snapshot();
            TargetList.ItemsSource = TimelineEditing.Filter(all, Filter);
            string previous = CharacterFilter.Text;
            CharacterFilter.ItemsSource = current.Characters().Select(c => c.Name).ToArray();
            CharacterFilter.Text = previous;
            ConnectionText.Text = $"接続：{current.Info.Timeline.Name} / {current.Info.Timeline.VideoInfo.FPS} fps / 全体 {all.Count:N0}個、条件一致 {((IReadOnlyList<EditItem>)TargetList.ItemsSource).Count:N0}個";
        }
        finally { refreshing = false; }
    }
    private void Safe(Action action)
    { try { if (!busy) action(); } catch (Exception error) { StatusText.Text = error.Message; } }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; WorkTabs.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { StatusText.Text = "中止しました。準備中の結果はタイムラインへ追加していません。生成済み音声のキャッシュはYMM4側に残る場合があります。"; pending = null; }
        catch (Exception error) { StatusText.Text = "実行できませんでした：" + error.Message; }
        finally { busy = false; WorkTabs.IsEnabled = true; CancelButton.IsEnabled = false; }
    }
    private void OnRefresh(object sender, RoutedEventArgs e) => Safe(() => { pending = null; ClearCheck(); Refresh(); });
    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (busy || refreshing) return;
        pending = null;
        if (PlanTitleText is not null) PlanTitleText.Text = "条件が変わりました。変更プレビューを作り直してください。";
        if (IsLoaded && session is not null) { filterTimer.Stop(); filterTimer.Start(); }
    }
    private int[] TargetKeys() => TimelineEditing.Filter(Session.Snapshot(), Filter).Select(i => i.Key).ToArray();
    private void OnSelect(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var keys = TargetKeys();
        bool grouped = Session.Snapshot().Any(i => keys.Contains(i.Key) && i.Group != 0);
        Session.Select(keys); Refresh();
        StatusText.Text = $"{keys.Length}個をYMM4で選択しました。" + (grouped
            ? "グループ所属があります。YMM4側で手動移動すると関連素材も追従するため、先にグループを解除してください。"
            : "画像・動画など条件外の素材は選択していません。");
    });
    private void Display(PreparedEdit edit)
    {
        ClearCheck();
        pending = edit; AllowWarningsBox.IsChecked = false;
        var output = new StringBuilder();
        foreach (var c in edit.Plan.Changes)
        {
            var before = edit.Snapshot[c.Key];
            output.AppendLine($"#{c.Key} {before.Kind} {before.Character}：開始 {before.Frame}→{c.Frame ?? before.Frame}、長さ {before.Length}→{c.Length ?? before.Length}、レイヤー {before.Layer}→{c.Layer ?? before.Layer}");
        }
        foreach (var item in edit.Additions.OfType<VoiceItem>())
            output.AppendLine($"追加：{item.CharacterName} / 開始 {item.Frame} / 長さ {item.Length} / レイヤー {item.Layer} / {item.Serif}");
        if (edit.Preset is { } preset) output.AppendLine(System.Text.Json.JsonSerializer.Serialize(preset));
        foreach (var issue in edit.Plan.Issues.Distinct()) output.AppendLine($"[{issue.Severity}] #{issue.Key} {issue.Message}");
        output.AppendLine("時刻はフレーム。レイヤー移動では音声・セリフ・長さ・再生開始位置を変更しません。移動先のレイヤー音量・非表示・合成順の影響は受けます。");
        PlanTitleText.Text = $"{edit.Plan.Title}：変更 {edit.Plan.Changes.Count}個 / 追加 {edit.Additions.Count}個 / {(edit.Plan.IsValid ? "確認後に実行可" : "エラーあり・実行不可")}";
        PlanOutput.Text = output.ToString(); WorkTabs.SelectedIndex = 3;
        StatusText.Text = "まだ変更していません。内容を確認してから実行してください。";
    }
    private void OnPrepareMove(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var filter = Filter; int value = Integer(LayerValueBox) ?? throw new InvalidOperationException("移動先／移動量を入力してください。");
        bool relative = MoveMode.SelectedIndex == 1;
        Display(Session.Prepare(all => TimelineEditing.MoveLayers(all, TimelineEditing.Filter(all, filter).Select(i => i.Key), value, relative, Timeline.LayerLimit)));
    });
    private void OnPrepareFit(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var filter = Filter;
        if (filter.Kind is not ("Voice" or "All")) throw new InvalidOperationException("種類をボイスにしてください。");
        bool ripple = RippleBox.IsChecked == true;
        Display(Session.Prepare(all => TimelineEditing.FitVoices(all, TimelineEditing.Filter(all, filter).Select(i => i.Key),
            Session.Info.Timeline.VideoInfo.FPS, ripple ? TimelineEditing.Filter(all, filter with { Kind = "All", Character = "", IncludeLocked = true, Query = "" }).Select(i => i.Key) : null)));
    });
    private ItemFilter CharacterScope()
    {
        var filter = Filter;
        if (filter.Kind is not ("Voice" or "All")) throw new InvalidOperationException("キャラ別整理では種類を「ボイスのみ」または「すべて」にしてください。");
        return filter with { Kind = "Voice", IncludeLocked = true };
    }
    private void OnBuildCharacterLayers(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var previous = (CharacterLayersGrid.ItemsSource as IEnumerable<CharacterLayerRow>)?.ToDictionary(x => x.Character, x => x.DestinationLayer)
            ?? new Dictionary<string, string>();
        var targets = TimelineEditing.Filter(Session.Snapshot(), CharacterScope());
        var mappings = targets.GroupBy(x => x.Character).OrderBy(g => g.Key).Select(g =>
            new CharacterLayerRow(g.Key, g.Count()) { DestinationLayer = previous.GetValueOrDefault(g.Key, "") }).ToArray();
        foreach (var mapping in mappings) mapping.PropertyChanged += (_, _) =>
        { pending = null; PlanTitleText.Text = "キャラの移動先が変わりました。変更内容を確認し直してください。"; };
        CharacterLayersGrid.ItemsSource = mappings; pending = null;
        StatusText.Text = $"対象は{targets.Count}ボイス／{mappings.Length}キャラ。移動先を入力してください。空欄はそのままです。";
    });
    private void OnPrepareCharacterLayers(object sender, RoutedEventArgs e) => Safe(() =>
    {
        CharacterLayersGrid.CommitEdit(DataGridEditingUnit.Cell, true); CharacterLayersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var filter = CharacterScope();
        var destinations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mapping in CharacterLayersGrid.ItemsSource as IEnumerable<CharacterLayerRow> ?? [])
        {
            if (string.IsNullOrWhiteSpace(mapping.DestinationLayer)) continue;
            if (!int.TryParse(mapping.DestinationLayer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer))
                throw new InvalidOperationException($"「{mapping.Character}」の移動先には整数を入力してください。");
            destinations.Add(mapping.Character, layer);
        }
        Display(Session.Prepare(all => TimelineEditing.MoveVoicesByCharacter(all,
            TimelineEditing.Filter(all, filter).Select(x => x.Key), destinations, Timeline.LayerLimit)));
    });
    private EditPreset Preset => new(Name: PresetNameBox.Text.Trim(), Font: string.IsNullOrWhiteSpace(FontBox.Text) ? null : FontBox.Text.Trim(),
        FontSize: Number(FontSizeBox), FontColor: string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim(),
        Bold: BoldBox.SelectedIndex switch { 1 => true, 2 => false, _ => null }, Volume: Number(VolumeBox), X: Number(XBox), Y: Number(YBox));
    private void PutPreset(EditPreset p)
    {
        PresetNameBox.Text = p.Name; FontBox.Text = p.Font ?? ""; ColorBox.Text = p.FontColor ?? "";
        FontSizeBox.Text = p.FontSize?.ToString(CultureInfo.InvariantCulture) ?? "";
        BoldBox.SelectedIndex = p.Bold is null ? 0 : p.Bold.Value ? 1 : 2;
        VolumeBox.Text = p.Volume?.ToString(CultureInfo.InvariantCulture) ?? "";
        XBox.Text = p.X?.ToString(CultureInfo.InvariantCulture) ?? ""; YBox.Text = p.Y?.ToString(CultureInfo.InvariantCulture) ?? "";
    }
    private void OnCapturePreset(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var item = Session.Info.Timeline.SelectedItems;
        if (item.Count != 1) throw new InvalidOperationException("YMM4でボイス／テキストを1個だけ選択してください。");
        PutPreset(Session.CapturePreset(item[0])); StatusText.Text = "選択素材の設定を取得しました。アニメーションは最初の値を取得します。";
    });
    private void OnLoadPreset(object sender, RoutedEventArgs e) => Safe(() =>
    { var d = new OpenFileDialog { Filter = "EditAssistプリセット|*.json" }; if (d.ShowDialog() == true) PutPreset(EditPreset.Load(d.FileName)); });
    private void OnSavePreset(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var preset = Preset; preset.Validate();
        var d = new SaveFileDialog { Filter = "EditAssistプリセット|*.json", FileName = "EditAssist-preset.json", OverwritePrompt = true };
        if (d.ShowDialog() == true) { preset.Save(d.FileName); StatusText.Text = "プリセットを保存しました。"; }
    });
    private void OnPreparePreset(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var p = Preset;
        if (p.Font is null && p.FontSize is null && p.FontColor is null && p.Bold is null && p.Volume is null && p.X is null && p.Y is null)
            throw new InvalidOperationException("変更する項目を入力してください。");
        Display(Session.PreparePreset(TargetKeys(), p, ReplaceAnimationBox.IsChecked == true));
    });
    private async void OnLoadScript(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var d = new OpenFileDialog { Filter = "台本 CSV/TSV/テキスト|*.csv;*.tsv;*.txt" };
        if (d.ShowDialog() != true) return;
        await RunAsync(async () =>
        {
            if (new FileInfo(d.FileName).Length > 8_000_000) throw new InvalidDataException("台本を8MB以内に分けてください。");
            ScriptBox.Text = await File.ReadAllTextAsync(d.FileName, new UTF8Encoding(false, true));
            ParseScript();
        });
    }
    public void ImportAiScript(string csv)
    {
        if (busy) throw new InvalidOperationException("編集中の処理が終わるまで待ってください。");
        _ = ScriptImport.Parse(csv);
        ScriptBox.Text = csv; pending = null; script = [];
        WorkTabs.SelectedIndex = 2;
        StatusText.Text = "AI台本を受け取りました。台本を解析し、キャラ割当を確認してください。";
    }
    private void OnScriptChanged(object sender, TextChangedEventArgs e) { pending = null; script = []; }
    private void ParseScript()
    {
        script = ScriptImport.Parse(ScriptBox.Text); parsedText = ScriptBox.Text;
        var characters = Session.Characters();
        MappingGrid.ItemsSource = script.Select(l => l.Character).Distinct().Select(name => new CharacterAssignment
            { Source = name, Destination = characters.Any(c => c.Name == name) ? name : "" }).ToArray();
        CharacterNamesText.Text = "YMM4のキャラ名：" + string.Join("、", characters.Select(c => c.Name));
        StatusText.Text = $"台本 {script.Count}行。割り当てを確認してから音声を生成してください。";
    }
    private void OnParseScript(object sender, RoutedEventArgs e) => Safe(ParseScript);
    private async void OnPrepareScript(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await RunAsync(async () =>
        {
            pending = null;
            if (script.Count == 0 || parsedText != ScriptBox.Text) throw new InvalidOperationException("先に台本を解析してください。");
            MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true); MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var current = Session; var characters = current.Characters().ToDictionary(c => c.Name);
            var mapping = new Dictionary<string, Character>();
            foreach (CharacterAssignment row in MappingGrid.ItemsSource)
            {
                if (!characters.TryGetValue(row.Destination.Trim(), out var character)) throw new InvalidOperationException($"「{row.Source}」に有効なYMM4のキャラ名を割り当ててください。");
                mapping.Add(row.Source, character);
            }
            int start = Integer(ScriptStartBox) ?? current.Info.Timeline.CurrentFrame;
            int gap = Integer(ScriptGapBox) ?? 0; int? layer = Integer(ScriptLayerBox);
            cancellation = new(); var token = cancellation.Token; CancelButton.IsEnabled = true;
            try
            {
                var edit = await current.PrepareScriptAsync(script, mapping, start, layer, gap, token,
                    new Progress<string>(s => StatusText.Text = s));
                if (session != current || !IsLoaded) throw new OperationCanceledException();
                Display(edit);
            }
            finally { cancellation.Dispose(); cancellation = null; }
        });
    }
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var edit = pending;
        if (edit is null) { StatusText.Text = "先に変更プレビューを作成してください。"; return; }
        if (!edit.Plan.IsValid) { StatusText.Text = "エラーがあるため実行できません。"; return; }
        if (MessageBox.Show($"{edit.Plan.Title}\n変更 {edit.Plan.Changes.Count}個／追加 {edit.Additions.Count}個を実行しますか？\n事前にプロジェクトのバックアップを作ります。元ファイルを上書き保存しません。",
            "変更の最終確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool allow = AllowWarningsBox.IsChecked == true;
        await RunAsync(async () =>
        {
            var current = Session;
            await current.ApplyAsync(edit, allow); pending = null; ClearCheck();
            if (session == current) Refresh();
            StatusText.Text = "変更しました。YMM4の「元に戻す」または「直前の変更を復元」で戻せます。バックアップ：" + current.LastBackupPath;
        });
    }
    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (MessageBox.Show("このツールで直前に実行した変更を戻しますか？\nその後に別の編集があれば停止します。", "復元の確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { await Session.RestoreLastAsync(); pending = null; ClearCheck(); Refresh(); StatusText.Text = "直前の変更を復元しました。"; });
    }
    private void OnExportCopy(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var d = new SaveFileDialog { Filter = "YMM4プロジェクト|*.ymmp", FileName = "EditAssist-copy.ymmp", OverwritePrompt = true };
        if (d.ShowDialog() == true) { Session.SaveProjectCopy(d.FileName); StatusText.Text = "別名で保存しました。既存ファイルには上書きしません。"; }
    });
    private void OnOpenBackups(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (!Directory.Exists(TimelineSession.DefaultBackupDirectory)) { StatusText.Text = "バックアップは、変更を実行したときに作成されます。"; return; }
        Process.Start(new ProcessStartInfo(TimelineSession.DefaultBackupDirectory) { UseShellExecute = true });
    });
    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            ClearCheck(); pending = null;
            var current = Session;
            var state = current.CaptureCheckState();
            var all = state.Snapshot;
            int gap = Integer(GapThresholdBox) ?? current.Info.Timeline.VideoInfo.FPS;
            var issues = TimelineEditing.Check(all, gap).ToList();
            var paths = current.Info.Timeline.Items.Select((item, i) => (i, Files: (item as BaseItem)?.GetFiles().ToArray() ?? [])).ToArray();
            issues.AddRange(await Task.Run(() => paths.SelectMany(p => p.Files.Where(f => !string.IsNullOrWhiteSpace(f) && !File.Exists(f))
                .Select(f => new EditIssue("確認", p.i, "素材が見つからない、またはアクセスできません：" + f))).ToArray()));
            if (session != current || !connected) throw new InvalidOperationException("タイムラインが切り替わったか、画面が閉じられました。チェックをやり直してください。");
            current.RequireCheckCurrent(state);
            checkedState = state; checkedSession = current;
            CheckIssuesGrid.ItemsSource = issues.Select(i => EditingCheckRow.From(i, all)).ToArray();
            CheckResultsPanel.Visibility = Visibility.Visible;
            PlanOutput.Text = string.Join("\n", issues.Select(i => $"[{i.Severity}] #{i.Key} {i.Message}"));
            PlanTitleText.Text = $"編集チェック：{issues.Count}件。意図した重なり・間はそのままにしてください。";
            pending = null; WorkTabs.SelectedIndex = 3;
            StatusText.Text = "チェックだけを行いました。素材・配置は変更していません。";
        });
    }
    private void ClearCheck()
    {
        checkedState = null; checkedSession = null;
        if (CheckIssuesGrid is not null) CheckIssuesGrid.ItemsSource = null;
        if (CheckResultsPanel is not null) CheckResultsPanel.Visibility = Visibility.Collapsed;
    }
    private void OnNavigateIssue(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (checkedState is null || checkedSession != Session) throw new InvalidOperationException("編集チェックを実行し直してください。");
        if (CheckIssuesGrid.SelectedItem is not EditingCheckRow { Key: { } key })
            throw new InvalidOperationException("対象の素材があるチェック結果を選んでください。");
        try { Session.NavigateTo(checkedState, key); }
        catch { ClearCheck(); throw; }
        StatusText.Text = $"素材 #{key} の開始位置へ移動し、その素材だけを選択しました。配置や内容は変更していません。";
    });
    private void OnCancel(object sender, RoutedEventArgs e) => cancellation?.Cancel();
}

