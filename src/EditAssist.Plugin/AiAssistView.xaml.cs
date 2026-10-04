using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EditAssist.Core;
using Microsoft.Win32;
using YukkuriMovieMaker.Project.Items;

namespace EditAssist.Plugin;

internal sealed record AiMaterialRow(Guid Id, int Line, string Title, string Reason);
public partial class AiAssistView : UserControl
{
    public Func<IReadOnlyList<MaterialEntry>> MaterialSource { get; set; } = () => [];
    public Action<string>? TransferScript { get; set; }
    public Action<Guid>? OpenMaterial { get; set; }
    private AiRequest? request;
    private AiResult? result;
    private CancellationTokenSource? cancellation;
    public AiAssistView() { InitializeComponent(); }
    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        request = null; ClearResult();
        if (PromptBox is not null) PromptBox.Text = "";
        if (SendConsentBox is not null) SendConsentBox.IsChecked = false;
    }
    private void OnResponseChanged(object sender, RoutedEventArgs e) => ClearResult();
    private void ClearResult()
    {
        result = null;
        if (LinesGrid is not null) LinesGrid.ItemsSource = null;
        if (MaterialsGrid is not null) MaterialsGrid.ItemsSource = null;
        if (NotesBox is not null) NotesBox.Text = "";
    }
    private void Safe(Action action) { try { if (cancellation is null) action(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private AiRequest BuildRequest()
    {
        var material = IncludeMaterialsBox.IsChecked == true
            ? MaterialSearch.Find(MaterialSource(), new(Query: MaterialQueryBox.Text)).Where(m => !m.IsMissing).Take(200)
                .Select(m => new AiMaterial(m.Id, m.Title, m.KindText, m.TagText, m.Notes)).ToArray() : [];
        return new(InstructionBox.Text.Trim(), SpeakingStyle.Settings(CharactersBox.Text, ManualStyleBox.Text, UseManualStyleBox.IsChecked == true,
            LearnedStyleBox.Text, UseLearnedStyleBox.IsChecked == true), DraftBox.Text, material);
    }
    private void OnPrompt(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var pending = BuildRequest(); string prompt = AiAssist.Prompt(pending); request = pending;
        ClearResult(); PromptBox.Text = prompt; SendConsentBox.IsChecked = false;
        StatusText.Text = $"依頼文を作成しました。素材候補 {pending.Materials.Count}件。送る内容を確認してください。";
    });
    private void OnCopy(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (request is null) OnPrompt(sender, e);
        if (request is null) return;
        Clipboard.SetText(AiAssist.Prompt(request)); StatusText.Text = "ChatGPTに依頼文を貼り付け、返答のJSONを戻してください。";
    });
    private void OnOpenChatGpt(object sender, RoutedEventArgs e) => Safe(() => Process.Start(new ProcessStartInfo("https://chatgpt.com/") { UseShellExecute = true }));
    private void OnCharacters(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var info = (DataContext as EditAssistViewModel)?.TimelineInfo ?? throw new InvalidOperationException("プロジェクトを開いてください。");
        CharactersBox.Text = string.Join("\n", new TimelineSession(info).Characters().Select(x => x.Name + "：口調・役割を記入"));
    });
    private void OnDraft(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var info = (DataContext as EditAssistViewModel)?.TimelineInfo ?? throw new InvalidOperationException("プロジェクトを開いてください。");
        DraftBox.Text = string.Join("\n", info.Timeline.SelectedItems.OfType<VoiceItem>().OrderBy(x => x.Frame).Select(x => x.CharacterName + "：" + x.Serif));
    });
    private sealed record StyleSettings(int Version, string Manual, string Learned, bool UseManual, bool UseLearned);
    private void OnLearnStyleFile(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var dialog = new OpenFileDialog { Filter = "参考台本（キャラ,セリフの2列）|*.csv;*.tsv" };
        if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 4_000_000) throw new InvalidDataException("参考台本が大きすぎます。");
        LearnedStyleBox.Text = SpeakingStyle.Extract(File.ReadAllText(dialog.FileName));
        StatusText.Text = "キャラ別の文末と例文を抽出しました。確認・修正後、使用チェックをONにしてください。既存の抽出結果は置き換わります。";
    });
    private void OnLearnStyleSelection(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var info = (DataContext as EditAssistViewModel)?.TimelineInfo ?? throw new InvalidOperationException("プロジェクトを開いてください。");
        var lines = info.Timeline.SelectedItems.OfType<VoiceItem>().OrderBy(x => x.Frame)
            .Select(x => new AiLine(x.CharacterName, x.Serif)).ToArray();
        LearnedStyleBox.Text = SpeakingStyle.Extract(AiAssist.Csv(new AiResult(1, lines, [], "")));
        StatusText.Text = "選択ボイスから口調の参考を抽出しました。確認して使用チェックをONにしてください。";
    });
    private void OnClearLearnedStyle(object sender, RoutedEventArgs e) => Safe(() =>
    { LearnedStyleBox.Text = ""; UseLearnedStyleBox.IsChecked = false; });
    private void OnSaveStyle(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var settings = new StyleSettings(1, ManualStyleBox.Text, LearnedStyleBox.Text,
            UseManualStyleBox.IsChecked == true, UseLearnedStyleBox.IsChecked == true);
        _ = SpeakingStyle.Settings("", settings.Manual, true, settings.Learned, true);
        var dialog = new SaveFileDialog { Filter = "口調設定|*.json", FileName = "EditAssist-speaking-style.json" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        StatusText.Text = "口調設定と参考例文を保存しました。";
    });
    private void OnLoadStyle(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var dialog = new OpenFileDialog { Filter = "口調設定|*.json" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 100_000) throw new InvalidDataException("口調設定が大きすぎます。");
        var settings = System.Text.Json.JsonSerializer.Deserialize<StyleSettings>(File.ReadAllText(dialog.FileName))
            ?? throw new InvalidDataException("口調設定が空です。");
        if (settings.Version != 1 || settings.Manual is null || settings.Learned is null)
            throw new InvalidDataException("口調設定の形式が不正です。");
        _ = SpeakingStyle.Settings("", settings.Manual, true, settings.Learned, true);
        ManualStyleBox.Text = settings.Manual; LearnedStyleBox.Text = settings.Learned;
        UseManualStyleBox.IsChecked = settings.UseManual; UseLearnedStyleBox.IsChecked = settings.UseLearned;
        StatusText.Text = "口調設定を読み込みました。生成前に内容を確認してください。";
    });
    private void OnBrowseCli(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Codex CLI|codex.exe;codex.cmd|実行ファイル|*.exe;*.cmd" };
        if (dialog.ShowDialog() == true) CliPathBox.Text = dialog.FileName;
    });
    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) return;
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(PromptBox.Text)) throw new InvalidOperationException("先に依頼文を作成してください。");
            if (SendConsentBox.IsChecked != true) throw new InvalidOperationException("送信内容と利用枠の使用を確認してください。");
            var current = request; IAiProvider provider = new CodexCliProvider(CliPathBox.Text, ModelBox.Text.Trim());
            using var source = new CancellationTokenSource(); cancellation = source; AiTabs.IsEnabled = false; CancelButton.IsEnabled = true;
            StatusText.Text = "Codexで台本・素材候補を生成しています…";
            var generated = await provider.GenerateAsync(current, source.Token);
            if (!ReferenceEquals(request, current)) throw new InvalidOperationException("依頼が変わりました。もう一度生成してください。");
            ResponseBox.Text = System.Text.Json.JsonSerializer.Serialize(generated, new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true });
            ShowResult(generated); AiTabs.SelectedIndex = 1; StatusText.Text = "生成しました。台本・素材候補を確認してください。";
        }
        catch (OperationCanceledException) { StatusText.Text = "AI生成を中止しました。台本やプロジェクトは変更していません。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { cancellation = null; AiTabs.IsEnabled = true; CancelButton.IsEnabled = false; }
    }
    private void ShowResult(AiResult value)
    {
        result = value; LinesGrid.ItemsSource = value.Lines; NotesBox.Text = value.Notes;
        MaterialsGrid.ItemsSource = value.Materials.Select(m => new AiMaterialRow(Guid.Parse(m.MaterialId), m.Line,
            request!.Materials.First(x => x.Id == Guid.Parse(m.MaterialId)).Title, m.Reason)).ToArray();
    }
    private void OnImport(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (request is null) throw new InvalidOperationException("返答の元となる依頼文を先に作成してください。");
        ClearResult(); ShowResult(AiAssist.Parse(ResponseBox.Text, request)); StatusText.Text = "返答の形式と素材IDを確認しました。内容を確認して台本編集へ渡してください。";
    });
    private void OnOpenResult(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var dialog = new OpenFileDialog { Filter = "AIの返答|*.json;*.txt" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 4_000_000) throw new InvalidDataException("返答が大きすぎます。");
        ResponseBox.Text = File.ReadAllText(dialog.FileName); OnImport(sender, e);
    });
    private void OnTransfer(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (result is null || result.Lines.Count == 0) throw new InvalidOperationException("有効な台本を読み込んでください。");
        if (TransferScript is null) throw new InvalidOperationException("台本編集に接続できません。");
        TransferScript(AiAssist.Csv(result)); StatusText.Text = "台本編集へ渡しました。キャラ割当・音声生成・配置を確認してください。";
    });
    private void OnSaveCsv(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (result is null) throw new InvalidOperationException("先に返答を読み込んでください。");
        var dialog = new SaveFileDialog { Filter = "台本|*.csv", FileName = "EditAssist-AI-script.csv" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, AiAssist.Csv(result), new UTF8Encoding(true));
    });
    private void OnMaterial(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (MaterialsGrid.SelectedItem is not AiMaterialRow row) throw new InvalidOperationException("素材候補を選択してください。");
        if (!MaterialSource().Any(x => x.Id == row.Id)) throw new InvalidOperationException("素材の登録が変わっています。候補を作り直してください。");
        OpenMaterial?.Invoke(row.Id);
    });
    private void OnCancel(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void OnUnloaded(object sender, RoutedEventArgs e) => cancellation?.Cancel();
}
