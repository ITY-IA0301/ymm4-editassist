using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using EditAssist.Plugin;

internal static class Program
{
    // No visible window, real catalog, installation, or YMM4 project is opened.
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the YMM4 folder and optional read-only sample project.");
            var hostDirectory = Path.GetFullPath(args[0]);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (string.IsNullOrEmpty(name.Name) || name.Name != Path.GetFileName(name.Name))
                    return null;
                var path = Path.Combine(hostDirectory, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            return Run(hostDirectory, args.Length > 1 ? args[1] : null);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string hostDirectory, string? sampleProject)
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int passed = 0;
            void Check(bool success, string message)
            {
                if (!success) throw new InvalidOperationException(message);
                Console.WriteLine("PASS " + message);
                passed++;
            }
            var assembly = typeof(EditAssistView).Assembly;
            var toolType = assembly.GetType("EditAssist.Plugin.EditAssistToolPlugin", throwOnError: true)!;
            var tool = Activator.CreateInstance(toolType)!;
            var contract = toolType.GetInterfaces().Single(i => i.FullName == "YukkuriMovieMaker.Plugin.IToolPlugin");
            Check(contract.Assembly.Location.Equals(Path.Combine(hostDirectory, "YukkuriMovieMaker.Plugin.dll"),
                StringComparison.OrdinalIgnoreCase), "tool implements the actual YMM4 interface");
            Check(toolType.GetProperty("ViewType")!.GetValue(tool) as Type == typeof(EditAssistView),
                "tool factory returns the real WPF view type");
            Check(toolType.GetProperty("ViewModelType")!.GetValue(tool) as Type == typeof(EditAssistViewModel),
                "tool factory returns the view model type");
            Check(toolType.GetProperty("AllowMultipleInstances")!.GetValue(tool) is false,
                "tool refuses duplicate instances");
            var view = new EditAssistView { DataContext = new EditAssistViewModel() };
            Check(view.Content is Grid, "compiled XAML creates the control tree");
            Check(view.FindName("MediaPanel") is MediaReferencesView, "EditAssist hosts the video reference tab");
            Check(!assembly.GetReferencedAssemblies().Any(x => x.Name is "YMM4.PreviewLite" or "0Harmony"),
                "EditAssist media repair is independent of PreviewLite and preview hooks");
            foreach (var name in new[] { "QueryBox", "SceneBox", "KindBox", "SearchFiltersPanel", "ItemsList",
                "TitleBox", "TagsBox", "NotesBox", "SourceBox", "LicenseBox", "FavoriteBox", "VolumeSlider",
                "AssetActionsPanel", "DetailsPanel", "CancelScanButton", "MeasurementFields", "MeasureButton",
                "CancelMeasureButton", "EnvironmentBox", "CaseBox", "ConditionBox", "DecoderBox", "ProxyBox",
                "SymptomBox", "MeasurementStatus", "ReportBox" })
                Check(view.FindName(name) is FrameworkElement, "XAML field exists: " + name);
            view.Measure(new Size(1040, 720));
            view.Arrange(new Rect(0, 0, 1040, 720));
            Check(view.ActualWidth == 1040 && view.ActualHeight == 720, "WPF layout completes at the default size");
            Check(((Slider)view.FindName("VolumeSlider")).Value == 0.35,
                "initial volume event completes during XAML construction");
            var context = (EditAssistViewModel)view.DataContext;
            bool notified = false;
            context.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(context.Status);
            context.Status = "test";
            Check(notified, "view model sends property notifications");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            TimelineChecks.Run(Check);
            MediaChecks.Run(Check);
            WorkflowChecks.Run(Check);
            if (sampleProject is not null) Task.Run(() => ReadOnlyProjectProbe.RunAsync(sampleProject)).GetAwaiter().GetResult();
            application.Shutdown();
            Console.WriteLine($"All {passed} Windows smoke checks passed. Live YMM4 playback/drop remain untested.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
