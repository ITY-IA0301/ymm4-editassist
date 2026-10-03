using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EditAssist.Core;

public sealed record EnvironmentSnapshot(string HostVersion, string Framework,
    string OperatingSystem, string Architecture, int LogicalProcessors,
    IReadOnlyList<string> LoadedLibraries, HostVideoSettings? VideoSettings = null);

public sealed record PlaybackObservation(DateTimeOffset RecordedAt, string CaseLabel,
    string Condition, string Decoder, string ProxyState, string Symptom,
    double IntervalSeconds, double ProcessCpuPercent, double WorkingSetMiB,
    double ManagedHeapMiB);

public static class DiagnosticService
{
    public static EnvironmentSnapshot Collect()
    {
        string hostVersion = "不明";
        string? hostDirectory = null;
        try
        {
            var host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "YukkuriMovieMaker");
            if (host is not null && !string.IsNullOrEmpty(host.Location))
            {
                hostVersion = FileVersionInfo.GetVersionInfo(host.Location).FileVersion ?? "不明";
                hostDirectory = Path.GetDirectoryName(host.Location);
            }
            else if (Environment.ProcessPath is { } path &&
                Path.GetFileNameWithoutExtension(path).Equals("YukkuriMovieMaker",
                    StringComparison.OrdinalIgnoreCase))
            {
                hostVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "不明";
                hostDirectory = Path.GetDirectoryName(path);
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        { }
        var libraries = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic).Select(a => a.GetName())
            .Where(a => a.Name is not null && !a.Name.StartsWith("System", StringComparison.Ordinal)
                && !a.Name.StartsWith("Microsoft", StringComparison.Ordinal))
            .Select(a => $"{a.Name} {a.Version}")
            .OrderBy(a => a, StringComparer.Ordinal).ToArray();
        return new(hostVersion, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, libraries,
            hostDirectory is null ? null : HostVideoSettings.Read(hostDirectory, hostVersion));
    }

    public static async Task<PlaybackObservation> MeasureAsync(string caseLabel, string condition,
        string decoder, string proxyState, string symptom, CancellationToken token,
        TimeSpan? interval = null)
    {
        using var process = Process.GetCurrentProcess();
        var duration = interval ?? TimeSpan.FromSeconds(10);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var cpuStart = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        await Task.Delay(duration, token).ConfigureAwait(false);
        process.Refresh();
        var elapsed = clock.Elapsed.TotalSeconds;
        var percent = (process.TotalProcessorTime - cpuStart).TotalSeconds / elapsed
            / Environment.ProcessorCount * 100;
        return new(DateTimeOffset.Now, caseLabel, condition, decoder, proxyState, symptom,
            elapsed, Math.Clamp(percent, 0, 100), process.WorkingSet64 / 1048576d,
            GC.GetTotalMemory(false) / 1048576d);
    }

    private static string Cell(string text) => text.Replace("|", "／")
        .Replace("\r", " ").Replace("\n", " ");

    public static string CreateReport(EnvironmentSnapshot snapshot,
        IEnumerable<PlaybackObservation> observations)
    {
        var report = new StringBuilder();
        report.AppendLine("# YMM4 EditAssist 比較記録");
        report.AppendLine("初版 0.1.0 / 自動取得情報には素材・設定のパスを含めません。");
        report.AppendLine("自由記述は共有前に確認してください。プレビュー不調の修正や実描画FPS測定は行いません。");
        report.AppendLine();
        report.AppendLine($"- YMM4版：{snapshot.HostVersion}");
        report.AppendLine($"- 実行環境：{snapshot.Framework}");
        report.AppendLine($"- OS：{snapshot.OperatingSystem}");
        report.AppendLine($"- プロセス：{snapshot.Architecture} / 論理CPU数：{snapshot.LogicalProcessors}");
        report.AppendLine();
        report.AppendLine("CPUはプロセス全体の区間平均（全論理CPUで100%）。メモリは終了時点。");
        report.AppendLine("GPU、描画FPS、シーク待ち、プロキシ適用状態は自動取得していません。");
        report.AppendLine("デコード、プロキシ、症状、条件は利用者の記録です。");
        report.AppendLine();
        report.AppendLine("| 記録時刻 | ケース | 条件 | デコード（手入力） | プロキシ（手入力） | 症状（手入力） | 計測秒 | CPU % | 作業メモリ MiB | 管理メモリ MiB |");
        report.AppendLine("|---|---|---|---|---|---|---:|---:|---:|---:|");
        foreach (var o in observations)
            report.AppendLine(FormattableString.Invariant(
                $"| {o.RecordedAt:yyyy-MM-dd HH:mm:ss zzz} | {Cell(o.CaseLabel)} | {Cell(o.Condition)} | {Cell(o.Decoder)} | {Cell(o.ProxyState)} | {Cell(o.Symptom)} | {o.IntervalSeconds:F1} | {o.ProcessCpuPercent:F1} | {o.WorkingSetMiB:F1} | {o.ManagedHeapMiB:F1} |"));
        report.AppendLine();
        report.AppendLine("## 保存された動画読み込み設定（現在の再生状態ではありません）");
        if (snapshot.VideoSettings is { } settings)
        {
            for (int i = 0; i < settings.ReaderOrder.Count; i++)
                report.AppendLine($"{i + 1}. {settings.ReaderOrder[i]}");
            static string OnOff(bool? value) => value is null ? "不明" : value.Value ? "有効" : "無効";
            static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "不明";
            report.AppendLine($"- ProxyForge 1.x：{OnOff(settings.ProxyEnabled)} / 対象最小 {Number(settings.ProxyMinimumMiB)} MiB / 解像度 {Number(settings.ProxyScalePercent)}%");
            report.AppendLine($"- 最大キャッシュメモリ {Number(settings.ProxyCacheMiB)} MiB / ディスクフォールバック {OnOff(settings.DiskFallbackEnabled)}");
            int proxyIndex = settings.ReaderOrder.ToList().FindIndex(p => p.StartsWith("ProxyForge.", StringComparison.Ordinal));
            if (proxyIndex > 0)
                report.AppendLine("確認候補：ProxyForgeより前に別の読込プラグインがあります。先のプラグインが動画を開くと、ProxyForgeが使われない可能性があります。設定画面で順序と生成状況を確認してください。");
            if (settings.ProxyEnabled is false)
                report.AppendLine("確認候補：保存された設定ではプロキシ利用が無効です。");
        }
        else report.AppendLine("取得できませんでした。設定画面で手入力してください。");
        report.AppendLine("設定画面の未保存変更、選択中素材の実際の読込先、生成完了・適用状態は取得していません。設定は変更しません。");
        report.AppendLine();
        report.AppendLine("## 読み込み済みライブラリ（プラグイン一覧や優先順位とは異なります）");
        foreach (var library in snapshot.LoadedLibraries) report.AppendLine($"- {library}");
        return report.ToString();
    }
}

