using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PreviewLite;

public partial class PreviewLiteView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public PreviewLiteView()
    {
        InitializeComponent();
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { PreviewRuntime.Prepare(); timer.Start(); Refresh(); };
        Unloaded += (_, _) => { timer.Stop(); PreviewRuntime.Disable(); ModeText.Text = "OFF：通常のプレビュー"; };
    }

    private int Fps(ComboBox box) => int.Parse((string)((ComboBoxItem)box.SelectedItem).Tag);
    private void EnableClick(object sender, RoutedEventArgs e) => Apply(new(true, true, Fps(PlaybackBox), Fps(IdleBox)));
    private void MeasureClick(object sender, RoutedEventArgs e) => Apply(new(false, true, 30, 10));
    private void DisableClick(object sender, RoutedEventArgs e)
    {
        PreviewRuntime.Disable();
        PreviewRuntime.Metrics.Clear();
        ModeText.Text = "OFF：通常のプレビュー";
        Refresh();
    }

    private void Apply(PreviewOptions value)
    {
        try
        {
            PreviewRuntime.Configure(value);
            ModeText.Text = value.Enabled ? $"ON：再生 {value.PlaybackFps} fps ／ 編集 {value.IdleFps} fps を目安に抑制" : "通常状態を計測中（軽量化OFF）";
        }
        catch (Exception ex) { PreviewRuntime.Disable(); ModeText.Text = "OFF：" + ex.GetBaseException().Message; }
        Refresh();
    }

    private void Refresh()
    {
        ConnectionText.Text = PreviewRuntime.Status;
        EnableButton.IsEnabled = MeasureButton.IsEnabled = PreviewRuntime.Ready;
        var result = PreviewRuntime.Metrics.Snapshot();
        MetricsText.Text = result.Iterations == 0
            ? "計測待ち：再生してください。ONにしても観測描画が0のままなら、YMM4を再起動してください。"
            : $"直近最大60回の描画処理（{(result.Playing ? "再生時" : "停止・編集時")}）\n" +
              $"描画前（操作反映・更新・準備）：平均 {result.UpdateMs:F1} ms\n描画呼出し：平均 {result.DrawMs:F1} ms\n" +
              $"描画処理の実行頻度：約 {result.CadenceFps:F1} 回/秒（画面表示FPSではありません）\n" +
              $"観測描画 {result.Iterations} 回 ／ 追加待機した描画 {result.DelayChanges} 回\n" +
              "停止中に再描画がない場合、描画値は更新されません。";
    }
}
