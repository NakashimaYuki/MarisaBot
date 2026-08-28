using System.Net;
using Flurl.Http;
using Marisa.Configuration;
using Marisa.Plugin.Shared.DivingFish;
using Marisa.Plugin.Shared.Interface;
using Marisa.Plugin.Shared.Util;
using Marisa.Plugin.Shared.Util.SongDb;
using Newtonsoft.Json;

namespace Marisa.Plugin.Shared.Chunithm.DataFetcher;

public class DivingFishDataFetcher(SongDb<ChunithmSong> songDb) : DataFetcher(songDb), ICanReset
{
    private static readonly TimeSpan LatestVersionCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan LatestVersionMaxStale = TimeSpan.FromHours(24);
    private static readonly TimeSpan LatestVersionRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly object LatestVersionCacheGate = new();
    private static LatestVersionCacheEntry? _latestVersionCache;
    private static Task<LatestVersionCacheEntry>? _latestVersionRefresh;
    private static DateTimeOffset _latestVersionRetryAfter;
    private static long _latestVersionCacheGeneration;

    private Dictionary<string, ChunithmSong>? _songTitleIndexer;

    protected virtual bool OAuthEnabled => DivingFishOAuth.IsConfigured;
    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    private Dictionary<string, ChunithmSong> SongTitleIndexer => _songTitleIndexer ??= GetSongList()
        .GroupBy(song => song.Title, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public override List<ChunithmSong> GetSongList()
    {
        return LxnsDataFetcher.GetSharedSongList();
    }

    public override async Task<ChunithmRating> GetRating(Message message)
    {
        var (username, qq) = AtOrSelf(message, false);

        // 是否"查自己"：没给用户名、没@别人（qq 即发送者）
        var isSelf = username.IsWhiteSpace() && qq == message.Sender.Id;

        // OAuth 模式：查 b30+n20
        if (OAuthEnabled)
        {
            // 1. 优先走公开 /query/player（JSON，无需验证、不耗配额、服务端已截好 b30+n20）
            //    qq 或 username 都能查；400 user not exists（QQ 未绑定）/403 隐私
            try
            {
                var raw = username.IsWhiteSpace()
                    ? await FetchScoresByQq(qq)
                    : await FetchScoresByUsername(username);

                raw.DataSource = "DivingFish";
                raw.Records.Best = NormalizeRecords(raw.Records.Best).Where(x => !DeletedSongs.Contains(x.Id)).ToArray();
                // 2026 版公开响应把新版本 Best 20 放在 n20，r10 保留为空数组。
                var newBest = raw.Records.N20.Length > 0 ? raw.Records.N20 : raw.Records.Recent;
                raw.Records.Recent = NormalizeRecords(newBest).Where(x => !DeletedSongs.Contains(x.Id)).ToArray();
                return raw;
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                // 2. 只有"查自己"才回落 OAuth：Bearer /player/records 全量 + 本地截取 b30+n20。
                //    @别人 / 用户名查询是查他人数据，OAuth token 只代表发送者本人，不能代他人换票，
                //    直接抛出公开接口的错误（QQ 未绑定 / 未公开成绩）。
                if (!isSelf) throw;

                var json = await FetchScores(message, false);
                json.DataSource = "DivingFish";
                json.Records.Best = NormalizeRecords(json.Records.Best).Where(x => !DeletedSongs.Contains(x.Id)).ToArray();
                json.Records.Recent = NormalizeRecords(json.Records.Recent).ToArray();
                return await GroupBestAndRecent(json);
            }
        }

        // DevToken 模式（废弃端点，过渡期兼容）：完整成绩按版本分组截取
        var devJson = await FetchScores(message, false);
        devJson.DataSource = "DivingFish";
        devJson.Records.Best = NormalizeRecords(devJson.Records.Best).Where(x => !DeletedSongs.Contains(x.Id)).ToArray();
        devJson.Records.Recent = NormalizeRecords(devJson.Records.Recent).ToArray();

        return await GroupBestAndRecent(devJson);
    }

    /// <summary>
    ///     把完整成绩按版本新旧分组：旧版本取 rating 前 30 作为 Best，新版本取前 20 作为 Recent。
    ///     水鱼 OAuth 的 /player/records 返回全量成绩，必须截取，否则前端会渲染全部记录。
    /// </summary>
    private async Task<ChunithmRating> GroupBestAndRecent(ChunithmRating raw)
    {
        var allScores = raw.Records.Best.Concat(raw.Records.Recent);

        var songList = GetSongList();
        var versionMap = songList.ToDictionary(s => s.Id, s => s.Version);
        var newest = await GetLatestVersions();

        var div = allScores
            .GroupBy(x => newest.Contains(versionMap.GetValueOrDefault(x.Id, "")))
            .ToList();

        return new ChunithmRating
        {
            DataSource = raw.DataSource,
            Username = raw.Username,
            Records = new Records
            {
                Best = div.FirstOrDefault(x => !x.Key)?
                           .OrderByDescending(x => x.Rating).Take(30).ToArray() ?? [],
                Recent = div.FirstOrDefault(x => x.Key)?
                             .OrderByDescending(x => x.Rating).Take(20).ToArray() ?? []
            }
        };
    }

    /// <summary>
    ///     水鱼以此公开端点声明当前应计入 n20 的版本。版本轮换时不能依赖客户端硬编码。
    /// </summary>
    protected virtual async Task<IReadOnlyCollection<string>> FetchLatestVersions()
    {
        var response = await "https://www.diving-fish.com/api/chunithmprober/latest_version"
            .GetJsonAsync<LatestVersionResponse>();

        return response.Versions;
    }

    /// <summary>
    ///     缓存当前版本，避免每次 b30 查询都访问水鱼；过期刷新采用进程级 single-flight。
    ///     刷新失败时只允许短期使用最近一次成功值，超过 24 小时则显式失败，避免静默错分 b30/n20。
    /// </summary>
    private async Task<IReadOnlySet<string>> GetLatestVersions()
    {
        Task<LatestVersionCacheEntry> refresh;
        long generation;
        var now = UtcNow;

        lock (LatestVersionCacheGate)
        {
            if (_latestVersionCache is { } cached)
            {
                var age = now - cached.FetchedAt;
                if (age <= LatestVersionCacheTtl ||
                    age <= LatestVersionMaxStale && now < _latestVersionRetryAfter)
                {
                    return cached.Versions;
                }
            }

            generation = _latestVersionCacheGeneration;
            refresh = _latestVersionRefresh ??= RefreshLatestVersions(
                FetchLatestVersions,
                () => UtcNow,
                generation);
        }

        try
        {
            return (await refresh).Versions;
        }
        catch (Exception e)
        {
            var failedAt = UtcNow;
            lock (LatestVersionCacheGate)
            {
                if (_latestVersionCacheGeneration == generation &&
                    _latestVersionCache is { } cached &&
                    failedAt - cached.FetchedAt <= LatestVersionMaxStale)
                {
                    if (ReferenceEquals(_latestVersionRefresh, refresh))
                    {
                        _latestVersionRetryAfter = failedAt + LatestVersionRetryDelay;
                    }

                    return cached.Versions;
                }
            }

            throw new HttpRequestException(
                "无法获取水鱼当前新曲版本，为避免错误计算 b30/n20，已停止本次查询", e);
        }
        finally
        {
            lock (LatestVersionCacheGate)
            {
                if (ReferenceEquals(_latestVersionRefresh, refresh)) _latestVersionRefresh = null;
            }
        }
    }

    private static async Task<LatestVersionCacheEntry> RefreshLatestVersions(
        Func<Task<IReadOnlyCollection<string>>> fetch,
        Func<DateTimeOffset> utcNow,
        long generation)
    {
        // 不在缓存锁内执行可重写的网络方法。
        await Task.Yield();
        var rawVersions = await fetch();
        var versions = rawVersions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (versions.Count == 0)
        {
            throw new InvalidDataException("水鱼 latest_version 返回了空版本列表");
        }

        var entry = new LatestVersionCacheEntry(versions, utcNow());
        lock (LatestVersionCacheGate)
        {
            // Reset 后旧请求可以完成其原始调用，但不得覆盖新一代缓存。
            if (_latestVersionCacheGeneration == generation)
            {
                _latestVersionCache = entry;
                _latestVersionRetryAfter = DateTimeOffset.MinValue;
            }
        }

        return entry;
    }

    public override async Task<Dictionary<(long Id, int LevelIdx), ChunithmScore>> GetScores(Message message)
    {
        var scores = await GetScoresCore(message, true);

        return scores.Records.Best
            .Where(x => !DeletedSongs.Contains(x.Id))
            .ToDictionary(x => (x.Id, (int)x.LevelIndex), x => x);
    }

    private async Task<ChunithmRating> GetScoresCore(Message message, bool qqOnly)
    {
        var json = await FetchScores(message, qqOnly);
        json.DataSource = "DivingFish";
        json.Records.Best = NormalizeRecords(json.Records.Best).Where(x => !DeletedSongs.Contains(x.Id)).ToArray();
        json.Records.Recent = NormalizeRecords(json.Records.Recent).ToArray();

        return json;
    }

    /// <summary>
    ///     公开端点 /query/player：按用户名查 b30+n20（JSON，无需验证）
    /// </summary>
    protected virtual async Task<ChunithmRating> FetchScoresByUsername(ReadOnlyMemory<char> username)
    {
        var response = await "https://www.diving-fish.com/api/chunithmprober/query/player"
            .AllowHttpStatus("400,403")
            .PostJsonAsync(new
            {
                username = username.ToString()
            });

        if (response.StatusCode is 400 or 403)
        {
            var body = await response.GetStringAsync();
            throw new HttpRequestException(ProberError.DivingFish(response.StatusCode, body),
                null, (HttpStatusCode)response.StatusCode);
        }

        return await response.GetJsonAsync<ChunithmRating>();
    }

    /// <summary>
    ///     公开端点 /query/player：按 QQ 号查 b30+n20（JSON，无需验证）
    /// </summary>
    protected virtual async Task<ChunithmRating> FetchScoresByQq(long qq)
    {
        var response = await "https://www.diving-fish.com/api/chunithmprober/query/player"
            .AllowHttpStatus("400,403")
            .PostJsonAsync(new
            {
                qq
            });

        if (response.StatusCode is 400 or 403)
        {
            var body = await response.GetStringAsync();
            throw new HttpRequestException(ProberError.DivingFish(response.StatusCode, body),
                null, (HttpStatusCode)response.StatusCode);
        }

        return await response.GetJsonAsync<ChunithmRating>();
    }

    protected virtual async Task<ChunithmRating> FetchScores(Message message, bool qqOnly)
    {
        var (username, qq) = AtOrSelf(message, qqOnly);
        var isSelf = username.IsWhiteSpace() && qq == message.Sender.Id;

        // OAuth 模式：查询对象由 token 决定，URL 不带 qq/username
        if (OAuthEnabled)
        {
            if (!isSelf)
            {
                throw OAuthSelfOnly();
            }

            var response = await SendBearerWithOneRetry(message, "chunithm", token =>
                "https://www.diving-fish.com/api/chunithmprober/player/records"
                    .WithHeader("Authorization", $"Bearer {token}")
                    .AllowHttpStatus("400,401,403,429,503")
                    .GetAsync());

            if (IsOAuthError(response.StatusCode))
            {
                var body = await response.GetStringAsync();
                throw new HttpRequestException(ProberError.DivingFishOAuth(response.StatusCode, body),
                    null, (HttpStatusCode)response.StatusCode);
            }

            return await response.GetJsonAsync<ChunithmRating>();
        }

        // DevToken 模式（废弃端点，过渡期兼容）
        var uri = username.IsWhiteSpace()
            ? $"https://www.diving-fish.com/api/chunithmprober/dev/player/records?qq={qq}"
            : $"https://www.diving-fish.com/api/chunithmprober/dev/player/records?username={username}";

        var devResponse = await uri
            .WithHeader("Developer-Token", ConfigurationManager.Configuration.DivingFish.DevToken)
            .AllowHttpStatus("400,401,403,410")
            .GetAsync();

        if (devResponse.StatusCode is 400 or 401 or 403 or 410)
        {
            var body = await devResponse.GetStringAsync();
            throw new HttpRequestException(ProberError.DivingFish(devResponse.StatusCode, body),
                null, (HttpStatusCode)devResponse.StatusCode);
        }

        return await devResponse.GetJsonAsync<ChunithmRating>();
    }

    /// <summary>
    ///     获取严格属于消息发送者的 OAuth token。
    ///     调用方必须先确认请求没有用户名或 @ 他人选择器。
    /// </summary>
    private static async Task<string> GetRequiredToken(Message message, string game)
    {
        var token = (await DivingFishTokenStore.GetValidToken(message.Sender.Id, game))?.AccessToken;
        if (token != null) return token;
        throw new HttpRequestException("未绑定水鱼查分器，请先使用 bind 命令完成绑定后再查询");
    }

    /// <summary>
    ///     资源端点返回 401 时，清除 access-token 缓存并重新换票后至多重试一次。
    /// </summary>
    private static async Task<IFlurlResponse> SendBearerWithOneRetry(
        Message message,
        string game,
        Func<string, Task<IFlurlResponse>> send)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetRequiredToken(message, game);

            var response = await send(token);
            if (response.StatusCode != (int)HttpStatusCode.Unauthorized) return response;

            DivingFishTokenStore.RemoveToken(message.Sender.Id, game);
            if (attempt == 1) return response;
        }

        throw new InvalidOperationException("DivingFish OAuth retry loop exited unexpectedly");
    }

    private static bool IsOAuthError(int statusCode) =>
        statusCode is 400 or 401 or 403 or 429 or 503;

    private static HttpRequestException OAuthSelfOnly() =>
        new("水鱼 OAuth 只能读取发送者本人的完整成绩；查询用户名或 @ 他人仅支持公开成绩");

    private IEnumerable<ChunithmScore> NormalizeRecords(IEnumerable<ChunithmScore> records)
    {
        foreach (var record in records)
        {
            if (SongDb.SongIndexer.ContainsKey(record.Id))
            {
                yield return record;
                continue;
            }

            if (!SongTitleIndexer.TryGetValue(record.Title, out var matchedSong)) continue;

            record.Id = matchedSong.Id;
            yield return record;
        }
    }

    public void Reset()
    {
        _songTitleIndexer = null;

        lock (LatestVersionCacheGate)
        {
            _latestVersionCacheGeneration++;
            _latestVersionCache = null;
            _latestVersionRefresh = null;
            _latestVersionRetryAfter = DateTimeOffset.MinValue;
        }
    }

    private sealed record LatestVersionCacheEntry(
        IReadOnlySet<string> Versions,
        DateTimeOffset FetchedAt);

    private sealed class LatestVersionResponse
    {
        [JsonProperty("version", Required = Required.Always)]
        public string[] Versions { get; set; } = [];
    }
}
