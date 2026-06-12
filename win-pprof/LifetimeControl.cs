#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace win_pprof;

/// <summary>
/// Object lifetime vs allocation class visualizations (code-only control — avoids WPF XAML temp project issues).
/// </summary>
public sealed class LifetimeControl : UserControl
{
    private readonly ComboBox _cbVisualization = new();
    private readonly ComboBox _cbWeight = new();
    private readonly ComboBox _cbTopN = new();
    private readonly TextBlock _tbTopNLabel = new();
    private readonly TextBlock _tbEmpty = new();
    private readonly Border _chartHost = new();

    private bool _suspendUi;
    private LifetimeAnalysis? _analysis;
    private readonly IsometricLifetimeView _isometric = new();

    public LifetimeControl()
    {

        _cbVisualization.Width = 180;
        _cbVisualization.Margin = new Thickness(0, 0, 16, 0);
        _cbVisualization.SelectionChanged += OnVisualizationOrWeightChanged;

        _cbWeight.Width = 100;
        _cbWeight.Margin = new Thickness(0, 0, 16, 0);
        _cbWeight.SelectionChanged += OnVisualizationOrWeightChanged;

        _tbTopNLabel.Text = "Top N";
        _tbTopNLabel.VerticalAlignment = VerticalAlignment.Center;
        _tbTopNLabel.Margin = new Thickness(0, 0, 8, 0);
        _tbTopNLabel.Visibility = Visibility.Collapsed;

        _cbTopN.Width = 56;
        _cbTopN.Visibility = Visibility.Collapsed;
        _cbTopN.SelectionChanged += OnTopNChanged;

        _tbEmpty.Text =
            "No samples with an \"object lifetime\" label were found in this profile.";
        _tbEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _tbEmpty.VerticalAlignment = VerticalAlignment.Center;
        _tbEmpty.TextWrapping = TextWrapping.Wrap;
        _tbEmpty.MaxWidth = 480;
        _tbEmpty.TextAlignment = TextAlignment.Center;
        _tbEmpty.Foreground = System.Windows.Media.Brushes.White;
        _tbEmpty.Visibility = Visibility.Collapsed;

        _chartHost.Background = System.Windows.Media.Brushes.White;
        _chartHost.Visibility = Visibility.Collapsed;

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 4, 4, 8),
        };
        header.Children.Add(Labeled("Visualization", _cbVisualization));
        header.Children.Add(Labeled("Weight", _cbWeight));
        header.Children.Add(_tbTopNLabel);
        header.Children.Add(_cbTopN);

        var body = new Grid { Margin = new Thickness(4, 0, 4, 4) };
        body.Children.Add(_tbEmpty);
        body.Children.Add(_chartHost);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(header);
        root.Children.Add(body);

        Content = root;
        Loaded += OnLoaded;
    }

    private static StackPanel Labeled(string caption, ComboBox combo)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        sp.Children.Add(combo);
        return sp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _suspendUi = true;
        _cbVisualization.Items.Clear();
        _cbVisualization.Items.Add("Heatmap");
        _cbVisualization.Items.Add("Bubble (p95 age vs mass)");
        _cbVisualization.Items.Add("CDF (age distribution)");
        _cbVisualization.Items.Add("Isometric (3D bars)");
        _cbVisualization.SelectedIndex = 0;

        _cbWeight.Items.Clear();
        _cbWeight.Items.Add("Bytes");
        _cbWeight.Items.Add("Count");
        _cbWeight.SelectedIndex = 0;

        _cbTopN.Items.Clear();
        foreach (var n in new[] { 3, 4, 5, 6, 8, 10, 12 })
            _cbTopN.Items.Add(n);
        _cbTopN.SelectedItem = 6;
        _suspendUi = false;
        UpdateTopNVisibility();
        Render();
    }

    public void Update(LifetimeAnalysis? analysis)
    {
        _analysis = analysis;
        Render();
    }

    private void OnVisualizationOrWeightChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTopNVisibility();
        if (!_suspendUi)
            Render();
    }

    private void OnTopNChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suspendUi && _cbVisualization.SelectedIndex == 2)
            Render();
    }

    private void UpdateTopNVisibility()
    {
        var survival = _cbVisualization.SelectedIndex == 2;
        _tbTopNLabel.Visibility = survival ? Visibility.Visible : Visibility.Collapsed;
        _cbTopN.Visibility = survival ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool WeightByBytes => _cbWeight.SelectedIndex <= 0;

    private void Render()
    {
        if (!IsLoaded || _suspendUi)
            return;

        if (_analysis == null || !_analysis.HasLifetimeData)
        {
            _tbEmpty.Visibility = Visibility.Visible;
            _chartHost.Visibility = Visibility.Collapsed;
            _chartHost.Child = null;
            return;
        }

        _tbEmpty.Visibility = Visibility.Collapsed;
        _chartHost.Visibility = Visibility.Visible;

        if (_cbVisualization.SelectedIndex == 3)
        {
            RenderIsometric();
            return;
        }

        var chart = new CartesianChart();
        switch (_cbVisualization.SelectedIndex)
        {
            case 0:
                RenderHeatmap(chart);
                break;
            case 1:
                RenderBubble(chart);
                break;
            default:
                RenderSurvival(chart);
                break;
        }

        _chartHost.Child = chart;
    }

    private void RenderIsometric()
    {
        const int MinBucket = 9; // 10^9 ns = 1 second — focus on the 1s+ tail where leaks live

        var grid = _analysis!.BuildGrid(WeightByBytes, out var typeNames, out var bucketLabels, out var maxValue, MinBucket);
        _isometric.SetData(grid, typeNames, bucketLabels, maxValue, WeightByBytes);
        if (!ReferenceEquals(_chartHost.Child, _isometric))
            _chartHost.Child = _isometric;
    }

    private void RenderHeatmap(CartesianChart chart)
    {
        const int MinBucket = 9; // 10^9 ns = 1 second

        chart.LegendPosition = LegendPosition.Hidden;
        var cells = _analysis!.BuildHeatmapCells(WeightByBytes, out var typeNames, out var bucketLabels, MinBucket);
        var heatPoints = cells
            .Select(c => new WeightedPoint(c.BucketIndex, c.TypeIndex, c.Weight))
            .ToArray();

        chart.Series =
        [
            new HeatSeries<WeightedPoint>
            {
                Values = heatPoints,
                HeatMap =
                [
                    SKColor.Parse("#0D47A1").AsLvcColor(),
                    SKColor.Parse("#42A5F5").AsLvcColor(),
                    SKColors.Yellow.AsLvcColor(),
                    SKColors.OrangeRed.AsLvcColor()
                ],
            }
        ];

        var nBuckets = bucketLabels.Length;
        chart.XAxes =
        [
            new Axis
            {
                Labels = bucketLabels,
                LabelsRotation = -35,
                TextSize = 11,
                MinLimit = -0.5,
                MaxLimit = nBuckets - 0.5,
            }
        ];

        chart.YAxes =
        [
            new Axis
            {
                Labels = typeNames,
                TextSize = 11,
                MinLimit = -0.5,
                MaxLimit = typeNames.Length - 0.5,
            }
        ];
    }

    private void RenderBubble(CartesianChart chart)
    {
        const double MaxDiameter = 56;
        const double MinDiameter = 12;

        chart.LegendPosition = LegendPosition.Right;
        var bubbles = _analysis!.BuildBubblePoints(WeightByBytes);
        if (bubbles.Count == 0)
            return;

        var maxCount = bubbles.Max(b => b.CountForSize);
        var minLogAge = bubbles.Min(b => b.LogAge);
        var maxLogAge = bubbles.Max(b => b.LogAge);

        double minLogW = double.MaxValue, maxLogW = double.MinValue;
        var series = new List<ISeries>();
        foreach (var b in bubbles.OrderByDescending(b => b.TotalWeight))
        {
            var logW = b.TotalWeight > 0 ? Math.Log10(b.TotalWeight) : 0;
            if (logW < minLogW) minLogW = logW;
            if (logW > maxLogW) maxLogW = logW;

            var ratio = maxCount > 0 ? b.CountForSize / maxCount : 0;
            var diameter = MinDiameter + Math.Sqrt(ratio) * (MaxDiameter - MinDiameter);

            series.Add(new ScatterSeries<ObservablePoint>
            {
                Name = TruncateLabel(b.Type, 48),
                Values = [new ObservablePoint(b.LogAge, logW)],
                GeometrySize = diameter,
            });
        }

        var xPad = Math.Max((maxLogAge - minLogAge) * 0.1, 0.5);
        var yPad = Math.Max((maxLogW - minLogW) * 0.08, 0.3);
        chart.Series = series.ToArray();
        chart.XAxes =
        [
            new Axis
            {
                Name = "Age (p95 per type)",
                Labeler = d => LifetimeAnalysis.FormatAgeFromLog10(d),
                MinLimit = minLogAge - xPad,
                MaxLimit = maxLogAge + xPad,
            }
        ];
        chart.YAxes =
        [
            new Axis
            {
                Name = WeightByBytes ? "Bytes (total)" : "Count (total)",
                Labeler = d => FormatNumberCompact(Math.Pow(10, d)),
                MinLimit = minLogW - yPad,
                MaxLimit = maxLogW + yPad,
            }
        ];
    }

    private void RenderSurvival(CartesianChart chart)
    {
        const long MinAgeNs = 1_000_000_000; // 1 second
        const double MinLogAge = 9;

        chart.LegendPosition = LegendPosition.Right;
        var topN = _cbTopN.SelectedItem is int n ? n : 6;
        var curves = _analysis!.BuildSurvivalCurves(topN, WeightByBytes, MinAgeNs);

        var maxLog = MinLogAge;
        var allSeries = new List<ISeries>();
        foreach (var c in curves)
        {
            if (c.Points.Count == 0)
                continue;

            var pts = c.Points.Select(p => new ObservablePoint(p.LogX, 1.0 - p.Y)).ToList();
            maxLog = Math.Max(maxLog, pts.Max(p => p.X!.Value));
            allSeries.Add(new LineSeries<ObservablePoint>
            {
                Name = TruncateLabel(c.Type, 40),
                Values = pts,
                Fill = null,
                LineSmoothness = 0,
                GeometrySize = 0,
                YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue * 100:0.##}%",
            });
        }

        chart.Series = allSeries.ToArray();
        chart.XAxes =
        [
            new Axis
            {
                Name = "Age (threshold)",
                Labeler = d => LifetimeAnalysis.FormatAgeFromLog10(d),
                MinLimit = MinLogAge - 0.2,
                MaxLimit = maxLog + 0.5,
            }
        ];
        chart.YAxes =
        [
            new Axis
            {
                Name = "P(age ≤ threshold)",
                Labeler = d => $"{d * 100:0}%",
                MinLimit = 0,
                MaxLimit = 1.05,
            }
        ];
    }

    internal static string TruncateLabel(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen)
            return s;

        var collapsed = CollapseGenerics(s);
        if (collapsed.Length <= maxLen)
            return collapsed;

        // Find the last top-level dot: everything after it is the type name, before it the namespace.
        int depth = 0, lastDot = -1;
        for (int i = 0; i < collapsed.Length; i++)
        {
            if (collapsed[i] == '<') depth++;
            else if (collapsed[i] == '>') depth--;
            else if (collapsed[i] == '.' && depth == 0) lastDot = i;
        }

        // No namespace to preserve — just keep the head of the type name.
        if (lastDot <= 0)
            return collapsed[..(maxLen - 1)] + "…";

        var typeName = collapsed[(lastDot + 1)..];

        // The type name is the key identifier, so it always wins. If even it overflows, trim its tail.
        if (typeName.Length >= maxLen)
            return typeName[..(maxLen - 1)] + "…";

        // Prepend whole namespace segments, nearest (most specific) first, while they fit.
        // A leading "…" marks where the outer namespace was clipped, e.g. "…Concurrent.Dictionary<…>".
        var nsSegments = collapsed[..lastDot].Split('.');
        var result = typeName;
        var clipped = false;
        for (int i = nsSegments.Length - 1; i >= 0; i--)
        {
            // +1 for the joining dot, +1 reserved for a possible leading ellipsis.
            if (result.Length + 1 + nsSegments[i].Length + 1 <= maxLen)
                result = nsSegments[i] + "." + result;
            else
            {
                clipped = true;
                break;
            }
        }

        return clipped ? "…" + result : result;
    }

    private static string CollapseGenerics(string s)
    {
        if (!s.Contains('<'))
            return s;

        var sb = new System.Text.StringBuilder(s.Length);
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '<')
            {
                if (depth == 0)
                    sb.Append("<…>");
                depth++;
            }
            else if (s[i] == '>')
            {
                depth--;
            }
            else if (depth == 0)
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static string FormatNumberCompact(double v)
    {
        if (v >= 1_000_000_000)
            return $"{v / 1_000_000_000:0.##} G";
        if (v >= 1_000_000)
            return $"{v / 1_000_000:0.##} M";
        if (v >= 1_000)
            return $"{v / 1_000:0.##} k";
        return v.ToString("0.##");
    }
}
