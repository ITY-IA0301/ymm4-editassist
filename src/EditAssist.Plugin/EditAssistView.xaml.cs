using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EditAssist.Core;
using Microsoft.Win32;

namespace EditAssist.Plugin;

public partial class EditAssistView : UserControl
{
    private EditAssistViewModel? vm;
    private CatalogStore? store;
    private MaterialCatalog catalog = new();
    private MaterialEntry? selected;
    private bool ready, busy, changingSelection;
    private Task operation = Task.CompletedTask;
    private CancellationTokenSource? scanCancellation, measureCancellation;
    private readonly MediaPlayer player = new();
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Point dragOrigin;
    private MaterialEntry? dragItem;
    private EnvironmentSnapshot? environment;
    private readonly List<PlaybackObservation> observations = [];

    public EditAssistView()
    {
        InitializeComponent();
        AiPanel.MaterialSource = () => catalog.Entries.Select(x => x.Clone()).ToArray();
        AiPanel.TransferScript = csv => { TimelinePanel.ImportAiScript(csv); MainTabs.SelectedIndex = 0; };
        AiPanel.OpenMaterial = id =>
        {
            if (!ready || busy) throw new InvalidOperationException("素材ライブラリの読み込みが終わるまで待ってください。");
            if (!ResolveUnsaved()) return;
            var material = catalog.Entries.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("素材が見つかりません。");
            QueryBox.Text = ""; KindBox.SelectedIndex = 0; SceneBox.SelectedIndex = 0; FavoriteOnly.IsChecked = false;
            ShowResults(); ItemsList.SelectedItem = vm?.Results.FirstOrDefault(x => x.Id == id); MainTabs.SelectedIndex = 4;
        };
        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            if (ResolveUnsaved()) ShowResults();
        };
        player.MediaFailed += (_, _) =>
        {
            player.Close();
            SetStatus("この音声はWindowsの再生機能で開けませんでした。WAVまたはMP3でも確認してください。");
        };
        player.MediaEnded += (_, _) => player.Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await operation;
        if (!IsLoaded) return;
        vm = DataContext as EditAssistViewModel ?? new EditAssistViewModel();
        DataContext = vm;
        if (SceneBox.Items.Count == 0)
        {
            SceneBox.Items.Add("すべて");
            foreach (var scene in MaterialSearch.Scenes.Keys) SceneBox.Items.Add(scene);
            SceneBox.SelectedIndex = 0;
        }
        RefreshEnvironment();
        if (store is not null) { ShowResults(); SetEnabled(); return; }
        var newStore = new CatalogStore(CatalogStore.DefaultPath);
        store = newStore;
        await RunBusyAsync(async () =>
        {
            try
            {
                catalog = await Task.Run(newStore.Load);
                ready = true;
                ShowResults();
                SetStatus($"{catalog.Entries.Count:N0}件の素材を読み込みました。元ファイルは変更しません。");
            }
            catch
            {
                newStore.Dispose();
                if (ReferenceEquals(store, newStore)) store = null;
                ready = false;
                throw;
            }
        });
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        scanCancellation?.Cancel();
        measureCancellation?.Cancel();
        searchTimer.Stop();
        player.Close();
        await operation;
        if (!IsLoaded)
        {
            store?.Dispose();
            store = null;
            ready = false;
        }
    }

    private void SetStatus(string message) { if (vm is not null) vm.Status = message; }
    private void SetEnabled()
    {
        bool canEdit = ready && !busy && measureCancellation is null;
        AssetActionsPanel.IsEnabled = canEdit;
        SearchFiltersPanel.IsEnabled = canEdit;
        ItemsList.IsEnabled = canEdit;
        DetailsPanel.IsEnabled = canEdit && selected is not null;
        CancelScanButton.IsEnabled = busy && scanCancellation is not null;
    }

    private Task RunBusyAsync(Func<Task> action)
    {
        if (busy) return Task.CompletedTask;
        busy = true;
        SetEnabled();
        operation = ExecuteAsync(action);
        return operation;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("処理を中止しました。未保存の走査結果は反映していません。"); }
        catch (Exception ex)
        {
            SetStatus($"処理できませんでした：{ex.Message}　素材データの破損や同時起動の場合は、使い方とREADMEを確認してください。");
        }
        finally { busy = false; SetEnabled(); }
    }

    private async Task SaveCatalogAsync(MaterialCatalog next)
    {
        var currentStore = store ?? throw new InvalidOperationException("素材データを読み込めていません。");
        await Task.Run(() => currentStore.Save(next));
        catalog = next;
        ShowResults();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || busy) return;
        searchTimer.Stop();
        searchTimer.Start();
    }
    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        if (ResolveUnsaved()) ShowResults();
    }

    private string Scene => SceneBox.SelectedIndex <= 0 ? "" : SceneBox.SelectedItem as string ?? "";
    private void ShowResults()
    {
        if (!ready || vm is null) return;
        var oldId = selected?.Id;
        var kind = KindBox.SelectedIndex switch
        {
            1 => MaterialKind.Audio, 2 => MaterialKind.Image,
            3 => MaterialKind.Video, _ => (MaterialKind?)null
        };
        var results = MaterialSearch.Find(catalog.Entries,
            new(QueryBox.Text, Scene, kind, FavoriteOnly.IsChecked == true));
        changingSelection = true;
        try
        {
            vm.Results = results;
            ItemsList.SelectedItem = results.FirstOrDefault(m => m.Id == oldId);
            LoadDetails(ItemsList.SelectedItem as MaterialEntry);
        }
        finally { changingSelection = false; }
        SetEnabled();
    }

    private bool IsDirty() => selected is not null &&
        (TitleBox.Text != selected.Title ||
         !MaterialSearch.ParseTags(TagsBox.Text).SequenceEqual(selected.Tags) ||
         NotesBox.Text != selected.Notes || SourceBox.Text != selected.SourceUrl ||
         LicenseBox.Text != selected.LicenseNote || FavoriteBox.IsChecked != selected.Favorite);

    private bool ResolveUnsaved() => !IsDirty() || MessageBox.Show(
        "素材の情報に未保存の変更があります。変更を破棄して続けますか？\n保存する場合は「いいえ」を選び、「変更を保存」を押してください。",
        "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingSelection) return;
        if (!ResolveUnsaved())
        {
            changingSelection = true;
            ItemsList.SelectedItem = selected;
            changingSelection = false;
            return;
        }
        player.Close();
        LoadDetails(ItemsList.SelectedItem as MaterialEntry);
        SetEnabled();
    }

    private void LoadDetails(MaterialEntry? item)
    {
        selected = item;
        TitleBox.Text = item?.Title ?? "";
        TagsBox.Text = item is null ? "" : string.Join(", ", item.Tags);
        NotesBox.Text = item?.Notes ?? "";
        SourceBox.Text = item?.SourceUrl ?? "";
        LicenseBox.Text = item?.LicenseNote ?? "";
        FavoriteBox.IsChecked = item?.Favorite ?? false;
    }

    private async void OnSaveMetadata(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || selected is null) return;
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { SetStatus("表示名を入力してください。"); return; }
        var next = catalog.Clone();
        var item = next.Entries.First(m => m.Id == selected.Id);
        item.Title = TitleBox.Text.Trim();
        item.Tags = MaterialSearch.ParseTags(TagsBox.Text);
        item.Notes = NotesBox.Text;
        item.SourceUrl = SourceBox.Text.Trim();
        item.LicenseNote = LicenseBox.Text;
        item.Favorite = FavoriteBox.IsChecked == true;
        await RunBusyAsync(async () =>
        {
            await SaveCatalogAsync(next);
            SetStatus("素材情報を保存しました。直前のデータはバックアップに残しています。");
        });
    }

    private void OnAddSceneTag(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Scene)) { SetStatus("上の「場面」を選んでください。"); return; }
        var tags = MaterialSearch.ParseTags(TagsBox.Text);
        if (!tags.Contains(Scene)) tags.Add(Scene);
        TagsBox.Text = string.Join(", ", tags);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || selected is null) return;
        if (MessageBox.Show("この素材をライブラリから外しますか？\n元ファイルは削除しません。フォルダの再スキャンで再登録されます。",
            "登録解除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var next = catalog.Clone();
        next.Entries.RemoveAll(m => m.Id == selected.Id);
        await RunBusyAsync(async () => { await SaveCatalogAsync(next); SetStatus("登録を外しました。"); });
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || !ResolveUnsaved()) return;
        var dialog = new OpenFolderDialog { Title = "素材フォルダを選択", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await ScanAsync([dialog.FolderName], dialog.FolderName);
    }
    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || !ResolveUnsaved()) return;
        var dialog = new OpenFileDialog { Title = "素材を選択", Multiselect = true, Filter = "素材ファイル|*.*" };
        if (dialog.ShowDialog() != true) return;
        await ScanAsync(dialog.FileNames, null);
    }
    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || !ResolveUnsaved()) return;
        // Also refresh individually added files; paths are deduplicated in the scanner.
        var paths = catalog.Roots.Concat(catalog.Entries.Select(m => m.FilePath)).ToArray();
        if (paths.Length == 0) { SetStatus("フォルダかファイルを追加してください。"); return; }
        await ScanAsync(paths, null);
    }

    private async Task ScanAsync(string[] paths, string? root)
    {
        scanCancellation = new CancellationTokenSource();
        var cancellation = scanCancellation;
        try
        {
            await RunBusyAsync(async () =>
            {
                SetStatus("素材を走査しています。音声・映像のデコードは行いません。");
                var progress = new Progress<int>(count =>
                { if (busy && ReferenceEquals(scanCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    SetStatus($"{count:N0}件の素材を確認中…"); });
                var result = await Task.Run(() => MaterialScanner.Scan(paths, cancellation.Token, progress));
                cancellation.Token.ThrowIfCancellationRequested();
                var next = catalog.Clone();
                if (root is not null && !next.Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    next.Roots.Add(Path.GetFullPath(root));
                MaterialScanner.Merge(next, result);
                cancellation.Token.ThrowIfCancellationRequested();
                await SaveCatalogAsync(next);
                SetStatus($"{result.Entries.Count:N0}件を確認。総登録 {catalog.Entries.Count:N0}件。" +
                    (result.LimitReached ? " 走査上限2万件に達しました。小さいフォルダに分けて追加してください。" : "") +
                    (result.UnreadableItems > 0 ? $" 読めなかった項目：{result.UnreadableItems}件。" : "") +
                    " 権限のないフォルダとリンクは走査対象外です。");
            });
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(scanCancellation, cancellation)) scanCancellation = null;
            SetEnabled();
        }
    }

    private async void OnCheckMissing(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || !ResolveUnsaved()) return;
        scanCancellation = new CancellationTokenSource();
        var cancellation = scanCancellation;
        try
        {
            await RunBusyAsync(async () =>
            {
                var next = catalog.Clone();
                await Task.Run(() =>
                {
                    foreach (var item in next.Entries)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        item.IsMissing = !File.Exists(item.FilePath);
                    }
                });
                cancellation.Token.ThrowIfCancellationRequested();
                catalog = next; // This is runtime availability, not metadata to save.
                ShowResults();
                SetStatus($"欠損またはアクセス不可：{next.Entries.Count(m => m.IsMissing):N0}件。登録は保持しています。");
            });
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(scanCancellation, cancellation)) scanCancellation = null;
            SetEnabled();
        }
    }
    private void OnCancelScan(object sender, RoutedEventArgs e) => scanCancellation?.Cancel();

    private void RunUi(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetStatus($"操作できませんでした：{ex.Message}"); }
    }
    private void OnPlay(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        if (selected is null) return;
        if (selected.Kind != MaterialKind.Audio) { SetStatus("初版の試聴は音声素材のみです。"); return; }
        if (!File.Exists(selected.FilePath)) { SetStatus("素材ファイルが見つかりません。"); return; }
        player.Close();
        player.Open(new Uri(selected.FilePath, UriKind.Absolute));
        player.Volume = VolumeSlider.Value;
        player.Play();
        SetStatus("試聴中。選択を変えるか「停止」で終了します。");
    });
    private void OnStop(object sender, RoutedEventArgs e) => player.Close();
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => player.Volume = e.NewValue;
    private void OnCopyPath(object sender, RoutedEventArgs e) => RunUi(() =>
    { if (selected is not null) Clipboard.SetText(selected.FilePath); });
    private void OnOpenLocation(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        if (selected is null) return;
        var parent = Path.GetDirectoryName(selected.FilePath);
        if (parent is not null && Directory.Exists(parent))
            Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
        else SetStatus("素材のフォルダが見つかりません。");
    });
    private void OpenWeb(string address) => RunUi(() =>
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        { SetStatus("http または https の配布元ページURLを入力してください。"); return; }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    });
    private void OnOpenSource(object sender, RoutedEventArgs e) => OpenWeb(SourceBox.Text.Trim());
    private void OnSoundEffectLab(object sender, RoutedEventArgs e) => OpenWeb("https://soundeffect-lab.info/");
    private void OnOtoLogic(object sender, RoutedEventArgs e) => OpenWeb("https://otologic.jp/");
    private void OnPreviewFaq(object sender, RoutedEventArgs e) => OpenWeb("https://manjubox.net/ymm4/faq/editing/プレビュー画面がフリーズする/");
    private void OnOpenCatalogFolder(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        var folder = Path.GetDirectoryName(CatalogStore.DefaultPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    });

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = e.GetPosition(ItemsList);
        dragItem = (ItemsControl.ContainerFromElement(ItemsList,
            e.OriginalSource as DependencyObject) as ListViewItem)?.DataContext as MaterialEntry;
    }
    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragItem is null || busy ||
            measureCancellation is not null || selected?.Id != dragItem.Id) return;
        var delta = e.GetPosition(ItemsList) - dragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = dragItem;
        dragItem = null;
        RunUi(() =>
        {
            if (!File.Exists(item.FilePath)) { SetStatus("素材ファイルが見つかりません。"); return; }
            player.Close();
            var files = new StringCollection { item.FilePath };
            var data = new DataObject();
            data.SetFileDropList(files);
            DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy);
        });
    }

    private void RefreshEnvironment()
    {
        try
        {
            environment = DiagnosticService.Collect();
            EnvironmentBox.Text = $"YMM4：{environment.HostVersion}\n{environment.Framework} / {environment.OperatingSystem}\n" +
                $"{environment.Architecture} / 論理CPU {environment.LogicalProcessors} / EditAssist 0.1.0";
            RefreshReport();
        }
        catch (Exception ex) { EnvironmentBox.Text = $"環境情報を取得できませんでした：{ex.Message}"; }
    }
    private void RefreshReport()
    {
        if (environment is not null) ReportBox.Text = DiagnosticService.CreateReport(environment, observations);
    }
    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (measureCancellation is not null) return;
        if (busy) { MeasurementStatus.Text = "素材の処理が終わってから計測してください。"; return; }
        player.Close();
        searchTimer.Stop();
        measureCancellation = new CancellationTokenSource();
        var cancellation = measureCancellation;
        MeasureButton.IsEnabled = false;
        MeasurementFields.IsEnabled = false;
        CancelMeasureButton.IsEnabled = true;
        SetEnabled();
        try
        {
            MeasurementStatus.Text = "10秒計測中… YMM4のプレビューを再生してください。";
            var observation = await DiagnosticService.MeasureAsync(CaseBox.Text, ConditionBox.Text,
                DecoderBox.Text, ProxyBox.Text, SymptomBox.Text, cancellation.Token);
            observations.Add(observation);
            RefreshReport();
            MeasurementStatus.Text = $"記録{observations.Count}件 / CPU {observation.ProcessCpuPercent:F1}% / " +
                $"作業メモリ {observation.WorkingSetMiB:F0} MiB。観察した症状を入力し「最後の記録に症状を反映」で更新できます。";
        }
        catch (OperationCanceledException) { MeasurementStatus.Text = "計測を中止しました。"; }
        catch (Exception ex) { MeasurementStatus.Text = $"計測できませんでした：{ex.Message}"; }
        finally
        {
            cancellation.Dispose();
            measureCancellation = null;
            MeasureButton.IsEnabled = true;
            MeasurementFields.IsEnabled = true;
            CancelMeasureButton.IsEnabled = false;
            SetEnabled();
        }
    }
    private void OnCancelMeasure(object sender, RoutedEventArgs e) => measureCancellation?.Cancel();
    private void OnUpdateObservation(object sender, RoutedEventArgs e)
    {
        if (observations.Count == 0 || measureCancellation is not null) return;
        observations[^1] = observations[^1] with { Symptom = SymptomBox.Text };
        RefreshReport();
        MeasurementStatus.Text = "最後の計測記録に症状を反映しました。";
    }
    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        if (environment is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "比較記録を保存（共有前に内容を確認）", Filter = "Markdown|*.md",
            FileName = "EditAssist-comparison.md", OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var report = DiagnosticService.CreateReport(environment, observations);
            await File.WriteAllTextAsync(dialog.FileName, report);
            MeasurementStatus.Text = "比較記録を保存しました。共有前に自由記述の個人情報を確認してください。";
        }
        catch (Exception ex) { MeasurementStatus.Text = $"保存できませんでした：{ex.Message}"; }
    }
}


