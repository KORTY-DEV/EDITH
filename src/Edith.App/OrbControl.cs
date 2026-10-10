using System.Windows;
using System.Windows.Media;
using Edith.Core;

namespace Edith.App;

/// <summary>
/// EDITH's presence on screen: a living orb drawn every frame. A soft glow, three blobs whose outline
/// breathes and wobbles, and per state: listening (reacts to the microphone), thinking (arcs circling
/// it), speaking (ripples with the voice's rhythm), warning (red pulse). On activation a ring flies
/// out and the orb springs in; on rest it shrinks away. Drawn by hand (no effects, no bitmaps):
/// cheap enough to run only while visible.
/// </summary>
public sealed class OrbControl : FrameworkElement
{
    public const double Size = 170;

    private const int Points = 56;
    private const double BaseRadius = 24;

    private static readonly Color Cyan = Color.FromRgb(85, 223, 255);
    private static readonly Color Amber = Color.FromRgb(255, 193, 92);
    private static readonly Color Sky = Color.FromRgb(168, 214, 255);
    private static readonly Color Red = Color.FromRgb(255, 85, 95);

    private AssistantState _state = AssistantState.Listening;
    private double _r = Cyan.R, _g = Cyan.G, _b = Cyan.B; // current colour, eased towards the state's
    private double _smoothLevel;
    private double _phase;      // accumulated, so a change of speed bends the motion instead of jumping it
    private double _peakIn = 0.03, _peakOut = 0.05; // running loudness peaks: the orb moves the same for a quiet mic and a loud one
    private double _smoothOut;
    private volatile float _level;
    private double _burstStart = double.NegativeInfinity;
    private double _closeStart = double.NegativeInfinity;
    private Action? _closed;
    private double _lastT;
    private readonly DateTime _origin = DateTime.UtcNow;
    private bool _running;

    public OrbControl()
    {
        Width = Size;
        Height = Size;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    private volatile float _outLevel;

    /// <summary>Microphone loudness 0..1 (thread-safe), shown while listening.</summary>
    public float Level
    {
        set => _level = value;
    }

    /// <summary>Loudness of her own voice 0..1 (thread-safe), shown while speaking.</summary>
    public float OutputLevel
    {
        set => _outLevel = value;
    }

    private int _framesDrawn;
    private double _fpsSince;

    public void SetState(AssistantState state)
    {
        _state = state;
        _closeStart = double.NegativeInfinity;
        _closed = null;
    }

    /// <summary>The activation: a ring flies out and the orb springs up.</summary>
    public void Burst() => _burstStart = Now();

    /// <summary>Shrinks the orb away, then calls <paramref name="done"/>.</summary>
    public void Close(Action done)
    {
        _closeStart = Now();
        _closed = done;
    }

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private double Now() => (DateTime.UtcNow - _origin).TotalSeconds;

    private TimeSpan _lastFrame;

    private void OnFrame(object? sender, EventArgs e)
    {
        // 30 frames a second are enough for a soft glow and half the cost of 60.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime - _lastFrame < TimeSpan.FromMilliseconds(14))
                return;
            _lastFrame = args.RenderingTime;
        }

        InvalidateVisual();
        if (_closeStart > double.NegativeInfinity && Now() - _closeStart >= CloseSeconds && _closed is { } done)
        {
            _closed = null;
            done();
        }
    }

    private const double CloseSeconds = 0.22;

    /// <summary>Renders one frame at a given moment, for previews and tests.</summary>
    public void RenderAt(DrawingContext dc, double t, AssistantState state, double level, double burstAgo = double.PositiveInfinity)
    {
        _state = state;
        _level = (float)level;
        _burstStart = double.IsPositiveInfinity(burstAgo) ? double.NegativeInfinity : t - burstAgo;
        _closeStart = double.NegativeInfinity;
        Draw(dc, t);
    }

    protected override void OnRender(DrawingContext dc) => Draw(dc, Now());

    private void Draw(DrawingContext dc, double t)
    {
        // Real frame rate, written now and then (data/diagnostics/orb-fps.txt) so a slow orb is a number, not a feeling.
        _framesDrawn++;
        if (_fpsSince == 0)
            _fpsSince = t;
        else if (t - _fpsSince >= 2.5)
        {
            try
            {
                System.IO.File.AppendAllText(@"C:\EDITH\data\diagnostics\orb-fps.txt", $"{DateTime.Now:HH:mm:ss} {_framesDrawn / (t - _fpsSince):F1} fps ({_state})" + Environment.NewLine);
            }
            catch
            {
            }

            _framesDrawn = 0;
            _fpsSince = t;
        }

        double dt = Math.Clamp(t - _lastT, 0, 0.1);
        _lastT = t;

        Color target = _state switch
        {
            AssistantState.Thinking => Amber,
            AssistantState.Speaking => Sky,
            AssistantState.Warning => Red,
            _ => Cyan,
        };
        double ease = 1 - Math.Exp(-dt * 9);
        _r += (target.R - _r) * ease;
        _g += (target.G - _g) * ease;
        _b += (target.B - _b) * ease;
        var color = Color.FromRgb((byte)_r, (byte)_g, (byte)_b);

        // Loudness is smoothed: fast up, slow down (like a level meter).
        // Auto gain: speech sits near the recent peak whatever the microphone's volume; the noise floor stays low.
        double rawIn = _level;
        _peakIn = Math.Max(0.02, Math.Max(rawIn, _peakIn * Math.Exp(-dt * 0.35)));
        double level = Math.Clamp(Math.Sqrt(Math.Max(0, rawIn - 0.004) / _peakIn), 0, 1);
        _smoothLevel += (level - _smoothLevel) * (level > _smoothLevel ? Math.Min(1, dt * 16) : Math.Min(1, dt * 6));

        // How big the outline's wobble is, and how fast the orb breathes, per state.
        double wobble, breathe, rate;
        switch (_state)
        {
            case AssistantState.Listening:
                wobble = 0.06 + 0.42 * _smoothLevel;
                breathe = 0.03 * Math.Sin(t * 2.4) + 0.05 * _smoothLevel;
                rate = 1.0 + 0.8 * _smoothLevel;
                break;
            case AssistantState.Thinking:
                wobble = 0.09;
                breathe = 0.06 * Math.Sin(t * 5.2);
                rate = 1.8;
                break;
            case AssistantState.Speaking:
                // The real loudness of the voice, gently smoothed: the playback meter reports 20 times a second, and a
                // fast follower made the orb shake. A calm swell with the words, never a jitter.
                double rawOut = _outLevel;
                _peakOut = Math.Max(0.05, Math.Max(rawOut, _peakOut * Math.Exp(-dt * 0.25)));
                double voice = rawOut > 0.002f ? Math.Clamp(rawOut / _peakOut, 0, 1) : 0.30 + 0.10 * Math.Sin(t * 3.0);
                _smoothOut += (voice - _smoothOut) * Math.Min(1, dt * (voice > _smoothOut ? 7 : 3.5));
                wobble = 0.08 + 0.20 * _smoothOut;
                breathe = 0.04 * _smoothOut;
                rate = 1.0 + 0.35 * _smoothOut;
                break;
            case AssistantState.Warning:
                wobble = 0.1;
                breathe = 0.09 * Math.Sin(t * 8);
                rate = 2.2;
                break;
            default:
                wobble = 0.07;
                breathe = 0.04 * Math.Sin(t * 2.4);
                rate = 1.0;
                break;
        }

        _phase += dt * rate;

        // Entrance (spring) and exit (shrink).
        double scale = 1;
        double burstAge = t - _burstStart;
        if (burstAge is >= 0 and < 0.6)
            scale = EaseOutBack(burstAge / 0.6, 1.9);
        else if (burstAge is >= 0 and < 1.2)
            scale = 1;
        if (_closeStart > double.NegativeInfinity)
        {
            double p = Math.Clamp((t - _closeStart) / CloseSeconds, 0, 1);
            scale *= 1 - p * p;
        }

        var center = new Point(Size / 2, Size / 2);
        double radius = BaseRadius * scale * (1 + breathe);
        if (radius < 0.5)
            return;

        // Glow behind everything.
        double glowAlpha = _state == AssistantState.Speaking ? 0.45 : 0.32;
        Brush glow = Radial(1L << 60 | (long)(glowAlpha * 100) << 32 | (long)color.R << 16 | (long)color.G << 8 | color.B,
            Color.FromArgb((byte)(255 * glowAlpha), color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), false);
        double glowRadius = radius * (2.4 + 1.2 * wobble);
        dc.DrawEllipse(glow, null, center, glowRadius, glowRadius);

        // Three blobs: outer pale, middle, inner solid.
        for (int layer = 0; layer < 3; layer++)
        {
            double size = layer switch { 0 => 1.22, 1 => 1.06, _ => 0.9 };
            byte alpha = layer switch { 0 => 70, 1 => 125, _ => 235 };
            double phase = layer * 2.1;
            Geometry shape = Blob(center, radius * size, wobble * (1.0 - layer * 0.12), _phase, phase);

            Color light = Lighten(color, layer == 2 ? 0.55 : 0.2);
            Brush fill = Radial(2L << 60 | (long)layer << 40 | (long)color.R << 16 | (long)color.G << 8 | color.B,
                Color.FromArgb(alpha, light.R, light.G, light.B), Color.FromArgb(alpha, color.R, color.G, color.B), true);
            dc.DrawGeometry(fill, null, shape);
        }

        // A small specular highlight.
        dc.DrawEllipse(Solid(Color.FromArgb(110, 255, 255, 255)), null,
            new Point(center.X - radius * 0.28, center.Y - radius * 0.32), radius * 0.2, radius * 0.13);

        if (_state == AssistantState.Thinking)
            DrawArcs(dc, center, radius * 1.62, color, t);
        else if (_state == AssistantState.Speaking)
            DrawRipples(dc, center, radius, color, t);

        DrawBurst(dc, center, radius, color, burstAge);
    }

    // Brushes and pens are made once per colour: a steady state allocates nothing per frame.
    private readonly Dictionary<long, Brush> _brushes = [];
    private readonly Dictionary<long, Pen> _pens = [];

    private Brush Solid(Color c)
    {
        long key = (long)c.A << 24 | (long)c.R << 16 | (long)c.G << 8 | c.B;
        if (!_brushes.TryGetValue(key, out Brush? brush))
        {
            if (_brushes.Count > 400)
                _brushes.Clear();
            brush = new SolidColorBrush(c);
            brush.Freeze();
            _brushes[key] = brush;
        }

        return brush;
    }

    private Pen PenOf(Color c, double thickness, bool round = false)
    {
        long key = ((long)c.A << 24 | (long)c.R << 16 | (long)c.G << 8 | c.B) ^ ((long)(thickness * 10) << 32) ^ (round ? 1L << 50 : 0);
        if (!_pens.TryGetValue(key, out Pen? pen))
        {
            if (_pens.Count > 400)
                _pens.Clear();
            pen = new Pen(Solid(c), thickness) { StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat, EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat };
            pen.Freeze();
            _pens[key] = pen;
        }

        return pen;
    }

    private Brush Radial(long key, Color inner, Color outer, bool offsetLight)
    {
        if (!_brushes.TryGetValue(key, out Brush? brush))
        {
            if (_brushes.Count > 400)
                _brushes.Clear();
            var gradient = offsetLight
                ? new RadialGradientBrush(inner, outer) { GradientOrigin = new Point(0.38, 0.32), Center = new Point(0.5, 0.5) }
                : new RadialGradientBrush(inner, outer);
            gradient.Freeze();
            brush = gradient;
            _brushes[key] = brush;
        }

        return brush;
    }

    private static Geometry Blob(Point center, double radius, double wobble, double t, double phase)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int i = 0; i < Points; i++)
            {
                double a = 2 * Math.PI * i / Points;
                double bump = 0.55 * Math.Sin(2 * a + t * 1.7 + phase)
                            + 0.30 * Math.Sin(3 * a - t * 2.3 + phase * 1.3)
                            + 0.15 * Math.Sin(5 * a + t * 3.1 - phase);
                double rr = radius * (1 + wobble * bump);
                var p = new Point(center.X + rr * Math.Cos(a), center.Y + rr * Math.Sin(a));
                if (i == 0)
                    context.BeginFigure(p, true, true);
                else
                    context.LineTo(p, true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawArcs(DrawingContext dc, Point center, double radius, Color color, double t)
    {
        Pen pen = PenOf(Color.FromArgb(230, color.R, color.G, color.B), 3, round: true);
        for (int i = 0; i < 3; i++)
        {
            double start = t * 3.6 + i * 2 * Math.PI / 3;
            double sweep = 0.9 + 0.35 * Math.Sin(t * 2.1 + i);
            var figure = new StreamGeometry();
            using (StreamGeometryContext context = figure.Open())
            {
                context.BeginFigure(PointOn(center, radius, start), false, false);
                context.ArcTo(PointOn(center, radius, start + sweep), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }

            figure.Freeze();
            dc.DrawGeometry(null, pen, figure);
        }
    }

    private void DrawRipples(DrawingContext dc, Point center, double radius, Color color, double t)
    {
        for (int i = 0; i < 3; i++)
        {
            double p = (t * 0.7 + i / 3.0) % 1.0;
            double rr = radius * (1.25 + 1.5 * p);
            byte alpha = (byte)(150 * (1 - p) * (1 - p));
            dc.DrawEllipse(null, PenOf(Color.FromArgb((byte)(alpha / 8 * 8), color.R, color.G, color.B), Math.Round(2 * (1 - p) * 2) / 2 + 0.5), center, rr, rr);
        }
    }

    private void DrawBurst(DrawingContext dc, Point center, double radius, Color color, double age)
    {
        for (int ring = 0; ring < 2; ring++)
        {
            double p = (age - ring * 0.12) / 0.75;
            if (p is <= 0 or >= 1)
                continue;

            double eased = 1 - Math.Pow(1 - p, 3);
            double rr = radius * (1.1 + 2.1 * eased);
            byte alpha = (byte)(230 * (1 - p));
            Color c = Lighten(color, 0.35);
            dc.DrawEllipse(null, PenOf(Color.FromArgb((byte)(alpha / 8 * 8), c.R, c.G, c.B), Math.Round(3.2 * (1 - p) * 2) / 2 + 0.6), center, rr, rr);
        }

        // Sparks fly out with the rings: eight small points, each fading as it goes.
        if (age is >= 0 and < 0.7)
        {
            double q = age / 0.7, fly = 1 - Math.Pow(1 - q, 3);
            for (int k = 0; k < 8; k++)
            {
                double angle = k * Math.PI / 4 + 0.35 * Math.Sin(k * 2.7);
                double dist = radius * (1.2 + (1.6 + 0.5 * (k % 3)) * fly);
                byte a = (byte)(210 * (1 - q) * (1 - q));
                Color sc = Lighten(color, 0.6);
                dc.DrawEllipse(Solid(Color.FromArgb((byte)(a / 8 * 8), sc.R, sc.G, sc.B)), null,
                    new Point(center.X + dist * Math.Cos(angle), center.Y + dist * Math.Sin(angle)), 2.4 * (1 - q) + 0.4, 2.4 * (1 - q) + 0.4);
            }
        }
    }

    private static Point PointOn(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    private static Color Lighten(Color c, double amount) =>
        Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));

    /// <summary>0 → 0, 1 → 1 with a small overshoot in between (a spring).</summary>
    private static double EaseOutBack(double x, double overshoot)
    {
        x = Math.Clamp(x, 0, 1);
        double c3 = overshoot + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + overshoot * Math.Pow(x - 1, 2);
    }
}
