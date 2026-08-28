using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Marisa.Plugin.Shared.DivingFish;

public static class DivingFishPendingAuth
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, PendingState> Store = new();
    private static readonly Timer CleanupTimer = new(
        _ => TryCleanupFromTimer(), null, CleanupInterval, CleanupInterval);

    private static long _nextOperationCleanupTicks;

    public enum AcquireStatus
    {
        Acquired,
        NotFound,
        Expired,
        InProgress
    }

    public sealed record PendingStart(string State, string CodeChallenge);

    public sealed class PendingEntry
    {
        internal PendingEntry(
            string state,
            string leaseId,
            string codeVerifier,
            string game,
            DateTimeOffset expiresAt)
        {
            State = state;
            LeaseId = leaseId;
            CodeVerifier = codeVerifier;
            Game = game;
            ExpiresAt = expiresAt;
        }

        public string CodeVerifier { get; }

        public string Game { get; }

        public DateTimeOffset ExpiresAt { get; }

        internal string State { get; }

        internal string LeaseId { get; }
    }

    public readonly record struct AcquireResult(AcquireStatus Status, PendingEntry? Entry)
    {
        public bool IsAcquired => Status == AcquireStatus.Acquired && Entry is not null;
    }

    private sealed record PendingState(
        string CodeVerifier,
        string Game,
        DateTimeOffset ExpiresAt,
        string? LeaseId = null);

    public static PendingStart Begin(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) throw new ArgumentException("游戏标识不能为空", nameof(game));

        _ = DivingFishOAuth.ScopeOf(game);
        CleanupExpiredIfDue();

        var expiresAt = DateTimeOffset.UtcNow.Add(PendingLifetime);
        var codeVerifier = RandomToken(32);
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var normalizedGame = game.Trim().ToLowerInvariant();

        string state;
        do
        {
            state = RandomToken(32);
        } while (!Store.TryAdd(state, new PendingState(codeVerifier, normalizedGame, expiresAt)));

        return new PendingStart(state, codeChallenge);
    }

    public static AcquireResult AcquireForCallback(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return new AcquireResult(AcquireStatus.NotFound, null);

        CleanupExpiredIfDue();

        while (Store.TryGetValue(state, out var current))
        {
            if (DateTimeOffset.UtcNow >= current.ExpiresAt)
            {
                TryRemoveExact(Store, state, current);
                return new AcquireResult(AcquireStatus.Expired, null);
            }

            if (current.LeaseId is not null) return new AcquireResult(AcquireStatus.InProgress, null);

            var leaseId = RandomToken(16);
            var claimed = current with { LeaseId = leaseId };
            if (!Store.TryUpdate(state, claimed, current)) continue;

            return new AcquireResult(
                AcquireStatus.Acquired,
                new PendingEntry(state, leaseId, current.CodeVerifier, current.Game, current.ExpiresAt));
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
            if (DateTimeOffset.UtcNow >= current.ExpiresAt)
            {
                TryRemoveExact(Store, entry.State, current);
                return false;
            }

            if (Store.TryUpdate(entry.State, current with { LeaseId = null }, current)) return true;
        }

        return false;
    }

    internal static bool TryComplete(PendingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CleanupExpiredIfDue();

        if (!Store.TryGetValue(entry.State, out var current) || !LeaseMatches(current, entry)) return false;
        if (DateTimeOffset.UtcNow >= current.ExpiresAt)
        {
            TryRemoveExact(Store, entry.State, current);
            return false;
        }

        return TryRemoveExact(Store, entry.State, current);
    }

    private static bool LeaseMatches(PendingState state, PendingEntry entry) =>
        string.Equals(state.LeaseId, entry.LeaseId, StringComparison.Ordinal);

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
        foreach (var pair in Store)
        {
            if (now >= pair.Value.ExpiresAt) TryRemoveExact(Store, pair.Key, pair.Value);
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
