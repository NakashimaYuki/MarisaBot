using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Marisa.Plugin.Shared.DivingFish;

public static class DivingFishBindingConfirmation
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, ConfirmationEntry> Store = new();
    private static readonly Timer CleanupTimer = new(
        _ => TryCleanupFromTimer(), null, CleanupInterval, CleanupInterval);

    private static long _nextOperationCleanupTicks;

    public enum ConsumeStatus
    {
        Success,
        NotFound,
        Expired
    }

    public sealed record ConfirmationEntry(
        string Sub,
        string Username,
        string Game,
        string Scope,
        DateTimeOffset ExpiresAt);

    public readonly record struct ConsumeResult(ConsumeStatus Status, ConfirmationEntry? Entry)
    {
        public bool IsSuccess => Status == ConsumeStatus.Success && Entry is not null;
    }

    public static string? Issue(
        DivingFishPendingAuth.PendingEntry pending,
        string sub,
        string username,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (string.IsNullOrWhiteSpace(sub)) throw new ArgumentException("水鱼 sub 不能为空", nameof(sub));
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("scope 不能为空", nameof(scope));
        if (!DivingFishPendingAuth.TryComplete(pending)) return null;

        CleanupExpiredIfDue();

        var entry = new ConfirmationEntry(
            sub,
            username ?? "",
            pending.Game,
            scope,
            DateTimeOffset.UtcNow.Add(Lifetime));

        string code;
        string codeHash;
        do
        {
            code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            codeHash = Sha256Hex(code);
        } while (!Store.TryAdd(codeHash, entry));

        return code;
    }

    public static ConsumeResult Consume(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return new ConsumeResult(ConsumeStatus.NotFound, null);

        var codeHash = Sha256Hex(code.Trim().ToUpperInvariant());
        if (!Store.TryRemove(codeHash, out var entry))
        {
            CleanupExpiredIfDue();
            return new ConsumeResult(ConsumeStatus.NotFound, null);
        }

        CleanupExpiredIfDue();
        return DateTimeOffset.UtcNow >= entry.ExpiresAt
            ? new ConsumeResult(ConsumeStatus.Expired, null)
            : new ConsumeResult(ConsumeStatus.Success, entry);
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

    private static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
