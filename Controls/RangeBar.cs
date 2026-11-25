using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TimelapseConverter.Controls;

/// <summary>
/// 시작·끝 손잡이가 두 개인 구간 선택 막대 (변환 구간 자르기용).
/// 값은 0 ~ Maximum(초) 범위이고, 두 손잡이 사이는 MinimumGap 이상 벌어진다.
/// </summary>
public sealed class RangeBar : UserControl
{
    const double ThumbSize = 22; // Porcelain.xaml의 SliderThumb 크기와 같음

    readonly Canvas _canvas = new() { Height = 28, Background = Brushes.Transparent };
    readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3) };
    readonly Border _fill = new() { Height = 6, CornerRadius = new CornerRadius(3) };
    readonly Thumb _startThumb = new() { Cursor = Cursors.SizeWE };
    readonly Thumb _endThumb = new() { Cursor = Cursors.SizeWE };

    double _max = 1;

    public double Maximum => _max;
    public double Start { get; private set; }
    public double End { get; private set; } = 1;
    public double MinimumGap { get; set; } = 1;

    /// <summary>손잡이를 움직이는 중 (true = 시작 손잡이)</summary>
    public event Action<bool>? RangeChanging;
    /// <summary>손잡이를 놓았거나 막대를 클릭함 (true = 시작 손잡이)</summary>
    public event Action<bool>? RangeCommitted;

    public RangeBar()
    {
        Focusable = false;
        Content = _canvas;
        _track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _startThumb.SetResourceReference(StyleProperty, "SliderThumb");
        _endThumb.SetResourceReference(StyleProperty, "SliderThumb");
        _startThumb.ToolTip = "시작 위치";
        _endThumb.ToolTip = "끝 위치";

        _canvas.Children.Add(_track);
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_startThumb);
        _canvas.Children.Add(_endThumb);

        _startThumb.DragDelta += (_, e) => DragBy(true, e.HorizontalChange);
        _endThumb.DragDelta += (_, e) => DragBy(false, e.HorizontalChange);
        _startThumb.DragCompleted += (_, _) => RangeCommitted?.Invoke(true);
        _endThumb.DragCompleted += (_, _) => RangeCommitted?.Invoke(false);
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;

        SizeChanged += (_, _) => Arrange();
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.5;
    }

    public void SetRange(double maximum, double start, double end)
    {
        _max = Math.Max(maximum, 0.001);
        End = Math.Clamp(end, 0, _max);
        Start = Math.Clamp(start, 0, End);
        Arrange();
    }

    double Usable => Math.Max(1, ActualWidth - ThumbSize);
    double ToX(double value) => ThumbSize / 2 + Usable * value / _max;
    double ToValue(double x) => Math.Clamp((x - ThumbSize / 2) / Usable * _max, 0, _max);

    void DragBy(bool start, double dx)
    {
        double gap = Math.Min(MinimumGap, _max);
        double delta = dx / Usable * _max;
        if (start) Start = Math.Clamp(Start + delta, 0, End - gap);
        else End = Math.Clamp(End + delta, Start + gap, _max);
        Arrange();
        RangeChanging?.Invoke(start);
    }

    void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Canvas && e.OriginalSource is not Border) return; // 손잡이 클릭은 Thumb가 처리
        double value = ToValue(e.GetPosition(_canvas).X);
        double gap = Math.Min(MinimumGap, _max);
        bool start = Math.Abs(value - Start) <= Math.Abs(value - End);
        if (start) Start = Math.Clamp(value, 0, End - gap);
        else End = Math.Clamp(value, Start + gap, _max);
        Arrange();
        RangeChanging?.Invoke(start);
        RangeCommitted?.Invoke(start);
        e.Handled = true;
    }

    void Arrange()
    {
        double width = ActualWidth;
        if (width <= 0) return;

        double top = (_canvas.Height - _track.Height) / 2;
        _track.Width = Math.Max(0, width - ThumbSize);
        Canvas.SetLeft(_track, ThumbSize / 2);
        Canvas.SetTop(_track, top);

        double xs = ToX(Start), xe = ToX(End);
        _fill.Width = Math.Max(0, xe - xs);
        Canvas.SetLeft(_fill, xs);
        Canvas.SetTop(_fill, top);

        double thumbTop = (_canvas.Height - ThumbSize) / 2;
        Canvas.SetLeft(_startThumb, xs - ThumbSize / 2);
        Canvas.SetTop(_startThumb, thumbTop);
        Canvas.SetLeft(_endThumb, xe - ThumbSize / 2);
        Canvas.SetTop(_endThumb, thumbTop);

        // 두 손잡이가 겹치면 움직일 수 있는 쪽이 위에 오도록 (끝까지 당긴 시작 손잡이 등)
        bool startOnTop = Start > _max / 2;
        Panel.SetZIndex(_startThumb, startOnTop ? 2 : 1);
        Panel.SetZIndex(_endThumb, startOnTop ? 1 : 2);
    }
}
