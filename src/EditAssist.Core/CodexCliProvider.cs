using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EditAssist.Core;

// Credentials remain in Codex. This class never reads or copies auth.json or API keys.
public sealed class CodexCliProvider(string executable, string model = "") : IAiProvider
{
    public string Name => "ChatGPT（Codex CLI）";
    public async Task<AiResult> GenerateAsync(AiRequest request, CancellationToken cancellation)
    {
        string prompt = AiAssist.Prompt(request);
        string path = Path.GetFullPath(executable.Trim().Trim('"'));
        if (!File.Exists(path)) throw new FileNotFoundException("Codex CLIの実行ファイルを選択してください。", path);
        if (model.Length > 100 || !Regex.IsMatch(model, "^[a-zA-Z0-9._-]*$")) throw new ArgumentException("モデル名を確認してください。");
        string directory = Path.Combine(Path.GetTempPath(), "YMM4EditAssist-AI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string schema = Path.Combine(directory, "result.schema.json"), output = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(schema, AiAssist.Schema, new UTF8Encoding(false), cancellation);
            var args = new List<string> { "--ask-for-approval", "never", "-c", "forced_login_method=\"chatgpt\"",
                "-c", "features.shell_tool=false", "-c", "features.unified_exec=false", "-c", "web_search=\"disabled\"",
                "exec", "--ignore-user-config", "--sandbox", "read-only", "--skip-git-repo-check", "--ephemeral", "--output-schema", schema,
                "--output-last-message", output };
            if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model); }
            args.Add("-");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                int code = await ExecuteAsync(path, args, prompt, directory, timeout.Token);
                cancellation.ThrowIfCancellationRequested();
                if (code != 0 || !File.Exists(output)) throw new InvalidOperationException("Codexが生成を完了できませんでした。CLIでChatGPTへのログイン・利用枠・モデル設定を確認してください。");
                if (new FileInfo(output).Length > 4_000_000) throw new InvalidDataException("Codexの返答が大きすぎます。");
                return AiAssist.Parse(await File.ReadAllTextAsync(output, timeout.Token), request);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException("AI生成が5分で完了しませんでした。短い依頼で再試行してください。"); }
        }
        finally { try { Directory.Delete(directory, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
    internal static ProcessStartInfo StartInfo(string path, IReadOnlyList<string> arguments, string directory)
    {
        var info = new ProcessStartInfo { FileName = path, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (OperatingSystem.IsWindows() && Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Launch npm's JS entry point directly, preserving ArgumentList quoting on Windows.
            string script = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (!File.Exists(script)) throw new FileNotFoundException("npm版codex.cmdの隣にCodex本体がありません。codex.exeを指定するかCLIを再インストールしてください。");
            string node = Path.Combine(Path.GetDirectoryName(path)!, "node.exe");
            info.FileName = File.Exists(node) ? node : "node.exe";
            info.ArgumentList.Add(script);
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
        }
        else foreach (string arg in arguments) info.ArgumentList.Add(arg);
        // A stale API environment must not silently change the requested subscription authentication.
        foreach (string name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "CODEX_ACCESS_TOKEN" }) info.Environment.Remove(name);
        return info;
    }
    private static async Task<int> ExecuteAsync(string path, IReadOnlyList<string> arguments, string input, string directory, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = StartInfo(path, arguments, directory) };
        if (!process.Start()) throw new InvalidOperationException("Codex CLIを起動できませんでした。");
        using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        async Task Drain(StreamReader stream)
        { var buffer = new char[4096]; while (await stream.ReadAsync(buffer.AsMemory(), cancellation) > 0) { } }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellation); process.StandardInput.Close();
            await process.WaitForExitAsync(cancellation); await Task.WhenAll(stdout, stderr); cancellation.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}
