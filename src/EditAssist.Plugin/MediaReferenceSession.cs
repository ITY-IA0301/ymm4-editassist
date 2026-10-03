using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;

namespace EditAssist.Plugin;

internal sealed record RelinkTarget(Timeline Timeline, VideoItem Item, int SceneIndex, int ItemIndex, string OldPath);
internal sealed record RelinkPlan(string Filename, string Destination, FileStamp DestinationStamp,
    string OriginalJson, IReadOnlyList<RelinkTarget> Targets)
{
    internal string Summary => $"{Filename}\n移動先：{Destination}\n対象：{Targets.Count}クリップ／{Targets.Select(x => x.Timeline.ID).Distinct().Count()}シーン\n" +
        string.Join("\n", Targets.GroupBy(x => x.Timeline.Name).Select(x => $"・{x.Key}：{x.Count()}クリップ"));
}

internal sealed class MediaReferenceSession(TimelineToolInfo info)
{
    internal TimelineToolInfo Info { get; } = info;
    internal string ProjectKey => MediaCatalog.ProjectKey(Info.Scenes.Timelines.Select(x => x.ID));
    internal string? LastBackupPath { get; private set; }
    internal static string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4EditAssist");

    internal IReadOnlyList<VideoReference> CaptureReferences()
    {
        RequireIdle();
        return Info.Scenes.Timelines.SelectMany(t => t.Items.OfType<VideoItem>().Select(v =>
            new VideoReference(t.Name, v.FilePath ?? "", [v.ContentOffset.TotalSeconds]))).ToArray();
    }

    internal void RequireIdle()
    {
        if (Info.AsyncAwaitStatus.IsBusy) throw new InvalidOperationException("YMM4の処理が終わってから実行してください。");
        if (!Info.Scenes.Timelines.Contains(Info.Timeline)) throw new InvalidOperationException("プロジェクトが切り替わりました。画面を開き直してください。");
    }

    internal RelinkPlan Prepare(string filename, string destination)
    {
        RequireIdle();
        filename = filename.Trim().Trim('"');
        destination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetFileName(destination), filename, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("移動先は、拡張子まで同じファイル名の動画を指定してください。");
        var stamp = FileStamp.Read(destination);
        var targets = Info.Scenes.Timelines.SelectMany((t, s) => t.Items.Select((v, i) => (t, s, v, i)))
            .Where(x => x.v is VideoItem video && string.Equals(Path.GetFileName(video.FilePath), filename, StringComparison.OrdinalIgnoreCase))
            .Select(x => new RelinkTarget(x.t, (VideoItem)x.v, x.s, x.i, ((VideoItem)x.v).FilePath!))
            .Where(x => !string.Equals(x.OldPath, destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("同名の変更対象がありません。すでに移動先を参照している可能性があります。");
        if (targets.Any(x => x.Item.IsLocked)) throw new InvalidOperationException("同名動画にロックされたクリップがあります。全件変更するため、先にロックを解除してください。");
        return new(filename, destination, stamp, CaptureJson(), targets);
    }

    internal async Task ApplyAsync(RelinkPlan plan, string? backupDirectory = null)
    {
        RequireIdle();
        if (plan.OriginalJson != CaptureJson()) throw new InvalidOperationException("確認後にプロジェクトが編集されました。もう一度変更内容を確認してください。");
        if (FileStamp.Read(plan.Destination) != plan.DestinationStamp) throw new IOException("確認後に移動先動画が更新されました。もう一度確認してください。");
        foreach (var target in plan.Targets)
            if (target.Item.IsLocked || target.Item.FilePath != target.OldPath || !ReferenceEquals(Info.Scenes.Timelines[target.SceneIndex].Items[target.ItemIndex], target.Item))
                throw new InvalidOperationException("変更対象の状態が変わりました。もう一度確認してください。");
        using var guard = Info.AsyncAwaitStatus.Lock(false);
        Backup(plan.OriginalJson, backupDirectory ?? Path.Combine(StorageRoot, "backups"));
        if (FileStamp.Read(plan.Destination) != plan.DestinationStamp) throw new IOException("バックアップ中に移動先動画が更新されました。変更はしていません。");
        var expected = JObject.Parse(plan.OriginalJson);
        foreach (var target in plan.Targets) expected["Timelines"]![target.SceneIndex]!["Items"]![target.ItemIndex]!["FilePath"] = plan.Destination;
        Info.UndoRedoManager.Record();
        try
        {
            foreach (var target in plan.Targets) { target.Item.BeginEdit(); target.Item.FilePath = plan.Destination; }
            foreach (var target in plan.Targets) await target.Item.EndEditAsync();
            if (!JToken.DeepEquals(expected, JObject.Parse(CaptureJson())))
                throw new InvalidOperationException("参照先以外の変更を検出しました。安全のため元に戻します。");
            Info.UndoRedoManager.Record();
        }
        catch
        {
            foreach (var target in plan.Targets) { target.Item.BeginEdit(); target.Item.FilePath = target.OldPath; }
            foreach (var target in plan.Targets) await target.Item.EndEditAsync();
            Info.UndoRedoManager.Record();
            throw;
        }
    }

    private void Backup(string json, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"EditAssist-before-relink-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.ymmp");
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        if (!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(bytes))) throw new IOException("バックアップを照合できません。変更を停止しました。");
        LastBackupPath = path;
    }

    internal string CaptureJson()
    {
        // Native JSON constructor: avoid loading global defaults unrelated to this project.
#pragma warning disable CS0618
        var project = new Project { SelectedTimelineIndex = Math.Max(0, Info.Scenes.Timelines.IndexOf(Info.Timeline)),
            FilePath = "", LayoutXml = "", ToolStates = new() };
#pragma warning restore CS0618
        project.Timelines.AddRange(Info.Scenes.Timelines);
        var characters = Info.Scenes.Timelines.SelectMany(t => t.Items.OfType<VoiceItem>()).Select(v => v.Character).ToList();
        if (Application.Current is { } app)
            foreach (Window window in app.Windows)
            {
                var vm = window.DataContext;
                if (vm?.GetType().FullName != "YukkuriMovieMaker.ViewModels.MainViewModel") continue;
                var active = vm.GetType().GetProperty("ActiveTimelineViewModel")?.GetValue(vm);
                if (active?.GetType().GetProperty("Characters")?.GetValue(active) is IEnumerable<Character> current) characters.AddRange(current);
            }
        project.Characters.AddRange(characters.DistinctBy(c => c.Name).OrderBy(c => c.Name));
        return Json.GetJsonText(project);
    }
}
