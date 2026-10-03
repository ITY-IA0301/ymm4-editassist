using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace EditAssist.Plugin;

internal sealed record DecodeResult(bool? Success, string Detail, double? DurationSeconds = null);
internal sealed record CommandResult(int ExitCode, string Output, string Error);

internal static class MediaDecoderProbe
{
    internal static (string? Probe, string? Decoder) Locate(string hostDirectory)
    {
        // Use only YMM's already-installed tools. No downloads, proxy readers, or conversions.
        string root = Path.Combine(hostDirectory, "Resources", "bin", "x64", "ffmpeg");
        string probe = Path.Combine(root, "ffprobe.exe"), decoder = Path.Combine(root, "ffmpeg.exe");
        return (File.Exists(probe) ? probe : null, File.Exists(decoder) ? decoder : null);
    }

    internal static async Task<DecodeResult> CheckAsync(string path, double[] offsets, string? probe, string? decoder,
        CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        FileStamp.Read(path);
        if (probe is null || decoder is null) return new(null, "未確認：YMM4同梱のffprobe／ffmpegが見つかりません。ファイルの存在だけ確認しました。");
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(20);
        var elapsed = Stopwatch.StartNew();
        TimeSpan Remaining()
        {
            var remaining = limit - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            return remaining;
        }
        try
        {
            var metadata = await RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=codec_type,width,height:format=duration", "-of", "json", path], token, Remaining());
            if (metadata.ExitCode != 0) return new(false, "動画情報の読み込みに失敗：" + metadata.Error);
            using var json = JsonDocument.Parse(metadata.Output);
            if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return new(false, "映像ストリームがありません。動画ファイルとして読み込めません。");
            var stream = streams[0];
            if (!stream.TryGetProperty("width", out var width) || width.GetInt32() <= 0 ||
                !stream.TryGetProperty("height", out var height) || height.GetInt32() <= 0)
                return new(false, "映像の幅・高さを取得できません。");
            double? duration = null;
            if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationValue) &&
                double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds > 0)
                duration = seconds;
            var positions = SamplePositions(offsets);
            if (duration.HasValue && positions.Any(x => x >= duration.Value))
                return new(false, $"指定クリップの開始位置が動画の長さ（{duration:F2}秒）を超えています。別動画への置換の可能性があります。", duration);
            foreach (double position in positions)
            {
                token.ThrowIfCancellationRequested();
                var result = await RunAsync(decoder, ["-nostdin", "-hide_banner", "-v", "error", "-xerror", "-ss",
                    position.ToString("0.######", CultureInfo.InvariantCulture), "-threads", "1", "-i", path,
                    "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1", "-f", "framemd5", "-"], token, Remaining());
                if (result.ExitCode != 0 || !HasDecodedFrame(result.Output))
                    return new(false, $"{position:F2}秒付近の映像を読み込めません：" + result.Error, duration);
            }
            return new(true, $"FFmpegで{positions.Length}か所の映像読み込みを確認。動画全編やYMM4の別リーダーでの正常動作を保証するものではありません。", duration);
        }
        catch (TimeoutException) { return new(null, "未確認：検査が時間制限を超えました。長いシーク・破損・読み取り待ちの可能性があります。"); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
        { return new(null, "検査ツール側の問題で未確認：" + ex.Message); }
    }

    internal static double[] SamplePositions(IEnumerable<double> offsets)
    {
        var values = offsets.Where(x => double.IsFinite(x) && x >= 0).Distinct().Order().ToArray();
        // Head + at most three used split positions. Seek to keyframes; never decode the entire hour.
        return new[] { 0d }.Concat(values.Length == 0 ? [] : new[] { values[0], values[values.Length / 2], values[^1] }).Distinct().ToArray();
    }

    internal static bool HasDecodedFrame(string output) => output.Split('\n').Any(line =>
        !line.StartsWith('#') && line.Split(',') is { Length: >= 6 } parts && int.TryParse(parts[0].Trim(), out _) &&
        long.TryParse(parts[4].Trim(), out long bytes) && bytes > 0 && parts[5].Trim().Length == 32);

    internal static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken token, TimeSpan timeout)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8 };
        foreach (string value in arguments) info.ArgumentList.Add(value);
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, 65536);
        var stderr = ReadBoundedAsync(process.StandardError, 8192);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048];
        int count;
        // Drain even once bounded to avoid blocking the child on a full redirected pipe.
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < limit) text.Append(buffer, 0, Math.Min(count, limit - text.Length));
        return text.ToString().Trim();
    }
}
