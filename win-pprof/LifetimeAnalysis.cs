#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace win_pprof;

/// <summary>
/// Extracts object age (from "object lifetime" label) and allocation type ("allocation class")
/// from pprof samples and builds aggregations for lifetime visualizations.
/// </summary>
public sealed class LifetimeAnalysis
{
    public const string LifetimeLabelKey = "object lifetime";
    public const string ClassLabelKey = "allocation class";

    public const int MinBucketIndex = 0;
    public const int MaxBucketIndex = 11;
    public const int OldTailBucketStart = 7;

    private LifetimeAnalysis(
        IReadOnlyList<Record> records,
        int bytesValueIndex,
        int countValueIndex)
    {
        Records = records;
        BytesValueIndex = bytesValueIndex;
        CountValueIndex = countValueIndex;
    }

    public IReadOnlyList<Record> Records { get; }

    public int BytesValueIndex { get; }

    public int CountValueIndex { get; }

    public bool HasLifetimeData => Records.Count > 0;

    public record struct Record(string Type, long AgeNs, long Bytes, long Count);

    public static LifetimeAnalysis Build(PProfFile profile)
    {
        var (bytesIdx, countIdx) = DetectValueIndices(profile.ValueTypes);
        var list = new List<Record>();

        foreach (var sample in profile.Samples)
        {
            if (!TryGetLabelValue(sample.Labels, LifetimeLabelKey, out var lifetimeStr))
                continue;

            if (!long.TryParse(lifetimeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lifetimeNum))
                continue;

            long ageNs;
            try
            {
                checked
                {
                    ageNs = -lifetimeNum;
                }
            }
            catch (OverflowException)
            {
                continue;
            }

            if (ageNs < 0)
                continue;

            TryGetLabelValue(sample.Labels, ClassLabelKey, out var type);
            if (string.IsNullOrEmpty(type))
                type = "<unknown>";

            long bytes = SafeGetValue(sample.Values, bytesIdx);
            long count = SafeGetValue(sample.Values, countIdx);

            list.Add(new Record(type, ageNs, bytes, count));
        }

        return new LifetimeAnalysis(list, bytesIdx, countIdx);
    }

    private static long SafeGetValue(IReadOnlyList<long> values, int index)
    {
        if (index < 0 || index >= values.Count)
            return 0;
        return values[index];
    }

    public string[] GetTypesOrderedForHeatmap(bool weightByBytes, int minBucket = MinBucketIndex, int maxTypes = 20)
    {
        var byType = Records.GroupBy(r => r.Type);
        var scores = new List<(string Type, double OldMass)>();
        foreach (var g in byType)
        {
            double oldMass = 0;
            foreach (var r in g)
            {
                var b = BucketIndex(r.AgeNs);
                if (b < minBucket)
                    continue;
                oldMass += Weight(r, weightByBytes);
            }

            if (oldMass <= 0)
                continue;

            scores.Add((g.Key, oldMass));
        }

        scores.Sort((a, b) => b.OldMass.CompareTo(a.OldMass));
        if (scores.Count > maxTypes)
            scores.RemoveRange(maxTypes, scores.Count - maxTypes);
        scores.Reverse();
        return scores.Select(s => s.Type).ToArray();
    }

    public record struct HeatCell(int BucketIndex, int TypeIndex, double Weight);

    public IReadOnlyList<HeatCell> BuildHeatmapCells(
        bool weightByBytes,
        out string[] typeNamesOrdered,
        out string[] bucketLabels,
        int minBucket = MinBucketIndex)
    {
        typeNamesOrdered = GetTypesOrderedForHeatmap(weightByBytes, minBucket);
        bucketLabels = GetBucketAxisLabels(minBucket);

        var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < typeNamesOrdered.Length; i++)
            typeIndex[typeNamesOrdered[i]] = i;

        var nTypes = typeNamesOrdered.Length;
        var nBuckets = MaxBucketIndex - minBucket + 1;
        var grid = new double[nTypes, nBuckets];

        foreach (var r in Records)
        {
            if (!typeIndex.TryGetValue(r.Type, out var ty))
                continue;
            var bx = BucketIndex(r.AgeNs) - minBucket;
            if (bx < 0 || bx >= nBuckets)
                continue;
            grid[ty, bx] += Weight(r, weightByBytes);
        }

        var cells = new List<HeatCell>();
        for (var ty = 0; ty < nTypes; ty++)
        {
            for (var bx = 0; bx < nBuckets; bx++)
            {
                if (grid[ty, bx] > 0)
                    cells.Add(new HeatCell(bx, ty, grid[ty, bx]));
            }
        }

        return cells;
    }

    public static string[] GetBucketAxisLabels(int minBucket = MinBucketIndex)
    {
        var labels = new string[MaxBucketIndex - minBucket + 1];
        for (int i = minBucket; i <= MaxBucketIndex; i++)
        {
            var lowNs = (long)Math.Pow(10, i);
            labels[i - minBucket] = FormatAge(lowNs);
        }

        return labels;
    }

    public IReadOnlyList<BubblePoint> BuildBubblePoints(bool weightByBytes)
    {
        var result = new List<BubblePoint>();
        foreach (var g in Records.GroupBy(r => r.Type))
        {
            var ages = g.Select(r => r.AgeNs).ToArray();
            var totalWeight = g.Sum(r => Weight(r, weightByBytes));
            var totalCount = g.Sum(r => Math.Max(0, r.Count));
            var p95 = PercentileAge(ages, 0.95);
            var logAge = Math.Log10(Math.Max(p95, 1));
            result.Add(new BubblePoint(g.Key, logAge, totalWeight, Math.Max(totalCount, 1)));
        }

        return result;
    }

    public record BubblePoint(string Type, double LogAge, double TotalWeight, double CountForSize);

    public IReadOnlyList<SurvivalSeries> BuildSurvivalCurves(int topN, bool weightByBytes, long minAgeNs = 0)
    {
        if (topN < 1)
            topN = 1;

        var byType = Records.GroupBy(r => r.Type).ToList();
        var ranked = byType
            .Select(g =>
            {
                var eligible = g.Where(r => r.AgeNs >= minAgeNs).ToList();
                return (Type: g.Key, Total: eligible.Sum(r => Weight(r, weightByBytes)), Items: eligible);
            })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total)
            .Take(topN)
            .ToList();

        var series = new List<SurvivalSeries>();
        foreach (var entry in ranked)
        {
            var totalW = entry.Total;

            var byAge = entry.Items
                .GroupBy(r => r.AgeNs)
                .Select(g => (Age: g.Key, W: g.Sum(r => Weight(r, weightByBytes))))
                .Where(x => x.W > 0)
                .OrderBy(x => x.Age)
                .ToList();

            if (byAge.Count == 0)
                continue;

            var points = new List<(double LogX, double Y)>();
            var tail = totalW;
            foreach (var (age, w) in byAge)
            {
                var logX = Math.Log10(Math.Max(age, 1));
                points.Add((logX, tail / totalW));
                tail -= w;
            }

            series.Add(new SurvivalSeries(entry.Type, points));
        }

        return series;
    }

    public static int BucketIndex(long ageNs)
    {
        var a = Math.Max(ageNs, 1);
        var k = (int)Math.Floor(Math.Log10(a));
        if (k < MinBucketIndex)
            k = MinBucketIndex;
        if (k > MaxBucketIndex)
            k = MaxBucketIndex;
        return k;
    }

    public static string FormatAge(long ageNs)
    {
        if (ageNs < 1000)
            return $"{ageNs} ns";
        if (ageNs < 1_000_000)
            return $"{ageNs / 1000.0:0.##} µs";
        if (ageNs < 1_000_000_000)
            return $"{ageNs / 1_000_000.0:0.##} ms";
        return $"{ageNs / 1_000_000_000.0:0.##} s";
    }

    public static string FormatAgeFromLog10(double log10Age)
    {
        if (double.IsNaN(log10Age) || double.IsInfinity(log10Age))
            return "?";
        var ns = Math.Pow(10, log10Age);
        if (ns > long.MaxValue)
            ns = long.MaxValue;
        return FormatAge((long)ns);
    }

    private static double Weight(Record r, bool weightByBytes) =>
        weightByBytes ? Math.Max(0, r.Bytes) : Math.Max(1, r.Count);

    private static long PercentileAge(long[] ages, double p)
    {
        if (ages.Length == 0)
            return 0;
        Array.Sort(ages);
        var idx = (int)Math.Floor(p * (ages.Length - 1));
        return ages[idx];
    }

    private static (int bytesIdx, int countIdx) DetectValueIndices(IReadOnlyList<ValueType> valueTypes)
    {
        var bytesIdx = -1;
        var countIdx = -1;
        for (var i = 0; i < valueTypes.Count; i++)
        {
            var u = valueTypes[i].Unit ?? string.Empty;
            var n = valueTypes[i].Name ?? string.Empty;
            if (bytesIdx < 0 && u.Equals("bytes", StringComparison.OrdinalIgnoreCase))
                bytesIdx = i;
            if (countIdx < 0 && (u.Equals("count", StringComparison.OrdinalIgnoreCase)
                                 || n.Contains("count", StringComparison.OrdinalIgnoreCase)
                                 || n.Contains("samples", StringComparison.OrdinalIgnoreCase)))
                countIdx = i;
        }

        if (bytesIdx < 0)
            bytesIdx = 0;
        if (countIdx < 0)
            countIdx = valueTypes.Count > 1 ? 1 : 0;

        return (bytesIdx, countIdx);
    }

    private static bool TryGetLabelValue(IEnumerable<Label> labels, string key, out string? value)
    {
        foreach (var label in labels)
        {
            if (string.Equals(label.Key, key, StringComparison.Ordinal))
            {
                value = label.Value ?? string.Empty;
                return true;
            }
        }

        value = null;
        return false;
    }

    public sealed class SurvivalSeries
    {
        public SurvivalSeries(string type, IReadOnlyList<(double LogX, double Y)> points)
        {
            Type = type;
            Points = points;
        }

        public string Type { get; }

        public IReadOnlyList<(double LogX, double Y)> Points { get; }
    }
}
