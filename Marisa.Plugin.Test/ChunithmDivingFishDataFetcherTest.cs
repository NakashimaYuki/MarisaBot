using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageSender;
using Marisa.Plugin.Shared.Chunithm;
using Marisa.Plugin.Shared.Chunithm.DataFetcher;
using Marisa.Plugin.Shared.Util.SongDb;
using NUnit.Framework;

namespace Marisa.Plugin.Test;

[NonParallelizable]
public class ChunithmDivingFishDataFetcherTest
{
    [SetUp]
    public void SetUp()
    {
        new DivingFishDataFetcher(CreateSongDb()).Reset();
    }

    [TearDown]
    public void TearDown()
    {
        new DivingFishDataFetcher(CreateSongDb()).Reset();
    }

    [Test]
    public void GetSongList_Should_Return_Songs()
    {
        var fetcher = new DivingFishDataFetcher(CreateSongDb());
        var songs = fetcher.GetSongList();

        Assert.That(songs, Is.Not.Null);
        Assert.That(songs, Is.Not.Empty);

        var first = songs[0];
        Assert.That(first.Id, Is.GreaterThan(0));
        Assert.That(first.Title, Is.Not.Empty);
        Assert.That(first.Artist, Is.Not.Empty);
        Assert.That(first.Genre, Is.Not.Empty);
        Assert.That(first.Version, Is.Not.Empty);
        Assert.That(first.Version, Does.StartWith("CHUNITHM")); // 版本号格式
        Assert.That(first.Constants, Is.Not.Empty);
        Assert.That(first.Levels, Is.Not.Empty);
        Assert.That(first.DiffNames, Is.Not.Empty);
        Assert.That(first.Charters, Is.Not.Empty);
        Assert.That(first.Constants.Count, Is.EqualTo(first.Levels.Count));
        Assert.That(first.Constants.Count, Is.EqualTo(first.DiffNames.Count));

        // 验证难度级别格式（7, 7+, 11, 13+ 等）
        Assert.That(first.Levels, Has.All.Match(@"^\d+\+?$"));

        // 验证定数范围（0.0 ~ 15.4），WE 难度定数为 0
        Assert.That(first.Constants, Has.All.InRange(0.0, 15.4));

        // 验证所有歌曲的 Title 和 Version 都不为空
        Assert.That(songs, Has.All.Matches<ChunithmSong>(s => !string.IsNullOrWhiteSpace(s.Title)));
        Assert.That(songs, Has.All.Matches<ChunithmSong>(s => !string.IsNullOrWhiteSpace(s.Version)));
    }

    [Test]
    public void Parsed_Fields_Should_Match_Raw_Response()
    {
        var raw = "https://maimai.lxns.net/api/v0/chunithm/song/list?notes=true"
            .GetJsonAsync().Result;

        var versionMap = new Dictionary<int, string>();
        foreach (var v in raw.versions)
        {
            versionMap[(int)v.version] = (string)v.title;
        }

        var fetcher = new DivingFishDataFetcher(CreateSongDb());
        var songs = fetcher.GetSongList();

        var rawSongs = ((IEnumerable<dynamic>)raw.songs).ToList();

        // 抽样验证：第1首、第500首、最后1首
        var sampleIndices = new[] { 0, 500, rawSongs.Count - 1 };

        foreach (var i in sampleIndices)
        {
            if (i >= rawSongs.Count) continue;

            var rawSong = rawSongs[i];
            var parsed = songs.FirstOrDefault(s => s.Id == (long)rawSong.id);

            if (parsed == null) continue; // 已删除歌曲会被过滤

            // Title
            Assert.That(parsed.Title, Is.EqualTo((string)rawSong.title));

            // Artist
            Assert.That(parsed.Artist, Is.EqualTo((string)rawSong.artist));

            // Genre
            Assert.That(parsed.Genre, Is.EqualTo((string)rawSong.genre));

            // Version (mapped from version ID)
            var expectedVersion = versionMap.GetValueOrDefault((int)rawSong.version, "");
            Assert.That(parsed.Version, Is.EqualTo(expectedVersion));

            // Difficulties: each difficulty in raw has level + level_value + note_designer
            if (rawSong.difficulties == null) continue;

            var rawDiffs = ((IEnumerable<dynamic>)rawSong.difficulties)
                .OrderBy(d => (int)d.difficulty).ToList();

            Assert.That(rawDiffs, Is.Not.Empty);
            // WE variant 的谱面会合并到原曲中，因此 parsed 可能比 raw 多
            Assert.That(parsed.Constants.Count, Is.GreaterThanOrEqualTo(rawDiffs.Count));
            Assert.That(parsed.Levels.Count, Is.GreaterThanOrEqualTo(rawDiffs.Count));
            Assert.That(parsed.Charters.Count, Is.GreaterThanOrEqualTo(rawDiffs.Count));

            for (var j = 0; j < rawDiffs.Count; j++)
            {
                Assert.That(parsed.Constants[j], Is.EqualTo((double)rawDiffs[j].level_value));
                Assert.That(parsed.Levels[j], Is.EqualTo((string)rawDiffs[j].level));
                Assert.That(parsed.Charters[j], Is.EqualTo((string)rawDiffs[j].note_designer));
            }
        }
    }

    [Test]
    public void GetSongList_Should_Return_Consistent_Results()
    {
        var db = CreateSongDb();
        var fetcher1 = new DivingFishDataFetcher(db);
        var songs1 = fetcher1.GetSongList();

        var fetcher2 = new DivingFishDataFetcher(db);
        var songs2 = fetcher2.GetSongList();

        Assert.That(songs1.Count, Is.EqualTo(songs2.Count));
        Assert.That(songs1[0].Id, Is.EqualTo(songs2[0].Id));
    }

    [Test]
    public async Task GetRating_And_GetScores_Should_Skip_Unmatched_Songs()
    {
        var songDb = CreateSongDb();
        var fetcher = new TestDivingFishDataFetcher(songDb, new ChunithmRating
        {
            Username = "tester",
            Records = new Records
            {
                Best =
                [
                    CreateScore(999, "known-song", 0, 1_009_000, 14.9m),
                    CreateScore(998, "missing-best", 0, 1_000_000, 14.0m)
                ],
                Recent =
                [
                    CreateScore(997, "missing-recent", 0, 1_000_000, 14.0m)
                ]
            }
        });
        var message = new Message(null!, [])
        {
            Sender = new SenderInfo(1, "test")
        };

        var rating = await fetcher.GetRating(message);
        var scores = await fetcher.GetScores(message);

        Assert.Multiple(() =>
        {
            Assert.That(rating.Records.Best.Select(x => x.Id), Is.EqualTo(new[] { 1L }));
            Assert.That(rating.Records.Recent, Is.Empty);
            Assert.That(scores.Keys, Is.EqualTo(new[] { (1L, 0) }));
        });
    }

    [Test]
    public async Task GetRating_Should_Use_Official_Latest_Versions_For_N20()
    {
        var songDb = CreateSongDbWithSongs(
            CreateSong(1, "old-song", "CHUNITHM OLD"),
            CreateSong(2, "new-song", "CHUNITHM CURRENT"));
        var fetcher = new TestDivingFishDataFetcher(songDb, new ChunithmRating
        {
            Username = "tester",
            Records = new Records
            {
                Best =
                [
                    CreateScore(1, "old-song", 0, 1_009_000, 14.9m),
                    CreateScore(2, "new-song", 0, 1_009_000, 14.8m)
                ]
            }
        }, () => Task.FromResult<IReadOnlyCollection<string>>(["CHUNITHM CURRENT"]));

        var rating = await fetcher.GetRating(CreateMessage());

        Assert.Multiple(() =>
        {
            Assert.That(rating.Records.Best.Select(score => score.Id), Is.EqualTo(new[] { 1L }));
            Assert.That(rating.Records.Recent.Select(score => score.Id), Is.EqualTo(new[] { 2L }));
        });
    }

    [Test]
    public async Task LatestVersion_Refresh_Should_Be_SingleFlight()
    {
        var songDb = CreateSongDbWithSongs(CreateSong(2, "new-song", "CHUNITHM CURRENT"));
        var release = new TaskCompletionSource<IReadOnlyCollection<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchCount = 0;
        var fetcher = new TestDivingFishDataFetcher(songDb, new ChunithmRating
        {
            Records = new Records
            {
                Best = [CreateScore(2, "new-song", 0, 1_009_000, 14.8m)]
            }
        }, () =>
        {
            Interlocked.Increment(ref fetchCount);
            return release.Task;
        });

        var requests = Enumerable.Range(0, 8)
            .Select(_ => fetcher.GetRating(CreateMessage()))
            .ToArray();

        await WaitUntil(() => Volatile.Read(ref fetchCount) == 1);
        Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(1));

        release.SetResult(["CHUNITHM CURRENT"]);
        var ratings = await Task.WhenAll(requests);

        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(1));
            Assert.That(ratings, Has.All.Matches<ChunithmRating>(rating =>
                rating.Records.Recent.Select(score => score.Id).SequenceEqual(new[] { 2L })));
        });
    }

    [Test]
    public void LatestVersion_Failure_Without_Cache_Should_Fail_Closed()
    {
        var fetcher = new TestDivingFishDataFetcher(CreateSongDb(), new ChunithmRating
        {
            Records = new Records
            {
                Best = [CreateScore(1, "known-song", 0, 1_009_000, 14.9m)]
            }
        }, () => Task.FromException<IReadOnlyCollection<string>>(
            new HttpRequestException("latest_version unavailable")));

        var error = Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetRating(CreateMessage()));

        Assert.That(error!.Message, Does.Contain("避免错误计算 b30/n20"));
    }

    [Test]
    public async Task LatestVersion_Failure_Should_Only_Use_Recent_Cache()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var fetchCount = 0;
        var fetcher = new TestDivingFishDataFetcher(CreateSongDb(), new ChunithmRating
        {
            Records = new Records
            {
                Best = [CreateScore(1, "known-song", 0, 1_009_000, 14.9m)]
            }
        }, () => Interlocked.Increment(ref fetchCount) == 1
            ? Task.FromResult<IReadOnlyCollection<string>>(["CHUNITHM CURRENT"])
            : Task.FromException<IReadOnlyCollection<string>>(new HttpRequestException("offline")),
            () => now);

        await fetcher.GetRating(CreateMessage());

        now = now.AddHours(2);
        var fallback = await fetcher.GetRating(CreateMessage());
        var cachedFallback = await fetcher.GetRating(CreateMessage());

        Assert.Multiple(() =>
        {
            Assert.That(fetchCount, Is.EqualTo(2));
            Assert.That(fallback.Records.Best.Select(score => score.Id), Is.EqualTo(new[] { 1L }));
            Assert.That(cachedFallback.Records.Best.Select(score => score.Id), Is.EqualTo(new[] { 1L }));
        });

        now = now.AddHours(23);
        var error = Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetRating(CreateMessage()));

        Assert.Multiple(() =>
        {
            Assert.That(fetchCount, Is.EqualTo(3));
            Assert.That(error!.Message, Does.Contain("避免错误计算 b30/n20"));
        });
    }

    private static SongDb<ChunithmSong> CreateSongDb()
    {
        return new SongDb<ChunithmSong>("", "", () => [CreateSong(1, "known-song")]);
    }

    private static SongDb<ChunithmSong> CreateSongDbWithSongs(params ChunithmSong[] songs)
    {
        return new SongDb<ChunithmSong>("", "", () => songs.ToList());
    }

    private static Message CreateMessage()
    {
        return new Message(null!, [])
        {
            Sender = new SenderInfo(1, "test")
        };
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ChunithmSong CreateSong(long id, string title, string version = "CHUNITHM LUMINOUS")
    {
        dynamic song = new ExpandoObject();
        song.Id = id;
        song.Title = title;
        song.Artist = "artist";
        song.Genre = "genre";
        song.Version = version;

        dynamic beatmap = new ExpandoObject();
        beatmap.Constant = 14.9;
        beatmap.Charter = "-";
        beatmap.LevelStr = "14+";
        beatmap.LevelName = "MASTER";
        beatmap.ChartName = "";
        beatmap.Bpm = "200";
        beatmap.MaxCombo = 1000;

        song.Beatmaps = new[] { beatmap };
        return new ChunithmSong(song);
    }

    private static ChunithmScore CreateScore(long id, string title, int levelIndex, int achievement, decimal constant)
    {
        return new ChunithmScore
        {
            Id = id,
            Title = title,
            LevelIndex = levelIndex,
            Achievement = achievement,
            Constant = constant,
            Level = "14+",
            LevelLabel = "MASTER",
            Fc = string.Empty
        };
    }

    private sealed class TestDivingFishDataFetcher(
        SongDb<ChunithmSong> songDb,
        ChunithmRating rating,
        Func<Task<IReadOnlyCollection<string>>>? latestVersions = null,
        Func<DateTimeOffset>? utcNow = null) : DivingFishDataFetcher(songDb)
    {
        private readonly Func<Task<IReadOnlyCollection<string>>> _latestVersions = latestVersions ??
            (() => Task.FromResult<IReadOnlyCollection<string>>(["CHUNITHM CURRENT"]));

        protected override bool OAuthEnabled => false;
        protected override DateTimeOffset UtcNow => utcNow?.Invoke() ?? DateTimeOffset.UtcNow;

        public override List<ChunithmSong> GetSongList()
        {
            return SongDb.SongList;
        }

        protected override Task<ChunithmRating> FetchScores(Message message, bool qqOnly)
        {
            return Task.FromResult(new ChunithmRating
            {
                Username = rating.Username,
                Records = new Records
                {
                    Best = rating.Records.Best.Select(CloneScore).ToArray(),
                    Recent = rating.Records.Recent.Select(CloneScore).ToArray()
                }
            });
        }

        protected override Task<IReadOnlyCollection<string>> FetchLatestVersions()
        {
            return _latestVersions();
        }

        private static ChunithmScore CloneScore(ChunithmScore score)
        {
            return new ChunithmScore
            {
                Id = score.Id,
                Title = score.Title,
                LevelIndex = score.LevelIndex,
                Achievement = score.Achievement,
                Constant = score.Constant,
                Level = score.Level,
                LevelLabel = score.LevelLabel,
                Fc = score.Fc
            };
        }
    }
}
