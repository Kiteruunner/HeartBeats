using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;

namespace HeartBeats;

public partial class MainWindow : Window
{
    private readonly BleHeartRateClient _hr = new();

    // 你的耳机 MAC
    private const string Mac = "C0:E2:30:5E:51:05";

    private HudSettings _settings = new();
    private bool _collapsed = false;

    // ==== Chart settings ====
    private readonly List<(DateTime t, int bpm)> _samples = new();

    private const int MinBpmFloor = 40;
    private const int MaxBpmCeil = 200;
    private const int TargetFps = 20;

    // 统一坐标系：plotRect 内画曲线/网格；padding 区域画 label
    private const double PadLeft = 30;     // 左侧：bpm 标签
    private const double PadRight = 10;    // 右侧：只留一点空间给圆点
    private const double PadTop = 10;      // 顶部
    private const double PadBottom = 14;   // 底部：时间轴数字

    private const double RightLabelGap = 4; // plotRight 到右侧数字的间隔

    private DateTime _lastRender = DateTime.MinValue;

    private volatile string _lastStatus = "INIT";
    private bool _isConnecting = false;
    private CancellationTokenSource? _connectCts;

    // GridMode: 0=Off, 1=Minimal, 2=Ticks
    private const int GridOff = 0;
    private const int GridMinimal = 1;
    private const int GridTicks = 2;

    // 动态窗口秒数
    private int WindowSeconds => ClampWindowSeconds(_settings.ChartWindowSeconds);

    private static int ClampWindowSeconds(int s)
    {
        if (s <= 40) return 30;
        if (s <= 90) return 60;
        return 120;
    }

    // ===== plot rect helper =====
    private readonly struct PlotRect
    {
        public PlotRect(double left, double top, double right, double bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }
        public double Left { get; }
        public double Top { get; }
        public double Right { get; }
        public double Bottom { get; }
        public double Width => Math.Max(1, Right - Left);
        public double Height => Math.Max(1, Bottom - Top);
    }

    private static double PixelAlign(double v) => Math.Round(v) + 0.5;

    private PlotRect GetPlotRect(double w, double h)
    {
        double actualPadLeft = Math.Max(0, PadLeft - 10);
        double actualPadRight = PadRight + 6;
        double left = actualPadLeft;
        double right = Math.Max(left + 10, w - actualPadRight);
        double top = PadTop;
        double bottom = Math.Max(top + 10, h - PadBottom);
        return new PlotRect(left, top, right, bottom);
    }

    public MainWindow()
    {
        InitializeComponent();

        _hr.OnStatus += s => Dispatcher.Invoke(() => OnHrStatus(s));
        _hr.OnBpm += bpm => Dispatcher.Invoke(() =>
        {
            BpmText.Text = bpm.ToString();
            AddSample(bpm);
            TryRenderChart();
        });

        Loaded += async (_, __) =>
        {
            _settings = HudSettings.Load();
            ApplyHudSettings(_settings);

            UpdateGridMenuChecks();
            UpdateWindowMenuChecks();

            await ConnectFlowAsync(userInitiated: false);

            TryRenderChart(force: true);
        };

        Deactivated += (_, __) => SaveHudSettings();
        Closing += (_, __) => SaveHudSettings();

        Closed += async (_, __) => await _hr.DisconnectAsync();

        SizeChanged += (_, __) => TryRenderChart(force: true);
    }

    // ========= Toggle / Collapse =========

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplyCollapsedUi(_collapsed);
        ClampToWorkingArea();
        SaveHudSettings();
    }

    private void ApplyCollapsedUi(bool collapsed)
    {
        // 获取按钮内的箭头 Path
        var arrow = FindArrowPath();

        if (collapsed)
        {
            // 折叠：箭头向右 >
            if (arrow != null)
                arrow.Data = System.Windows.Media.Geometry.Parse("M 3,1 L 7,5 L 3,9");

            Width = 220;
            Height = 120;

            BpmText.FontSize = 40;
            // 缩小模式：数字再上移一点
            BpmText.Margin = new Thickness(0, -4, 0, 12);

            ChartHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 展开：箭头向下 v
            if (arrow != null)
                arrow.Data = System.Windows.Media.Geometry.Parse("M 1,3 L 5,7 L 9,3");

            Width = 340;
            Height = 250;

            BpmText.FontSize = 68;
            BpmText.Margin = new Thickness(0, 0, 0, 0);

            ChartHost.Visibility = Visibility.Visible;
            TryRenderChart(force: true);
        }
    }

    private System.Windows.Shapes.Path? FindArrowPath()
    {
        if (ToggleBtn.Template.FindName("arrow", ToggleBtn) is System.Windows.Shapes.Path p)
            return p;
        return null;
    }

    // ========= Samples =========

    private void AddSample(int bpm)
    {
        bpm = Math.Clamp(bpm, MinBpmFloor, MaxBpmCeil);
        var now = DateTime.UtcNow;
        _samples.Add((now, bpm));

        var cutoff = now.AddSeconds(-WindowSeconds);
        int idx = _samples.FindIndex(s => s.t >= cutoff);
        if (idx > 0) _samples.RemoveRange(0, idx);
    }

    private void TryRenderChart(bool force = false)
    {
        if (_collapsed) return;

        var now = DateTime.UtcNow;
        if (!force && (now - _lastRender).TotalMilliseconds < (1000.0 / TargetFps)) return;

        _lastRender = now;
        RenderChart();
    }


    // ========= Render =========

    private void RenderChart()
    {
        if (ChartCanvas == null || ChartHost == null) return;

        double w = Math.Max(0, ChartHost.ActualWidth);
        double h = Math.Max(0, ChartHost.ActualHeight);

        if (w <= 5 || h <= 5) return;

        // 让 Canvas 拥有布局尺寸，避免初始阶段 ActualHeight 为 0 导致元素靠下
        ChartCanvas.Width = w;
        ChartCanvas.Height = h;

        ChartCanvas.Children.Clear();

        var plot = GetPlotRect(w, h);

        // 空数据：显示 "NO DATA" 文字
        if (_samples.Count < 2)
        {
            var noDataText = new TextBlock
            {
                Text = "NO DATA",
                FontSize = 14,
                Foreground = Brushes.Gray,
                Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            noDataText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = noDataText.DesiredSize.Width;
            double th = noDataText.DesiredSize.Height;

            // 居中到 plot 区域，避免初始阶段文字偏下
            double cx = (plot.Left + plot.Right - tw) / 2;
            double cy = (plot.Top + plot.Bottom - th) / 2;

            Canvas.SetLeft(noDataText, cx);
            Canvas.SetTop(noDataText, cy);
            ChartCanvas.Children.Add(noDataText);
            return;
        }

        var now = DateTime.UtcNow;
        int winSec = WindowSeconds;
        var t0 = now.AddSeconds(-winSec);

        int minBpm = Math.Max(MinBpmFloor, _samples.Min(s => s.bpm));
        int maxBpm = Math.Min(MaxBpmCeil, _samples.Max(s => s.bpm));
        if (maxBpm <= minBpm) maxBpm = minBpm + 1;

        const int MinSpan = 12;
        int span = maxBpm - minBpm;
        if (span < MinSpan)
        {
            int mid = (minBpm + maxBpm) / 2;
            minBpm = mid - MinSpan / 2;
            maxBpm = mid + MinSpan / 2;
        }

        minBpm = Math.Max(MinBpmFloor, minBpm - 2);
        maxBpm = Math.Min(MaxBpmCeil, maxBpm + 2);
        if (maxBpm <= minBpm) maxBpm = minBpm + 1;

        double MapX(DateTime t)
        {
            double x = (t - t0).TotalSeconds / winSec * plot.Width;
            x = plot.Left + x;
            x = Math.Clamp(x, plot.Left, plot.Right);
            return PixelAlign(x);
        }

        double MapY(int bpm)
        {
            double norm = (bpm - minBpm) / (double)(maxBpm - minBpm);
            double y = plot.Bottom - norm * plot.Height;
            y = Math.Clamp(y, plot.Top, plot.Bottom);
            return PixelAlign(y);
        }

        if (_settings.GridMode == GridMinimal)
            DrawMinimalGrid(plot, minBpm, maxBpm, winSec);
        else if (_settings.GridMode == GridTicks)
            DrawTicksGrid(plot, minBpm, maxBpm, winSec);

        var poly = new Polyline
        {
            StrokeThickness = 2,
            Opacity = 0.90,
            Stroke = Brushes.White
        };

        foreach (var s in _samples)
            poly.Points.Add(new Point(MapX(s.t), MapY(s.bpm)));

        ChartCanvas.Children.Add(poly);

        var last = _samples[^1];
        double lx = MapX(last.t);
        double ly = MapY(last.bpm);

        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brushes.White,
            Opacity = 0.90
        };
        Canvas.SetLeft(dot, lx - 3);
        Canvas.SetTop(dot, ly - 3);
        dot.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        dot.RenderTransform = scale;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.90, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ChartCanvas.Children.Add(dot);

        // 去掉右侧小数字

        DrawPlotBorder(plot);
    }

    private void DrawPlotBorder(PlotRect plot)
    {
        // 底部时间轴线隐藏，仅保留数字
    }

    private void DrawRightLastValue(PlotRect plot, double canvasW, int bpm, double yAt)
    {
        var tb = new TextBlock
        {
            Text = bpm.ToString(),
            FontSize = 11,
            Foreground = Brushes.White,
            Opacity = 0.75
        };

        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = tb.DesiredSize.Width;
        double th = tb.DesiredSize.Height;

        double x = plot.Right + RightLabelGap;
        if (x + tw > canvasW - 2)
            x = Math.Max(plot.Right + 2, canvasW - 2 - tw);

        double y = Math.Clamp(yAt - th / 2, plot.Top, plot.Bottom - th);

        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        ChartCanvas.Children.Add(tb);
    }

    // ========= Grid drawing =========

    private void DrawMinimalGrid(PlotRect plot, int minBpm, int maxBpm, int winSec)
    {
        // 使用 20%/50%/80% 更容易读取极端值
        double[] fracs = { 0.20, 0.50, 0.80 };

        double lastLabelY = double.NaN;
        double actualPadLeft = Math.Max(0, PadLeft - 6);

        foreach (var f in fracs)
        {
            double y = plot.Bottom - f * plot.Height;
            y = PixelAlign(y);

            ChartCanvas.Children.Add(new Line
            {
                X1 = plot.Left,
                Y1 = y,
                X2 = plot.Right,
                Y2 = y,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Opacity = 0.14
            });

            int val = (int)Math.Round(minBpm + f * (maxBpm - minBpm));
            var lb = new TextBlock
            {
                Text = val.ToString(),
                FontSize = 10,
                Foreground = Brushes.Gray,
                Opacity = 0.40  // 稍微亮一点
            };

            lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lh = lb.DesiredSize.Height;

            double x = Math.Max(2, plot.Left - actualPadLeft + 2);
            // 允许标签超出 plot 区域一点，确保可见
            double ty = (y - 0.5) - lh / 2;

            if (!double.IsNaN(lastLabelY) && Math.Abs(ty - lastLabelY) < lh + 4)
                continue;

            Canvas.SetLeft(lb, x);
            Canvas.SetTop(lb, ty);
            ChartCanvas.Children.Add(lb);

            lastLabelY = ty;
        }

        int tickSec = winSec switch { 30 => 10, 60 => 15, _ => 30 };
        for (int sec = tickSec; sec < winSec; sec += tickSec)
        {
            double x = plot.Left + (sec / (double)winSec) * plot.Width;
            x = PixelAlign(x);

            ChartCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = plot.Top,
                X2 = x, Y2 = plot.Bottom,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Opacity = 0.06
            });
        }

        DrawTimeAxis(plot, winSec, dense: false);
    }

    private void DrawTicksGrid(PlotRect plot, int minBpm, int maxBpm, int winSec)
    {
        int range = Math.Max(1, maxBpm - minBpm);
        int step = range switch
        {
            <= 30 => 5,
            <= 60 => 10,
            <= 120 => 20,
            _ => 25
        };

        int start = (minBpm / step) * step;
        int end = ((maxBpm + step - 1) / step) * step;

        double lastLabelY = double.NaN;
        double actualPadLeft = Math.Max(0, PadLeft - 6);

        double MapYGrid(int bpm)
        {
            double norm = (bpm - minBpm) / (double)(maxBpm - minBpm);
            double y = plot.Bottom - norm * plot.Height;
            return y;
        }

        for (int bpm = start; bpm <= end; bpm += step)
        {
            double y = MapYGrid(bpm);
            if (y < plot.Top || y > plot.Bottom) continue;

            y = PixelAlign(y);

            ChartCanvas.Children.Add(new Line
            {
                X1 = plot.Left,
                Y1 = y,
                X2 = plot.Right,
                Y2 = y,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Opacity = 0.18,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });

            var label = new TextBlock
            {
                Text = bpm.ToString(),
                FontSize = 10,
                Foreground = Brushes.Gray,
                Opacity = 0.33
            };

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lh = label.DesiredSize.Height;

            double x = Math.Max(2, plot.Left - actualPadLeft + 2);
            double ty = (y - 0.5) - lh / 2;

            if (!double.IsNaN(lastLabelY) && Math.Abs(ty - lastLabelY) < lh + 4)
                continue;

            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, ty);
            ChartCanvas.Children.Add(label);

            lastLabelY = ty;
        }

        int tickSec = winSec switch { 30 => 5, 60 => 10, _ => 20 };
        for (int sec = tickSec; sec < winSec; sec += tickSec)
        {
            double x = plot.Left + (sec / (double)winSec) * plot.Width;
            x = PixelAlign(x);

            ChartCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = plot.Top,
                X2 = x, Y2 = plot.Bottom,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Opacity = 0.10
            });
        }

        DrawTimeAxis(plot, winSec, dense: true);
    }

    private void DrawTimeAxis(PlotRect plot, int winSec, bool dense)
    {
        int tickSec = dense
            ? (winSec switch { 30 => 5, 60 => 10, _ => 20 })
            : (winSec switch { 30 => 10, 60 => 15, _ => 30 });

        // 时间数字进一步下移，贴近底部，同时避免越界
        double yText = Math.Max(plot.Top + 2, Math.Min(ChartCanvas.ActualHeight - 10, plot.Bottom - 2));

        for (int sec = 0; sec <= winSec; sec += tickSec)
        {
            double x = plot.Left + (sec / (double)winSec) * plot.Width;

            int labelVal = winSec - sec;

            var t = new TextBlock
            {
                Text = labelVal.ToString(),
                FontSize = 10,
                Foreground = Brushes.Gray,
                Opacity = dense ? 0.28 : 0.22
            };

            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = t.DesiredSize.Width;

            double tx = Math.Clamp(x - tw / 2, plot.Left, plot.Right - tw);

            Canvas.SetLeft(t, tx);
            Canvas.SetTop(t, yText);
            ChartCanvas.Children.Add(t);
        }
    }

    // ========= Drag + settings =========

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SaveHudSettings();
    }

    private void ApplyHudSettings(HudSettings s)
    {
        _collapsed = s.Collapsed;
        ApplyCollapsedUi(_collapsed);

        if (s.Xpx != int.MinValue && s.Ypx != int.MinValue)
        {
            SetWindowPositionFromPixels(s.Xpx, s.Ypx);
            ClampToWorkingArea();
            return;
        }

        PlaceTopRightDefault();
        ClampToWorkingArea();
    }

    private void SaveHudSettings()
    {
        var rb = RestoreBounds;
        var (xpx, ypx) = DipsToPixels(rb.Left, rb.Top);

        _settings.Xpx = xpx;
        _settings.Ypx = ypx;
        _settings.Collapsed = _collapsed;

        _settings.Save();
    }

    private void PlaceTopRightDefault()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Top + 16;
    }

    private void ClampToWorkingArea()
    {
        var wa = SystemParameters.WorkArea;

        double maxX = wa.Right - Width;
        double maxY = wa.Bottom - Height;

        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;

        if (Left > maxX) Left = maxX;
        if (Top > maxY) Top = maxY;
    }

    private void SetWindowPositionFromPixels(int xpx, int ypx)
    {
        var (xdip, ydip) = PixelsToDips(xpx, ypx);
        Left = xdip;
        Top = ydip;
    }

    private (int xpx, int ypx) DipsToPixels(double xDip, double yDip)
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null)
            return ((int)Math.Round(xDip), (int)Math.Round(yDip));

        Matrix m = src.CompositionTarget.TransformToDevice;
        int xpx = (int)Math.Round(xDip * m.M11);
        int ypx = (int)Math.Round(yDip * m.M22);
        return (xpx, ypx);
    }

    private (double xDip, double yDip) PixelsToDips(int xPx, int yPx)
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null)
            return (xPx, yPx);

        Matrix m = src.CompositionTarget.TransformFromDevice;
        double xDip = xPx * m.M11;
        double yDip = yPx * m.M22;
        return (xDip, yDip);
    }

    // ========= Context menu =========

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_collapsed)
        {
            e.Handled = true;
            return;
        }

        UpdateGridMenuChecks();
        UpdateWindowMenuChecks();
    }

    private void GridOff_Click(object sender, RoutedEventArgs e) => SetGridMode(GridOff);
    private void GridMinimal_Click(object sender, RoutedEventArgs e) => SetGridMode(GridMinimal);
    private void GridTicks_Click(object sender, RoutedEventArgs e) => SetGridMode(GridTicks);

    private void SetGridMode(int mode)
    {
        _settings.GridMode = mode;
        _settings.Save();
        UpdateGridMenuChecks();
        TryRenderChart(force: true);
    }

    private void UpdateGridMenuChecks()
    {
        if (GridOffItem != null) GridOffItem.IsChecked = _settings.GridMode == GridOff;
        if (GridMinimalItem != null) GridMinimalItem.IsChecked = _settings.GridMode == GridMinimal;
        if (GridTicksItem != null) GridTicksItem.IsChecked = _settings.GridMode == GridTicks;
    }

    private void Win30_Click(object sender, RoutedEventArgs e) => SetWindowSeconds(30);
    private void Win60_Click(object sender, RoutedEventArgs e) => SetWindowSeconds(60);
    private void Win120_Click(object sender, RoutedEventArgs e) => SetWindowSeconds(120);

    private void SetWindowSeconds(int sec)
    {
        _settings.ChartWindowSeconds = sec;
        _settings.Save();
        UpdateWindowMenuChecks();
        TryRenderChart(force: true);
    }

    private void UpdateWindowMenuChecks()
    {
        int sec = WindowSeconds;
        if (Win30Item != null) Win30Item.IsChecked = sec == 30;
        if (Win60Item != null) Win60Item.IsChecked = sec == 60;
        if (Win120Item != null) Win120Item.IsChecked = sec == 120;
    }

    // ========= Connect / Status =========

    private void OnHrStatus(string raw)
    {
        if (_isConnecting && string.Equals(raw, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return;

        _lastStatus = raw;

        var (display, canRetry, showSpin) = MapStatus(raw);

        if (StatusBtn != null) StatusBtn.Content = display;

        if (Spinner != null) Spinner.Visibility = (_isConnecting || showSpin) ? Visibility.Visible : Visibility.Collapsed;

        bool isLive = string.Equals(raw, "LIVE", StringComparison.OrdinalIgnoreCase);
        if (StatusBtn != null)
        {
            StatusBtn.IsEnabled = canRetry && !_isConnecting && !isLive;
            StatusBtn.Cursor = StatusBtn.IsEnabled ? Cursors.Hand : Cursors.Arrow;
            StatusBtn.Foreground = isLive ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#99FFFFFF")!;
        }
    }

    private static (string display, bool canRetry, bool showSpinner) MapStatus(string raw)
    {
        if (raw == null) return ("INIT", true, false);

        string r = raw.Trim();

        if (r.Equals("LIVE", StringComparison.OrdinalIgnoreCase))
            return ("LIVE", false, false);

        if (r.Contains("CONNECT", StringComparison.OrdinalIgnoreCase))
            return ("CONNECTING", false, true);

        if (r.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return ("CONNECT / CLICK TO RETRY", true, false);

        if (r.Equals("DEVICE NULL", StringComparison.OrdinalIgnoreCase))
            return ("NO DEVICE / CLICK TO RETRY", true, false);

        if (r.Equals("NO HR SERVICE", StringComparison.OrdinalIgnoreCase))
            return ("NO HR SERVICE / CLICK TO RETRY", true, false);

        if (r.Equals("NO HR CHAR", StringComparison.OrdinalIgnoreCase))
            return ("NO HR CHAR / CLICK TO RETRY", true, false);

        if (r.StartsWith("SUB FAIL", StringComparison.OrdinalIgnoreCase))
            return ("SUB FAIL / CLICK TO RETRY", true, false);

        return ($"{r} / CLICK TO RETRY", true, false);
    }

    private async void StatusBtn_Click(object sender, RoutedEventArgs e)
    {
        await ConnectFlowAsync(userInitiated: true);
    }

    private async Task ConnectFlowAsync(bool userInitiated)
    {
        if (_isConnecting) return;

        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;

        _isConnecting = true;
        Dispatcher.Invoke(() => OnHrStatus("CONNECTING"));

        try
        {
            await _hr.DisconnectAsync();
            await Task.Delay(150, ct);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                await _hr.ConnectAndSubscribeAsync(Mac);
                await Task.Delay(450, ct);

                if (string.Equals(_lastStatus, "LIVE", StringComparison.OrdinalIgnoreCase))
                    break;

                await Task.Delay(800, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => OnHrStatus($"ERR: {ex.Message}"));
        }
        finally
        {
            _isConnecting = false;
            Dispatcher.Invoke(() => OnHrStatus(_lastStatus));
        }
    }
}

