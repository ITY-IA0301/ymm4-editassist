namespace EditAssist.Core;

public sealed record EditItem(int Key, string Kind, string Character, int Frame, int Length,
    int Layer, bool Locked = false, int Group = 0, double VoiceSeconds = 0,
    double OffsetSeconds = 0, double PlaybackRate = 1, double AdditionalSeconds = 0,
    bool CanFitVoice = true, string Label = "", string SearchText = "")
{
    public long End => (long)Frame + Length;
}
public sealed record ItemFilter(string Kind = "Voice", string Character = "", int? FromFrame = null,
    int? ToFrame = null, int? FromLayer = null, int? ToLayer = null, bool IncludeLocked = false, string Query = "")
{
    public bool Matches(EditItem item) => (Kind == "All" || Kind == item.Kind) &&
        (Character.Length == 0 || Character == item.Character) && (IncludeLocked || !item.Locked) &&
        (FromFrame is null || item.End > FromFrame) && (ToFrame is null || item.Frame < ToFrame) &&
        (FromLayer is null || item.Layer >= FromLayer) && (ToLayer is null || item.Layer <= ToLayer) &&
        (string.IsNullOrWhiteSpace(Query) || MaterialSearch.Normalize(item.Label + "\n" + item.SearchText)
            .Contains(MaterialSearch.Normalize(Query.Trim()), StringComparison.Ordinal));
    public void Validate()
    {
        if (Query.Length > 1000) throw new ArgumentException("検索文字は1000文字以内にしてください。");
        if (FromFrame < 0 || ToFrame < 0 || FromLayer < 0 || ToLayer < 0 ||
            (FromFrame.HasValue && ToFrame.HasValue && FromFrame >= ToFrame) ||
            (FromLayer.HasValue && ToLayer.HasValue && FromLayer > ToLayer))
            throw new ArgumentException("範囲の開始と終了を確認してください。時刻はフレーム、終了時刻は含みません。");
    }
}
public sealed record ItemChange(int Key, int? Frame = null, int? Length = null, int? Layer = null);
public sealed record EditIssue(string Severity, int? Key, string Message);
public sealed class EditPlan
{
    public string Title { get; init; } = "";
    public List<ItemChange> Changes { get; } = [];
    public List<EditIssue> Issues { get; } = [];
    public bool IsValid => Issues.All(i => i.Severity != "エラー");
}

public static class TimelineEditing
{
    public static IReadOnlyList<EditItem> Filter(IEnumerable<EditItem> items, ItemFilter filter)
    { filter.Validate(); return items.Where(filter.Matches).OrderBy(i => i.Frame).ThenBy(i => i.Layer).ToArray(); }

    public static EditPlan MoveLayers(IReadOnlyList<EditItem> all, IEnumerable<int> keys, int value,
        bool relative, int layerLimit = 1000)
    {
        var targets = keys.ToHashSet();
        var plan = new EditPlan { Title = relative ? $"レイヤーを {value:+0;-0;0} 移動" : $"レイヤー {value} へ移動" };
        foreach (var item in all.Where(i => targets.Contains(i.Key)))
        {
            long destination = relative ? (long)item.Layer + value : value;
            if (destination < 0 || destination >= layerLimit)
                plan.Issues.Add(new("エラー", item.Key, $"レイヤーは0〜{layerLimit - 1}の範囲です。"));
            else if (destination != item.Layer) plan.Changes.Add(new(item.Key, Layer: (int)destination));
        }
        ValidatePlan(all, plan, layerLimit);
        return plan;
    }

    public static EditPlan MoveVoicesByCharacter(IReadOnlyList<EditItem> all, IEnumerable<int> keys,
        IReadOnlyDictionary<string, int> destinations, int layerLimit = 1000)
    {
        if (destinations.Count == 0) throw new ArgumentException("キャラの移動先レイヤーを1つ以上指定してください。");
        var targets = keys.ToHashSet();
        var plan = new EditPlan { Title = "キャラ別のボイスレイヤー整理" };
        foreach (var pair in destinations)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0 || pair.Value >= layerLimit)
                plan.Issues.Add(new("エラー", null, $"キャラ「{pair.Key}」の移動先は0〜{layerLimit - 1}で指定してください。"));
        foreach (int missing in targets.Except(all.Select(i => i.Key)))
            plan.Issues.Add(new("エラー", missing, "対象がありません。一覧を更新してください。"));
        var retained = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var item in all.Where(i => targets.Contains(i.Key)))
        {
            if (item.Kind != "Voice") { plan.Issues.Add(new("エラー", item.Key, "キャラ別整理はボイスだけを対象にします。")); continue; }
            if (!destinations.TryGetValue(item.Character, out int destination))
            {
                if (!retained.TryGetValue(item.Character, out var kept)) retained[item.Character] = kept = [];
                kept.Add(item.Key); continue;
            }
            if (destination != item.Layer) plan.Changes.Add(new(item.Key, Layer: destination));
        }
        foreach (var pair in retained)
            plan.Issues.Add(new("確認", pair.Value[0], $"キャラ「{pair.Key}」の{pair.Value.Count}個は移動先未指定のため、そのままにします。"));
        ValidatePlan(all, plan, layerLimit);
        return plan;
    }

    public static int VoiceFrames(EditItem item, int fps)
    {
        if (fps < 1 || fps > 1000 || !item.CanFitVoice || !double.IsFinite(item.VoiceSeconds) ||
            item.VoiceSeconds <= 0 || !double.IsFinite(item.PlaybackRate) || item.PlaybackRate <= 0 ||
            item.OffsetSeconds != 0 || !double.IsFinite(item.AdditionalSeconds) || item.AdditionalSeconds < 0)
            throw new InvalidOperationException("実長を安全に計算できません。分割済み・速度アニメーション・音声未生成のアイテムは対象外です。");
        var duration = item.VoiceSeconds / item.PlaybackRate + item.AdditionalSeconds;
        double frames = Math.Ceiling(duration * fps - 1e-8);
        if (frames < 1 || frames > int.MaxValue) throw new InvalidOperationException("音声の長さが範囲外です。");
        return (int)frames;
    }

    // Ripple affects only an explicit follower set. Long clips crossing a boundary are never trimmed implicitly.
    public static EditPlan FitVoices(IReadOnlyList<EditItem> all, IEnumerable<int> keys, int fps,
        IEnumerable<int>? followerKeys = null)
    {
        var targets = keys.ToHashSet();
        var followers = followerKeys?.ToHashSet() ?? [];
        var plan = new EditPlan { Title = "音声実長への尺調整" };
        var lengths = new Dictionary<int, int>();
        var events = new List<(long End, int Delta)>();
        foreach (var item in all.Where(i => targets.Contains(i.Key)))
        {
            if (item.Kind != "Voice") continue;
            try
            {
                int length = VoiceFrames(item, fps);
                lengths[item.Key] = length;
                if (length != item.Length) events.Add((item.End, length - item.Length));
            }
            catch (InvalidOperationException error) { plan.Issues.Add(new("注意", item.Key, error.Message)); }
        }
        if (followers.Count > 0)
        {
            long previousEnd = -1;
            foreach (var item in all.Where(i => lengths.ContainsKey(i.Key)).OrderBy(i => i.Frame))
            {
                if (item.Frame < previousEnd)
                    plan.Issues.Add(new("エラー", item.Key, "尺調整対象のボイス同士が重なっています。後続追従を外すか、先に順番を整理してください。"));
                previousEnd = Math.Max(previousEnd, item.End);
            }
        }
        foreach (var item in all)
        {
            long shifted = item.Frame;
            if (followers.Contains(item.Key))
            {
                foreach (var e in events)
                {
                    if (item.Frame < e.End && item.End > e.End && !lengths.ContainsKey(item.Key))
                        plan.Issues.Add(new("エラー", item.Key, "尺変更の境界をまたぐ素材があります。追従を外すか、YMM4で先に分割してください。"));
                    if (item.Frame >= e.End) shifted += e.Delta;
                }
            }
            int? length = lengths.TryGetValue(item.Key, out var l) && l != item.Length ? l : null;
            if (shifted != item.Frame || length.HasValue)
            {
                if (shifted < 0 || shifted > int.MaxValue) plan.Issues.Add(new("エラー", item.Key, "移動後の時刻が範囲外です。"));
                else plan.Changes.Add(new(item.Key, shifted != item.Frame ? (int)shifted : null, length));
            }
        }
        ValidatePlan(all, plan);
        return plan;
    }

    public static IReadOnlyList<EditIssue> Check(IReadOnlyList<EditItem> all, int gapThresholdFrames)
    {
        if (gapThresholdFrames < 1) throw new ArgumentOutOfRangeException(nameof(gapThresholdFrames));
        var issues = new List<EditIssue>();
        foreach (var group in all.GroupBy(i => i.Layer))
        {
            var sorted = group.OrderBy(i => i.Frame).ToArray();
            long end = -1;
            foreach (var item in sorted)
            {
                if (item.Frame < 0 || item.Length < 1 || item.Layer < 0)
                    issues.Add(new("エラー", item.Key, "時刻・長さ・レイヤーに不正な値があります。"));
                if (item.Frame < end) issues.Add(new("注意", item.Key, "同じレイヤーで素材が重なっています。"));
                end = Math.Max(end, item.End);
            }
        }
        long voiceEnd = -1;
        foreach (var voice in all.Where(i => i.Kind == "Voice").OrderBy(i => i.Frame))
        {
            if (voiceEnd >= 0 && voice.Frame - voiceEnd >= gapThresholdFrames)
                issues.Add(new("確認", voice.Key, $"直前のセリフから {voice.Frame - voiceEnd} フレームの空白があります（意図的な間か確認）。"));
            voiceEnd = Math.Max(voiceEnd, voice.End);
        }
        foreach (var group in all.Where(i => i.Kind == "Voice").GroupBy(i => i.Character))
        {
            long end = -1;
            foreach (var voice in group.OrderBy(i => i.Frame))
            {
                if (voice.Frame < end) issues.Add(new("注意", voice.Key, $"キャラ「{voice.Character}」のセリフが重なっています。"));
                end = Math.Max(end, voice.End);
            }
        }
        return issues;
    }

    public static void ValidatePlan(IReadOnlyList<EditItem> all, EditPlan plan, int layerLimit = 1000)
    {
        var map = all.ToDictionary(i => i.Key);
        if (plan.Changes.Select(c => c.Key).Distinct().Count() != plan.Changes.Count)
            plan.Issues.Add(new("エラー", null, "変更対象が重複しています。"));
        foreach (var change in plan.Changes)
        {
            if (!map.TryGetValue(change.Key, out var item)) { plan.Issues.Add(new("エラー", change.Key, "対象がありません。")); continue; }
            if (item.Locked) plan.Issues.Add(new("エラー", item.Key, "ロック中の素材は変更できません。"));
            if (item.Group != 0) plan.Issues.Add(new("エラー", item.Key, "グループ所属の素材です。YMM4で解除してから整理してください。画像・動画を巻き込まないため、自動解除はしません。"));
            if (change.Frame < 0 || change.Length < 1 || change.Layer < 0 || change.Layer >= layerLimit ||
                (long)(change.Frame ?? item.Frame) + (change.Length ?? item.Length) > int.MaxValue)
                plan.Issues.Add(new("エラー", item.Key, "変更後の配置が範囲外です。"));
        }
        var changes = plan.Changes.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var projected = all.Select(i => changes.TryGetValue(i.Key, out var c)
            ? i with { Frame = c.Frame ?? i.Frame, Length = c.Length ?? i.Length, Layer = c.Layer ?? i.Layer } : i).ToArray();
        foreach (var issue in Check(projected, int.MaxValue).Where(i => i.Key.HasValue && changes.ContainsKey(i.Key.Value) && i.Message.Contains("重な")))
            plan.Issues.Add(issue);
        // Also catch a changed earlier item colliding with a later unchanged item.
        foreach (var layer in projected.GroupBy(i => i.Layer).Concat(
            projected.Where(i => i.Kind == "Voice").GroupBy(i => i.Character).Select(g => g.GroupBy(_ => 0).Single())))
        {
            var active = new List<EditItem>();
            foreach (var item in layer.OrderBy(i => i.Frame))
            {
                active.RemoveAll(i => i.End <= item.Frame);
                if (active.Any(i => changes.ContainsKey(i.Key) || changes.ContainsKey(item.Key)))
                    plan.Issues.Add(new("注意", item.Key, "移動・尺調整後に同じレイヤーで重なる素材があります。"));
                active.Add(item);
            }
        }
    }
}
