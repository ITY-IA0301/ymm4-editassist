using EditAssist.Core;

// A dependency-free executable test suite. Run before building the plugin.
// Tests deliberately do not substitute a fake YMM4 API for compatibility checks.
int passed = 0;
var testRoot = Path.Combine(Path.GetTempPath(), "EditAssistChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
void MustThrow<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, message); return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
}
MaterialEntry Entry(string name, params string[] tags) => new()
{
    Title = name, FilePath = Path.Combine(testRoot, name + ".wav"), Tags = [.. tags]
};
try
{
    var success = Entry("お祝い", "成功");
    var failure = Entry("落胆", "失敗");
    var hit = Entry("ドン", "驚き");
    success.Favorite = true;
    MaterialEntry[] entries = [success, failure, hit];
    Check(MaterialSearch.Find(entries, new(Query: "ﾃｯﾃﾚｰ")).Single().Id == success.Id,
        "halfwidth spelling expands to the success scene");
    Check(MaterialSearch.Find(entries, new(Query: "どん")).Single().Id == hit.Id,
        "hiragana and katakana share normalized search");
    Check(MaterialSearch.Find(entries, new(Query: "失敗", Scene: "成功")).Count == 0,
        "query and scene are combined with AND");
    Check(MaterialSearch.Find(entries, new(FavoritesOnly: true)).Single().Id == success.Id,
        "favorites filter excludes other entries");
    hit.Kind = MaterialKind.Image;
    Check(MaterialSearch.Find(entries, new(Kind: MaterialKind.Image)).Single().Id == hit.Id,
        "media kind filter applies");
    failure.Notes = "失敗した時の小さな音";
    Check(MaterialSearch.Find(entries, new(Query: "小さな 音")).Single().Id == failure.Id,
        "multiple query terms match notes");
    Check(MaterialSearch.ParseTags("成功, 成功、完成;\n紹介").Count == 3,
        "duplicate tags and Japanese separators are handled");

    var files = Path.Combine(testRoot, "materials");
    Directory.CreateDirectory(files);
    File.WriteAllBytes(Path.Combine(files, "one.WAV"), new byte[] { 1, 2 });
    File.WriteAllBytes(Path.Combine(files, "two.png"), new byte[] { 1 });
    File.WriteAllText(Path.Combine(files, "ignored.txt"), "not media");
    var scan = MaterialScanner.Scan([files, files], CancellationToken.None);
    Check(scan.Entries.Count == 2, "scanner ignores unrelated extensions and duplicate roots");
    Check(!scan.LimitReached, "scan below limit is complete");
    Check(MaterialScanner.Scan([files], CancellationToken.None, limit: 1).LimitReached,
        "additional files set the truncation flag");
    Check(!MaterialScanner.Scan([Path.Combine(files, "one.WAV")], CancellationToken.None, limit: 1).LimitReached,
        "exactly reaching the limit is not a false truncation");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        MustThrow<OperationCanceledException>(() => MaterialScanner.Scan([files], cancelled.Token),
            "cancelled scans cannot produce a committed partial result");
    }
    var catalog = new MaterialCatalog();
    MaterialScanner.Merge(catalog, scan);
    var edited = catalog.Entries[0];
    edited.Title = "custom label";
    edited.Tags = ["edited"];
    edited.Favorite = true;
    var originalId = edited.Id;
    MaterialScanner.Merge(catalog, scan);
    Check(catalog.Entries.Count == 2 && catalog.Entries[0].Id == originalId &&
          catalog.Entries[0].Title == "custom label" && catalog.Entries[0].Favorite &&
          catalog.Entries[0].Tags.SequenceEqual(["edited"]),
        "rescans preserve IDs and human metadata");
    var copy = catalog.Clone();
    copy.Entries[0].Tags.Add("only in copy");
    Check(catalog.Entries[0].Tags.Count == 1, "editing a clone cannot mutate the live catalog");
    MustThrow<ArgumentOutOfRangeException>(() => MaterialScanner.Scan([files], CancellationToken.None, limit: 0),
        "nonpositive scan limits are refused");
    Check(MaterialScanner.Scan([Path.Combine(testRoot, "not-found")], CancellationToken.None).UnreadableItems == 1,
        "missing roots are counted without removing other material");
    var extensionOnlyFile = Path.Combine(testRoot, ".wav");
    File.WriteAllBytes(extensionOnlyFile, [1]);
    var edgeScan = MaterialScanner.Scan([extensionOnlyFile], CancellationToken.None);
    Check(edgeScan.Entries.Count == 1 && !string.IsNullOrWhiteSpace(edgeScan.Entries[0].Title),
        "extension-only filenames receive a valid display label");
    var missingCopy = catalog.Clone();
    missingCopy.Entries.ForEach(m => m.IsMissing = true);
    MaterialScanner.Merge(missingCopy, scan);
    Check(missingCopy.Entries.All(m => !m.IsMissing), "rescans clear missing status for rediscovered files");

    var catalogPath = Path.Combine(testRoot, "catalog.json");
    using (var store = new CatalogStore(catalogPath))
    {
        _ = store.Load();
        store.Save(catalog);
        var firstBytes = File.ReadAllBytes(catalogPath);
        var next = catalog.Clone();
        next.Entries[0].Notes = "日本語メモ";
        store.Save(next);
        Check(File.ReadAllBytes(catalogPath + ".bak").SequenceEqual(firstBytes),
            "atomic replacement preserves the previous catalog as a backup");
        var restored = store.Load();
        Check(restored.Entries[0].Notes == "日本語メモ" && restored.Entries[0].Favorite,
            "Unicode metadata and favorites survive persistence");
        using var competingWriter = new CatalogStore(catalogPath);
        MustThrow<IOException>(() => competingWriter.Load(), "concurrent writers are refused");
        var invalid = next.Clone();
        invalid.SchemaVersion = 2;
        MustThrow<InvalidDataException>(() => store.Save(invalid), "future schema is not overwritten");
        invalid = next.Clone();
        invalid.Entries.Add(invalid.Entries[0].Clone());
        MustThrow<InvalidDataException>(() => store.Save(invalid), "duplicate catalog entries are refused");
        invalid = next.Clone();
        invalid.Entries[0].SizeBytes = -1;
        MustThrow<InvalidDataException>(() => store.Save(invalid), "negative file sizes are refused");
        invalid = next.Clone();
        var aliased = invalid.Entries[0].Clone();
        aliased.Id = Guid.NewGuid();
        aliased.FilePath = Path.Combine(Path.GetDirectoryName(aliased.FilePath)!, ".", Path.GetFileName(aliased.FilePath));
        invalid.Entries.Add(aliased);
        MustThrow<InvalidDataException>(() => store.Save(invalid), "path aliases cannot create duplicate registrations");
        Check(File.ReadAllText(catalogPath).Contains("日本語", StringComparison.Ordinal) || store.Load().Entries[0].Notes == "日本語メモ",
            "invalid saves leave the last valid catalog readable");
    }
    File.WriteAllText(catalogPath, "{bad json");
    var corruptBytes = File.ReadAllBytes(catalogPath);
    using (var corrupted = new CatalogStore(catalogPath))
    {
        MustThrow<System.Text.Json.JsonException>(() => corrupted.Load(), "broken JSON is detected");
        MustThrow<InvalidOperationException>(() => corrupted.Save(catalog), "failed load cannot overwrite data");
        Check(File.ReadAllBytes(catalogPath).SequenceEqual(corruptBytes), "corrupt original remains available for recovery");
    }

    var snapshot = new EnvironmentSnapshot("test-version", ".NET test", "test OS", "X64", 4, ["Example 1.0"]);
    var observation = new PlaybackObservation(DateTimeOffset.Now, "A|B", "same\nsegment",
        "unknown", "manual", "stutter", 10, 12.3, 456, 78);
    var report = DiagnosticService.CreateReport(snapshot, [observation]);
    Check(report.Contains("A／B") && report.Contains("same segment"), "report escapes table separators and newlines");
    Check(!report.Contains(testRoot), "generated report does not include material paths");
    var settingsRoot = Path.Combine(testRoot, "host", "user", "setting", "4.56.1.1");
    Directory.CreateDirectory(settingsRoot);
    File.WriteAllText(Path.Combine(settingsRoot, "YukkuriMovieMaker.Plugin.PluginLoaderSettings.json"),
        """{"VideoFileSourcePlugins":["HighSpeedVideoReaderPlugin.Test","ProxyForge.Plugin.ProxyForgePlugin"],"Secret":"private-token"}""");
    File.WriteAllText(Path.Combine(settingsRoot, "ProxyForge.Settings.ProxyForgeSettings.json"),
        """{"UseProxy":true,"MinFileSizeForProxy":550,"Scale":40,"MaxCacheMemoryMb":16384,"EnableDiskFallback":false,"Secret":"private-token"}""");
    var videoSettings = HostVideoSettings.Read(Path.Combine(testRoot, "host"), "4.56.1.1");
    Check(videoSettings?.ReaderOrder.Count == 2 && videoSettings.ReaderOrder[1].StartsWith("ProxyForge."),
        "saved reader priority is read in its original order");
    Check(videoSettings is { ProxyEnabled: true, ProxyMinimumMiB: 550, ProxyScalePercent: 40,
        ProxyCacheMiB: 16384, DiskFallbackEnabled: false }, "known proxy settings are read without mutation");
    var settingsReport = DiagnosticService.CreateReport(snapshot with { VideoSettings = videoSettings }, []);
    Check(settingsReport.Contains("使われない可能性") && settingsReport.Contains("550 MiB"),
        "diagnostics distinguish a priority hypothesis from verified proxy use");
    Check(!settingsReport.Contains("private-token") && !settingsReport.Contains(testRoot),
        "settings diagnostics exclude unknown secrets and paths");
    Check(HostVideoSettings.Read(Path.Combine(testRoot, "host"), "../4.56.1.1") is null,
        "host version cannot traverse settings directories");
    File.WriteAllText(Path.Combine(settingsRoot, "ProxyForge.Settings.ProxyForgeSettings.json"), "[]");
    Check(HostVideoSettings.Read(Path.Combine(testRoot, "host"), "4.56.1.1")?.ProxyEnabled is null,
        "invalid proxy settings remain unknown without preventing other diagnostics");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        bool cancellationWorked = false;
        try { await DiagnosticService.MeasureAsync("a", "b", "c", "d", "e", cancelled.Token); }
        catch (OperationCanceledException) { cancellationWorked = true; }
        Check(cancellationWorked, "CPU observation can be cancelled");
    }
    var measured = await DiagnosticService.MeasureAsync("a", "b", "c", "d", "e",
        CancellationToken.None, TimeSpan.FromMilliseconds(50));
    Check(measured.IntervalSeconds > 0 && measured.ProcessCpuPercent >= 0 &&
          measured.ProcessCpuPercent <= 100 && measured.WorkingSetMiB > 0,
        "process measurement returns bounded CPU and positive memory");
    EditingChecks.Run(testRoot, Check);
    WorkflowChecks.Run(Check);
    Console.WriteLine($"All {passed} checks passed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    try { Directory.Delete(testRoot, true); } catch (IOException) { }
}

