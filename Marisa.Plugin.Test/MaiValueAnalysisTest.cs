using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Marisa.Plugin.Shared.MaiMaiDx;
using NUnit.Framework;

namespace Marisa.Plugin.Test;

[TestFixture]
public class MaiValueAnalysisTest
{
    private static readonly (string Token, MaiAchievementRank Rank, double Inside, double Outside)[] RankCases =
    [
        ("鸟加", MaiAchievementRank.SssPlus, 100.5, 100.4999),
        ("鸟",   MaiAchievementRank.Sss,     100.0, 100.5),
        ("SS+",  MaiAchievementRank.SsPlus,  99.5, 100.0),
        ("SS",   MaiAchievementRank.Ss,      99.0, 99.5),
        ("S+",   MaiAchievementRank.SPlus,   98.0, 99.0),
        ("S",    MaiAchievementRank.S,       97.0, 98.0),
        ("AAA",  MaiAchievementRank.Aaa,     94.0, 97.0),
        ("AA",   MaiAchievementRank.Aa,      90.0, 94.0),
        ("A",    MaiAchievementRank.A,       80.0, 90.0),
        ("BBB",  MaiAchievementRank.Bbb,     75.0, 80.0),
        ("BB",   MaiAchievementRank.Bb,      70.0, 75.0),
        ("B",    MaiAchievementRank.B,       60.0, 70.0),
        ("C",    MaiAchievementRank.C,       50.0, 60.0),
        ("D",    MaiAchievementRank.D,       49.9999, 50.0)
    ];

    [TestCaseSource(nameof(RankCases))]
    public void Achievement_Rank_Should_Parse_And_Use_Exact_Bounds(
        (string Token, MaiAchievementRank Rank, double Inside, double Outside) testCase)
    {
        Assert.Multiple(() =>
        {
            Assert.That(MaiAchievementRanks.TryParse(testCase.Token, out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(testCase.Rank));
            Assert.That(MaiAchievementRanks.Contains(testCase.Rank, testCase.Inside), Is.True);
            Assert.That(MaiAchievementRanks.Contains(testCase.Rank, testCase.Outside), Is.False);
        });
    }

    [Test]
    public void Value_Analysis_Filter_Should_Parse_Each_Field_And_Arbitrary_Combinations()
    {
        AssertFilter("14+", level: "14+", scope: "全成绩 · 等级 14+");
        AssertFilter("14", level: "14", scope: "全成绩 · 等级 14");
        AssertFilter("13.9", constant: 13.9, scope: "全成绩 · 定数 13.9");
        AssertFilter("MASTER", difficultyIndex: 3, scope: "全成绩 · MASTER");
        AssertFilter("紫谱", difficultyIndex: 3, scope: "全成绩 · MASTER");
        AssertFilter("Re:MASTER", difficultyIndex: 4, scope: "全成绩 · Re:MASTER");
        AssertFilter("白谱", difficultyIndex: 4, scope: "全成绩 · Re:MASTER");
        AssertFilter("Ｒｅ：ＭＡＳＴＥＲ", difficultyIndex: 4, scope: "全成绩 · Re:MASTER");
        AssertFilter("１ １．０", level: "1", constant: 1.0, scope: "全成绩 · 等级 1 · 定数 1.0");
        AssertFilter("1 14.8", level: "1", constant: 14.8, scope: "全成绩 · 等级 1 · 定数 14.8");
        AssertFilter("11.0", constant: 11.0, scope: "全成绩 · 定数 11.0");
        AssertFilter(
            "鸟加 14+ 紫谱",
            MaiAchievementRank.SssPlus,
            "14+",
            3,
            scope: "全成绩 · 达成率 SSS+ · 等级 14+ · MASTER");
        AssertFilter(
            "Re:MASTER 14.8 SSS+",
            MaiAchievementRank.SssPlus,
            difficultyIndex: 4,
            constant: 14.8,
            scope: "全成绩 · 达成率 SSS+ · Re:MASTER · 定数 14.8");
        AssertFilter("B BASIC 14+", MaiAchievementRank.B, "14+", 0);
        AssertFilter(
            "鸟＋14＋白谱14．8",
            MaiAchievementRank.SssPlus,
            "14+",
            4,
            14.8,
            "全成绩 · 达成率 SSS+ · 等级 14+ · Re:MASTER · 定数 14.8");

        static void AssertFilter(
            string input,
            MaiAchievementRank? rank = null,
            string? level = null,
            int? difficultyIndex = null,
            double? constant = null,
            string? scope = null)
        {
            Assert.That(MaiValueAnalysisFilters.TryParse(input, out var filter), Is.True, input);
            Assert.Multiple(() =>
            {
                Assert.That(filter.Rank, Is.EqualTo(rank), input);
                Assert.That(filter.Level, Is.EqualTo(level), input);
                Assert.That(filter.DifficultyIndex, Is.EqualTo(difficultyIndex), input);
                Assert.That(filter.Constant, Is.EqualTo(constant), input);
                if (scope != null) Assert.That(MaiValueAnalysisFilters.Scope(filter), Is.EqualTo(scope), input);
            });
        }
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("双星")]
    [TestCase("SSS++")]
    [TestCase("15+")]
    [TestCase("16")]
    [TestCase("014")]
    [TestCase("14.80")]
    [TestCase("13,9")]
    [TestCase("NaN")]
    [TestCase("紫")]
    [TestCase("白")]
    [TestCase("MASTERPIECE")]
    [TestCase("MASTEREXPERT")]
    [TestCase("14 13+")]
    [TestCase("13.9 14.8")]
    [TestCase("S S")]
    [TestCase("1 4")]
    public void Value_Analysis_Filter_Should_Reject_Unknown_Or_Duplicate_Fields(string input)
    {
        Assert.Multiple(() =>
        {
            Assert.That(MaiValueAnalysisFilters.TryParse(input, out var filter), Is.False);
            Assert.That(filter.IsEmpty, Is.True);
        });
    }

    [Test]
    public void Analysis_Should_Prefer_Curve_And_Fallback_Per_Chart()
    {
        var songs = Enumerable.Range(1, 4).Select(id => CreateSong(id, 14.0)).ToList();
        var curve = RecommendationDifficultyCatalog.FromJson("""
        {
          "1": {"charts":[{"li":0,"ds":14.0,"kind":"fitted_ds","curve":[[15000,14.3]],"pooled":0.3}]},
          "2": {"charts":[{"li":0,"ds":14.0,"kind":"fitted_ds","curve":[[15000,13.8]],"pooled":-0.2}]}
        }
        """);
        var fallback = DivingFishChartStatsCatalog.FromJson("""
        {"charts":{"3":[{"fit_diff":14.4}],"4":[{}]}}
        """);
        var scores = Enumerable.Range(1, 4).Select(id => CreateScore(id, 100.5)).ToList();
        var engine = new MaiValueAnalysisEngine(songs, curve);

        var gold = engine.Build("tester", 15000, "SSS+", scores, MaiValueAnalysisMode.Gold, fallback);
        var water = engine.Build("tester", 15000, "SSS+", scores, MaiValueAnalysisMode.Water, fallback);

        Assert.Multiple(() =>
        {
            Assert.That(engine.RequiresFallback(scores), Is.True);
            Assert.That(gold.SelectedCount, Is.EqualTo(4));
            Assert.That(gold.AnalyzedCount, Is.EqualTo(3));
            Assert.That(gold.CurveCount, Is.EqualTo(2));
            Assert.That(gold.DivingFishCount, Is.EqualTo(1));
            Assert.That(gold.MissingCount, Is.EqualTo(1));
            Assert.That(gold.TopCharts.Select(x => x.SongId), Is.EqualTo(new long[] { 3, 1, 2 }));
            Assert.That(water.TopCharts.Select(x => x.SongId), Is.EqualTo(new long[] { 2, 1, 3 }));
            Assert.That(gold.TopCharts[0].Source, Is.EqualTo("divingFish"));
            Assert.That(gold.TopCharts[1].Source, Is.EqualTo("curve"));
            Assert.That(gold.Statistics!.Mean, Is.EqualTo(1.0 / 6).Within(0.000001));
            Assert.That(gold.Statistics.MeanCiLow, Is.LessThan(gold.Statistics.Mean));
            Assert.That(gold.Statistics.MeanCiHigh, Is.GreaterThan(gold.Statistics.Mean));
        });
    }

    [Test]
    public void Zero_Deviation_Control_Should_Remain_Exactly_Null()
    {
        var curve = RecommendationDifficultyCatalog.FromJson("""
        {"1":{"charts":[{"li":0,"ds":14.0,"kind":"fitted_ds","curve":[[15000,14.0]],"pooled":0.0}]}}
        """);
        var engine = new MaiValueAnalysisEngine([CreateSong(1, 14.0)], curve);

        var result = engine.Build(
            "control", 15000, "B50", [CreateScore(1, 100.5)], MaiValueAnalysisMode.Gold);

        Assert.Multiple(() =>
        {
            Assert.That(engine.RequiresFallback([CreateScore(1, 100.5)]), Is.False);
            Assert.That(result.Statistics!.Mean, Is.Zero);
            Assert.That(result.Statistics.MeanCiLow, Is.Zero);
            Assert.That(result.Statistics.MeanCiHigh, Is.Zero);
            Assert.That(result.TopCharts.Single().Deviation, Is.Zero);
        });
    }

    [Test]
    public void Band_Percentile_Curve_Should_Fallback_To_DivingFish_Fitted_Difficulty()
    {
        var curve = RecommendationDifficultyCatalog.FromJson("""
        {"1":{"charts":[{"li":0,"ds":12.5,"kind":"band_pct","curve":[[12000,72.0]],"band_pct":72.0,"pooled":null}]}}
        """);
        var fallback = DivingFishChartStatsCatalog.FromJson("""
        {"charts":{"1":[{"fit_diff":12.72}]}}
        """);
        var engine = new MaiValueAnalysisEngine([CreateSong(1, 12.5)], curve);
        var score = CreateScore(1, 100.5);
        score.Constant = 12.5;
        score.Level = "12+";

        var result = engine.Build(
            "tester", 13000, "SSS+ · 全成绩", [score], MaiValueAnalysisMode.Gold, fallback);

        Assert.Multiple(() =>
        {
            Assert.That(engine.RequiresFallback([score]), Is.True);
            Assert.That(result.CurveCount, Is.Zero);
            Assert.That(result.DivingFishCount, Is.EqualTo(1));
            Assert.That(result.TopCharts.Single().Source, Is.EqualTo("divingFish"));
            Assert.That(result.TopCharts.Single().Deviation, Is.EqualTo(0.22).Within(0.000001));
        });
    }

    [Test]
    public void Missing_Fits_Control_Should_Not_Produce_A_Statistic()
    {
        var engine = new MaiValueAnalysisEngine(
            [CreateSong(1, 14.0)], RecommendationDifficultyCatalog.Empty);

        var result = engine.Build(
            "control", 15000, "B50", [CreateScore(1, 100.5)], MaiValueAnalysisMode.Gold);

        Assert.Multiple(() =>
        {
            Assert.That(result.AnalyzedCount, Is.Zero);
            Assert.That(result.MissingCount, Is.EqualTo(1));
            Assert.That(result.Statistics, Is.Null);
            Assert.That(result.TopCharts, Is.Empty);
        });
    }

    [Test]
    public void Rank_Filter_Should_Keep_Only_The_Exact_Grade()
    {
        var scores = new[]
        {
            CreateScore(1, 100.5),
            CreateScore(2, 100.4999),
            CreateScore(3, 100.0),
            CreateScore(4, 99.9999)
        };
        var engine = new MaiValueAnalysisEngine([]);

        var sssPlus = engine.FilterScores(scores, new(Rank: MaiAchievementRank.SssPlus));
        var sss = engine.FilterScores(scores, new(Rank: MaiAchievementRank.Sss));

        Assert.Multiple(() =>
        {
            Assert.That(sssPlus.Select(x => x.Id), Is.EqualTo(new long[] { 1 }));
            Assert.That(sss.Select(x => x.Id), Is.EqualTo(new long[] { 2, 3 }));
        });
    }

    [Test]
    public void Score_Filter_Should_Use_And_Semantics_For_All_Four_Dimensions()
    {
        var songs = new[]
        {
            CreateSong(1, 14.8, "14+", 3),
            CreateSong(2, 14.8, "14+", 3),
            CreateSong(3, 14.8, "14", 3),
            CreateSong(4, 14.8, "14+", 4),
            CreateSong(5, 14.7, "14+", 3)
        };
        var scores = new[]
        {
            CreateScore(1, 100.5, 3, "14+", 14.8),
            CreateScore(2, 100.0, 3, "14+", 14.8),
            CreateScore(3, 100.5, 3, "14", 14.8),
            CreateScore(4, 100.5, 4, "14+", 14.8),
            CreateScore(5, 100.5, 3, "14+", 14.7)
        };
        var filter = new MaiValueAnalysisFilter(MaiAchievementRank.SssPlus, "14+", 3, 14.8);
        var engine = new MaiValueAnalysisEngine(songs);

        var result = engine.FilterScores(scores, filter);

        Assert.Multiple(() =>
        {
            Assert.That(result.Select(x => x.Id), Is.EqualTo(new long[] { 1 }));
            Assert.That(engine.FilterScores([scores[0]], filter with { Rank = MaiAchievementRank.Sss }), Is.Empty);
            Assert.That(engine.FilterScores([scores[0]], filter with { Level = "14" }), Is.Empty);
            Assert.That(engine.FilterScores([scores[0]], filter with { DifficultyIndex = 4 }), Is.Empty);
            Assert.That(engine.FilterScores([scores[0]], filter with { Constant = 14.7 }), Is.Empty);
        });
    }

    [Test]
    public void Score_Filter_Should_Prefer_Song_Metadata_And_Fallback_For_Unknown_Songs()
    {
        var songs = new[]
        {
            CreateSong(1, 14.8, "14+", 3),
            CreateSong(2, 13.9, "13+", 3),
            CreateSong(3, 14.8, "14+", 0)
        };
        var scores = new[]
        {
            CreateScore(1, 100.5, 3, "13+", 13.9),
            CreateScore(2, 100.5, 3, "14+", 14.8),
            CreateScore(3, 100.5, 3, "14+", 14.8),
            CreateScore(999, 100.5, 3, "14+", 14.8),
            CreateScore(100001, 100.5, 3, "14+", 14.8)
        };
        var engine = new MaiValueAnalysisEngine(songs, RecommendationDifficultyCatalog.Empty);
        var fallback = DivingFishChartStatsCatalog.FromJson("""
        {"charts":{"999":[{}, {}, {}, {"fit_diff":14.9}]}}
        """);

        var result = engine.FilterScores(scores, new(Level: "14+", DifficultyIndex: 3, Constant: 14.8));
        var invalid = scores.Where(x => x.Id is 3 or 100001).ToList();
        var invalidBuild = engine.Build("tester", 15000, "全成绩", invalid, MaiValueAnalysisMode.Gold);
        var unknown = scores.Single(x => x.Id == 999);
        var unknownBuild = engine.Build("tester", 15000, "全成绩", [unknown], MaiValueAnalysisMode.Gold, fallback);

        Assert.Multiple(() =>
        {
            Assert.That(result.Select(x => x.Id), Is.EqualTo(new long[] { 1, 999 }));
            Assert.That(engine.RequiresFallback(invalid), Is.False);
            Assert.That(invalidBuild.SelectedCount, Is.Zero);
            Assert.That(invalidBuild.MissingCount, Is.Zero);
            Assert.That(engine.RequiresFallback([unknown]), Is.True);
            Assert.That(unknownBuild.SelectedCount, Is.EqualTo(1));
            Assert.That(unknownBuild.AnalyzedCount, Is.EqualTo(1));
            Assert.That(unknownBuild.TopCharts.Single().OfficialConstant, Is.EqualTo(14.8));
            Assert.That(unknownBuild.TopCharts.Single().Source, Is.EqualTo("divingFish"));
        });
    }

    [Test]
    public void Score_Filter_Should_Keep_Sd_And_Dx_Charts_With_The_Same_Title()
    {
        var songs = new[]
        {
            CreateSong(70, 14.8, "14+", 3, "SD", "same-title"),
            CreateSong(10070, 14.8, "14+", 3, "DX", "same-title")
        };
        var scores = new[]
        {
            CreateScore(70, 100.5, 3, "14+", 14.8, "SD", "same-title"),
            CreateScore(10070, 100.5, 3, "14+", 14.8, "DX", "same-title")
        };
        var engine = new MaiValueAnalysisEngine(songs);

        var result = engine.FilterScores(scores, new(Level: "14+", DifficultyIndex: 3, Constant: 14.8));

        Assert.That(result.Select(x => x.Id), Is.EqualTo(new long[] { 70, 10070 }));
    }

    [Test]
    public void Score_Filter_Should_Use_Strict_Constant_Tolerance_And_Reject_Invalid_Indexes()
    {
        var scores = new[]
        {
            CreateScore(1, 100.5, 3, "14+", Math.BitIncrement(14.8)),
            CreateScore(2, 100.5, 3, "14+", 14.8001),
            CreateScore(3, 100.5, 3, "14+", double.NaN),
            CreateScore(4, 100.5, 3, "14+", double.PositiveInfinity),
            CreateScore(5, 100.5, -1, "14+", 14.8),
            CreateScore(6, 100.5, 5, "14+", 14.8)
        };
        var engine = new MaiValueAnalysisEngine([]);

        var result = engine.FilterScores(scores, new(Constant: 14.8));

        Assert.That(result.Select(x => x.Id), Is.EqualTo(new long[] { 1 }));
    }

    [Test]
    public void Score_Filter_Should_Deduplicate_Before_Applying_Achievement_Rank()
    {
        var scores = new[]
        {
            CreateScore(1, 100.0),
            CreateScore(1, 100.5)
        };
        var engine = new MaiValueAnalysisEngine([]);

        var sss = engine.FilterScores(scores, new(Rank: MaiAchievementRank.Sss));
        var sssPlus = engine.FilterScores(scores, new(Rank: MaiAchievementRank.SssPlus));

        Assert.Multiple(() =>
        {
            Assert.That(sss, Is.Empty);
            Assert.That(sssPlus.Select(x => x.Achievement), Is.EqualTo(new[] { 100.5 }));
        });
    }

    [Test]
    public void DivingFish_Catalog_Should_Ignore_Invalid_Entries()
    {
        var catalog = DivingFishChartStatsCatalog.FromJson("""
        {"charts":{"bad":[{"fit_diff":14.1}],"1":null,"2":[{}, {"fit_diff":0}, {"fit_diff":21}],"3":[{"fit_diff":13.3}]}}
        """);

        Assert.Multiple(() =>
        {
            Assert.That(catalog.Count, Is.EqualTo(1));
            Assert.That(catalog.TryGet(3, 0, out var fitted), Is.True);
            Assert.That(fitted, Is.EqualTo(13.3));
            Assert.That(catalog.TryGet(2, 1, out _), Is.False);
        });
    }

    [Test]
    public async Task DivingFish_Provider_Should_Cache_And_Revalidate_With_Etag()
    {
        var handler = new QueueHandler(
            _ =>
            {
                var response = JsonResponse("{\"charts\":{\"1\":[{\"fit_diff\":14.2}]}}");
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                return response;
            },
            request =>
            {
                Assert.That(request.Headers.IfNoneMatch.Single().Tag, Is.EqualTo("\"v1\""));
                var response = new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
                return response;
            });
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        var provider = new DivingFishChartStatsProvider(new HttpClient(handler), time);

        var first = await provider.GetAsync();
        var cached = await provider.GetAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        var revalidated = await provider.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handler.RequestCount, Is.EqualTo(2));
            Assert.That(first, Is.SameAs(cached));
            Assert.That(first, Is.SameAs(revalidated));
            Assert.That(first.TryGet(1, 0, out var fitted), Is.True);
            Assert.That(fitted, Is.EqualTo(14.2));
        });
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private static MaiMaiSong CreateSong(
        long id,
        double constant,
        string? level = null,
        int levelIdx = 0,
        string type = "DX",
        string? title = null)
    {
        dynamic song = new ExpandoObject();
        song.id = id.ToString();
        song.title = title ?? $"song-{id}";
        song.type = type;

        dynamic info = new ExpandoObject();
        info.title = song.title;
        info.artist = "artist";
        info.genre = "genre";
        info.bpm = 180;
        info.release_date = "2026-01-01";
        info.from = "version";
        info.is_new = false;
        song.basic_info = info;

        var chartCount = levelIdx + 1;
        var constants = Enumerable.Repeat(1.0, chartCount).ToArray();
        var levels = Enumerable.Repeat("1", chartCount).ToArray();
        constants[levelIdx] = constant;
        levels[levelIdx] = level ?? DefaultLevel(constant);
        song.ds = constants;
        song.level = levels;

        var charts = new List<dynamic>();
        for (var i = 0; i < chartCount; i++)
        {
            dynamic chart = new ExpandoObject();
            chart.notes = new long[] { 100, 10, 10, 0 };
            chart.charter = "tester";
            charts.Add(chart);
        }
        song.charts = charts;
        return new MaiMaiSong(song);

        static string DefaultLevel(double value)
        {
            var integer = (int)Math.Floor(value);
            var tenths = (int)Math.Round((value - integer) * 10, MidpointRounding.AwayFromZero);
            return tenths >= 6 ? $"{integer}+" : integer.ToString();
        }
    }

    private static SongScore CreateScore(
        long id,
        double achievement,
        int levelIdx = 0,
        string level = "14",
        double constant = 14.0,
        string type = "DX",
        string? title = null)
    {
        return new SongScore
        {
            Id = id,
            Type = type,
            Constant = constant,
            Achievement = achievement,
            LevelIdx = levelIdx,
            Level = level,
            Title = title ?? $"song-{id}",
            Fc = "",
            Fs = ""
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
