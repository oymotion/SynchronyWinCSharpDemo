using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace SensorSdk.ExampleWinUI3.Controls;

/// <summary>Waveform view; pulls from a RingBuffer during Draw.</summary>
public sealed class WaveformControl : UserControl
{
    // Channel colors
    internal static readonly Color[] ChannelColors =
    [
        ColorHelper.FromArgb(255, 31, 119, 180), ColorHelper.FromArgb(255, 255, 127, 14),
        ColorHelper.FromArgb(255, 44, 160, 44), ColorHelper.FromArgb(255, 214, 39, 40),
        ColorHelper.FromArgb(255, 148, 103, 189), ColorHelper.FromArgb(255, 140, 86, 75),
        ColorHelper.FromArgb(255, 227, 119, 194), ColorHelper.FromArgb(255, 127, 127, 127),
    ];
    internal static readonly Color BackgroundColor = ColorHelper.FromArgb(255, 28, 28, 30);
    internal static readonly Color BorderColor = ColorHelper.FromArgb(255, 90, 90, 90);
    internal static readonly Color MidlineColor = ColorHelper.FromArgb(255, 60, 60, 60);
    internal static readonly Color PlaceholderColor = ColorHelper.FromArgb(255, 150, 150, 150);

    private const float SideMargin = 66;

    private readonly CanvasControl _canvas;

    private RingBuffer? _buffer;
    private object? _mutex;
    private int _channel = -1;
    private int _colorIndex = -1;
    private IReadOnlyList<string> _labels = [];
    private bool _fixedRange;
    private double _fixedLow = -1.0;
    private double _fixedHigh = 1.0;
    private string _placeholder = "Not connected";
    private string _sideText = string.Empty;
    private Color _sideColor = Microsoft.UI.Colors.White;

    public WaveformControl()
    {
        MinHeight = 48;
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        Content = _canvas;
    }

    public void Invalidate() => _canvas.Invalidate();

    /// <summary>channel == -1 draws all channels; colorIndex picks the curve color.</summary>
    public void SetSource(RingBuffer? buffer, object? mutex, int channel, int colorIndex = -1)
    {
        _buffer = buffer;
        _mutex = mutex;
        _channel = channel;
        _colorIndex = colorIndex;
        Invalidate();
    }

    public bool HasSource => _buffer != null;

    public void SetLabels(IReadOnlyList<string> labels)
    {
        _labels = labels;
        Invalidate();
    }

    public void SetFixedYRange(double low, double high)
    {
        _fixedRange = true;
        _fixedLow = low;
        _fixedHigh = high;
        Invalidate();
    }

    public void SetAutoYRange()
    {
        _fixedRange = false;
        Invalidate();
    }

    public void SetPlaceholder(string text)
    {
        _placeholder = text;
        Invalidate();
    }

    /// <summary>Right-margin text, drawn in the given color.</summary>
    public void SetSideText(string text, Color color)
    {
        _sideText = text;
        _sideColor = color;
        Invalidate();
    }

    private void DrawPlaceholder(CanvasDrawingSession ds, float x, float y, float w, float h)
    {
        if (string.IsNullOrEmpty(_placeholder))
            return;
        using var fmt = new CanvasTextFormat
        {
            FontSize = 12,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        ds.DrawText(_placeholder, new Windows.Foundation.Rect(x, y, w, h), PlaceholderColor, fmt);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        var size = new Vector2((float)sender.ActualWidth, (float)sender.ActualHeight);
        ds.Clear(BackgroundColor);
        if (size.X < 4 || size.Y < 4)
            return;

        // Plot rect
        float px = 2, py = 2;
        float pw = size.X - 2 - SideMargin - 2;
        float ph = size.Y - 4;
        if (pw < 2)
            return;
        ds.DrawRectangle(new Windows.Foundation.Rect(px, py, pw, ph), BorderColor, 1);
        ds.DrawLine(new Vector2(px, py + ph / 2), new Vector2(px + pw, py + ph / 2), MidlineColor, 1);

        RingBuffer? buf = _buffer;
        object? mutex = _mutex;
        if (buf == null || mutex == null)
        {
            DrawPlaceholder(ds, px, py, pw, ph);
            return;
        }

        lock (mutex)
        {
            if (!buf.Allocated || buf.Length < 2)
            {
                DrawPlaceholder(ds, px, py, pw, ph);
                return;
            }

            var channels = new List<int>();
            if (_channel >= 0)
            {
                if (_channel < buf.Channels)
                    channels.Add(_channel);
            }
            else
            {
                for (int ch = 0; ch < buf.Channels; ch++)
                    channels.Add(ch);
            }
            if (channels.Count == 0)
            {
                DrawPlaceholder(ds, px, py, pw, ph);
                return;
            }

            int w = (int)pw;
            int len = buf.Length;

            // Y range
            double low = _fixedLow;
            double high = _fixedHigh;
            if (!_fixedRange)
            {
                double mn = double.MaxValue;
                double mx = double.MinValue;
                foreach (int ch in channels)
                {
                    float[] samples = buf.Samples[ch];
                    int step = Math.Max(1, len / (w * 2));
                    for (int i = 0; i < len; i += step)
                    {
                        double v = samples[(buf.WriteIndex + i) % len];
                        mn = Math.Min(mn, v);
                        mx = Math.Max(mx, v);
                    }
                }
                if (mn > mx)
                {
                    mn = -1.0;
                    mx = 1.0;
                }
                double margin = Math.Max((mx - mn) * 0.1, 0.01);
                if (mn == mx)
                {
                    mn -= 1.0;
                    mx += 1.0;
                    margin = 0.0;
                }
                low = mn - margin;
                high = mx + margin;
            }
            double span = high - low;

            int labelRow = 0;
            foreach (int ch in channels)
            {
                int colorIdx = _colorIndex >= 0 ? _colorIndex : ch;
                Color color = ChannelColors[colorIdx % ChannelColors.Length];
                float[] samples = buf.Samples[ch];
                using (var builder = new CanvasPathBuilder(sender))
                {
                    builder.BeginFigure(new Vector2(px, py));
                    for (int x = 0; x < w; x++)
                    {
                        int si = (int)((long)x * len / w);
                        double v = samples[(buf.WriteIndex + si) % len];
                        double ty = py + ph - 1 - (v - low) / span * (ph - 2);
                        builder.AddLine(new Vector2(px + x, (float)ty));
                    }
                    builder.EndFigure(CanvasFigureLoop.Open);
                    using CanvasGeometry geo = CanvasGeometry.CreatePath(builder);
                    ds.DrawGeometry(geo, color, 1);
                }

                string label = labelRow < _labels.Count ? _labels[labelRow] : $"ch{ch}";
                using var fmt = new CanvasTextFormat { FontSize = 11 };
                ds.DrawText(label, new Vector2(px + 4, py + 1 + labelRow * 13), color, fmt);
                labelRow++;
            }
        }

        if (_sideText.Length > 0)
        {
            using var fmt = new CanvasTextFormat { FontSize = 11 };
            ds.DrawText(_sideText,
                new Windows.Foundation.Rect(size.X - SideMargin + 4, 0, SideMargin - 6, size.Y),
                _sideColor, fmt);
        }
    }
}
