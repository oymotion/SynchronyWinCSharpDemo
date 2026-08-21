using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using Windows.UI;

namespace SensorSdk.ExampleWinUI3.Controls;

/// <summary>3D quaternion cube view.</summary>
public sealed class CubeControl : UserControl
{
    // Unit cube vertices
    private static readonly double[,] Vertices =
    {
        {-1, -1, -1}, {1, -1, -1}, {1, 1, -1}, {-1, 1, -1},
        {-1, -1, 1}, {1, -1, 1}, {1, 1, 1}, {-1, 1, 1},
    };
    // Faces as vertex index quads
    private static readonly int[,] Faces =
    {
        {0, 1, 2, 3}, {4, 5, 6, 7}, {0, 1, 5, 4},
        {2, 3, 7, 6}, {0, 3, 7, 4}, {1, 2, 6, 5},
    };
    private static readonly Color[] FaceColors =
    [
        ColorHelper.FromArgb(255, 0, 180, 180), ColorHelper.FromArgb(255, 200, 60, 200),
        ColorHelper.FromArgb(255, 220, 200, 40), ColorHelper.FromArgb(255, 200, 60, 60),
        ColorHelper.FromArgb(255, 60, 160, 60), ColorHelper.FromArgb(255, 60, 100, 220),
    ];
    private const double CameraDist = 4.5;

    private readonly CanvasControl _canvas;

    private readonly double[] _quat = [1.0, 0.0, 0.0, 0.0];
    private bool _hasQuaternion;
    private string _placeholder = "Not connected";

    public CubeControl()
    {
        MinHeight = 160;
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        Content = _canvas;
    }

    public void Invalidate() => _canvas.Invalidate();

    public void SetQuaternion(double w, double x, double y, double z)
    {
        _quat[0] = w;
        _quat[1] = x;
        _quat[2] = y;
        _quat[3] = z;
        _hasQuaternion = true;
        Invalidate();
    }

    public void ClearQuaternion()
    {
        _hasQuaternion = false;
        _quat[0] = 1.0;
        _quat[1] = _quat[2] = _quat[3] = 0.0;
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
        float width = (float)sender.ActualWidth, height = (float)sender.ActualHeight;
        ds.Clear(WaveformControl.BackgroundColor);

        if (!_hasQuaternion)
        {
            using var fmt = new CanvasTextFormat
            {
                FontSize = 12,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            ds.DrawText(_placeholder, new Windows.Foundation.Rect(0, 0, width, height),
                WaveformControl.PlaceholderColor, fmt);
            return;
        }

        // Rotation matrix from the quaternion
        double w = _quat[0], x = _quat[1], y = _quat[2], z = _quat[3];
        double norm = Math.Sqrt(w * w + x * x + y * y + z * z);
        if (norm <= 0.0)
            return;
        w /= norm; x /= norm; y /= norm; z /= norm;
        double[,] R =
        {
            {1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)},
            {2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)},
            {2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)},
        };

        double scale = Math.Min(width, height) * 0.28;
        var center = new Vector2(width / 2.0f, height / 2.0f);

        double[] Rotate(double vx, double vy, double vz)
        {
            return
            [
                R[0, 0] * vx + R[0, 1] * vy + R[0, 2] * vz,
                R[1, 0] * vx + R[1, 1] * vy + R[1, 2] * vz,
                R[2, 0] * vx + R[2, 1] * vy + R[2, 2] * vz,
            ];
        }
        Vector2 Project(double vx, double vy, double vz)
        {
            double persp = CameraDist / (CameraDist + vz);
            return new Vector2((float)(center.X + vx * scale * persp),
                               (float)(center.Y - vy * scale * persp));
        }

        var rotated = new double[8][];
        for (int i = 0; i < 8; i++)
            rotated[i] = Rotate(Vertices[i, 0], Vertices[i, 1], Vertices[i, 2]);

        // Face draw order
        var order = new int[] { 0, 1, 2, 3, 4, 5 };
        Array.Sort(order, (a, b) =>
        {
            double za = 0, zb = 0;
            for (int k = 0; k < 4; k++)
            {
                za += rotated[Faces[a, k]][2];
                zb += rotated[Faces[b, k]][2];
            }
            return zb.CompareTo(za);
        });

        var borderColor = ColorHelper.FromArgb(255, 20, 20, 20);
        foreach (int f in order)
        {
            using var builder = new CanvasPathBuilder(sender);
            builder.BeginFigure(Project(rotated[Faces[f, 0]][0], rotated[Faces[f, 0]][1], rotated[Faces[f, 0]][2]));
            for (int k = 1; k < 4; k++)
            {
                double[] v = rotated[Faces[f, k]];
                builder.AddLine(Project(v[0], v[1], v[2]));
            }
            builder.EndFigure(CanvasFigureLoop.Closed);
            using CanvasGeometry geo = CanvasGeometry.CreatePath(builder);
            ds.FillGeometry(geo, FaceColors[f]);
            ds.DrawGeometry(geo, borderColor, 1);
        }

        // Body axes
        double[,] axes = { {1.5, 0, 0}, {0, 1.5, 0}, {0, 0, 1.5} };
        Color[] axisColors =
        [
            ColorHelper.FromArgb(255, 220, 60, 60), ColorHelper.FromArgb(255, 60, 200, 60),
            ColorHelper.FromArgb(255, 80, 120, 255),
        ];
        Vector2 origin = Project(0, 0, 0);
        for (int a = 0; a < 3; a++)
        {
            double[] tip = Rotate(axes[a, 0], axes[a, 1], axes[a, 2]);
            ds.DrawLine(origin, Project(tip[0], tip[1], tip[2]), axisColors[a], 2);
        }
    }
}
