using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows;
using HarmonyLib;
using PreviewLite;

internal class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        string host = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(host, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { return Run(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var on = new PreviewOptions(true, true, 20, 10).Validate();
        Check(!PreviewRuntime.Options.Enabled, "default OFF");
        Check(PreviewPolicy.AdditionalWait(10, true, on) == 40, "20 fps subtracts elapsed work and native wait");
        Check(PreviewPolicy.AdditionalWait(10, false, on) == 90, "idle cap subtracts elapsed work");
        Check(PreviewPolicy.AdditionalWait(100, true, on) == 0, "slow work no extra wait");
        Check(PreviewPolicy.AdditionalWait(0, true, on with { PlaybackFps = 15 }) == 67, "15 fps rounds up");
        Check(PreviewPolicy.AdditionalWait(0, true, on with { PlaybackFps = 30 }) == 34, "30 fps rounds up");
        Check(PreviewPolicy.AdditionalWait(0, false, on with { IdleFps = 5 }) == 200, "bounded idle wait");
        Check(PreviewPolicy.AdditionalWait(5, true, PreviewOptions.Off) == 0, "OFF adds no wait");
        Check(PreviewPolicy.AdditionalWait(5, true, on with { Enabled = false }) == 0, "comparison adds no wait");
        foreach (double invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
            Check(PreviewPolicy.AdditionalWait(invalid, true, on) == 0, "invalid timing passthrough");
        Check(PreviewPolicy.AdditionalWait(double.MaxValue, true, on) == 0, "large elapsed safe");
        Throws<ArgumentOutOfRangeException>(() => (on with { PlaybackFps = 0 }).Validate(), "invalid FPS rejected");
        Throws<ArgumentOutOfRangeException>(() => (on with { IdleFps = 100 }).Validate(), "invalid idle FPS rejected");

        var metrics = new PreviewMetrics();
        long tick = Stopwatch.Frequency / 20;
        for (int i = 1; i <= 100; i++) metrics.Add(i * tick, i * tick + 1, 8, 2, true, true);
        var snapshot = metrics.Snapshot();
        Check(snapshot.Samples == 60 && snapshot.Iterations == 100 && snapshot.DelayChanges == 100, "bounded rolling metrics");
        Check(Math.Abs(snapshot.CadenceFps - 20) < .01, "measured redraw cadence");
        Check(snapshot.UpdateMs == 8 && snapshot.DrawMs == 2, "separate preparation/draw costs");
        metrics.Clear();
        Check(metrics.Snapshot().Samples == 0 && metrics.Snapshot().Iterations == 0, "reset metrics");
        metrics.Add(tick, tick + 1, 8, 2, false, false);
        metrics.Add(tick * 2, tick * 2, -1, -1, false, false);
        metrics.Add(tick * 3, tick * 3 + 1, 8, 2, false, false);
        Check(Math.Abs(metrics.Snapshot().CadenceFps - 10) < .01, "idle polling excluded from redraw frequency");
        metrics.Add(tick * 4, tick * 4 + 1, 8, 2, true, false);
        Check(metrics.Snapshot().Samples == 1, "playing mode resets sampling");

        var fake = HostBinding.Create(Method(typeof(FakePlayer), nameof(FakePlayer.Loop)),
            Method(typeof(FakePlayer), nameof(FakePlayer.Edit)), Method(typeof(FakePlayer), nameof(FakePlayer.Draw)),
            typeof(FakePlayer).GetProperty(nameof(FakePlayer.IsPlaying))!.GetMethod!);
        PreviewRuntime.Install(fake);
        Check(PreviewRuntime.Ready, "synthetic Harmony install");
        var player = new FakePlayer();
        var source = new FakeSource();
        player.Loop(source);
        Check(player.Edits == 1 && player.Draws == 1 && source.Updates == 1, "OFF preserves original calls");
        Check(PreviewRuntime.Metrics.Snapshot().Iterations == 0, "OFF no metrics");
        PreviewRuntime.Configure(on);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) player.Loop(source);
        Check(watch.ElapsedMilliseconds >= 155, "enabled caps synthetic preview cadence");
        snapshot = PreviewRuntime.Metrics.Snapshot();
        Check(snapshot.Iterations == 4 && snapshot.DelayChanges == 3, "first frame immediate then throttled");
        Check(source.Updates == 5 && player.Draws == 5 && player.Edits == 5, "no original source/frame calls dropped");
        Check(source.Usage == 0 && source.Time == TimeSpan.FromSeconds(1), "source time/usage preserved");
        PreviewRuntime.Configure(on with { Enabled = false });
        player.Loop(source);
        Check(PreviewRuntime.Metrics.Snapshot().DelayChanges == 0, "measurement-only no throttle");
        source.Fail = true;
        Throws<InvalidOperationException>(() => player.Loop(source), "source exceptions propagate");
        source.Fail = false;
        player.FailDraw = true;
        Throws<InvalidOperationException>(() => player.Loop(source), "draw finalizer does not suppress exceptions");
        player.FailDraw = false;
        player.FailEdit = true;
        Throws<InvalidOperationException>(() => player.Loop(source), "edit exceptions propagate");
        player.FailEdit = false;
        player.IsPlaying = false;
        PreviewRuntime.Configure(on);
        player.Loop(source);
        Check(source.Usage == 1 && !PreviewRuntime.Metrics.Snapshot().Playing, "idle path preserved");
        player.Loop(source);
        Check(PreviewRuntime.Metrics.Snapshot().DelayChanges == 1, "idle cap applied");
        PreviewRuntime.Disable();
        Check(!PreviewRuntime.Options.Enabled && !PreviewRuntime.Options.Measure, "OFF passthrough");
        PreviewRuntime.Configure(on with { IdleFps = 5 });
        player.Loop(source);
        var pending = Task.Run(() => player.Loop(source));
        Thread.Sleep(40);
        PreviewRuntime.Disable();
        Check(pending.Wait(1000), "OFF interrupts a pending extra wait");
        Check(typeof(Task).GetMethods().Where(x => x.Name == "Delay").All(x => Harmony.GetPatchInfo(x)?.Owners.Contains(PreviewRuntime.Owner) != true), "Task.Delay never patched");
        Check(Harmony.GetPatchInfo(Method(typeof(FakeSource), nameof(FakeSource.Update)))?.Owners.Contains(PreviewRuntime.Owner) != true, "source update never patched");
        Check(Harmony.GetPatchInfo(fake.Loop)?.Owners.Contains(PreviewRuntime.Owner) != true, "preview loop/error recovery unmodified");
        PreviewRuntime.RemoveForChecks();
        Check(Harmony.GetPatchInfo(fake.Edit)?.Owners.Contains(PreviewRuntime.Owner) != true, "edit cleanup");
        Check(Harmony.GetPatchInfo(fake.Draw)?.Owners.Contains(PreviewRuntime.Owner) != true, "draw cleanup");
        var competitor = new Harmony("PreviewLite.Checks.competitor");
        competitor.Patch(fake.Edit, prefix: new HarmonyMethod(typeof(Program), nameof(NoOp)));
        Throws<InvalidOperationException>(() => PreviewRuntime.Install(fake), "conflicting patch rejected");
        competitor.Unpatch(fake.Edit, HarmonyPatchType.All, competitor.Id);

        // Actual host hooks compile in this isolated process; no GPU/video/project construction.
        var actual = HostBinding.Resolve(Assembly.Load("YukkuriMovieMaker"));
        Check(actual.Loop.DeclaringType!.FullName == "YukkuriMovieMaker.Player.TimelineVideoPlayer", "actual preview class");
        Check(!actual.Edit.IsPublic && !actual.Draw.IsPublic, "actual internal APIs guarded");
        PreviewRuntime.Install(actual);
        Check(PreviewRuntime.Ready && Harmony.GetPatchInfo(actual.Edit)!.Owners.Contains(PreviewRuntime.Owner), "actual Edit hook compilation");
        Check(Harmony.GetPatchInfo(actual.Draw)!.Owners.Contains(PreviewRuntime.Owner), "actual Draw hook compilation");
        Check(Harmony.GetPatchInfo(actual.Loop)?.Owners.Contains(PreviewRuntime.Owner) != true, "actual loop recovery unchanged");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var view = new PreviewLiteView();
        Check(view.FindName("MediaPanel") is null && view.Content is System.Windows.Controls.ScrollViewer,
            "PreviewLite has only the lightweight preview UI");
        Check(typeof(PreviewLiteView).Assembly.GetTypes().All(x => !x.Name.StartsWith("MediaReference") && x.Name is not "MediaCatalog" and not "MediaDecoderProbe"),
            "PreviewLite contains no relink, reference history or video inspection module");
        Check(!typeof(PreviewLiteView).Assembly.GetReferencedAssemblies().Any(x => x.Name is "YMM4.EditAssist" or "EditAssist.Core" or "Newtonsoft.Json"),
            "PreviewLite remains independent of EditAssist and project serialization");
        Check(typeof(PreviewLiteViewModel).GetInterfaces().All(x => x.Name != "ITimelineToolViewModel"),
            "lightweight preview does not request project editing access");
        Check(view.FindName("EnableButton") is System.Windows.Controls.Button, "WPF controls constructed");
        Check(((System.Windows.Controls.ComboBox)view.FindName("PlaybackBox")).SelectedIndex == 1, "default playback choice 20 fps");
        view.Measure(new Size(720, 700));
        view.Arrange(new Rect(0, 0, 720, 700));
        view.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(720, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(view);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "PreviewLite-UI.png"))) png.Save(stream);
        Check(view.ActualWidth == 720, "offscreen WPF layout and rendering");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PreviewRuntime.Configure(on);
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(!PreviewRuntime.Options.Enabled, "closing tool disables scheduling");
        PreviewRuntime.RemoveForChecks();
        app.Shutdown();
        Console.WriteLine($"PASS: {checks} checks. Actual host hook compilation: passed. Live GPU playback/export: not tested.");
        return 0;
    }

    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void NoOp() { }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception("FAIL: " + name);
    }
}

internal sealed class FakeSource
{
    internal int Updates, Usage;
    internal TimeSpan Time;
    internal bool Fail;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Update(TimeSpan time, int usage)
    {
        if (Fail) throw new InvalidOperationException("Synthetic original source failure");
        Updates++; Time = time; Usage = usage;
    }
}

internal sealed class FakePlayer
{
    public bool IsPlaying { get; set; } = true;
    internal int Edits, Draws;
    internal bool FailDraw, FailEdit;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Edit()
    {
        if (FailEdit) throw new InvalidOperationException("Synthetic original edit failure");
        Edits++;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Draw()
    {
        if (FailDraw) throw new InvalidOperationException("Synthetic original draw failure");
        Draws++;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Loop(FakeSource source)
    {
        Edit();
        if (IsPlaying) { source.Update(TimeSpan.FromSeconds(1), 0); Draw(); }
        else { source.Update(TimeSpan.FromSeconds(1), 1); Draw(); }
        Task.Delay(16).Wait();
    }
}
