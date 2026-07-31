using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Services;
using Brush = System.Windows.Media.Brush;

namespace WindowsLosslessSwitcher.Controls;

/// <summary>
/// The live brand mark: six frequency bars following Apple Music's audio (and only Apple
/// Music's — the capture pipeline is process-scoped). When inactive it renders a static bar
/// arrangement echoing the Phosphor waveform glyph at 45% opacity, so this control simply
/// replaces the static icon in the title bar and nav tile. Rendering is a plain OnRender at
/// ~30 fps per instance while visible; the shared pipeline is refcounted by visibility leases,
/// so hidden windows cost zero CPU.
/// </summary>
public sealed class MiniSpectrograph : FrameworkElement
{
    private static readonly float[] IdleBars = [0.35f, 0.75f, 0.5f, 0.95f, 0.55f, 0.3f];
    private static readonly Brush BarBrush = CreateFrozen(0xFF, 0x91, 0x84, 0xD9);
    private static readonly Brush TipBrush = CreateFrozen(0xFF, 0xD2, 0xCE, 0xFD);

    private readonly DispatcherTimer _timer;
    private readonly float[] _displayed = (float[])IdleBars.Clone();
    private ISpectrumSource? _source;
    private IDisposable? _lease;
    private double _activity; // 0 = idle glyph, 1 = live; lerped for the ~250 ms transition

    public MiniSpectrograph()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += (_, _) => OnFrame();
        Loaded += (_, _) => UpdateLease();
        Unloaded += (_, _) => UpdateLease();
        IsVisibleChanged += (_, _) => UpdateLease();
    }

    /// <summary>Wires the shared pipeline; called once from window code-behind.</summary>
    public void Attach(ISpectrumSource source)
    {
        _source = source;
        UpdateLease();
    }

    private void UpdateLease()
    {
        var shouldHold = _source is not null && IsLoaded && IsVisible;
        if (shouldHold && _lease is null)
        {
            _lease = _source!.AcquireVisibleLease();
            _timer.Start();
        }
        else if (!shouldHold && _lease is not null)
        {
            _timer.Stop();
            _lease.Dispose();
            _lease = null;
            _activity = 0;
            IdleBars.CopyTo(_displayed, 0);
        }
    }

    private void OnFrame()
    {
        var source = _source;
        var active = source?.IsActive == true;
        var targets = active ? source!.CurrentBars : IdleBars;
        var targetActivity = active ? 1.0 : 0.0;
        _activity += (targetActivity - _activity) * 0.15;

        var changed = false;
        for (var i = 0; i < _displayed.Length && i < targets.Length; i++)
        {
            var target = Math.Clamp(targets[i], 0f, 1f);
            var next = _displayed[i] + (target - _displayed[i]) * 0.28f;
            if (Math.Abs(next - _displayed[i]) > 0.001f)
            {
                _displayed[i] = next;
                changed = true;
            }
        }

        if (changed || Math.Abs((active ? 1 : 0) - _activity) > 0.01)
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var barCount = _displayed.Length;
        var gap = Math.Max(1.0, width * 0.06);
        var barWidth = (width - gap * (barCount - 1)) / barCount;
        if (barWidth <= 0)
        {
            return;
        }

        // Idle glyph sits at 45% opacity; live bars fade up to full.
        var opacity = 0.45 + 0.55 * Math.Clamp(_activity, 0, 1);
        drawingContext.PushOpacity(opacity);

        for (var i = 0; i < barCount; i++)
        {
            var value = Math.Clamp(_displayed[i], 0.06f, 1f);
            var barHeight = Math.Max(2.0, value * height);
            var x = i * (barWidth + gap);
            var y = height - barHeight;
            var radius = Math.Min(1.0, barWidth / 2);
            drawingContext.DrawRoundedRectangle(BarBrush, null, new Rect(x, y, barWidth, barHeight), radius, radius);

            // Accent-light tip on tall live bars.
            if (_activity > 0.5 && value > 0.55)
            {
                var tipHeight = Math.Min(2.0, barHeight);
                drawingContext.DrawRoundedRectangle(TipBrush, null, new Rect(x, y, barWidth, tipHeight), radius, radius);
            }
        }

        drawingContext.Pop();
    }

    private static Brush CreateFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
