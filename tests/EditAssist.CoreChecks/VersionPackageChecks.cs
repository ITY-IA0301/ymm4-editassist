using System.IO.Compression;
using EditAssist.Versioning;

internal static class VersionPackageChecks
{
    public static void Run(string temp, Action<bool, string> check)
    {
        var root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "archive", "EditAssist")))
            root = Directory.GetParent(root)?.FullName ?? throw new IOException("Run checks in the source root.");
        var paths = Directory.GetFiles(Path.Combine(root, "archive", "EditAssist"), "*.ymme", SearchOption.AllDirectories);
        check(paths.Length >= 6, "historical packages available for selector");
        foreach (var path in paths)
        {
            var version = VersionPackages.Inspect(path);
            check(version.RuntimeMajor == 10 && version.Sha256.Length == 64, "real archived package metadata: " + version.Display);
        }
        var package = VersionPackages.Inspect(paths.First(x => x.Contains("0.5.1")));
        var stage = Path.Combine(temp, "version-stage"); VersionPackages.Extract(package, stage);
        check(File.Exists(Path.Combine(stage, "YMM4.EditAssist.dll")), "validated package extracts plugin");
        check(File.Exists(Path.Combine(stage, "EditAssist.Core.dll")), "validated package extracts core");
        var copy = Path.Combine(temp, "changed.ymme"); File.Copy(package.Path, copy);
        var cached = VersionPackages.Inspect(copy); File.AppendAllText(copy, "changed");
        Reject(() => VersionPackages.Extract(cached, Path.Combine(temp, "stale-stage")), "changed selection is rejected");
        check(!Directory.Exists(Path.Combine(temp, "stale-stage")), "stale package leaves no stage");
        var invalid = Path.Combine(temp, "invalid.ymme");
        void WithExtra(string extra)
        {
            File.Copy(package.Path, invalid, true);
            using var archive = ZipFile.Open(invalid, ZipArchiveMode.Update);
            using var writer = new StreamWriter(archive.CreateEntry(extra).Open()); writer.Write("x");
        }
        foreach (var extra in new[] { "../outside.txt", "/outside.txt", "docs/../outside.md", "docs\\outside.md", "evil.dll", "docs/run.exe", "YMM4.EditAssist.dll" })
        { WithExtra(extra); Reject(() => VersionPackages.Inspect(invalid), "reject unsafe archive: " + extra); }
        var host = Path.Combine(temp, "version-host");
        var plugin = Path.Combine(host, "user", "plugin", "EditAssist"); Directory.CreateDirectory(plugin);
        File.WriteAllBytes(Path.Combine(plugin, "YMM4.EditAssist.dll"), [1]);
        check(VersionPackages.FindInstallation(host) == plugin, "existing installation detected");
        File.WriteAllBytes(Path.Combine(plugin, "Other.dll"), [1]);
        Reject(() => VersionPackages.FindInstallation(host), "mixed plugin folder rejected"); File.Delete(Path.Combine(plugin, "Other.dll"));
        var duplicate = Path.Combine(host, "user", "plugin", "Duplicate"); Directory.CreateDirectory(duplicate);
        File.WriteAllBytes(Path.Combine(duplicate, "YMM4.EditAssist.dll"), [1]);
        Reject(() => VersionPackages.FindInstallation(host), "duplicate installation rejected");
        void Reject(Action action, string name)
        { try { action(); } catch (InvalidDataException) { check(true, name); return; } throw new Exception(name); }
    }
}
