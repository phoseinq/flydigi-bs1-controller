using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Bs1Cooler.App;

/// The tray icon with a green leaf badge, shown while quiet mode is on. Drawn at startup from the normal icon.
public static class QuietIcon
{
    public static readonly Color Green = Color.Parse("#2DB467");
    // a leaf pointing up, in a -1..1 box: two curves meeting at the tips, a short stem below
    private static readonly Geometry Leaf = Geometry.Parse("M0,-1 C0.75,-0.55 0.75,0.45 0,0.8 C-0.75,0.45 -0.75,-0.55 0,-1 Z");

    public static Bitmap Draw()
    {
        using var s = AssetLoader.Open(new Uri("avares://Bs1Cooler/Assets/cooler.ico"));
        var icon = new Bitmap(s);
        const int n = 64;
        var bmp = new RenderTargetBitmap(new PixelSize(n, n));
        using (var dc = bmp.CreateDrawingContext())
        {
            dc.DrawImage(icon, new Rect(0, 0, n, n));
            double r = n * 0.27, c = n - r - 1;
            // dark ring keeps the badge apart from the icon at 16 px
            dc.DrawEllipse(new SolidColorBrush(Green), new Pen(new SolidColorBrush(Color.Parse("#202020")), n * 0.05), new Point(c, c), r, r);
            using (dc.PushTransform(Matrix.CreateScale(r * 0.62, r * 0.62) * Matrix.CreateRotation(Math.PI * 40 / 180) * Matrix.CreateTranslation(c, c)))
            {
                dc.DrawGeometry(Brushes.White, null, Leaf);
                dc.DrawLine(new Pen(new SolidColorBrush(Green), 0.14), new Point(0, -0.6), new Point(0, 0.55));  // vein
                dc.DrawLine(new Pen(Brushes.White, 0.16), new Point(0, 0.7), new Point(0, 1.15));                // stem
            }
        }
        return bmp;
    }
}
