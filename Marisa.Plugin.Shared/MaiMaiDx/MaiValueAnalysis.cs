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

public sealed record MaiValueAnalysisFilter(
    MaiAchievementRank? Rank = null,
    string? Level = null,
    int? DifficultyIndex = null,
    double? Constant = null)
{
    public static MaiValueAnalysisFilter Empty { get; } = new();

    public bool IsEmpty => Rank is null && Level is null && DifficultyIndex is null && Constant is null;
}

public static class MaiValueAnalysisFilters
{
    private static readonly (string Token, int DifficultyIndex)[] DifficultyTokens =
        PlateData.DifficultyAliasMap
            .Select(x => (x.Key, x.Value))
            .OrderByDescending(x => x.Key.Length)
            .ToArray();

    public static bool TryParse(string input, out MaiValueAnalysisFilter filter)
    {
        var parts = input.Normalize(System.Text.NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var current = MaiValueAnalysisFilter.Empty;
        foreach (var part in parts)
        {
            if (!TryParseRemaining(part, current, out var next))
            {
                filter = MaiValueAnalysisFilter.Empty;
                return false;
            }
            current = next;
        }

        filter = current;
        return !filter.IsEmpty;

        static bool TryParseRemaining(
            string remaining,
            MaiValueAnalysisFilter current,
            out MaiValueAnalysisFilter result)
        {
            if (remaining.Length == 0)
            {
                result = current;
                return true;
            }

            if (current.DifficultyIndex is null)
            {
                foreach (var (token, difficultyIndex) in DifficultyTokens)
                {
                    if (!remaining.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryParseRemaining(
                            remaining[token.Length..],
                            current with { DifficultyIndex = difficultyIndex },
                            out result)) return true;
                }
            }

            if (current.Constant is null)
            {
                for (var length = Math.Min(4, remaining.Length); length >= 3; length--)
                {
                    if (!TryParseConstant(remaining[..length], out var constant)) continue;
                    if (TryParseRemaining(
                            remaining[length..],
                            current with { Constant = constant },
                            out result)) return true;
                }
            }

            if (current.Level is null)
            {
                for (var length = Math.Min(3, remaining.Length); length >= 1; length--)
                {
                    if (!TryParseLevel(remaining[..length], out var level)) continue;
                    if (TryParseRemaining(
                            remaining[length..],
                            current with { Level = level },
                            out result)) return true;
                }
            }

            if (current.Rank is null)
            {
                for (var length = Math.Min(4, remaining.Length); length >= 1; length--)
                {
                    if (!MaiAchievementRanks.TryParse(remaining[..length], out var rank)) continue;
                    if (TryParseRemaining(
                            remaining[length..],
                            current with { Rank = rank },
                            out result)) return true;
                }
            }

            result = MaiValueAnalysisFilter.Empty;
            return false;
        }
    }

    public static string Scope(MaiValueAnalysisFilter filter)
    {
        if (filter.IsEmpty) return "B50";

        var parts = new List<string> { "全成绩" };
        if (filter.Rank is { } rank) parts.Add($"达成率 {MaiAchievementRanks.Label(rank)}");
        if (filter.Level is { } level) parts.Add($"等级 {level}");
        if (filter.DifficultyIndex is { } difficultyIndex) parts.Add(DifficultyLabel(difficultyIndex));
        if (filter.Constant is { } constant)
            parts.Add($"定数 {constant.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }

    private static bool TryParseLevel(string value, out string level)
    {
        level = "";
        var plus = value.EndsWith('+');
        var core = plus ? value[..^1] : value;
        if (core.Length is 0 or > 2 || !core.All(char.IsAsciiDigit) || core[0] == '0') return false;

        var parsed = int.Parse(core);
        if (parsed < 1 || parsed > (plus ? 14 : 15)) return false;

        level = plus ? $"{parsed}+" : parsed.ToString();
        return true;
    }

    private static bool TryParseConstant(string value, out double constant)
    {
        constant = 0;
        var dot = value.IndexOf('.');
        if (dot < 1 || dot != value.LastIndexOf('.')) return false;

        var integer = value[..dot];
        var fraction = value[(dot + 1)..];
        if (integer.Length is 0 or > 2 || !integer.All(char.IsAsciiDigit) || integer[0] == '0') return false;
        if (fraction.Length != 1 || !char.IsAsciiDigit(fraction[0])) return false;

        constant = int.Parse(integer) + (fraction[0] - '0') / 10.0;
        return constant is >= 1 and <= 15;
    }

    private static string DifficultyLabel(int difficultyIndex)
    {
        return difficultyIndex switch
        {
            0 => "BASIC",
            1 => "ADVANCED",
            2 => "EXPERT",
            3 => "MASTER",
            4 => "Re:MASTER",
            _ => throw new ArgumentOutOfRangeException(nameof(difficultyIndex), difficultyIndex, null)
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
    double? MeanCiLow,
    double? MeanCiHigh,
    double Median,
    double? StandardDeviation,
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
    private const int MinimumConfidenceSampleSize = 10;

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
            TryResolveChartMetadata(score, out _, out _, out _) &&
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
        var selected = DistinctScores(selectedScores)
            .Where(score => TryResolveChartMetadata(score, out _, out _, out _))
            .ToList();
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

    public IReadOnlyList<SongScore> FilterScores(
        IEnumerable<SongScore> scores,
        MaiValueAnalysisFilter filter)
    {
        return DistinctScores(scores).Where(score => MatchesFilter(score, filter)).ToList();
    }

    private MaiValueAnalysisItem? CreateItem(
        SongScore score,
        DivingFishChartStatsCatalog fallbackCatalog)
    {
        if (!TryResolveChartMetadata(score, out var song, out var level, out var officialConstant)) return null;

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

    private bool MatchesFilter(SongScore score, MaiValueAnalysisFilter filter)
    {
        if (!TryResolveChartMetadata(score, out _, out var level, out var officialConstant)) return false;
        if (filter.Rank is { } rank && !MaiAchievementRanks.Contains(rank, score.Achievement)) return false;
        if (filter.DifficultyIndex is { } difficultyIndex && score.LevelIdx != difficultyIndex) return false;

        if (filter.Level is { } expectedLevel && !string.Equals(level, expectedLevel, StringComparison.Ordinal))
            return false;
        if (filter.Constant is { } expectedConstant
            && Math.Abs(officialConstant - expectedConstant) >= 0.000001)
            return false;
        return true;
    }

    private bool TryResolveChartMetadata(
        SongScore score,
        out MaiMaiSong? song,
        out string level,
        out double constant)
    {
        song = null;
        level = "";
        constant = 0;
        if (score.Id is <= 0 or > 100000 || score.LevelIdx is < 0 or > 4) return false;

        if (_songs.TryGetValue(score.Id, out song))
        {
            if (score.LevelIdx >= song.Levels.Count
                || score.LevelIdx >= song.Constants.Count
                || score.LevelIdx >= song.Charts.Count) return false;
            level = song.Levels[score.LevelIdx];
            constant = song.Constants[score.LevelIdx];
        }
        else
        {
            level = score.Level ?? "";
            constant = score.Constant;
        }

        return double.IsFinite(constant) && constant > 0;
    }

    private static MaiValueAnalysisStatistics? BuildStatistics(IReadOnlyList<MaiValueAnalysisItem> items)
    {
        if (items.Count == 0) return null;

        var values = items.Select(x => x.Deviation).Order().ToArray();
        var mean = values.Average();
        double? standardDeviation = values.Length == 1
            ? null
            : Math.Sqrt(values.Sum(x => Math.Pow(x - mean, 2)) / values.Length);
        double? ciLow = null;
        double? ciHigh = null;
        if (values.Length >= MinimumConfidenceSampleSize)
        {
            (ciLow, ciHigh) = BootstrapMeanConfidenceInterval(values);
        }

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
