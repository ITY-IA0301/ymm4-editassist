using System.ComponentModel;
using EditAssist.Core;

namespace EditAssist.Plugin;

public sealed class CharacterLayerRow(string character, int clips)
    : INotifyPropertyChanged
{
    public string Character { get; } = character;
    public int Clips { get; } = clips;
    private string destinationLayer = "";
    public string DestinationLayer
    {
        get => destinationLayer;
        set { if (destinationLayer == value) return; destinationLayer = value; PropertyChanged?.Invoke(this, new(nameof(DestinationLayer))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record EditingCheckRow(string Severity, int? Key, int? Frame, string Label, string Message)
{
    public static EditingCheckRow From(EditIssue issue, IReadOnlyList<EditItem> snapshot)
    {
        var item = snapshot.FirstOrDefault(x => x.Key == issue.Key);
        return new(issue.Severity, issue.Key, item?.Frame, item?.Label ?? "", issue.Message);
    }
}
