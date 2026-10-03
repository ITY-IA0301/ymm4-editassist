using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using EditAssist.Plugin;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;

internal static class MediaChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "media-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = RunAsync(root, check);
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        // Keep only generated fixture artifacts for diagnosis; never remove any user files.
    }

    private static async Task RunAsync(string root, Action<bool, string> check)
    {
        async Task Refused(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException)
            { check(true, label); return; }
            throw new Exception("Expected refusal: " + label);
        }
        string oldDirectory = Path.Combine(root, "old"), newDirectory = Path.Combine(root, "new"), sub = Path.Combine(newDirectory, "nested");
        Directory.CreateDirectory(oldDirectory); Directory.CreateDirectory(newDirectory); Directory.CreateDirectory(sub);
        string old = Path.Combine(oldDirectory, "分割 & #7.mp4"), destination = Path.Combine(newDirectory, "分割 & #7.mp4");
        File.WriteAllText(old, "fixture old"); File.WriteAllText(destination, "fixture new");
        string empty = Path.Combine(root, "empty.mp4"); File.WriteAllBytes(empty, []);
        string missing = Path.Combine(root, "missing.mp4");
        var refs = new[] { new VideoReference("A", old, [0]), new VideoReference("A", old, [5]), new VideoReference("B", missing, [0]), new VideoReference("B", empty, [0]) };
        var rows = MediaCatalog.Inspect(refs, null, CancellationToken.None);
        check(rows.Count == 3 && rows.Single(x => x.Path == old).Clips == 2, "split clips grouped by reference path");
        check(rows.Single(x => x.Path == missing).HasWarning, "missing video warns");
        check(rows.Single(x => x.Path == empty).HasWarning, "zero-byte video warns");
        check(!rows.Single(x => x.Path == old).HasWarning && rows.Single(x => x.Path == old).Detail.Contains("未確認"), "basic readability is not decode success");
        var key = MediaCatalog.ProjectKey([Guid.Parse("11111111-1111-1111-1111-111111111111")]);
        var baseline = MediaCatalog.Capture(key, rows);
        string history = Path.Combine(root, "history");
        MediaCatalog.SaveBaseline(history, baseline);
        check(MediaCatalog.LoadBaseline(history, key)!.Entries.Length == 3, "baseline persists and reloads");
        string migratedHistory = Path.Combine(root, "migrated-history");
        byte[] legacyHistoryBytes = File.ReadAllBytes(Path.Combine(history, key + ".json"));
        check(MediaCatalog.LoadWithLegacy(migratedHistory, key, history)!.Entries.Length == 3 && File.Exists(Path.Combine(migratedHistory, key + ".json")),
            "PreviewLite reference baseline imports into independent EditAssist storage");
        check(File.ReadAllBytes(Path.Combine(history, key + ".json")).SequenceEqual(legacyHistoryBytes),
            "migration leaves old history and backups untouched");
        MediaCatalog.SaveBaseline(migratedHistory, new(1, key, []));
        check(MediaCatalog.LoadWithLegacy(migratedHistory, key, history)!.Entries.Length == 0,
            "existing EditAssist baseline takes precedence over old PreviewLite history");
        check(MediaCatalog.LoadBaseline(history, MediaCatalog.ProjectKey([Guid.NewGuid()])) is null, "project baseline isolated by scene identity");
        var moved = MediaCatalog.Inspect([new("A", destination, [0])], baseline, CancellationToken.None).Single();
        check(moved.HasWarning && moved.Detail.Contains("参照先が異なり"), "reference path change warns");
        File.AppendAllText(old, " changed");
        check(MediaCatalog.Inspect(refs, baseline, CancellationToken.None).Single(x => x.Path == old).Detail.Contains("サイズまたは更新日時"), "same-name replacement/update warns");
        check(MediaCatalog.FindCandidates(Path.GetFileName(destination), newDirectory, true, CancellationToken.None).Single() == destination, "exact filename candidate lookup");
        File.WriteAllText(Path.Combine(sub, Path.GetFileName(destination)), "duplicate fixture");
        check(MediaCatalog.FindCandidates(Path.GetFileName(destination), newDirectory, true, CancellationToken.None).Length == 2, "ambiguous candidate set retained");
        check(MediaCatalog.FindCandidates(Path.GetFileName(destination), newDirectory, false, CancellationToken.None).Length == 1, "nonrecursive lookup resolves a chosen folder");
        await Refused(() => Task.Run(() => MediaCatalog.FindCandidates("*.mp4", newDirectory, true, CancellationToken.None)), "wildcards refused");
        await Refused(() => Task.Run(() => MediaCatalog.FindCandidates(destination, newDirectory, true, CancellationToken.None)), "full path is not accepted as filename");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await Refused(() => Task.Run(() => MediaCatalog.FindCandidates(Path.GetFileName(destination), newDirectory, true, cancel.Token)), "folder search cancellable");
        }
        check(MediaDecoderProbe.SamplePositions([0, 1, 2, 3, 4, 5, 6]).Length <= 4, "bounded sample positions");
        check(MediaDecoderProbe.SamplePositions([-1, double.NaN, double.PositiveInfinity]).SequenceEqual(new[] { 0d }), "invalid sample positions excluded");
        check(!MediaDecoderProbe.HasDecodedFrame("# header only\n"), "empty decode cannot pass");
        check(MediaDecoderProbe.HasDecodedFrame("0, 0, 0, 1, 128, 0123456789abcdef0123456789abcdef"), "actual decoded frame marker recognized");

        var host = Path.GetDirectoryName(typeof(IPlugin).Assembly.Location)!;
        var tools = MediaDecoderProbe.Locate(host);
        check(tools.Probe is not null && tools.Decoder is not null, "installed YMM ffmpeg tools located without download");
        string video = Path.Combine(root, "tiny 演算 & #7.mp4");
        var generated = await MediaDecoderProbe.RunAsync(tools.Decoder!, ["-nostdin", "-v", "error", "-f", "lavfi", "-i",
            "color=c=red:s=64x64:r=10", "-t", "1", "-c:v", "mpeg4", video], CancellationToken.None, TimeSpan.FromSeconds(20));
        check(generated.ExitCode == 0 && File.Exists(video), "generated one-second synthetic video only");
        var good = await MediaDecoderProbe.CheckAsync(video, [0, .2, .5], tools.Probe, tools.Decoder, CancellationToken.None);
        check(good.Success == true && good.DurationSeconds is > 0, "real ffprobe and sample decoding success with spaces/unicode/special chars");
        var outOfRange = await MediaDecoderProbe.CheckAsync(video, [1000], tools.Probe, tools.Decoder, CancellationToken.None);
        check(outOfRange.Success == false && outOfRange.Detail.Contains("開始位置"), "used split position beyond replacement duration warns");
        var bad = await MediaDecoderProbe.CheckAsync(old, [0], tools.Probe, tools.Decoder, CancellationToken.None);
        check(bad.Success == false, "existing but undecodable video warns");
        var notProbed = await MediaDecoderProbe.CheckAsync(video, [0], null, null, CancellationToken.None);
        check(notProbed.Success is null, "missing inspection tool remains unconfirmed");
        string audio = Path.Combine(root, "audio-only.wav");
        await MediaDecoderProbe.RunAsync(tools.Decoder!, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", audio], CancellationToken.None, TimeSpan.FromSeconds(20));
        check((await MediaDecoderProbe.CheckAsync(audio, [0], tools.Probe, tools.Decoder, CancellationToken.None)).Success == false, "audio-only source is not valid video");
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        try { await MediaDecoderProbe.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 5"], CancellationToken.None, TimeSpan.FromMilliseconds(50)); throw new Exception("Expected timeout"); }
        catch (TimeoutException) { check(true, "hung inspection killed on deadline"); }
        using (var cancel = new CancellationTokenSource())
        {
            cancel.CancelAfter(50);
            await Refused(() => MediaDecoderProbe.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 5"], cancel.Token, TimeSpan.FromSeconds(10)), "in-progress child inspection cancels");
        }

        // Isolated host plugin registry, no changes to the user's YMM instance.
        var hostAssembly = typeof(Timeline).Assembly;
        typeof(PluginLoader).GetProperty("Plugins")!.SetValue(null, new IPlugin[] {
            new YukkuriMovieMaker.Shape.QuadrilateralShapePlugin(),
            (IPlugin)Activator.CreateInstance(hostAssembly.GetType("YukkuriMovieMaker.Plugin.Brush.SolidColorBrushPlugin", true)!, true)!,
            (IPlugin)Activator.CreateInstance(hostAssembly.GetType("YukkuriMovieMaker.Transition.FadeTransitionPlugin", true)!, true)! });
        var scenes = new Scenes(true);
        var first = new Timeline("Main", 2560, 1440, 60, 48000, new TimelineVerticalLine());
        var second = new Timeline("Nested scene", 1280, 720, 30, 48000, new TimelineVerticalLine());
        scenes.AddScene(first); scenes.AddScene(second);
        var a = new VideoItem { FilePath = old, Frame = 10, Length = 20, Layer = 2, ContentOffset = TimeSpan.FromSeconds(.25) };
        a.Volume.SetFirstValue(37);
        a.X.SetFirstValue(42);
        var b = new VideoItem { FilePath = old, Frame = 50, Length = 25, Layer = 5, ContentOffset = TimeSpan.FromSeconds(.5) };
        var c = new VideoItem { FilePath = old, Frame = 100, Length = 30, Layer = 1, ContentOffset = TimeSpan.FromSeconds(.75) };
        var image = new ImageItem { FilePath = old, Frame = 0, Length = 10, Layer = 8 };
        var character = new Character { Name = "Fixture character" };
        var voice = new VoiceItem { Character = character, Serif = "保持", Frame = 0, Length = 30, Layer = 3 };
        first.Items = ImmutableList.Create<IItem>(a, image, voice, b);
        second.Items = ImmutableList.Create<IItem>(c);
        scenes.UndoRedoManager.Subscribe(first); scenes.UndoRedoManager.Subscribe(second);
        var info = new TimelineToolInfo(first, scenes, scenes.UndoRedoManager, scenes.AsyncAwaitStatus);
        var session = new MediaReferenceSession(info);
        var plan = session.Prepare(Path.GetFileName(destination), destination);
        check(plan.Targets.Count == 3 && plan.Targets.Select(x => x.Timeline.ID).Distinct().Count() == 2, "all same-name split video clips across all scenes planned");
        check(session.CaptureReferences().Count == 3, "voice/image paths excluded");
        var before = session.CaptureJson();
        string backups = Path.Combine(root, "backups");
        await session.ApplyAsync(plan, backups);
        check(new[] { a, b, c }.All(x => x.FilePath == destination), "native all-scene batch changes only video references");
        check(a.Frame == 10 && a.Length == 20 && a.Layer == 2 && a.ContentOffset == TimeSpan.FromSeconds(.25) && b.ContentOffset == TimeSpan.FromSeconds(.5), "cut timing/layers/split offsets unchanged");
        check(image.FilePath == old && voice.Serif == "保持" && first.VideoInfo.FPS == 60, "image/voice/project settings unchanged");
        check(a.Volume.GetFirstValue() == 37 && a.X.GetFirstValue() == 42, "nondefault audio and visual parameters unchanged");
        var backup = Json.LoadFromText<Project>(File.ReadAllText(session.LastBackupPath!))!;
        check(backup.Timelines.Count == 2 && backup.Characters.Any(x => x.Name == character.Name) && ((VideoItem)backup.Timelines[0].Items[0]).FilePath == old, "native pre-mutation YMMP includes all scenes and characters");
        check(JToken.DeepEquals(JObject.Parse(before), JObject.Parse(File.ReadAllText(session.LastBackupPath!))), "backup exact native before state");
        await scenes.UndoRedoManager.UndoAsync();
        check(new[] { a, b, c }.All(x => x.FilePath == old), "one host Undo restores full multi-scene batch");
        await scenes.UndoRedoManager.RedoAsync();
        check(new[] { a, b, c }.All(x => x.FilePath == destination), "one host Redo reapplies batch");
        await scenes.UndoRedoManager.UndoAsync();
        a.IsLocked = true;
        await Refused(() => Task.Run(() => session.Prepare(Path.GetFileName(destination), destination)), "locked split prevents partial relink");
        a.IsLocked = false;
        var stale = session.Prepare(Path.GetFileName(destination), destination);
        b.Frame++;
        await Refused(() => session.ApplyAsync(stale, backups), "any external edit invalidates checked plan");
        check(a.FilePath == old && c.FilePath == old, "stale plan changes nothing");
        b.Frame--;
        var valid = session.Prepare(Path.GetFileName(destination), destination);
        File.AppendAllText(destination, "changed");
        await Refused(() => session.ApplyAsync(valid, backups), "destination changed after confirmation refused");
        string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "keep");
        await Refused(() => session.ApplyAsync(session.Prepare(Path.GetFileName(destination), destination), blocked), "backup failure prevents mutation");
        check(a.FilePath == old && c.FilePath == old, "failed backup leaves all references intact");
        await Refused(() => Task.Run(() => session.Prepare("other-name.mp4", destination)), "different filename is not silently matched");

        var vm = new EditAssistViewModel(); vm.SetTimelineToolInfo(info);
        check(vm is ITimelineToolViewModel && vm.TimelineInfo == info, "tool binds actual host timeline contract");
        var panel = new MediaReferencesView { HistoryDirectory = Path.Combine(root, "ui-history"), LegacyHistoryDirectory = null, DataContext = vm };
        foreach (var field in new[] { "MediaList", "FilenameBox", "DirectoryBox", "ApplyRelinkButton", "WatchBox", "MediaDetailText" })
            check(panel.FindName(field) is FrameworkElement, "media UI field " + field);
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        for (int i = 0; i < 100 && ((DataGrid)panel.FindName("MediaList")).Items.Count == 0; i++) await Task.Delay(20);
        var grid = (DataGrid)panel.FindName("MediaList");
        check(grid.Items.Count == 1 && ((MediaRow)grid.Items[0]).Clips == 3, "media panel initially scans current project");
        check(File.Exists(Path.Combine(panel.HistoryDirectory, session.ProjectKey + ".json")), "initial comparison history saved to isolated fixture path");
        a.FilePath = destination;
        var inspect = typeof(MediaReferencesView).GetMethod("InspectAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)inspect.Invoke(panel, new object[] { false, false })!;
        check(grid.Items.Cast<MediaRow>().Any(x => x.Path == destination && x.HasWarning), "panel retains and displays reference-change warning");
        grid.SelectedIndex = 0;
        check(((TextBox)panel.FindName("FilenameBox")).Text == Path.GetFileName(old), "selecting warning fills matching filename");
        a.FilePath = old;
        await (Task)inspect.Invoke(panel, new object[] { true, false })!;
        check(grid.Items.Cast<MediaRow>().Any(x => x.Status == "映像読み込み警告"), "decode warning shown in UI");
        await (Task)inspect.Invoke(panel, new object[] { false, false })!;
        check(grid.Items.Cast<MediaRow>().Any(x => x.Status == "映像読み込み警告"), "lightweight watch keeps previous decode warning");
        File.AppendAllText(old, " stamp invalidated");
        await (Task)inspect.Invoke(panel, new object[] { false, false })!;
        check(grid.Items.Cast<MediaRow>().Single().Detail.Contains("未確認"), "changed file invalidates cached decoder result");
        ((TextBox)panel.FindName("DirectoryBox")).Text = newDirectory;
        ((CheckBox)panel.FindName("RecursiveBox")).IsChecked = false;
        ((Button)panel.FindName("PrepareRelinkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 100 && !((Button)panel.FindName("ApplyRelinkButton")).IsEnabled; i++) await Task.Delay(20);
        check(((Button)panel.FindName("ApplyRelinkButton")).IsEnabled && ((TextBlock)panel.FindName("RelinkPlanText")).Text.Contains("3クリップ"), "UI previews all-scene relink before mutation");
        ((TextBox)panel.FindName("FilenameBox")).Text = "changed.mp4";
        check(!((Button)panel.FindName("ApplyRelinkButton")).IsEnabled, "input edits invalidate confirmed plan");
        panel.Measure(new Size(900, 1000)); panel.Arrange(new Rect(0, 0, 900, 1000)); panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 1000, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "EditAssist-Media-UI.png"))) encoder.Save(output);
        check(panel.ActualWidth == 900, "media panel layout and rendering");
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }
}
