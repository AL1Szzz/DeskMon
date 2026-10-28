using System;
using System.Windows;
using System.Windows.Media;

namespace DeskMon
{
    // 24x24 grid, drawn from primitives so every corner and cap is genuinely round
    // (SF Symbols / macOS System Settings feel). Strokes are applied by the host Path:
    // round joins, round caps, no fills.
    internal static class Icons
    {
        static Geometry Rounded(double x, double y, double w, double h, double radius)
            => new RectangleGeometry(new Rect(x, y, w, h), radius, radius);

        static Geometry Line(double x1, double y1, double x2, double y2)
            => new LineGeometry(new Point(x1, y1), new Point(x2, y2));

        static Geometry Ring(double cx, double cy, double r)
            => new EllipseGeometry(new Point(cx, cy), r, r);

        static Geometry Combine(params Geometry[] parts)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var p in parts) g.Children.Add(p);
            return g;
        }

        // Chip: rounded body, rounded die, four pins per side.
        public static readonly Geometry Cpu = Combine(
            Rounded(6.5, 6.5, 11, 11, 3),
            Rounded(10, 10, 4, 4, 1.2),
            Line(9.5, 3.6, 9.5, 6.5), Line(14.5, 3.6, 14.5, 6.5),
            Line(9.5, 17.5, 9.5, 20.4), Line(14.5, 17.5, 14.5, 20.4),
            Line(3.6, 9.5, 6.5, 9.5), Line(3.6, 14.5, 6.5, 14.5),
            Line(17.5, 9.5, 20.4, 9.5), Line(17.5, 14.5, 20.4, 14.5));

        // Graphics card: rounded shroud with one fan and a PCIe foot.
        public static readonly Geometry Gpu = Combine(
            Rounded(3, 6.6, 18, 9, 2.6),
            Ring(8.6, 11.1, 2.3),
            Line(13.6, 9.4, 18.2, 9.4),
            Line(13.6, 12.8, 16.6, 12.8),
            Rounded(4.6, 15.6, 7.4, 2.6, 1.2));

        // Fan: three curved blades around a hub inside a shroud. Straight spokes read as a
        // car badge, so each blade is a swept petal rotated about the hub.
        public static readonly Geometry Fan = FanGeometry();

        static Geometry FanGeometry()
        {
            var blade = Geometry.Parse(
                "M 12 10.7 C 14.5 9.6 15.6 6.9 14.1 4.6 C 12.4 5.8 11.4 8.4 12 10.7 Z");
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (int i = 0; i < 3; i++) g.Children.Add(Rotated(blade, i * 120));
            g.Children.Add(Ring(12, 12, 8.7));
            g.Children.Add(Ring(12, 12, 1.35));
            return g;
        }

        static Geometry Rotated(Geometry source, double angle)
        {
            var clone = source.Clone();
            clone.Transform = new RotateTransform(angle, 12, 12);
            clone.Freeze();
            return clone;
        }

        // Memory: a stick with two chips and edge pins.
        public static readonly Geometry Ram = Combine(
            Rounded(3.2, 7.4, 17.6, 7.6, 2),
            Rounded(6.2, 9.6, 3.2, 3.2, 0.7),
            Rounded(14.6, 9.6, 3.2, 3.2, 0.7),
            Line(5.6, 15, 5.6, 17.6), Line(12, 15, 12, 17.6), Line(18.4, 15, 18.4, 17.6));

        // Video memory: same stick silhouette with a single wide die, so the pair reads as
        // "system memory" vs "memory on the card" without needing a different metaphor.
        public static readonly Geometry Vram = Combine(
            Rounded(3.2, 7.4, 17.6, 7.6, 2),
            Rounded(7.6, 9.4, 8.8, 3.6, 0.9),
            Line(5.6, 15, 5.6, 17.6), Line(12, 15, 12, 17.6), Line(18.4, 15, 18.4, 17.6));

        // Network: Wi-Fi arcs. The value already prints its own up/down arrows, so a second
        // pair of arrows here was just noise.
        public static readonly Geometry Net = Combine(
            Geometry.Parse("M 4.63 12.27 A 9 9 0 0 1 19.37 12.27"),
            Geometry.Parse("M 7.42 14.29 A 5.6 5.6 0 0 1 16.58 14.29"),
            Ring(12, 17.2, 1.05));
    }
}
