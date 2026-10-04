using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EditAssist.Core;
using EditAssist.Plugin;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;

internal static class SubtitleAiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = RunAsync(check); var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static async Task RunAsync(Action<bool, string> check)
    {
        var scenes = new Scenes(true);
        var timeline = new Timeline("Subtitle check", 1920, 1080, 30, 48000, new TimelineVerticalLine()); scenes.AddScene(timeline);
        var character = new Character { Name = "案内", Font = "Yu Gothic" };
        var voice = new VoiceItem { Character = character, Serif = "これは長いセリフです。字幕を順番に切り替えます。", Frame = 10, Length = 120, Layer = 1 };
        typeof(VoiceItem).GetProperty("IsVoiceChanged")!.SetValue(voice, false); voice.IsHatsuonChanged = false;
        var image = new ImageItem { Frame = 0, Length = 300, Layer = 0 };
        timeline.Items = ImmutableList.Create<IItem>(voice, image); timeline.RefreshTimelineLengthAndMaxLayer(); scenes.UndoRedoManager.Subscribe(timeline);
        var info = new TimelineToolInfo(timeline, scenes, scenes.UndoRedoManager, scenes.AsyncAwaitStatus);
        var session = new TimelineSession(info); string before = Json.GetJsonText(timeline.Items);
        var disabled = session.PrepareSubtitles([0], new(), 10, 0, 350);
        check(disabled.Rows.Count == 0 && disabled.Edit.Plan.Changes.Count == 0 && Json.GetJsonText(timeline.Items) == before,
            "all three subtitle switches OFF produce no native edits");
        var prepared = session.PrepareSubtitles([0], new(Split: true, Wrap: true, MaxCharacters: 8), 10, 0, 350);
        check(prepared.Edit.Plan.IsValid && prepared.Rows.Count > 1 && Json.GetJsonText(timeline.Items) == before, "subtitle preview creates detached pages without editing source voice");
        check(prepared.Edit.Additions.All(x => x is TextItem) && prepared.Edit.Additions.Sum(x => x.Length) == voice.Length,
            "native subtitle pages cover source voice duration");
        string serif = voice.Serif; string file = voice.FilePath; var visibility = voice.JimakuVisibility;
        string root = Path.Combine(AppContext.BaseDirectory, "subtitle-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await session.ApplyAsync(prepared.Edit, true, root);
            check(voice.Serif == serif && voice.FilePath == file && voice.Frame == 10 && voice.Length == 120 && voice.Layer == 1,
                "subtitle application preserves source dialogue audio timing and layer");
            check(voice.JimakuVisibility == JimakuVisibility.Hidden && timeline.Items.OfType<TextItem>().Count() == prepared.Rows.Count,
                "subtitle application hides native voice captions and inserts standalone measured pages");
            check(File.Exists(session.LastBackupPath) && Json.LoadFromText<Project>(File.ReadAllText(session.LastBackupPath!))!.Timelines[0].Items.Count == 2,
                "subtitle backup retains complete original native timeline");
            await scenes.UndoRedoManager.UndoAsync();
            check(Json.GetJsonText(timeline.Items) == before, "one native Undo restores both voice visibility and inserted subtitles");
            await scenes.UndoRedoManager.RedoAsync();
            await session.RestoreLastAsync(root);
            check(Json.GetJsonText(timeline.Items) == before && voice.JimakuVisibility == visibility, "subtitle restore returns original voice captions and removes all pages");
        }
        finally { Directory.Delete(root, true); }
        voice.IsLocked = true; bool refused = false;
        try { session.PrepareSubtitles([0], new(Split: true), 10, 0, 350); } catch (InvalidOperationException) { refused = true; }
        check(refused, "locked voice cannot be converted to standalone captions"); voice.IsLocked = false;
        var collision = session.PrepareSubtitles([0], new(Split: true), 0, 0, 350);
        check(!collision.Edit.Plan.IsValid, "subtitle layer collision prevents apply");
        var vm = new EditAssistViewModel(); vm.SetTimelineToolInfo(info);
        var subtitles = new SubtitleAssistView { DataContext = vm };
        foreach (string name in new[] { "SplitBox", "ResizeBox", "WrapBox", "PreviewGrid", "ConfirmBox" }) check(subtitles.FindName(name) is FrameworkElement, "subtitle WPF control exists: " + name);
        check(new[] { "SplitBox", "ResizeBox", "WrapBox" }.All(n => ((CheckBox)subtitles.FindName(n)).IsChecked == false), "subtitle switches are independent and default OFF");
        var ai = new AiAssistView { DataContext = vm };
        foreach (string name in new[] { "InstructionBox", "CharactersBox", "DraftBox", "PromptBox", "ResponseBox", "LinesGrid", "MaterialsGrid", "CancelButton" }) check(ai.FindName(name) is FrameworkElement, "AI WPF control exists: " + name);
        check(((CheckBox)ai.FindName("SendConsentBox")).IsChecked == false, "no AI send without explicit send control");
        var workbench = new TimelineWorkbench { DataContext = vm };
        workbench.ImportAiScript("キャラ,セリフ\n案内,完成です");
        check(((TextBox)workbench.FindName("ScriptBox")).Text.Contains("完成です") && timeline.Items.Count == 2, "AI transfer fills script editor without inserting voice items");
        check(SubtitleMeasure.Measure("日本語字幕", 48, "Yu Gothic", false, false).Width > 0, "Japanese WPF font measurement returns finite text extent");
    }
}
