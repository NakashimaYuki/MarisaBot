using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using NLog;

namespace Marisa.Plugin.Shared.MaiMaiDx;

public sealed class DivingFishChartStatsCatalog
{
    private readonly Dictionary<(long SongId, int LevelIndex), double> _charts;

    private DivingFishChartStatsCatalog(Dictionary<(long SongId, int LevelIndex), double> charts)
    {
        _charts = charts;
    }

    public static DivingFishChartStatsCatalog Empty { get; } = new([]);

    public int Count => _charts.Count;

    public static DivingFishChartStatsCatalog FromJson(string json)
    {
        var response = JsonConvert.DeserializeObject<ChartStatsResponse>(json);
        if (response?.Charts == null) return Empty;

        var charts = new Dictionary<(long SongId, int LevelIndex), double>();
        foreach (var (songIdText, levels) in response.Charts)
        {
            if (!long.TryParse(songIdText, out var songId) || levels == null) continue;

            for (var levelIndex = 0; levelIndex < levels.Count; levelIndex++)
            {
                var fittedDifficulty = levels[levelIndex]?.FittedDifficulty;
                if (fittedDifficulty is > 0 and < 20)
                {
                    charts[(songId, levelIndex)] = fittedDifficulty.Value;
                }
            }
        }

        return new DivingFishChartStatsCatalog(charts);
    }

    public bool TryGet(long songId, int levelIndex, out double fittedDifficulty)
    {
        return _charts.TryGetValue((songId, levelIndex), out fittedDifficulty);
    }

    private sealed class ChartStatsResponse
    {
        [JsonProperty("charts")]
        public Dictionary<string, List<ChartStat?>?>? Charts { get; set; }
    }

    private sealed class ChartStat
    {
        [JsonProperty("fit_diff")]
        public double? FittedDifficulty { get; set; }
    }
}

public sealed class DivingFishChartStatsProvider
{
    private const string Endpoint = "https://www.diving-fish.com/api/maimaidxprober/chart_stats";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Lazy<DivingFishChartStatsProvider> DefaultProvider =
        new(() => new DivingFishChartStatsProvider(SharedHttpClient));

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DivingFishChartStatsCatalog? _catalog;
    private DateTimeOffset _expiresAt;
    private EntityTagHeaderValue? _etag;

    public DivingFishChartStatsProvider(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient  = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static DivingFishChartStatsProvider Default => DefaultProvider.Value;

    public async Task<DivingFishChartStatsCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_catalog != null && now < _expiresAt) return _catalog;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_catalog != null && now < _expiresAt) return _catalog;

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            if (_etag != null) request.Headers.IfNoneMatch.Add(_etag);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && _catalog != null)
            {
                _expiresAt = now + CacheLifetime(response.Headers.CacheControl);
                return _catalog;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var catalog = DivingFishChartStatsCatalog.FromJson(json);
            if (catalog.Count == 0)
            {
                throw new InvalidDataException("DivingFish chart_stats returned no fitted difficulty data");
            }

            _catalog   = catalog;
            _etag      = response.Headers.ETag;
            _expiresAt = now + CacheLifetime(response.Headers.CacheControl);
            return catalog;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.Warn(exception, "Failed to refresh DivingFish chart statistics; using available cached data");
            _catalog ??= DivingFishChartStatsCatalog.Empty;
            _expiresAt = now + TimeSpan.FromMinutes(5);
            return _catalog;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static TimeSpan CacheLifetime(CacheControlHeaderValue? cacheControl)
    {
        return cacheControl?.MaxAge is { } maxAge && maxAge > TimeSpan.Zero
            ? maxAge
            : TimeSpan.FromDays(1);
    }
}
