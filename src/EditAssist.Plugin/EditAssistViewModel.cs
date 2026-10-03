using System.ComponentModel;
using EditAssist.Core;
using YukkuriMovieMaker.Plugin;

namespace EditAssist.Plugin;

// YMM4 creates this through IToolPlugin. Avoid IO in the constructor.
public sealed class EditAssistViewModel : INotifyPropertyChanged, ITimelineToolViewModel
{
    private IReadOnlyList<MaterialEntry> results = Array.Empty<MaterialEntry>();
    private string status = "準備中…";
    public TimelineToolInfo? TimelineInfo { get; private set; }
    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        TimelineInfo = info;
        PropertyChanged?.Invoke(this, new(nameof(TimelineInfo)));
    }
    public IReadOnlyList<MaterialEntry> Results
    {
        get => results;
        set { results = value; PropertyChanged?.Invoke(this, new(nameof(Results))); }
    }
    public string Status
    {
        get => status;
        set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

