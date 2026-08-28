using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Marisa.Plugin.Shared.DivingFish;

public static class DivingFishPendingAuth
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GenerationLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, PendingState> Store = new();
    private static readonly ConcurrentDictionary<long, LatestGeneration> LatestByQq = new();
    private static readonly object GenerationGate = new();

    private static readonly Timer CleanupTimer = new(
        _ => TryCleanupFromTimer(), null, CleanupInterval, CleanupInterval);

    private static long _nextOperationCleanupTicks;

    public enum AcquireStatus
    {
        Acquired,
        NotFound,
        Expired,
        Superseded,
        InProgress
    }

    public sealed record PendingStart(
        string State,
        string CodeVerifier,
        string CodeChallenge,
        string Generation,
        DateTimeOffset ExpiresAt);

    public sealed class PendingEntry
    {
        internal PendingEntry(
            string state,
            string leaseId,
            long qq,
            long groupId,
            string codeVerifier,
            string game,
            string generation,
            DateTimeOffset expiresAt)
        {
            State = state;
            LeaseId = leaseId;
            Qq = qq;
            GroupId = groupId;
            CodeVerifier = codeVerifier;
            Game = game;
            Generation = generation;
            ExpiresAt = expiresAt;
        }

        public long Qq { get; }

        public long GroupId { get; }

        public string CodeVerifier { get; }

        public string Game { get; }

        public string Generation { get; }

        public DateTimeOffset ExpiresAt { get; }

        internal string State { get; }

        internal string LeaseId { get; }
    }

    public readonly record struct AcquireResult(AcquireStatus Status, PendingEntry? Entry)
    {
        public bool IsAcquired => Status == AcquireStatus.Acquired && Entry is not null;
    }

    private sealed record PendingState(
        long Qq,
        long GroupId,
        string CodeVerifier,
        string Game,
        string Generation,
        DateTimeOffset ExpiresAt,
        string? LeaseId = null);

    private sealed record LatestGeneration(string Generation, string State, DateTimeOffset ExpiresAt);

    public static PendingStart Begin(long qq, long groupId, string game)
    {
        if (qq <= 0) throw new ArgumentOutOfRangeException(nameof(qq));
        if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));
        if (string.IsNullOrWhiteSpace(game)) throw new ArgumentException("游戏标识不能为空", nameof(game));

        _ = DivingFishOAuth.ScopeOf(game);

        CleanupExpiredIfDue();

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(PendingLifetime);
        var generation = RandomToken(16);
        var codeVerifier = RandomToken(32);
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var normalizedGame = game.Trim().ToLowerInvariant();

        string state;
        lock (GenerationGate)
        {
            do
            {
                state = RandomToken(32);
            } while (!Store.TryAdd(state, new PendingState(
                         qq, groupId, codeVerifier, normalizedGame, generation, expiresAt)));

            var latest = new LatestGeneration(generation, state, now.Add(GenerationLifetime));
            if (LatestByQq.TryGetValue(qq, out var previous))
            {
                Store.TryRemove(previous.State, out _);
            }

            LatestByQq[qq] = latest;
        }

        return new PendingStart(state, codeVerifier, codeChallenge, generation, expiresAt);
    }

    public static AcquireResult AcquireForCallback(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return new AcquireResult(AcquireStatus.NotFound, null);

        CleanupExpiredIfDue();

        while (Store.TryGetValue(state, out var current))
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= current.ExpiresAt)
            {
                TryRemoveExact(Store, state, current);
                return new AcquireResult(AcquireStatus.Expired, null);
            }

            if (!IsCurrentGeneration(current.Qq, current.Generation))
            {
                TryRemoveExact(Store, state, current);
                return new AcquireResult(AcquireStatus.Superseded, null);
            }

            if (current.LeaseId is not null)
            {
                return new AcquireResult(AcquireStatus.InProgress, null);
            }

            var leaseId = RandomToken(16);
            var claimed = current with { LeaseId = leaseId };
            if (!Store.TryUpdate(state, claimed, current)) continue;

            return new AcquireResult(
                AcquireStatus.Acquired,
                new PendingEntry(
                    state,
                    leaseId,
                    current.Qq,
                    current.GroupId,
                    current.CodeVerifier,
                    current.Game,
                    current.Generation,
                    current.ExpiresAt));
        }

        return new AcquireResult(AcquireStatus.NotFound, null);
    }

    public static bool Release(PendingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CleanupExpiredIfDue();

        while (Store.TryGetValue(entry.State, out var current))
        {
            if (!LeaseMatches(current, entry)) return false;

            if (DateTimeOffset.UtcNow >= current.ExpiresAt ||
                !IsCurrentGeneration(current.Qq, current.Generation))
            {
                TryRemoveExact(Store, entry.State, current);
                return false;
            }

            if (Store.TryUpdate(entry.State, current with { LeaseId = null }, current)) return true;
        }

        return false;
    }

    public static bool IsCurrentGeneration(long qq, string generation)
    {
        if (qq <= 0 || string.IsNullOrWhiteSpace(generation)) return false;
        CleanupExpiredIfDue();

        if (!LatestByQq.TryGetValue(qq, out var latest)) return false;
        if (DateTimeOffset.UtcNow < latest.ExpiresAt)
            return string.Equals(latest.Generation, generation, StringComparison.Ordinal);

        if (TryRemoveExact(LatestByQq, qq, latest)) Store.TryRemove(latest.State, out _);
        return false;
    }

    internal static bool TryComplete(PendingEntry entry, DateTimeOffset confirmationExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CleanupExpiredIfDue();

        if (!Store.TryGetValue(entry.State, out var current) || !LeaseMatches(current, entry)) return false;
        if (DateTimeOffset.UtcNow >= current.ExpiresAt)
        {
            TryRemoveExact(Store, entry.State, current);
            return false;
        }

        if (!TryExtendCurrentGeneration(current.Qq, current.Generation, confirmationExpiresAt))
        {
            TryRemoveExact(Store, entry.State, current);
            return false;
        }

        if (!TryRemoveExact(Store, entry.State, current)) return false;

        return IsCurrentGeneration(current.Qq, current.Generation);
    }

    private static bool TryExtendCurrentGeneration(long qq, string generation, DateTimeOffset expiresAt)
    {
        while (LatestByQq.TryGetValue(qq, out var current))
        {
            if (!string.Equals(current.Generation, generation, StringComparison.Ordinal)) return false;
            if (DateTimeOffset.UtcNow >= current.ExpiresAt) return false;
            if (current.ExpiresAt >= expiresAt) return true;

            if (LatestByQq.TryUpdate(qq, current with { ExpiresAt = expiresAt }, current)) return true;
        }

        return false;
    }

    private static bool LeaseMatches(PendingState state, PendingEntry entry)
    {
        return string.Equals(state.LeaseId, entry.LeaseId, StringComparison.Ordinal) &&
               string.Equals(state.Generation, entry.Generation, StringComparison.Ordinal) &&
               state.Qq == entry.Qq;
    }

    private static void CleanupExpiredIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        var nowTicks = now.UtcDateTime.Ticks;
        var nextTicks = Volatile.Read(ref _nextOperationCleanupTicks);
        if (nowTicks < nextTicks) return;

        var newNextTicks = now.Add(CleanupInterval).UtcDateTime.Ticks;
        if (Interlocked.CompareExchange(ref _nextOperationCleanupTicks, newNextTicks, nextTicks) != nextTicks) return;
        CleanupExpired(now);
    }

    private static void TryCleanupFromTimer()
    {
        try
        {
            CleanupExpired(DateTimeOffset.UtcNow);
        }
        catch
        {
        }
    }

    private static void CleanupExpired(DateTimeOffset now)
    {
        lock (GenerationGate)
        {
            foreach (var pair in LatestByQq)
            {
                if (now < pair.Value.ExpiresAt) continue;
                if (TryRemoveExact(LatestByQq, pair.Key, pair.Value)) Store.TryRemove(pair.Value.State, out _);
            }

            foreach (var pair in Store)
            {
                var entry = pair.Value;
                var isCurrent = LatestByQq.TryGetValue(entry.Qq, out var latest) &&
                                now < latest.ExpiresAt &&
                                string.Equals(latest.Generation, entry.Generation, StringComparison.Ordinal);
                if (now >= entry.ExpiresAt || !isCurrent) TryRemoveExact(Store, pair.Key, entry);
            }
        }
    }

    private static bool TryRemoveExact<TKey, TValue>(ConcurrentDictionary<TKey, TValue> store, TKey key, TValue value)
        where TKey : notnull
    {
        return ((ICollection<KeyValuePair<TKey, TValue>>)store).Remove(new KeyValuePair<TKey, TValue>(key, value));
    }

    private static string RandomToken(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
