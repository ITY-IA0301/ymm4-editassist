using EditAssist.Core;

internal static class WorkflowChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Refused(Action action, string name)
        {
            try { action(); } catch (ArgumentException) { check(true, name); return; }
            throw new Exception("Expected refusal: " + name);
        }
        EditItem[] items = [
            new(0, "Voice", "案内", 0, 20, 1, Label: "字幕", SearchText: "かまど ABC の説明"),
            new(1, "Image", "", 5, 10, 9, Label: "見本", SearchText: "かまど.png"),
            new(2, "Voice", "相方", 30, 20, 1, SearchText: "ﾏｲﾝｸﾗﾌﾄ"),
            new(3, "Voice", "案内", 60, 20, 2, SearchText: "次の説明"),
            new(4, "Voice", "未指定", 90, 20, 4, SearchText: "そのまま")];
        check(TimelineEditing.Filter(items, new(Query: "かまど")).Single().Key == 0, "speech query excludes interleaved image by kind");
        check(TimelineEditing.Filter(items, new(Kind: "All", Query: "かまど")).Count == 2, "material filename and speech searchable");
        check(TimelineEditing.Filter(items, new(Query: "Ａｂｃ")).Single().Key == 0, "query normalizes fullwidth and letter case");
        check(TimelineEditing.Filter(items, new(Query: "まいんくらふと")).Single().Key == 2, "query normalizes kana spelling");
        check(TimelineEditing.Filter(items, new(Query: " 字幕 ")).Single().Key == 0, "query searches custom label and trims outer spaces");
        check(TimelineEditing.Filter(items, new(Query: "説明", FromFrame: 60)).Single().Key == 3, "speech search combines with frame constraints");
        check(TimelineEditing.Filter(items, new(Query: "説明", Character: "相方")).Count == 0, "speech search combines with exact character");
        check(TimelineEditing.Filter(items, new(Query: "   ")).Count == 4, "empty query retains previous filter behavior");
        check(TimelineEditing.Filter([items[0] with { Locked = true }], new(Query: "かまど")).Count == 0, "keyword filter does not bypass lock exclusion");
        check(TimelineEditing.Filter(items, new(Query: "not present")).Count == 0, "no search hit is not an all-item match");
        Refused(() => TimelineEditing.Filter(items, new(Query: new string('x', 1001))), "oversized timeline query rejected");
        var destinations = new Dictionary<string, int> { ["案内"] = 5, ["相方"] = 7 };
        var voices = TimelineEditing.Filter(items, new()).Select(x => x.Key).ToArray();
        var plan = TimelineEditing.MoveVoicesByCharacter(items, voices, destinations, 20);
        check(plan.IsValid && plan.Changes.Count == 3, "one mapped plan covers multiple speakers");
        check(plan.Changes.Where(x => x.Key is 0 or 3).All(x => x.Layer == 5) && plan.Changes.Single(x => x.Key == 2).Layer == 7,
            "each character reaches its assigned layer");
        check(plan.Changes.All(x => x.Frame is null && x.Length is null && x.Key != 1), "character routing only changes voice layers");
        check(plan.Changes.All(x => x.Key != 4) && plan.Issues.Any(x => x.Message.Contains("未指定")), "unassigned character retained explicitly");
        check(!TimelineEditing.MoveVoicesByCharacter(items, [0, 1], destinations).IsValid, "nonvoice routing targets refused");
        check(!TimelineEditing.MoveVoicesByCharacter(items, [99], destinations).IsValid, "missing routing target refused");
        check(!TimelineEditing.MoveVoicesByCharacter(items, [0], new Dictionary<string, int> { ["案内"] = -1 }).IsValid, "negative character layer refused");
        check(!TimelineEditing.MoveVoicesByCharacter(items, [0], new Dictionary<string, int> { ["案内"] = 20 }, 20).IsValid, "character layer upper limit exclusive");
        check(!TimelineEditing.MoveVoicesByCharacter(items, [0], new Dictionary<string, int> { [""] = 5 }).IsValid, "empty character mapping refused");
        Refused(() => TimelineEditing.MoveVoicesByCharacter(items, [0], new Dictionary<string, int>()), "empty assignment cannot silently succeed");
        check(!TimelineEditing.MoveVoicesByCharacter([items[0] with { Locked = true }], [0], destinations).IsValid, "locked voice blocks character routing");
        check(!TimelineEditing.MoveVoicesByCharacter([items[0] with { Group = 8 }], [0], destinations).IsValid, "grouped voice blocks character routing");
        check(TimelineEditing.MoveVoicesByCharacter([items[0] with { Layer = 5 }], [0], destinations).Changes.Count == 0, "character routing omits unchanged layers");
        var colliding = items.Append(new EditItem(5, "Video", "", 1, 10, 5)).ToArray();
        check(TimelineEditing.MoveVoicesByCharacter(colliding, [0], destinations).Issues.Any(x => x.Severity == "注意"), "assigned voice layer collision warns");
        check(TimelineEditing.MoveVoicesByCharacter(items, voices,
            new Dictionary<string, int> { ["案内"] = 5, ["相方"] = 5 }).IsValid, "shared layer assignments are permitted when no overlap");
        check(TimelineEditing.MoveVoicesByCharacter(items, voices, destinations).Changes.SequenceEqual(plan.Changes), "routing plan is deterministic and source unchanged");
    }
}
