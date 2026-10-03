using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EditAssist.Core;
using EditAssist.Plugin;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;

internal static class WorkflowChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = RunAsync(check);
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "workflow-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Refused(Action action, string label)
        {
            try { action(); } catch (InvalidOperationException) { check(true, label); return; }
            throw new Exception("Expected refusal: " + label);
        }
        var scenes = new Scenes(true);
        var timeline = new Timeline("Workflow check", 1920, 1080, 30, 48000, new TimelineVerticalLine());
        scenes.AddScene(timeline);
        var a = new Character { Name = "案内" }; var b = new Character { Name = "相方" };
        var first = new VoiceItem { Character = a, Serif = "かまどを作る", Frame = 0, Length = 20, Layer = 1, ContentOffset = TimeSpan.FromSeconds(.5) };
        var second = new VoiceItem { Character = b, Serif = "相方の返事", Frame = 20, Length = 20, Layer = 2 };
        var third = new VoiceItem { Character = a, Serif = "次の手順", Frame = 40, Length = 20, Layer = 3 };
        foreach (var voice in new[] { first, second, third })
        { typeof(VoiceItem).GetProperty("IsVoiceChanged")!.SetValue(voice, false); voice.IsHatsuonChanged = false; }
        var image = new ImageItem { Frame = 5, Length = 5, Layer = 9 };
        var video = new VideoItem { FilePath = Path.Combine(root, "missing-かまど.mp4"), Frame = 80, Length = 20, Layer = 9 };
        var text = new TextItem { Text = "かまどの文字", Frame = 100, Length = 20, Layer = 10 };
        timeline.Items = ImmutableList.Create<IItem>(first, second, image, third, video, text);
        timeline.RefreshTimelineLengthAndMaxLayer(); scenes.UndoRedoManager.Subscribe(timeline);
        var info = new TimelineToolInfo(timeline, scenes, scenes.UndoRedoManager, scenes.AsyncAwaitStatus);
        var session = new TimelineSession(info);
        var snapshot = session.Snapshot();
        check(TimelineEditing.Filter(snapshot, new(Query: "かまど")).Single().Key == 0, "native speech filter reads Serif not only display label");
        check(TimelineEditing.Filter(snapshot, new(Kind: "Video", Query: "かまど")).Single().Key == 4, "native video filename searchable without opening video");
        check(TimelineEditing.Filter(snapshot, new(Kind: "Text", Query: "かまど")).Single().Key == 5, "native text content searchable");
        var vm = new EditAssistViewModel(); vm.SetTimelineToolInfo(info);
        var panel = new TimelineWorkbench { DataContext = vm };
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        void Invoke(string name) => typeof(TimelineWorkbench).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [panel, new RoutedEventArgs()]);
        foreach (string name in new[] { "ContentQueryBox", "CharacterLayersGrid", "CheckIssuesGrid", "CheckResultsPanel" })
            check(panel.FindName(name) is FrameworkElement, "workflow WPF control: " + name);
        var query = (TextBox)panel.FindName("ContentQueryBox");
        query.Text = "かまど"; Invoke("OnRefresh");
        check(((ListView)panel.FindName("TargetList")).Items.Count == 1, "UI keyword filters voice list without interleaved media");
        Invoke("OnSelect"); check(timeline.SelectedItems.SequenceEqual(new IItem[] { first }), "keyword selection selects exact native voice");
        query.Text = ""; Invoke("OnRefresh"); Invoke("OnBuildCharacterLayers");
        var grid = (DataGrid)panel.FindName("CharacterLayersGrid");
        var assignments = grid.ItemsSource.Cast<CharacterLayerRow>().ToArray();
        check(assignments.Length == 2 && assignments.Single(x => x.Character == a.Name).Clips == 2, "UI builds character assignment counts from scoped voices");
        check(assignments.All(x => x.DestinationLayer == ""), "no character destination assumed before user input");
        assignments.Single(x => x.Character == a.Name).DestinationLayer = "5";
        assignments.Single(x => x.Character == b.Name).DestinationLayer = "7";
        Invoke("OnPrepareCharacterLayers");
        var pendingField = typeof(TimelineWorkbench).GetField("pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var plan = (PreparedEdit?)pendingField.GetValue(panel);
        check(plan is not null && plan.Plan.IsValid && plan.Plan.Changes.Count == 3, "UI previews combined multi-speaker layer plan");
        check(first.Layer == 1 && second.Layer == 2 && third.Layer == 3, "character preview never mutates original layers");
        assignments[0].DestinationLayer = "6";
        check(pendingField.GetValue(panel) is null, "editing character assignment invalidates preview immediately");
        assignments.Single(x => x.Character == a.Name).DestinationLayer = "5";
        assignments.Single(x => x.Character == b.Name).DestinationLayer = "bad"; Invoke("OnPrepareCharacterLayers");
        check(pendingField.GetValue(panel) is null && ((TextBlock)panel.FindName("StatusText")).Text.Contains("整数"), "invalid UI character destination rejected");
        assignments.Single(x => x.Character == b.Name).DestinationLayer = "7";
        var routed = session.Prepare(all => TimelineEditing.MoveVoicesByCharacter(all, [0, 1, 3],
            new Dictionary<string, int> { [a.Name] = 5, [b.Name] = 7 }, Timeline.LayerLimit));
        string beforeItems = Json.GetJsonText(timeline.Items);
        await session.ApplyAsync(routed, false, root);
        check(first.Layer == 5 && third.Layer == 5 && second.Layer == 7, "actual host applies per-character layer assignment as one batch");
        check(image.Layer == 9 && video.Layer == 9 && text.Layer == 10 && first.ContentOffset == TimeSpan.FromSeconds(.5), "character routing preserves unrelated media and split offset");
        check(Json.LoadFromText<Project>(File.ReadAllText(session.LastBackupPath!))!.Timelines[0].Items[0].Layer == 1, "character routing native backup contains full before state");
        await scenes.UndoRedoManager.UndoAsync();
        check(Json.GetJsonText(timeline.Items) == beforeItems, "single native Undo restores complete multi-speaker assignment");
        await scenes.UndoRedoManager.RedoAsync(); check(first.Layer == 5 && second.Layer == 7, "single native Redo reapplies character assignment");
        await session.RestoreLastAsync(root);
        var nav = session.CaptureCheckState();
        string navBefore = Json.GetJsonText(timeline.Items);
        session.NavigateTo(nav, 3);
        check(timeline.CurrentFrame == 40 && timeline.SelectedItems.SequenceEqual(new IItem[] { third }), "navigation selects one checked item and seeks its start");
        session.NavigateTo(nav, 0);
        check(timeline.CurrentFrame == 0 && timeline.SelectedItems.SequenceEqual(new IItem[] { first }), "seeking and selection do not stale a valid check snapshot");
        check(Json.GetJsonText(timeline.Items) == navBefore, "check navigation does not alter any item property");
        using (scenes.AsyncAwaitStatus.Lock(false))
            Refused(() => session.NavigateTo(nav, 3), "busy YMM cannot be navigated by check results");
        Refused(() => session.NavigateTo(nav, 99), "invalid navigation key refused");
        first.Frame = 1;
        int previousFrame = timeline.CurrentFrame;
        Refused(() => session.NavigateTo(nav, 3), "edited project invalidates old check navigation");
        check(timeline.CurrentFrame == previousFrame, "stale check never seeks a potentially wrong item");
        first.Frame = 0;
        Invoke("OnCheck");
        var results = (DataGrid)panel.FindName("CheckIssuesGrid");
        for (int n = 0; n < 100 && results.Items.Count == 0; n++) await Task.Delay(20);
        check(results.Items.Cast<EditingCheckRow>().Any(x => x.Key == 4 && x.Message.Contains("見つからない")), "missing media check publishes selectable structured rows");
        results.SelectedItem = results.Items.Cast<EditingCheckRow>().First(x => x.Key == 4);
        Invoke("OnNavigateIssue");
        check(timeline.CurrentFrame == 80 && timeline.SelectedItems.SequenceEqual(new IItem[] { video }), "UI check result navigates to exact missing video");
        first.Layer = 11;
        Invoke("OnNavigateIssue");
        check(results.Items.Count == 0 && ((TextBlock)panel.FindName("StatusText")).Text.Contains("変わりました"), "UI clears stale navigation results after external edits");
        first.Layer = 1;
        Invoke("OnCheck");
        for (int n = 0; n < 100 && results.Items.Count == 0; n++) await Task.Delay(20);
        panel.Measure(new Size(1000, 700)); panel.Arrange(new Rect(0, 0, 1000, 700)); panel.UpdateLayout();
        var imageRender = new RenderTargetBitmap(1000, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        imageRender.Render(panel); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(imageRender));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "EditAssist-Workflow-UI.png"))) encoder.Save(output);
        check(panel.ActualHeight == 700 && ((FrameworkElement)panel.FindName("CheckResultsPanel")).Visibility == Visibility.Visible,
            "integrated workflow result table renders offscreen");
        check(results.Columns.All(x => x.ActualWidth >= 55), "check table columns remain readable on first layout");
        ((TabControl)panel.FindName("WorkTabs")).SelectedIndex = 0;
        ((Expander)panel.FindName("CharacterLayerExpander")).IsExpanded = true;
        panel.Measure(new Size(1000, 700)); panel.Arrange(new Rect(0, 0, 1000, 700)); panel.UpdateLayout();
        var mappingRender = new RenderTargetBitmap(1000, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        mappingRender.Render(panel); var mappingEncoder = new PngBitmapEncoder(); mappingEncoder.Frames.Add(BitmapFrame.Create(mappingRender));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "EditAssist-CharacterLayers-UI.png"))) mappingEncoder.Save(output);
        check(grid.Columns.All(x => x.ActualWidth >= 150), "character assignments remain readable in expanded workflow layout");
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        check(results.Items.Count == 0, "closing workbench clears check navigation state");
    }
}
