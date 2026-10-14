using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Bs1Cooler.App;

/// Temperatures page: CPU, each GPU, each disk and the motherboard, whichever this PC reports.
public partial class MainWindow
{
    private static readonly (string Kind, string Title, Symbol Icon, string Tile)[] Kinds =
    [
        ("cpu", "CPU", Symbol.DeveloperBoard, "orange"),
        ("gpu", "GPU", Symbol.Desktop, "green"),
        ("disk", "Disk", Symbol.HardDrive, "blue"),
        ("board", "Motherboard", Symbol.Board, "purple"),
    ];

    private string shownParts = "";
    private readonly Dictionary<string, (Arc Ring, Ellipse Track, SymbolIcon Icon, TextBlock Value)> rings = [];

    private void WireTemps()
    {
        TempsBtn.Click += (_, _) => ShowPage(TempsPage);
        Stats.PointerReleased += (_, _) => ShowPage(TempsPage);
        TempsBack.Click += (_, _) => ShowPage(Home);
    }

    private void RefreshTemps()
    {
        var parts = ctl.Parts;
        string key = string.Join("|", parts.Select(p => p.Kind + p.Name)) + Tx.Lang;
        if (key != shownParts) { shownParts = key; BuildTemps(parts); }
        foreach (var p in parts) ShowTemp(p.Kind + p.Name, p.Temp);
    }

    private void ShowTemp(string id, double t)
    {
        var (ring, track, icon, value) = rings[id];
        var heat = Color.Parse(t < 60 ? "#2DB467" : t < 80 ? "#F0922F" : "#E5484D");
        ring.SweepAngle = System.Math.Clamp(t, 0, 100) * 3.6;  // 100 °C = full circle
        ring.Stroke = icon.Foreground = new SolidColorBrush(heat);
        track.Stroke = new SolidColorBrush(heat, 0.18);
        value.Text = $"{t:0}°C";
    }

    private void BuildTemps(IReadOnlyList<Part> parts)
    {
        TempsList.Children.Clear();
        rings.Clear();
        if (parts.Count == 0) { TempsList.Children.Add(new TextBlock { Classes = { "desc" }, Text = Tx.Tr("Reading sensors…") }); return; }
        foreach (var (kind, title, icon, tile) in Kinds)
        {
            var mine = parts.Where(p => p.Kind == kind).ToList();
            if (mine.Count == 0) continue;  // not on this PC
            var head = new StackPanel { Classes = { "sechead" } };
            head.Children.Add(new Border { Classes = { "tile", tile }, Child = new SymbolIcon { Symbol = icon } });
            head.Children.Add(new TextBlock { Text = Tx.Tr(title) });
            var grid = new UniformGrid { Columns = mine.Count == 1 ? 1 : 2 };
            foreach (var p in mine) grid.Children.Add(Gauge(p));
            var box = new StackPanel { Spacing = 4 };
            box.Children.Add(head);
            box.Children.Add(grid);
            TempsList.Children.Add(box);
        }
    }

    /// A card: a ring filled by the temperature around a thermometer, the part's name below.
    private Border Gauge(Part p)
    {
        const double size = 104;
        var track = new Ellipse { Width = size, Height = size, StrokeThickness = 6 };
        var ring = new Arc { Width = size, Height = size, StrokeThickness = 6, StartAngle = -90, StrokeLineCap = PenLineCap.Round };
        var value = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new SymbolIcon { Symbol = Symbol.Temperature, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center };
        middle.Children.Add(icon);
        middle.Children.Add(value);
        var dial = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        dial.Children.Add(track);
        dial.Children.Add(ring);
        dial.Children.Add(middle);
        var name = new TextBlock { Classes = { "desc" }, Text = p.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 8, 0, 0) };
        var card = new StackPanel();
        card.Children.Add(dial);
        card.Children.Add(name);
        rings[p.Kind + p.Name] = (ring, track, icon, value);
        return new Border { Classes = { "section" }, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 14), Child = card };
    }

    /// Dev (--smoke): fill the page with this laptop's parts so the layout can be checked without admin.
    private void SampleTemps()
    {
        Part[] sample =
        [
            new("cpu", "13th Gen Intel Core i7-13700H", 52), new("gpu", "NVIDIA GeForce RTX 4060 Laptop GPU", 41),
            new("gpu", "Intel Iris Xe Graphics", 60), new("disk", "WD_BLACK SN770 1TB", 37),
            new("disk", "TEAM TM8FP6001T", 29), new("board", "LENOVO LNVNB161216", 84),
        ];
        BuildTemps(sample);
        foreach (var p in sample) ShowTemp(p.Kind + p.Name, p.Temp);
    }
}
