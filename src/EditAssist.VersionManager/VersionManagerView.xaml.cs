using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using EditAssist.Versioning;
using Microsoft.Win32;

namespace EditAssist.VersionManager;

public partial class VersionManagerView : UserControl
{
    private readonly string host = AppContext.BaseDirectory;
    private string Storage => Path.Combine(host, "user", "EditAssistVersions");
    private string Bundled => Path.Combine(Path.GetDirectoryName(typeof(VersionManagerView).Assembly.Location)!, "versions");
    private bool preparing;
    private bool scheduled;
    private VersionPackage? Selected => VersionsList.SelectedItem as VersionPackage;
    public VersionManagerView() { InitializeComponent(); }
    private void OnLoaded(object sender, RoutedEventArgs e) => Safe(Refresh);
    private void OnRefresh(object sender, RoutedEventArgs e) => Safe(Refresh);
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabled();
    private void UpdateEnabled() => ApplyButton.IsEnabled = !preparing && !scheduled && Selected is not null && Selected.RuntimeMajor == Environment.Version.Major;
    private void Refresh()
    {
        var target = VersionPackages.FindInstallation(host);
        var dll = Path.Combine(target, "YMM4.EditAssist.dll");
        // The executing assembly may be an older instance; show the version on disk and the loaded instance.
        var installed = File.Exists(dll) ? VersionPackages.ReadAssembly(File.ReadAllBytes(dll)).Version.ToString(3) : "未導入";
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "YMM4.EditAssist")?.GetName().Version?.ToString(3) ?? "未読み込み";
        CurrentText.Text = $"導入済み：{installed}　起動中：{loaded}　／　.NET {Environment.Version.Major}";
        var packages = new List<VersionPackage>(); var skipped = 0;
        foreach (var folder in new[] { Bundled, Path.Combine(Storage, "packages") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.ymme", SearchOption.AllDirectories))
                try { packages.Add(VersionPackages.Inspect(path)); } catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException) { skipped++; }
        }
        VersionsList.ItemsSource = packages.DistinctBy(x => x.Sha256).OrderByDescending(x => x.Version).ToArray();
        StatusText.Text = $"{packages.DistinctBy(x => x.Sha256).Count()}版を読み込みました。" + (skipped > 0 ? $" 読み込めないパッケージ：{skipped}件。" : "");
        var lastLog = Path.Combine(Storage, "last-result.txt");
        if (File.Exists(lastLog)) StatusText.Text += "\n前回の切替：" + File.ReadAllText(lastLog);
        UpdateEnabled();
    }
    private void OnImport(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var dialog = new OpenFileDialog { Filter = "EditAssist導入ファイル|*.ymme", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var folder = Path.Combine(Storage, "packages"); Directory.CreateDirectory(folder);
        foreach (var path in dialog.FileNames)
        {
            var package = VersionPackages.Inspect(path);
            var target = Path.Combine(folder, package.Sha256 + ".ymme");
            if (!File.Exists(target)) { File.Copy(path, target); if (VersionPackages.Inspect(target).Sha256 != package.Sha256) { File.Delete(target); throw new IOException("追加中にファイルが変わりました。"); } }
        }
        Refresh();
    });
    private void OnOpenHistory(object sender, RoutedEventArgs e) => Safe(() =>
    { Directory.CreateDirectory(Storage); Process.Start(new ProcessStartInfo(Storage) { UseShellExecute = true }); });
    private void OnApply(object sender, RoutedEventArgs e) => Safe(() =>
    {
        var package = Selected ?? throw new InvalidOperationException("版を選んでください。");
        if (package.RuntimeMajor != Environment.Version.Major) throw new InvalidOperationException("YMM4と.NETの世代が異なります。");
        if (MessageBox.Show($"EditAssist {package.Version.ToString(3)}への切替を予約します。\nプロジェクトを保存してYMM4を終了してください。\n終了後にバックアップを作り、導入ファイルを置き換えます。",
            "バージョン切替", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        preparing = true; UpdateEnabled();
        try
        {
            var target = VersionPackages.FindInstallation(host);
            var request = Path.Combine(Storage, "requests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(request);
            var stage = Path.Combine(request, "stage"); VersionPackages.Extract(package, stage);
            var script = Path.Combine(request, "Apply-Version.ps1");
            using (var resource = typeof(VersionManagerView).Assembly.GetManifestResourceStream("EditAssist.VersionManager.Apply-Version.ps1")
                ?? throw new IOException("切替処理が見つかりません。再ビルドしてください。"))
            using (var reader = new StreamReader(resource, Encoding.UTF8)) File.WriteAllText(script, reader.ReadToEnd(), new UTF8Encoding(true));
            var executable = Path.Combine(host, "YukkuriMovieMaker.exe");
            if (!File.Exists(executable)) throw new IOException("YMM4本体の場所を確認できません。");
            var plan = new { Target = target, Stage = stage, HostExe = executable, HostPid = Environment.ProcessId,
                HostStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"),
                Version = package.Version.ToString(3), Storage, Request = request,
                OriginalHash = File.Exists(Path.Combine(target, "YMM4.EditAssist.dll")) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "YMM4.EditAssist.dll")))) : "",
                Files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(path => new {
                    Name = Path.GetRelativePath(stage, path), Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray() };
            File.WriteAllText(Path.Combine(request, "plan.json"), JsonSerializer.Serialize(plan), new UTF8Encoding(false));
            // EncodedCommand keeps paths (quotes, $, non-ASCII) out of PowerShell source interpolation.
            var command = "& '" + script.Replace("'", "''") + "'";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
            using var worker = Process.Start(start) ?? throw new IOException("切替処理を起動できません。");
            scheduled = true;
            StatusText.Text = $"{package.Version.ToString(3)}への切替を予約しました。保存してYMM4を終了してください。別ウィンドウに完了と表示されたらYMM4を起動できます。";
        }
        finally { preparing = false; UpdateEnabled(); }
    });
    private void Safe(Action action)
    { try { action(); } catch (Exception ex) { StatusText.Text = "操作できませんでした：" + ex.Message; } }
}
