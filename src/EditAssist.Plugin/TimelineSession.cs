using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using EditAssist.Core;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Json = YukkuriMovieMaker.Json.Json;

namespace EditAssist.Plugin;

public sealed class PreparedEdit
{
    public required EditPlan Plan { get; init; }
    public required string Fingerprint { get; init; }
    public required IReadOnlyList<IItem> OriginalItems { get; init; }
    public required IReadOnlyList<EditItem> Snapshot { get; init; }
    public IReadOnlyList<IItem> Additions { get; init; } = [];
    public EditPreset? Preset { get; init; }
    internal IReadOnlyList<IItem> RemoveItems { get; init; } = [];
    internal IReadOnlyList<int> HideVoiceSubtitles { get; init; } = [];
}

internal sealed record CheckedTimelineState(string Fingerprint, IReadOnlyList<IItem> OriginalItems, IReadOnlyList<EditItem> Snapshot);

public sealed partial class TimelineSession(TimelineToolInfo info)
{
    public TimelineToolInfo Info { get; } = info;
    private (string After, IReadOnlyList<Action> Undo, IReadOnlyList<Animatable> Touched)? last;
    public string? LastBackupPath { get; private set; }
    public static string DefaultBackupDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4EditAssist", "backups");
    public bool CanRestoreLast => last.HasValue;
    public static string Kind(IItem item) => item switch
    {
        VoiceItem => "Voice", TextItem => "Text", ImageItem => "Image", VideoItem => "Video",
        AudioItem => "Audio", _ => "Other"
    };
    public IReadOnlyList<EditItem> Snapshot()
    {
        var voices = Info.Timeline.Items.OfType<VoiceItem>().ToArray();
        var duplicateVoiceFiles = voices.Where(v => !string.IsNullOrWhiteSpace(v.FilePath))
            .GroupBy(v => v.FilePath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Info.Timeline.Items.Select((item, i) =>
        {
            var voice = item as VoiceItem;
            bool rateIsConstant = item.PlaybackRate2.AnimationType == AnimationType.なし;
            return new EditItem(i, Kind(item), voice?.CharacterName ?? "", item.Frame, item.Length,
                item.Layer, item.IsLocked, item.Group, voice?.VoiceLength.TotalSeconds ?? 0,
                item.ContentOffset.TotalSeconds, item.PlaybackRate2.GetFirstValue() / 100d,
                voice?.AdditionalTime ?? 0, rateIsConstant && (voice is null || !duplicateVoiceFiles.Contains(voice.FilePath)), item.Label,
                voice?.Serif ?? (item is TextItem text ? text.Text : string.Join("\n", (item as BaseItem)?.GetFiles().Select(Path.GetFileName) ?? [])));
        }).ToArray();
    }
    private string Fingerprint()
    {
        string json = Json.GetJsonText(Info.Timeline) + "\n" + Json.GetJsonText(Characters());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
    public PreparedEdit Prepare(Func<IReadOnlyList<EditItem>, EditPlan> build)
    {
        RequireIdle();
        var snapshot = Snapshot();
        var plan = build(snapshot);
        foreach (var change in plan.Changes.Where(c => c.Layer.HasValue || c.Frame.HasValue || c.Length.HasValue))
        {
            var original = snapshot.FirstOrDefault(i => i.Key == change.Key);
            if (original is null) continue;
            foreach (var group in Info.Timeline.Items.OfType<GroupItem>())
                if ((OverlapsGroup(original.Frame, original.Length, original.Layer, group) ||
                    OverlapsGroup(change.Frame ?? original.Frame, change.Length ?? original.Length, change.Layer ?? original.Layer, group)))
                    plan.Issues.Add(new("エラー", change.Key, "グループ制御アイテムの範囲内へ／範囲外へ移動する可能性があります。先に範囲を整理してください。"));
        }
        return new() { Plan = plan, Snapshot = snapshot, OriginalItems = Info.Timeline.Items, Fingerprint = Fingerprint() };
    }
    private static bool InGroup(int layer, GroupItem group) => layer > group.Layer && layer <= (long)group.Layer + group.GroupRange;
    private static bool OverlapsGroup(int frame, int length, int layer, GroupItem group) =>
        (long)frame + length > group.Frame && frame < (long)group.Frame + group.Length && InGroup(layer, group);

    public PreparedEdit PreparePreset(IEnumerable<int> keys, EditPreset preset, bool replaceAnimation)
    {
        preset.Validate(); var targets = keys.ToHashSet();
        var prepared = Prepare(all =>
        {
            var plan = new EditPlan { Title = "プリセット適用：" + preset.Name };
            foreach (var item in all.Where(i => targets.Contains(i.Key)))
            {
                if (item.Kind is not ("Voice" or "Text"))
                { plan.Issues.Add(new("注意", item.Key, "字幕プリセットはボイス／テキストのみです。対象から除外しました。")); continue; }
                if (!PresetProperties(Info.Timeline.Items[item.Key], preset).Any())
                { plan.Issues.Add(new("確認", item.Key, "この種類に適用できるプリセット項目がないため除外しました。")); continue; }
                plan.Changes.Add(new(item.Key));
            }
            TimelineEditing.ValidatePlan(all, plan); return plan;
        });
        foreach (var c in prepared.Plan.Changes)
        {
            object item = prepared.OriginalItems[c.Key];
            foreach (var (property, value) in PresetProperties(item, preset))
                if (value is double && EffectiveProperty(item, property) is Animation a &&
                    a.AnimationType != AnimationType.なし && !replaceAnimation)
                    prepared.Plan.Issues.Add(new("エラー", c.Key, "既存のアニメーションがあります。「固定値で上書き」を許可するか、その項目を外してください。"));
            if (item is VoiceItem && PresetProperties(item, preset).Any(p => p.Property != "Volume"))
                prepared.Plan.Issues.Add(new("注意", c.Key, "ボイス字幕を個別設定へ切り替えます。字幕の見た目を確認してください。"));
        }
        return new() { Plan = prepared.Plan, Fingerprint = prepared.Fingerprint, Snapshot = prepared.Snapshot,
            OriginalItems = prepared.OriginalItems, Preset = preset };
    }
    public EditPreset CapturePreset(IItem item)
    {
        if (item is not (VoiceItem or TextItem)) throw new InvalidOperationException("ボイスまたはテキストを選んでください。");
        object source = item is VoiceItem v && v.JimakuVisibility == JimakuVisibility.UseCharacterSetting ? v.Character : item;
        object? Get(string name) => source.GetType().GetProperty(name)?.GetValue(source);
        double First(string name) => ((Animation)Get(name)!).GetFirstValue();
        return new(Name: string.IsNullOrWhiteSpace(item.Label) ? "選択素材の設定" : item.Label.Length > 100 ? item.Label[..100] : item.Label,
            Font: (string)Get("Font")!, FontSize: First("FontSize"), FontColor: ((Color)Get("FontColor")!).ToString(),
            Bold: (bool)Get("Bold")!, Volume: item is VoiceItem voice ? voice.Volume.GetFirstValue() : null,
            X: First(source is VoiceItem ? "JimakuX" : "X"), Y: First(source is VoiceItem ? "JimakuY" : "Y"));
    }
    private static IEnumerable<(string Property, object Value)> PresetProperties(object item, EditPreset p)
    {
        if (p.Font is not null) yield return ("Font", p.Font);
        if (p.FontSize.HasValue) yield return ("FontSize", p.FontSize.Value);
        if (p.FontColor is not null) yield return ("FontColor", ColorConverter.ConvertFromString(p.FontColor)!);
        if (p.Bold.HasValue) yield return ("Bold", p.Bold.Value);
        if (p.Volume.HasValue && item is VoiceItem) yield return ("Volume", p.Volume.Value);
        if (p.X.HasValue) yield return (item is VoiceItem ? "JimakuX" : "X", p.X.Value);
        if (p.Y.HasValue) yield return (item is VoiceItem ? "JimakuY" : "Y", p.Y.Value);
    }
    private static object? EffectiveProperty(object item, string name) =>
        item is VoiceItem v && v.JimakuVisibility == JimakuVisibility.UseCharacterSetting && name != "Volume"
            ? v.Character.GetType().GetProperty(SubtitleSourceName(name))?.GetValue(v.Character)
            : item.GetType().GetProperty(name)?.GetValue(item);
    private static string SubtitleSourceName(string name) => name is "JimakuFadeIn" or "JimakuFadeOut" or "JimakuVideoEffects"
        ? name : name.StartsWith("Jimaku", StringComparison.Ordinal) ? name[6..] : name;
    // Copy inherited appearance before switching to Custom: blank preset fields keep their effective values.
    private static readonly string[] SubtitleProperties = ["JimakuX", "JimakuY", "JimakuZ", "JimakuOpacity", "JimakuZoom", "JimakuRotation",
        "JimakuFadeIn", "JimakuFadeOut", "JimakuBlend", "JimakuIsInverted", "JimakuIsClippingWithObjectAbove", "JimakuIsAlwaysOnTop", "JimakuIsZOrderEnabled",
        "Font", "FontSize", "LineHeight2", "LetterSpacing2", "WordWrap", "MaxWidth", "BasePoint", "FontColor", "Style", "StyleColor",
        "Bold", "Italic", "Underline", "Strikethrough", "IsTrimEndSpace", "IsDevidedPerCharacter", "DisplayInterval", "DisplayDirection",
        "HideInterval", "HideDirection", "JimakuVideoEffects"];

    public void Select(IEnumerable<int> keys)
    {
        RequireIdle();
        var items = Info.Timeline.Items; var targets = keys.ToHashSet();
        if (targets.Any(k => k < 0 || k >= items.Count)) throw new InvalidOperationException("一覧を更新してください。");
        // Do not call movement helpers that implicitly include grouped image/video items.
        Info.Timeline.SelectedItems = items.Where((_, i) => targets.Contains(i)).ToImmutableList();
    }
    public void RequireIdle()
    {
        if (Info.AsyncAwaitStatus.IsBusy) throw new InvalidOperationException("YMM4の処理が終わってから実行してください。");
    }
    public void RequireCurrent(PreparedEdit prepared)
    {
        if (prepared.Fingerprint != Fingerprint() || !Info.Timeline.Items.SequenceEqual(prepared.OriginalItems, ReferenceEqualityComparer.Instance))
            throw new InvalidOperationException("変更確認後にタイムラインが変わりました。再度プレビューしてください。");
    }
    private string CheckFingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Json.GetJsonText(Info.Timeline.Items) + "\n" + Json.GetJsonText(Info.Timeline.VideoInfo) + "\n" + Json.GetJsonText(Characters()))));
    internal CheckedTimelineState CaptureCheckState()
    {
        RequireIdle(); return new(CheckFingerprint(), Info.Timeline.Items, Snapshot());
    }
    internal void RequireCheckCurrent(CheckedTimelineState state)
    {
        RequireIdle();
        if (!Info.Scenes.Timelines.Contains(Info.Timeline)) throw new InvalidOperationException("シーンがなくなりました。編集チェックをやり直してください。");
        if (state.Fingerprint != CheckFingerprint() || !Info.Timeline.Items.SequenceEqual(state.OriginalItems, ReferenceEqualityComparer.Instance))
            throw new InvalidOperationException("チェック後にタイムラインが変わりました。編集チェックをやり直してください。");
    }
    internal void NavigateTo(CheckedTimelineState checkedState, int key)
    {
        RequireCheckCurrent(checkedState);
        if (key < 0 || key >= Info.Timeline.Items.Count) throw new InvalidOperationException("チェック結果を更新してください。");
        var item = Info.Timeline.Items[key];
        if (item.Frame < 0) throw new InvalidOperationException("開始位置が不正な素材には移動できません。");
        Select([key]);
        Info.Timeline.CurrentFrame = item.Frame;
    }
    public async Task ApplyAsync(PreparedEdit prepared, bool allowWarnings, string? backupDirectory = null)
    {
        RequireIdle(); RequireCurrent(prepared);
        if (!prepared.Plan.IsValid) throw new InvalidOperationException("エラーのある変更は実行できません。");
        if (!allowWarnings && prepared.Plan.Issues.Any(i => i.Severity == "注意"))
            throw new InvalidOperationException("重なり等の注意事項を確認し、許可するか条件を変えてください。");
        if (prepared.Plan.Changes.Count == 0 && prepared.Additions.Count == 0) throw new InvalidOperationException("変更対象がありません。");
        using var guard = Info.AsyncAwaitStatus.Lock(false);
        Backup(backupDirectory);
        var undo = new List<Action>();
        var touched = new List<IItem>();
        Info.UndoRedoManager.Record();
        try
        {
            foreach (var c in prepared.Plan.Changes)
            {
                var item = prepared.OriginalItems[c.Key];
                if (item.IsLocked || item.Group != 0) throw new InvalidOperationException("対象のロック／グループが変わりました。");
                if (item is Animatable animated) animated.BeginEdit();
                touched.Add(item);
                void Scalar(string property, object value)
                {
                    var p = item.GetType().GetProperty(property)!;
                    var before = p.GetValue(item);
                    undo.Add(() => p.SetValue(item, before)); p.SetValue(item, value);
                }
                void Set(string property, object value)
                {
                    if (item.GetType().GetProperty(property)!.GetValue(item) is Animation a)
                    {
                        var copy = new Animation(); copy.CopyFrom(a); undo.Add(() => a.CopyFrom(copy));
                        if (value is Animation source) a.CopyFrom(source);
                        else { a.AnimationType = AnimationType.なし; a.SetFirstValue((double)value); }
                    }
                    else Scalar(property, value);
                }
                if (prepared.HideVoiceSubtitles.Contains(c.Key)) Scalar("JimakuVisibility", JimakuVisibility.Hidden);
                if (c.Frame.HasValue) Scalar("Frame", c.Frame.Value);
                if (c.Length.HasValue) Scalar("Length", c.Length.Value);
                if (c.Layer.HasValue) Scalar("Layer", c.Layer.Value);
                if (prepared.Preset is { } preset)
                {
                    var properties = PresetProperties(item, preset).ToArray();
                    if (item is VoiceItem v && properties.Any(p => p.Property != "Volume"))
                    {
                        if (v.JimakuVisibility == JimakuVisibility.UseCharacterSetting)
                            foreach (string property in SubtitleProperties)
                            {
                                var inherited = EffectiveProperty(v, property) ?? throw new InvalidOperationException("字幕設定APIに互換性がありません：" + property);
                                Set(property, inherited);
                            }
                        Scalar("JimakuVisibility", JimakuVisibility.Custom);
                    }
                    foreach (var (property, value) in properties)
                        Set(property, value);
                }
            }
            if (prepared.Additions.Count > 0 || prepared.RemoveItems.Count > 0)
            {
                var before = Info.Timeline.Items; undo.Add(() => Info.Timeline.Items = before);
                Info.Timeline.Items = before.RemoveRange(prepared.RemoveItems).AddRange(prepared.Additions);
            }
            foreach (var item in touched.OfType<Animatable>()) await item.EndEditAsync();
            Info.Timeline.RefreshTimelineLengthAndMaxLayer();
            Info.UndoRedoManager.Record();
            last = (Fingerprint(), undo.ToArray(), touched.OfType<Animatable>().ToArray());
        }
        catch
        {
            foreach (var action in undo.AsEnumerable().Reverse()) action();
            foreach (var item in touched.OfType<Animatable>()) await item.EndEditAsync();
            Info.Timeline.RefreshTimelineLengthAndMaxLayer(); Info.UndoRedoManager.Record();
            throw;
        }
    }
    public async Task RestoreLastAsync(string? backupDirectory = null)
    {
        RequireIdle();
        var batch = last ?? throw new InvalidOperationException("このツールで直前に実行した変更はありません。YMM4の「元に戻す」も利用できます。");
        if (Fingerprint() != batch.After) throw new InvalidOperationException("その後に編集が行われています。誤って巻き戻さないため、YMM4の「元に戻す」か保存済みバックアップを使ってください。");
        using var guard = Info.AsyncAwaitStatus.Lock(false);
        Backup(backupDirectory); Info.UndoRedoManager.Record();
        foreach (var item in batch.Touched) item.BeginEdit();
        foreach (var action in batch.Undo.Reverse()) action();
        foreach (var item in batch.Touched) await item.EndEditAsync();
        Info.Timeline.RefreshTimelineLengthAndMaxLayer(); Info.UndoRedoManager.Record(); last = null;
    }
    public void SaveProjectCopy(string path)
    {
        RequireIdle();
        var project = ProjectCopy();
        string text = Json.GetJsonText(project);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true);
    }
    private void Backup(string? directory)
    {
        directory ??= DefaultBackupDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"EditAssist-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.ymmp");
        var project = ProjectCopy();
        string text = Json.GetJsonText(project);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true);
        LastBackupPath = path;
    }
    private Project ProjectCopy()
    {
        // This native constructor is explicitly for JSON. Do not invoke the normal project constructor,
        // which loads global character defaults; here we serialize the actual connected scene/characters only.
#pragma warning disable CS0618
        var project = new Project { SelectedTimelineIndex = Math.Max(0, Info.Scenes.Timelines.IndexOf(Info.Timeline)),
            FilePath = "", LayoutXml = "", ToolStates = new() };
#pragma warning restore CS0618
        project.Timelines.AddRange(Info.Scenes.Timelines);
        project.Characters.AddRange(Characters());
        return project;
    }
    public IReadOnlyList<Character> Characters()
    {
        var found = Info.Scenes.Timelines.SelectMany(t => t.Items.OfType<VoiceItem>()).Select(v => v.Character).ToList();
        // MainViewModel is internal; only its known public getter is read. Never change it.
        if (Application.Current is { } app)
            foreach (Window window in app.Windows)
            {
                object? vm = window.DataContext;
                if (vm?.GetType().FullName != "YukkuriMovieMaker.ViewModels.MainViewModel") continue;
                var active = vm.GetType().GetProperty("ActiveTimelineViewModel")?.GetValue(vm);
                if (active?.GetType().GetProperty("Characters")?.GetValue(active) is IEnumerable<Character> characters)
                    found.AddRange(characters);
            }
        return found.Where(c => c is not null).DistinctBy(c => c.Name).OrderBy(c => c.Name).ToArray();
    }
    public async Task<PreparedEdit> PrepareScriptAsync(IReadOnlyList<ScriptLine> lines,
        IReadOnlyDictionary<string, Character> mapping, int startFrame, int? layer, int gapFrames,
        CancellationToken cancellation, IProgress<string>? progress = null,
        Func<Character, string, Task<VoiceItem>>? factory = null)
    {
        RequireIdle();
        if (startFrame < 0 || gapFrames < 0 || layer < 0 || layer >= Timeline.LayerLimit || lines.Count == 0)
            throw new InvalidOperationException("台本と開始時刻・レイヤー・間隔を確認してください。");
        foreach (var line in lines)
            if (!mapping.ContainsKey(line.Character)) throw new InvalidOperationException($"キャラ「{line.Character}」の割り当てがありません。");
        var prepared = Prepare(_ => new EditPlan { Title = "台本からボイスを追加" });
        var additions = new List<IItem>(); long frame = startFrame;
        factory ??= (character, text) => VoiceItem.CreateVoiceItemAsync(character, text, [], "");
        using (Info.AsyncAwaitStatus.Lock(true))
        {
            foreach (var line in lines)
            {
                cancellation.ThrowIfCancellationRequested(); progress?.Report($"{additions.Count + 1}/{lines.Count}：音声を生成中…");
                var voice = await factory(mapping[line.Character], line.Text);
                cancellation.ThrowIfCancellationRequested();
                voice.SetFPS(Info.Timeline.VideoInfo.FPS);
                int length = TimelineEditing.VoiceFrames(new(0, "Voice", "", 0, 1, 0,
                    VoiceSeconds: voice.VoiceLength.TotalSeconds, PlaybackRate: voice.PlaybackRate2.GetFirstValue() / 100d,
                    OffsetSeconds: voice.ContentOffset.TotalSeconds,
                    AdditionalSeconds: voice.AdditionalTime,
                    CanFitVoice: voice.PlaybackRate2.AnimationType == AnimationType.なし), Info.Timeline.VideoInfo.FPS);
                if (frame + length > int.MaxValue) throw new InvalidOperationException("台本がタイムラインの上限を超えます。");
                voice.Frame = (int)frame; voice.Layer = layer ?? mapping[line.Character].Layer; voice.Length = length;
                if (voice.Layer < 0 || voice.Layer >= Timeline.LayerLimit) throw new InvalidOperationException("キャラのレイヤーが範囲外です。");
                voice.Group = 0; additions.Add(voice); frame += length + (long)gapFrames;
            }
        }
        RequireCurrent(prepared);
        foreach (var item in additions)
            if (Info.Timeline.Items.OfType<GroupItem>().Any(g => OverlapsGroup(item.Frame, item.Length, item.Layer, g)))
                prepared.Plan.Issues.Add(new("エラー", null, "追加先にグループ制御範囲があります。先にレイヤーまたは範囲を変更してください。"));
        var combined = prepared.Snapshot.Concat(additions.Select((v, i) => new EditItem(prepared.Snapshot.Count + i,
            "Voice", ((VoiceItem)v).CharacterName, v.Frame, v.Length, v.Layer))).ToArray();
        prepared.Plan.Issues.AddRange(TimelineEditing.Check(combined, int.MaxValue)
            .Where(i => i.Message.Contains("重な")));
        return new() { Plan = prepared.Plan, Snapshot = prepared.Snapshot, OriginalItems = prepared.OriginalItems,
            Fingerprint = prepared.Fingerprint, Additions = additions };
    }
}

