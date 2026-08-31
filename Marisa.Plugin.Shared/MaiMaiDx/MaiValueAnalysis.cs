namespace Marisa.Plugin.Shared.MaiMaiDx;

public enum MaiValueAnalysisMode
{
    Gold,
    Water
}

public enum MaiAchievementRank
{
    SssPlus,
    Sss,
    SsPlus,
    Ss,
    SPlus,
    S,
    Aaa,
    Aa,
    A,
    Bbb,
    Bb,
    B,
    C,
    D
}

public static class MaiAchievementRanks
{
    private static readonly IReadOnlyDictionary<string, MaiAchievementRank> Tokens =
        new Dictionary<string, MaiAchievementRank>(StringComparer.OrdinalIgnoreCase)
        {
            ["SSS+"] = MaiAchievementRank.SssPlus,
            ["鸟加"]  = MaiAchievementRank.SssPlus,
            ["鸟+"]   = MaiAchievementRank.SssPlus,
            ["SSS"]  = MaiAchievementRank.Sss,
            ["鸟"]    = MaiAchievementRank.Sss,
            ["SS+"]  = MaiAchievementRank.SsPlus,
            ["SS"]   = MaiAchievementRank.Ss,
            ["S+"]   = MaiAchievementRank.SPlus,
            ["S"]    = MaiAchievementRank.S,
            ["AAA"]  = MaiAchievementRank.Aaa,
            ["AA"]   = MaiAchievementRank.Aa,
            ["A"]    = MaiAchievementRank.A,
            ["BBB"]  = MaiAchievementRank.Bbb,
            ["BB"]   = MaiAchievementRank.Bb,
            ["B"]    = MaiAchievementRank.B,
            ["C"]    = MaiAchievementRank.C,
            ["D"]    = MaiAchievementRank.D
        };

    public static bool TryParse(string token, out MaiAchievementRank rank)
    {
        return Tokens.TryGetValue(token.Trim().Replace('＋', '+'), out rank);
    }

    public static bool Contains(MaiAchievementRank rank, double achievement)
    {
        var (minimum, maximum) = Bounds(rank);
        return achievement >= minimum && achievement < maximum;
    }

    public static MaiAchievementRank FromAchievement(double achievement)
    {
        return achievement switch
        {
            >= 100.5 => MaiAchievementRank.SssPlus,
            >= 100   => MaiAchievementRank.Sss,
            >= 99.5  => MaiAchievementRank.SsPlus,
            >= 99    => MaiAchievementRank.Ss,
            >= 98    => MaiAchievementRank.SPlus,
            >= 97    => MaiAchievementRank.S,
            >= 94    => MaiAchievementRank.Aaa,
            >= 90    => MaiAchievementRank.Aa,
            >= 80    => MaiAchievementRank.A,
            >= 75    => MaiAchievementRank.Bbb,
            >= 70    => MaiAchievementRank.Bb,
            >= 60    => MaiAchievementRank.B,
            >= 50    => MaiAchievementRank.C,
            _        => MaiAchievementRank.D
        };
    }

    public static string Label(MaiAchievementRank rank)
    {
        return rank switch
        {
            MaiAchievementRank.SssPlus => "SSS+",
            MaiAchievementRank.Sss     => "SSS",
            MaiAchievementRank.SsPlus  => "SS+",
            MaiAchievementRank.Ss      => "SS",
            MaiAchievementRank.SPlus   => "S+",
            MaiAchievementRank.S       => "S",
            MaiAchievementRank.Aaa     => "AAA",
            MaiAchievementRank.Aa      => "AA",
            MaiAchievementRank.A       => "A",
            MaiAchievementRank.Bbb     => "BBB",
            MaiAchievementRank.Bb      => "BB",
            MaiAchievementRank.B       => "B",
            MaiAchievementRank.C       => "C",
            MaiAchievementRank.D       => "D",
            _                          => throw new ArgumentOutOfRangeException(nameof(rank), rank, null)
        };
    }

    private static (double Minimum, double Maximum) Bounds(MaiAchievementRank rank)
    {
        return rank switch
        {
            MaiAchievementRank.SssPlus => (100.5, 101.0001),
            MaiAchievementRank.Sss     => (100, 100.5),
            MaiAchievementRank.SsPlus  => (99.5, 100),
            MaiAchievementRank.Ss      => (99, 99.5),
            MaiAchievementRank.SPlus   => (98, 99),
            MaiAchievementRank.S       => (97, 98),
            MaiAchievementRank.Aaa     => (94, 97),
            MaiAchievementRank.Aa      => (90, 94),
            MaiAchievementRank.A       => (80, 90),
            MaiAchievementRank.Bbb     => (75, 80),
            MaiAchievementRank.Bb      => (70, 75),
            MaiAchievementRank.B       => (60, 70),
            MaiAchievementRank.C       => (50, 60),
            MaiAchievementRank.D       => (0, 50),
            _                          => throw new ArgumentOutOfRangeException(nameof(rank), rank, null)
        };
    }
}

public sealed record MaiValueAnalysisItem(
    long SongId,
    string Title,
    string Type,
    int LevelIndex,
    string Level,
    double Achievement,
    string AchievementRank,
    int Rating,
    int DxScore,
    string Fc,
    string Fs,
    double OfficialConstant,
    double FittedConstant,
    double Deviation,
    string Source);

public sealed record MaiValueAnalysisStatistics(
    double Mean,
    double MeanCiLow,
    double MeanCiHigh,
    double Median,
    double StandardDeviation,
    double Minimum,
    double Maximum);

public sealed record MaiValueAnalysisCardData(
    string Mode,
    string Nickname,
    int PlayerRating,
    string Scope,
    int SelectedCount,
    int AnalyzedCount,
    int CurveCount,
    int DivingFishCount,
    int MissingCount,
    MaiValueAnalysisStatistics? Statistics,
    IReadOnlyList<MaiValueAnalysisItem> TopCharts);

public sealed class MaiValueAnalysisEngine
{
    private readonly Dictionary<long, MaiMaiSong> _songs;
    private readonly RecommendationDifficultyCatalog _curveCatalog;

    public MaiValueAnalysisEngine(
        IReadOnlyList<MaiMaiSong> songs,
        RecommendationDifficultyCatalog? curveCatalog = null)
    {
        _songs        = songs.ToDictionary(x => x.Id);
        _curveCatalog = curveCatalog ?? RecommendationDifficultyCatalog.Default;
    }

    public bool RequiresFallback(IEnumerable<SongScore> scores)
    {
        return DistinctScores(scores).Any(score =>
            !_curveCatalog.TryGetPooled(score.Id, score.LevelIdx, out _));
    }

    public MaiValueAnalysisCardData Build(
        string nickname,
        int playerRating,
        string scope,
        IEnumerable<SongScore> selectedScores,
        MaiValueAnalysisMode mode,
        DivingFishChartStatsCatalog? fallbackCatalog = null)
    {
        fallbackCatalog ??= DivingFishChartStatsCatalog.Empty;
        var selected = DistinctScores(selectedScores).ToList();
        var items = selected
            .Select(score => CreateItem(score, fallbackCatalog))
            .Where(item => item != null)
            .Cast<MaiValueAnalysisItem>()
            .ToList();

        var top = mode == MaiValueAnalysisMode.Gold
            ? items.OrderByDescending(x => x.Deviation)
                .ThenByDescending(x => x.FittedConstant)
                .ThenBy(x => x.SongId)
                .ThenBy(x => x.LevelIndex)
                .Take(10)
                .ToList()
            : items.OrderBy(x => x.Deviation)
                .ThenBy(x => x.FittedConstant)
                .ThenBy(x => x.SongId)
                .ThenBy(x => x.LevelIndex)
                .Take(10)
                .ToList();

        return new MaiValueAnalysisCardData(
            mode == MaiValueAnalysisMode.Gold ? "gold" : "water",
            nickname,
            playerRating,
            scope,
            selected.Count,
            items.Count,
            items.Count(x => x.Source == "curve"),
            items.Count(x => x.Source == "divingFish"),
            selected.Count - items.Count,
            BuildStatistics(items),
            top);
    }

    public static IReadOnlyList<SongScore> FilterByRank(
        IEnumerable<SongScore> scores,
        MaiAchievementRank rank)
    {
        return scores.Where(x => MaiAchievementRanks.Contains(rank, x.Achievement)).ToList();
    }

    private MaiValueAnalysisItem? CreateItem(
        SongScore score,
        DivingFishChartStatsCatalog fallbackCatalog)
    {
        if (score.LevelIdx is < 0 or > 4) return null;

        _songs.TryGetValue(score.Id, out var song);
        var officialConstant = song != null && score.LevelIdx < song.Constants.Count
            ? song.Constants[score.LevelIdx]
            : score.Constant;
        if (!double.IsFinite(officialConstant) || officialConstant <= 0) return null;

        double fittedConstant;
        string source;
        if (_curveCatalog.TryGetPooled(score.Id, score.LevelIdx, out fittedConstant))
        {
            source = "curve";
        }
        else if (fallbackCatalog.TryGet(score.Id, score.LevelIdx, out fittedConstant))
        {
            source = "divingFish";
        }
        else
        {
            return null;
        }

        if (!double.IsFinite(fittedConstant) || fittedConstant <= 0) return null;

        var title = song?.Title ?? score.Title;
        var type  = song?.Type ?? score.Type;
        var level = song != null && score.LevelIdx < song.Levels.Count
            ? song.Levels[score.LevelIdx]
            : score.Level;

        return new MaiValueAnalysisItem(
            score.Id,
            title,
            type,
            score.LevelIdx,
            level,
            score.Achievement,
            MaiAchievementRanks.Label(MaiAchievementRanks.FromAchievement(score.Achievement)),
            SongScore.Ra(score.Achievement, officialConstant),
            score.DxScore,
            score.Fc ?? "",
            score.Fs ?? "",
            officialConstant,
            fittedConstant,
            fittedConstant - officialConstant,
            source);
    }

    private static MaiValueAnalysisStatistics? BuildStatistics(IReadOnlyList<MaiValueAnalysisItem> items)
    {
        if (items.Count == 0) return null;

        var values = items.Select(x => x.Deviation).Order().ToArray();
        var mean = values.Average();
        var standardDeviation = Math.Sqrt(values.Sum(x => Math.Pow(x - mean, 2)) / values.Length);
        var (ciLow, ciHigh) = BootstrapMeanConfidenceInterval(values);

        return new MaiValueAnalysisStatistics(
            mean,
            ciLow,
            ciHigh,
            Quantile(values, 0.5),
            standardDeviation,
            values[0],
            values[^1]);
    }

    private static (double Low, double High) BootstrapMeanConfidenceInterval(double[] values)
    {
        if (values.Length == 1) return (values[0], values[0]);

        const int iterations = 2000;
        var random = new Random(0x4D4149 + values.Length);
        var means = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var sum = 0.0;
            for (var i = 0; i < values.Length; i++) sum += values[random.Next(values.Length)];
            means[iteration] = sum / values.Length;
        }

        Array.Sort(means);
        return (Quantile(means, 0.025), Quantile(means, 0.975));
    }

    private static double Quantile(IReadOnlyList<double> sorted, double probability)
    {
        var position = (sorted.Count - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static IEnumerable<SongScore> DistinctScores(IEnumerable<SongScore> scores)
    {
        return scores
            .GroupBy(x => (x.Id, x.LevelIdx))
            .Select(group => group.MaxBy(x => x.Achievement)!);
    }
}
