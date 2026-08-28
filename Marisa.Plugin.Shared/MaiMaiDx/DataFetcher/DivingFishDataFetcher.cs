using System.Net;
using System.Runtime.CompilerServices;
using Flurl.Http;
using Marisa.Configuration;
using Marisa.Plugin.Shared.DivingFish;
using Marisa.Plugin.Shared.Util;
using Marisa.Plugin.Shared.Util.SongDb;
using Newtonsoft.Json;

namespace Marisa.Plugin.Shared.MaiMaiDx.DataFetcher;

public class DivingFishDataFetcher : DataFetcher
{
    public const int OldScoreLimit = 35;
    public const int NewScoreLimit = 15;

    // b35 会先 GetRating、再用同一个 Message GetScores。用户名/@他人的公开查询成功后，
    // 后一步绝不能因为 GetScores 的 qqOnly 语义而退回发送者 Bearer token。
    private readonly ConditionalWeakTable<Message, object> _publicOtherQueries = new();

    protected virtual bool OAuthEnabled => DivingFishOAuth.IsConfigured;

    public DivingFishDataFetcher(SongDb<MaiMaiSong> songDb) : base(songDb)
    {
    }

    public override async Task<DxRating> GetRating(Message message)
    {
        var (username, qq) = Chunithm.DataFetcher.DataFetcher.AtOrSelf(message, false);
        var isSelf = username.IsWhiteSpace() && qq == message.Sender.Id;

        // OAuth 模式：查 b50
        if (OAuthEnabled)
        {
            // 1. 优先走公开 /query/player（JSON，无需验证、不耗配额、服务端已截好 b50）
            //    qq 或 username 都能查；只有严格“查自己”时，400/403 才能回落 OAuth。
            try
            {
                var rating = username.IsWhiteSpace()
                    ? ToDxRating(await FetchScoresByQq(qq))
                    : ToDxRating(await FetchScoresByUsername(username));

                if (!isSelf)
                {
                    _publicOtherQueries.GetValue(message, static _ => new object());
                }

                return rating;
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                // Bearer 查询对象由 token 决定。用户名或 @ 他人的查询绝不能拿发送者或目标的 token 回落。
                if (!isSelf) throw;

                // 2. 严格查自己：Bearer /player/records 全量 + 本地分组截取 b35+b15
                return ToDxRating(await FetchScores(message, false));
            }
        }

        // DevToken 模式（废弃端点，过渡期兼容）：全量本地截取
        return ToDxRating(await FetchScores(message, false));
    }

    /// <summary>
    ///     把成绩记录按新旧分组，旧取 35（b35）、新取 15（b15）
    /// </summary>
    private DxRating ToDxRating(DivingFishDxRatingResponse raw)
    {
        // /query/player already returns the server-authoritative b35/b15 split. Preserve it
        // instead of flattening and reclassifying with a possibly stale local song database.
        if (raw.PublicOldScores != null && raw.PublicNewScores != null)
        {
            return new DxRating
            {
                Nickname = raw.Nickname,
                OldScores = raw.PublicOldScores
                    .Where(x => x.Id <= 100000)
                    .OrderByDescending(x => x.Rating)
                    .ThenByDescending(x => x.Id)
                    .Take(OldScoreLimit)
                    .ToList(),
                NewScores = raw.PublicNewScores
                    .Where(x => x.Id <= 100000)
                    .OrderByDescending(x => x.Rating)
                    .ThenByDescending(x => x.Id)
                    .Take(NewScoreLimit)
                    .ToList()
            };
        }

        var group = raw.Records
            .Where(x => x.Id <= 100000 && SongDb.SongIndexer.ContainsKey(x.Id))
            .GroupBy(x => SongDb.SongIndexer[x.Id].Info.IsNew)
            .ToList();

        return new DxRating
        {
            Nickname = raw.Nickname,
            OldScores = group.FirstOrDefault(x => !x.Key)?
                            .OrderByDescending(x => x.Rating)
                            .ThenByDescending(x => x.Id)
                            .Take(OldScoreLimit)
                            .ToList()
                        ?? [],
            NewScores = group.FirstOrDefault(x => x.Key)?
                            .OrderByDescending(x => x.Rating)
                            .ThenByDescending(x => x.Id)
                            .Take(NewScoreLimit)
                            .ToList()
                        ?? []
        };
    }

    public override async Task<Dictionary<(long Id, int LevelIdx), SongScore>> GetScores(Message message)
    {
        if (OAuthEnabled && _publicOtherQueries.TryGetValue(message, out _))
        {
            throw new NotSupportedException("OAuth 不能用 Bearer token 补充用户名或 @ 他人的完整成绩");
        }

        var scores = await FetchScores(message, true);

        return scores.Records
            .ToDictionary(x => (x.Id, x.LevelIdx), x => x);
    }

    public override async Task<(string? Nickname, Dictionary<int, SongScore> Scores)> GetSongScore(Message message, MaiMaiSong song)
    {
        // OAuth 模式：查询对象由 token 决定，body 只带 music_id；DevToken 模式：附带 qq/username
        var (username, qq) = Chunithm.DataFetcher.DataFetcher.AtOrSelf(message, true);
        var isSelf = username.IsWhiteSpace() && qq == message.Sender.Id;

        var body = new Dictionary<string, object> { ["music_id"] = new[] { song.Id } };
        IFlurlResponse response;

        if (OAuthEnabled)
        {
            if (!isSelf)
            {
                throw OAuthSelfOnly();
            }

            response = await SendBearerWithOneRetry(message, "maimai", token =>
                "https://www.diving-fish.com/api/maimaidxprober/player/record"
                    .WithHeader("Authorization", $"Bearer {token}")
                    .AllowHttpStatus("400,401,403,429,503")
                    .PostJsonAsync(body));

            if (IsOAuthError(response.StatusCode))
            {
                var errBody = await response.GetStringAsync();
                throw new HttpRequestException(ProberError.DivingFishOAuth(response.StatusCode, errBody),
                    null, (HttpStatusCode)response.StatusCode);
            }
        }
        else
        {
            if (username.IsWhiteSpace()) body["qq"] = qq;
            else body["username"] = username.ToString();

            response = await "https://www.diving-fish.com/api/maimaidxprober/dev/player/record"
                .WithHeader("Developer-Token", ConfigurationManager.Configuration.DivingFish.DevToken)
                .AllowHttpStatus("400,401,403,410")
                .PostJsonAsync(body);

            if (response.StatusCode is 400 or 401 or 403 or 410)
            {
                var errBody = await response.GetStringAsync();
                throw new HttpRequestException(ProberError.DivingFish(response.StatusCode, errBody),
                    null, (HttpStatusCode)response.StatusCode);
            }
        }

        // 单曲接口返回 { "<music_id>": [ 各难度成绩 ] }，只含已游玩难度，且不含昵称
        var byMusic = await response.GetJsonAsync<Dictionary<string, List<SongScore>>>();

        var scores = byMusic.Values
            .SelectMany(x => x)
            .GroupBy(x => x.LevelIdx)
            .ToDictionary(g => g.Key, g => g.First());

        return (null, scores);
    }

    /// <summary>
    ///     公开端点 /query/player：按用户名查 b50（JSON，无需验证，用户隐私决定可否查询）
    /// </summary>
    protected virtual async Task<DivingFishDxRatingResponse> FetchScoresByUsername(ReadOnlyMemory<char> username)
    {
        var response = await "https://www.diving-fish.com/api/maimaidxprober/query/player"
            .AllowHttpStatus("400,403")
            .PostJsonAsync(new
            {
                username = username.ToString(),
                b50 = true
            });

        if (response.StatusCode is 400 or 403)
        {
            var body = await response.GetStringAsync();
            throw new HttpRequestException(ProberError.DivingFish(response.StatusCode, body),
                null, (HttpStatusCode)response.StatusCode);
        }

        return ToFullResponse(await response.GetJsonAsync<DivingFishDxPublicResponse>());
    }

    /// <summary>
    ///     公开端点 /query/player：按 QQ 号查 b50（JSON，无需验证，用户隐私决定可否查询）
    /// </summary>
    protected virtual async Task<DivingFishDxRatingResponse> FetchScoresByQq(long qq)
    {
        var response = await "https://www.diving-fish.com/api/maimaidxprober/query/player"
            .AllowHttpStatus("400,403")
            .PostJsonAsync(new
            {
                qq,
                b50 = true
            });

        if (response.StatusCode is 400 or 403)
        {
            var body = await response.GetStringAsync();
            throw new HttpRequestException(ProberError.DivingFish(response.StatusCode, body),
                null, (HttpStatusCode)response.StatusCode);
        }

        return ToFullResponse(await response.GetJsonAsync<DivingFishDxPublicResponse>());
    }

    private static DivingFishDxRatingResponse ToFullResponse(DivingFishDxPublicResponse response)
    {
        var records = response.Charts.Sd.Concat(response.Charts.Dx).ToList();
        return new DivingFishDxRatingResponse(
            response.Nickname,
            records,
            response.Charts.Sd,
            response.Charts.Dx);
    }

    protected virtual async Task<DivingFishDxRatingResponse> FetchScores(Message message, bool qqOnly)
    {
        var (username, qq) = Chunithm.DataFetcher.DataFetcher.AtOrSelf(message, qqOnly);

        // OAuth 模式：查询对象由 token 决定，URL 不带 qq/username
        if (OAuthEnabled)
        {
            var isSelf = username.IsWhiteSpace() && qq == message.Sender.Id;
            if (!isSelf)
            {
                throw OAuthSelfOnly();
            }

            var response = await SendBearerWithOneRetry(message, "maimai", token =>
                "https://www.diving-fish.com/api/maimaidxprober/player/records"
                    .WithHeader("Authorization", $"Bearer {token}")
                    .AllowHttpStatus("400,401,403,429,503")
                    .GetAsync());

            if (IsOAuthError(response.StatusCode))
            {
                var body = await response.GetStringAsync();
                throw new HttpRequestException(ProberError.DivingFishOAuth(response.StatusCode, body),
                    null, (HttpStatusCode)response.StatusCode);
            }

            return await response.GetJsonAsync<DivingFishDxRatingResponse>();
        }

        // DevToken 模式（废弃端点，过渡期兼容）
        var uri = username.IsWhiteSpace()
            ? $"https://www.diving-fish.com/api/maimaidxprober/dev/player/records?qq={qq}"
            : $"https://www.diving-fish.com/api/maimaidxprober/dev/player/records?username={username}";

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

        return await devResponse.GetJsonAsync<DivingFishDxRatingResponse>();
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
    ///     读取类 GET/POST 都是幂等操作，因此一次重试不会产生重复写入。
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

    protected sealed record DivingFishDxRatingResponse(
        string Nickname,
        List<SongScore> Records,
        List<SongScore>? PublicOldScores = null,
        List<SongScore>? PublicNewScores = null);

    private sealed class DivingFishDxPublicResponse
    {
        [JsonProperty("nickname")]
        public string Nickname { get; set; } = "";

        [JsonProperty("charts")]
        public DivingFishDxCharts Charts { get; set; } = new();
    }

    private sealed class DivingFishDxCharts
    {
        [JsonProperty("sd")]
        public List<SongScore> Sd { get; set; } = [];

        [JsonProperty("dx")]
        public List<SongScore> Dx { get; set; } = [];
    }
}
