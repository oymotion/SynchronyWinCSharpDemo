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

/// <summary>FFT spectrum strip view.</summary>
public sealed class SpectrumControl : UserControl
{
    private readonly CanvasControl _canvas;

    private float[] _freqs = [];
    private List<float[]> _mags = new();
    private IReadOnlyList<string> _labels = [];
    private string _placeholder = "Not connected";

    public SpectrumControl()
    {
        MinHeight = 48;
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        Content = _canvas;
    }

    public void Invalidate() => _canvas.Invalidate();

    public void SetResult(float[] freqs, List<float[]> mags)
    {
        _freqs = freqs;
        _mags = mags;
        Invalidate();
    }

    public void ClearResult()
    {
        _freqs = [];
        _mags = new List<float[]>();
        Invalidate();
    }

    public void SetLabels(IReadOnlyList<string> labels)
    {
        _labels = labels;
        Invalidate();
    }

    public void SetPlaceholder(string text)
    {
        _placeholder = text;
        Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        ds.Clear(WaveformControl.BackgroundColor);
        if (w < 4 || h < 18)
            return;

        // Bottom axis-text strip
        float px = 2, py = 2;
        float pw = w - 4, ph = h - 2 - 14 - 2;
        ds.DrawRectangle(new Windows.Foundation.Rect(px, py, pw, ph), WaveformControl.BorderColor, 1);

        if (_freqs.Length < 2 || _mags.Count == 0)
        {
            using var fmt = new CanvasTextFormat
            {
                FontSize = 12,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            ds.DrawText(_placeholder, new Windows.Foundation.Rect(px, py, pw, ph),
                WaveformControl.PlaceholderColor, fmt);
            return;
        }

        double fMax = _freqs[^1];
        if (fMax <= 0.0 || pw < 2)
            return;

        // Y range
        double peak = 0.0;
        foreach (float[] row in _mags)
            foreach (float v in row)
                peak = Math.Max(peak, v);
        double yMax = peak > 0.0 ? peak * 1.1 : 1.0;

        int labelRow = 0;
        for (int ch = 0; ch < _mags.Count; ch++)
        {
            float[] row = _mags[ch];
            Color color = WaveformControl.ChannelColors[ch % WaveformControl.ChannelColors.Length];
            using (var builder = new CanvasPathBuilder(sender))
            {
                builder.BeginFigure(new Vector2(px, py + ph));
                for (int i = 0; i < row.Length && i < _freqs.Length; i++)
                {
                    double x = px + _freqs[i] / fMax * (pw - 1);
                    double y = py + ph - 1 - row[i] / yMax * (ph - 2);
                    builder.AddLine(new Vector2((float)x, (float)y));
                }
                builder.EndFigure(CanvasFigureLoop.Open);
                using CanvasGeometry geo = CanvasGeometry.CreatePath(builder);
                ds.DrawGeometry(geo, color, 1);
            }

            string label = labelRow < _labels.Count ? _labels[labelRow] : $"ch{ch}";
            using var lfmt = new CanvasTextFormat { FontSize = 11 };
            ds.DrawText(label, new Vector2(px + 4, py + 1 + labelRow * 13), color, lfmt);
            labelRow++;
        }

        // Frequency axis labels
        using var afmt = new CanvasTextFormat { FontSize = 10 };
        ds.DrawText("0", new Windows.Foundation.Rect(px + 2, py + ph + 1, 40, 12),
            WaveformControl.PlaceholderColor, afmt);
        using var afmt2 = new CanvasTextFormat
        {
            FontSize = 10,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
        };
        ds.DrawText($"{fMax:F1} Hz", new Windows.Foundation.Rect(w - 100 - 2, py + ph + 1, 100, 12),
            WaveformControl.PlaceholderColor, afmt2);
    }
}
