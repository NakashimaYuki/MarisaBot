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

        var sssPlus = MaiValueAnalysisEngine.FilterByRank(scores, MaiAchievementRank.SssPlus);
        var sss = MaiValueAnalysisEngine.FilterByRank(scores, MaiAchievementRank.Sss);

        Assert.Multiple(() =>
        {
            Assert.That(sssPlus.Select(x => x.Id), Is.EqualTo(new long[] { 1 }));
            Assert.That(sss.Select(x => x.Id), Is.EqualTo(new long[] { 2, 3 }));
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

    private static MaiMaiSong CreateSong(long id, double constant)
    {
        dynamic song = new ExpandoObject();
        song.id = id.ToString();
        song.title = $"song-{id}";
        song.type = "DX";

        dynamic info = new ExpandoObject();
        info.title = song.title;
        info.artist = "artist";
        info.genre = "genre";
        info.bpm = 180;
        info.release_date = "2026-01-01";
        info.from = "version";
        info.is_new = false;
        song.basic_info = info;

        song.ds = new[] { constant };
        song.level = new[] { constant.ToString("0.0") };

        dynamic chart = new ExpandoObject();
        chart.notes = new long[] { 100, 10, 10, 0 };
        chart.charter = "tester";
        song.charts = new[] { chart };
        return new MaiMaiSong(song);
    }

    private static SongScore CreateScore(long id, double achievement)
    {
        return new SongScore
        {
            Id = id,
            Type = "DX",
            Constant = 14.0,
            Achievement = achievement,
            LevelIdx = 0,
            Level = "14.0",
            Title = $"song-{id}",
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
