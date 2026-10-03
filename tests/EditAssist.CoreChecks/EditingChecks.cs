using EditAssist.Core;

internal static class EditingChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        void Throws(Action action, string label)
        {
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
            { check(true, label); return; }
            throw new InvalidOperationException("Expected refusal: " + label);
        }
        EditItem[] items = [new(0, "Voice", "A", 0, 30, 3, VoiceSeconds: 2),
            new(1, "Image", "", 5, 10, 3), new(2, "Voice", "B", 30, 30, 5, VoiceSeconds: 1),
            new(3, "Audio", "", 60, 30, 2), new(4, "Voice", "A", 90, 30, 4, Locked: true)];
        check(TimelineEditing.Filter(items, new()).Select(i => i.Key).SequenceEqual([0, 2]), "voice filter excludes image, audio and locked items");
        check(TimelineEditing.Filter(items, new(Character: "A")).Single().Key == 0, "character filter matches only the requested speaker");
        check(TimelineEditing.Filter(items, new(FromFrame: 30, ToFrame: 60)).Single().Key == 2, "frame range is overlapping and half-open");
        check(TimelineEditing.Filter(items, new(FromLayer: 5, ToLayer: 5)).Single().Key == 2, "layer range is inclusive");
        check(TimelineEditing.Filter(items, new(IncludeLocked: true)).Count == 3, "read-only filtering can include locked items explicitly");
        Throws(() => TimelineEditing.Filter(items, new(FromFrame: 5, ToFrame: 5)), "empty frame ranges are rejected");
        Throws(() => TimelineEditing.Filter(items, new(FromLayer: 6, ToLayer: 2)), "inverted layer ranges are rejected");
        var move = TimelineEditing.MoveLayers(items, [0, 2], 7, false);
        check(move.IsValid && move.Changes.All(c => c.Layer == 7 && c.Frame is null && c.Length is null), "absolute movement changes layers only");
        var shift = TimelineEditing.MoveLayers(items, [0, 2], 2, true);
        check(shift.Changes.Select(c => c.Layer).SequenceEqual(new int?[] { 5, 7 }), "relative movement preserves spacing between layers");
        check(!TimelineEditing.MoveLayers(items, [0], -4, true).IsValid, "negative destination layers are refused");
        check(!TimelineEditing.MoveLayers(items, [2], int.MaxValue, true).IsValid, "overflowing relative shifts are refused");
        check(!TimelineEditing.MoveLayers(items, [0], 1000, false).IsValid, "upper layer limit is exclusive");
        check(!TimelineEditing.MoveLayers(items, [4], 8, false).IsValid, "locked items cannot be moved");
        var grouped = items.Select(i => i.Key == 0 ? i with { Group = 1 } : i).ToArray();
        check(!TimelineEditing.MoveLayers(grouped, [0], 8, false).IsValid, "grouped items are not silently detached");
        var overlap = TimelineEditing.MoveLayers(items, [1], 3, false);
        check(overlap.Changes.Count == 0, "no-op moves are not listed as changes");
        overlap = TimelineEditing.MoveLayers(items, [0], 3, false);
        check(overlap.Changes.Count == 0, "unchanged voice layers remain untouched");
        EditItem[] collision = [new(0, "Voice", "A", 0, 30, 1), new(1, "Image", "", 20, 30, 2)];
        check(TimelineEditing.MoveLayers(collision, [0], 2, false).Issues.Any(i => i.Severity == "注意"), "earlier changed items colliding with later unchanged items warn");
        collision = [new(0, "Voice", "A", 0, 30, 1), new(1, "Voice", "A", 20, 30, 2)];
        check(TimelineEditing.MoveLayers(collision, [0], 4, false).Issues.Any(i => i.Severity == "注意"), "same-speaker overlap warns across different layers");
        check(TimelineEditing.VoiceFrames(items[0], 30) == 60, "voice duration converts at 30fps");
        check(TimelineEditing.VoiceFrames(items[0], 60) == 120, "voice duration converts at 60fps");
        check(TimelineEditing.VoiceFrames(items[0] with { VoiceSeconds = 1.001 }, 30) == 31, "fractional frames round upward");
        check(TimelineEditing.VoiceFrames(items[0] with { PlaybackRate = 2, AdditionalSeconds = .5 }, 30) == 45, "speed and trailing time are included");
        Throws(() => TimelineEditing.VoiceFrames(items[0] with { OffsetSeconds = 1 }, 30), "split voice offsets are protected");
        Throws(() => TimelineEditing.VoiceFrames(items[0] with { CanFitVoice = false }, 30), "animated speed and duplicate source safeguards are honored");
        Throws(() => TimelineEditing.VoiceFrames(items[0] with { VoiceSeconds = 0 }, 30), "ungenerated audio is not resized");
        Throws(() => TimelineEditing.VoiceFrames(items[0] with { VoiceSeconds = double.NaN }, 30), "non-finite duration is rejected");
        var fit = TimelineEditing.FitVoices(items, [0, 2], 30);
        check(fit.IsValid && fit.Changes.Single() == new ItemChange(0, Length: 60), "without ripple only voice length changes");
        EditItem[] sequential = [new(0, "Voice", "A", 0, 30, 1, VoiceSeconds: 2),
            new(1, "Voice", "A", 30, 30, 1, VoiceSeconds: 1), new(2, "Image", "", 60, 10, 2)];
        fit = TimelineEditing.FitVoices(sequential, [0, 1], 30, [0, 1, 2]);
        check(fit.IsValid && fit.Changes.Single(c => c.Key == 1).Frame == 60 && fit.Changes.Single(c => c.Key == 2).Frame == 90,
            "explicit followers shift by preceding deltas without changing their length");
        check(!TimelineEditing.FitVoices([items[0], items[1] with { Frame = 20, Length = 20 }], [0], 30, [0, 1]).IsValid, "unrelated clips crossing a ripple boundary are not trimmed");
        var overlappingVoices = sequential.Select(i => i.Key == 1 ? i with { Frame = 20 } : i).ToArray();
        check(!TimelineEditing.FitVoices(overlappingVoices, [0, 1], 30, [0, 1]).IsValid, "ripple of overlapping target voices is refused");
        var split = TimelineEditing.FitVoices([items[0] with { OffsetSeconds = 1 }], [0], 30);
        check(split.Changes.Count == 0 && split.Issues.Count == 1, "unsafe voice resizing is skipped with an explicit warning");
        check(TimelineEditing.Check([items[0], items[0] with { Key = 1, Frame = 90 }], 60).Any(i => i.Severity == "確認"), "intentional silence is reported rather than deleted");
        var duplicate = new EditPlan(); duplicate.Changes.AddRange([new(0, Layer: 7), new(0, Layer: 8)]);
        TimelineEditing.ValidatePlan(items, duplicate);
        check(!duplicate.IsValid, "duplicate change keys are refused");
        var absent = new EditPlan(); absent.Changes.Add(new(99, Layer: 7)); TimelineEditing.ValidatePlan(items, absent);
        check(!absent.IsValid, "missing change targets are refused");
        var csv = ScriptImport.Parse("\uFEFFキャラ,セリフ\r\nA,\"こんにちは,世界\"\r\nB,\"一行目\n二行目と\"\"引用\"\"\"\n");
        check(csv.Count == 2 && csv[0].Text == "こんにちは,世界" && csv[1].Text == "一行目\n二行目と\"引用\"", "CSV supports BOM, commas, escaped quotes and multiline speech");
        check(ScriptImport.Parse("Character\tText\nA\t hello \n\n").Single().Text == "hello", "TSV accepts a header and blank lines");
        check(ScriptImport.Parse("\n\nA\thello,world").Single().Text == "hello,world", "TSV detection ignores leading blank lines and commas inside speech");
        check(ScriptImport.Parse("A,\"hello\tworld\"").Single().Text == "hello\tworld", "tabs inside quoted CSV fields do not change the delimiter");
        check(ScriptImport.Parse("A,こんにちは").Single().Number == 1, "headerless scripts keep their first line");
        Throws(() => ScriptImport.Parse("A,\"not closed"), "unclosed CSV quotes are rejected");
        Throws(() => ScriptImport.Parse("A,\"closed\"bad"), "characters after a closing quote are rejected");
        Throws(() => ScriptImport.Parse("A,hello,extra"), "scripts with more than two columns are rejected");
        Throws(() => ScriptImport.Parse("A,"), "empty speech is rejected");
        Throws(() => ScriptImport.Parse(",hello"), "empty speaker is rejected");
        Throws(() => ScriptImport.Parse(string.Join('\n', Enumerable.Repeat("A,hello", 2001))), "oversized script row counts are rejected");
        var preset = new EditPreset(Name: "テスト", Font: "Meiryo", FontSize: 32, FontColor: "#FF112233", Bold: true, Volume: 80, X: 4, Y: 5);
        var path = Path.Combine(root, "preset.json"); preset.Save(path);
        check(EditPreset.Load(path) == preset, "Unicode presets round-trip all supported fields");
        (preset with { Volume = 70 }).Save(path);
        check(EditPreset.Load(path + ".bak") == preset, "preset replacement preserves the previous file");
        Throws(() => (preset with { SchemaVersion = 2 }).Save(path), "unknown preset versions cannot overwrite existing files");
        Throws(() => (preset with { FontSize = double.PositiveInfinity }).Validate(), "non-finite preset sizes are refused");
        Throws(() => (preset with { FontColor = "red" }).Validate(), "preset color uses a constrained hex format");
        Throws(() => (preset with { Volume = -1 }).Validate(), "negative preset volume is refused");
        check(EditPreset.Load(path).Volume == 70 && Directory.GetFiles(root, "*.tmp").Length == 0, "invalid saves preserve valid presets and leave no temporary file");
    }
}
