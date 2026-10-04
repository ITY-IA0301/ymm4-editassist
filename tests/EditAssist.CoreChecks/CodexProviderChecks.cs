using System.Diagnostics;
using System.Text.Json;
using EditAssist.Core;

internal static class CodexProviderChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        // An offline CLI fixture. No login, credentials, network, or real Codex usage.
        if (OperatingSystem.IsWindows()) return;
        string executable = Path.Combine(root, "codex fixture's path.sh");
        string json = "{\"version\":1,\"lines\":[{\"character\":\"案内\",\"text\":\"完成\"}],\"materials\":[],\"notes\":\"fixture\"}";
        await File.WriteAllTextAsync(executable, "#!/bin/sh\n" +
            "test -z \"$CODEX_API_KEY\" || exit 9\ntest -z \"$OPENAI_API_KEY\" || exit 9\n" +
            "out=''\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = '--output-last-message' ]; then shift; out=\"$1\"; fi; shift; done\n" +
            "cat >/dev/null\nprintf '%s' '" + json.Replace("'", "'\\''") + "' >\"$out\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var request = new AiRequest("台本を作る $(touch SHOULD_NOT_EXIST)", "案内：丁寧", "", []);
        var generated = await new CodexCliProvider(executable).GenerateAsync(request, CancellationToken.None);
        check(generated.Lines.Single().Text == "完成", "Codex fixture roundtrip reads schema-constrained final JSON without a shell prompt");
        check(!File.Exists(Path.Combine(root, "SHOULD_NOT_EXIST")), "AI prompt is stdin data and never interpreted as shell commands");
        bool refused = false;
        try { await new CodexCliProvider(executable, "model;touch bad").GenerateAsync(request, CancellationToken.None); }
        catch (ArgumentException) { refused = true; }
        check(refused, "invalid Codex model cannot inject process arguments");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\ncat >/dev/null\nexit 7\n");
        refused = false;
        try { await new CodexCliProvider(executable).GenerateAsync(request, CancellationToken.None); }
        catch (InvalidOperationException) { refused = true; }
        check(refused, "failed CLI does not produce an accepted AI draft");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\ncat >/dev/null\nsleep 30\n");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var watch = Stopwatch.StartNew(); refused = false;
        try { await new CodexCliProvider(executable).GenerateAsync(request, cancel.Token); }
        catch (OperationCanceledException) { refused = true; }
        check(refused && watch.Elapsed < TimeSpan.FromSeconds(5), "cancelling AI kills the CLI process tree and returns promptly");
    }
}
