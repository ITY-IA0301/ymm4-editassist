using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EditAssist.Core;
using EditAssist.Plugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;
using GroupItem = YukkuriMovieMaker.Project.Items.GroupItem;

internal static class TimelineChecks
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "EditAssistHostChecks-" + Guid.NewGuid().ToString("N"));
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
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static async Task RunAsync(string root, Action<bool, string> check)
    {
        async Task Refused(Func<Task> action, string label)
        {
            try { await action(); } catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException)
            { check(true, label); return; }
            throw new InvalidOperationException("Expected refusal: " + label);
        }
        var scenes = new Scenes(true);
        var timeline = new Timeline("Test only", 1920, 1080, 30, 48000, new TimelineVerticalLine());
        scenes.AddScene(timeline);
        var character = new Character { Name = "EditAssist test character", Layer = 3 };
        var voice = new VoiceItem { Character = character, Serif = "移動テスト", Frame = 10, Length = 30, Layer = 3,
            ContentOffset = TimeSpan.FromSeconds(.25) };
        var second = new VoiceItem { Character = character, Serif = "二つ目", Frame = 50, Length = 30, Layer = 5 };
        // Minimal built-in registry in this isolated test process, not the user's running YMM4.
        var hostAssembly = typeof(Timeline).Assembly;
        var commonsAssembly = typeof(IPlugin).Assembly;
        var fixturePlugins = new IPlugin[] {
            new YukkuriMovieMaker.Shape.QuadrilateralShapePlugin(),
            (IPlugin)Activator.CreateInstance(hostAssembly.GetType("YukkuriMovieMaker.Plugin.Brush.SolidColorBrushPlugin", true)!, true)!,
            (IPlugin)Activator.CreateInstance(hostAssembly.GetType("YukkuriMovieMaker.Transition.FadeTransitionPlugin", true)!, true)! };
        typeof(PluginLoader).GetProperty("Plugins")!.SetValue(null, fixturePlugins);
        var image = new ImageItem { Frame = 12, Length = 20, Layer = 4 };
        typeof(VoiceItem).GetProperty("IsVoiceChanged")!.SetValue(voice, false); voice.IsHatsuonChanged = false;
        typeof(VoiceItem).GetProperty("IsVoiceChanged")!.SetValue(second, false); second.IsHatsuonChanged = false;
        timeline.Items = ImmutableList.Create<IItem>(voice, second, image);
        timeline.RefreshTimelineLengthAndMaxLayer();
        // The real TimelineViewModel subscribes its model; no view model is present in this fixture.
        scenes.UndoRedoManager.Subscribe(timeline);
        var info = new TimelineToolInfo(timeline, scenes, scenes.UndoRedoManager, scenes.AsyncAwaitStatus);
        var session = new TimelineSession(info);
        var vm = new EditAssistViewModel(); vm.SetTimelineToolInfo(info);
        check(vm.TimelineInfo == info && vm is ITimelineToolViewModel, "view model receives the actual host timeline contract");
        var workbench = new TimelineWorkbench { DataContext = vm };
        workbench.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        check(((TextBlock)workbench.FindName("ConnectionText")).Text.Contains("Test only"), "workbench binds a synthetic real-host timeline");
        workbench.Measure(new Size(1040, 720)); workbench.Arrange(new Rect(0, 0, 1040, 720));
        check(((ListView)workbench.FindName("TargetList")).Items.Count == 2, "workbench defaults to voices only");
        for (int i = 0; i < 4; i++)
        {
            ((TabControl)workbench.FindName("WorkTabs")).SelectedIndex = i;
            workbench.Measure(new Size(900, 600)); workbench.Arrange(new Rect(0, 0, 900, 600));
            check(workbench.ActualWidth == 900, "editing tab layout completes: " + i);
        }
        workbench.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        session.Select([0, 1]);
        check(timeline.SelectedItems.SequenceEqual(new IItem[] { voice, second }), "selection does not include interleaved images");
        var before = Json.GetJsonText(timeline);
        var plan = session.Prepare(all => TimelineEditing.MoveLayers(all, [0, 1], 2, true, Timeline.LayerLimit));
        check(plan.Plan.IsValid, "real voice items produce a valid relative layer plan");
        await session.ApplyAsync(plan, false, root);
        check(voice.Layer == 5 && second.Layer == 7 && image.Layer == 4, "only selected voice layers change on the actual host types");
        check(voice.Frame == 10 && voice.Length == 30 && voice.Serif == "移動テスト" && voice.ContentOffset == TimeSpan.FromSeconds(.25), "layer moves preserve timing, speech and split offsets");
        check(File.Exists(session.LastBackupPath), "a YMMP backup is written before mutation");
        var backup = Json.LoadFromText<Project>(File.ReadAllText(session.LastBackupPath!)) ?? throw new InvalidOperationException("Empty backup");
        check(backup.Timelines.Count == 1 && backup.Timelines[0].Items[0].Layer == 3 &&
            ((VoiceItem)backup.Timelines[0].Items[0]).Serif == "移動テスト", "full-project backup deserializes using YMM4 and contains the before state");
        check(backup.Characters.Any(c => c.Name == character.Name), "backup includes the actual voice character rather than only global defaults");
        check(scenes.UndoRedoManager.IsUndoable, "batch mutation creates host undo history");
        await scenes.UndoRedoManager.UndoAsync();
        check(voice.Layer == 3 && second.Layer == 5, "one host undo reverses the complete layer batch");
        await scenes.UndoRedoManager.RedoAsync();
        check(voice.Layer == 5 && second.Layer == 7, "host redo reapplies the complete batch");
        await session.RestoreLastAsync(root);
        check(voice.Layer == 3 && second.Layer == 5 && voice.ContentOffset == TimeSpan.FromSeconds(.25), "tool restoration reverses the last batch without touching offsets");
        var stale = session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false));
        voice.Serif = "external edit";
        await Refused(() => session.ApplyAsync(stale, true, root), "external edits invalidate an already previewed plan");
        check(voice.Layer == 3, "stale plans never mutate the project");
        typeof(VoiceItem).GetProperty("IsVoiceChanged")!.SetValue(voice, false); voice.IsHatsuonChanged = false;
        var forbiddenBackup = Path.Combine(root, "not-a-directory"); File.WriteAllText(forbiddenBackup, "keep");
        var valid = session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false));
        await Refused(() => session.ApplyAsync(valid, true, forbiddenBackup), "backup failure prevents mutation");
        check(voice.Layer == 3, "failed backups preserve original layers");
        var scene2 = new Timeline("Second scene", 1280, 720, 60, 48000, new TimelineVerticalLine()); scenes.AddScene(scene2);
        var multi = session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false));
        await session.ApplyAsync(multi, true, root);
        var multiBackup = Json.LoadFromText<Project>(File.ReadAllText(session.LastBackupPath!))!;
        check(multiBackup.Timelines.Count == 2 && multiBackup.Timelines[1].VideoInfo.FPS == 60, "backups retain all scenes and each frame rate");
        voice.Frame++;
        await Refused(() => session.RestoreLastAsync(root), "restoration refuses to erase subsequent edits");
        voice.Frame--;
        await session.RestoreLastAsync(root);
        var changedCharacter = session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false));
        character.Font = "different font";
        await Refused(() => session.ApplyAsync(changedCharacter, true, root), "character setting changes invalidate previewed plans");
        voice.IsLocked = true;
        check(!session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false)).Plan.IsValid, "real item locks block mutation");
        voice.IsLocked = false; voice.Group = 12;
        check(!session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false)).Plan.IsValid, "real numeric groups block isolated mutation");
        voice.Group = 0;
        var group = new GroupItem { Frame = 0, Length = 100, Layer = 2, GroupRange = 3 };
        timeline.Items = timeline.Items.Add(group);
        check(!session.Prepare(all => TimelineEditing.MoveLayers(all, [0], 8, false)).Plan.IsValid, "group control ranges are protected");
        timeline.Items = timeline.Items.Remove(group);
        character.Italic = true; character.Rotation.SetFirstValue(12);
        var preset = new EditPreset(Name: "Host test", FontSize: 42, FontColor: "#FF112233", Volume: 75, X: 10, Y: 20);
        var oldFontSize = voice.FontSize.GetFirstValue(); var oldVolume = voice.Volume.GetFirstValue();
        var presetPlan = session.PreparePreset([0], preset, false);
        await Refused(() => session.ApplyAsync(presetPlan, false, root), "unacknowledged subtitle-mode warnings cannot be applied");
        await session.ApplyAsync(presetPlan, true, root);
        check(voice.FontSize.GetFirstValue() == 42 && voice.Volume.GetFirstValue() == 75 &&
            voice.JimakuX.GetFirstValue() == 10 && voice.JimakuVisibility == JimakuVisibility.Custom, "subtitle and volume preset reaches actual host properties");
        check(voice.Italic && voice.JimakuRotation.GetFirstValue() == 12 && voice.Font == character.Font,
            "switching inherited subtitles preserves unspecified style, rotation and font");
        await session.RestoreLastAsync(root);
        check(voice.FontSize.GetFirstValue() == oldFontSize && voice.Volume.GetFirstValue() == oldVolume, "preset restoration reverses animation values");
        check(!string.IsNullOrWhiteSpace(session.CapturePreset(second).Name), "captured presets always have a valid name");
        second.JimakuVisibility = JimakuVisibility.Custom;
        second.FontSize.AnimationType = (AnimationType)1;
        check(!session.PreparePreset([1], preset, false).Plan.IsValid, "animated preset fields require explicit overwrite permission");
        check(session.PreparePreset([1], preset, true).Plan.IsValid, "explicit fixed-value overwrite can be previewed");
        second.FontSize.AnimationType = AnimationType.なし;
        var script = ScriptImport.Parse("Alias,First\nAlias,Second");
        var mapping = new Dictionary<string, Character> { ["Alias"] = character };
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Refused(() => session.PrepareScriptAsync(script, mapping, 100, 8, 3, cancellation.Token), "cancelled script preparation does not generate or insert items");
        }
        check(timeline.Items.Count == 3, "cancelled scripts leave the timeline unchanged");
        await Refused(() => session.PrepareScriptAsync(script, new Dictionary<string, Character>(), 100, 8, 3, CancellationToken.None), "unassigned script characters are refused before generation");
        using (var middleCancel = new CancellationTokenSource())
        {
            await Refused(() => session.PrepareScriptAsync(script, mapping, 100, 8, 3, middleCancel.Token,
                factory: (c, s) => { middleCancel.Cancel(); return Task.FromResult(new VoiceItem(c)); }),
                "cancellation after engine completion does not insert a partial script");
        }
        check(timeline.Items.Count == 3, "mid-generation cancellation leaves all existing items intact");
        var additions = await session.PrepareScriptAsync(script, mapping, 100, 8, 3, CancellationToken.None,
            factory: (c, s) =>
            {
                var fixture = new VoiceItem { Character = c, Serif = s, AdditionalTime = 0 };
                // Test fixture only: avoid calling any configured speech engine or creating audio caches.
                typeof(VoiceItem).GetProperty("VoiceLength")!.SetValue(fixture, TimeSpan.FromSeconds(1));
                return Task.FromResult(fixture);
            });
        check(timeline.Items.Count == 3 && additions.Additions.Count == 2 && additions.Additions[1].Frame == 133,
            "script generation stages voice items and preview timing before insertion");
        await session.ApplyAsync(additions, true, root);
        check(timeline.Items.Count == 5 && ((VoiceItem)timeline.Items[3]).Serif == "First", "script application inserts the whole staged batch");
        await session.RestoreLastAsync(root);
        check(timeline.Items.Count == 3, "script insertion can be restored as one batch");
        var copyPath = Path.Combine(root, "separate.ymmp"); session.SaveProjectCopy(copyPath);
        await Refused(() => { session.SaveProjectCopy(copyPath); return Task.CompletedTask; }, "project export never overwrites an existing file");
    }
}
