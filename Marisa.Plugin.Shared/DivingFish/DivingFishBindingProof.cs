using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Marisa.Plugin.Shared.DivingFish;

/// <summary>
///     水鱼绑定的一次性证明。字典仅以 SHA-256(code) 为 key；
///     消费时先原子移除（烧毁），再校验 QQ、群和 generation。
/// </summary>
public static class DivingFishBindingProof
{
    private static readonly TimeSpan ProofLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, ProofEntry> Store = new();
    private static readonly Timer CleanupTimer = new(
        _ => TryCleanupFromTimer(), null, CleanupInterval, CleanupInterval);

    private static long _nextOperationCleanupTicks;

    public enum ConsumeStatus
    {
        Success,
        NotFound,
        Expired,
        SenderMismatch,
        GroupMismatch,
        Superseded
    }

    public sealed record ProofEntry(
        long Qq,
        long GroupId,
        string Sub,
        string Username,
        string Game,
        string Scope,
        string Generation,
        DateTimeOffset ExpiresAt);

    public readonly record struct ConsumeResult(ConsumeStatus Status, ProofEntry? Entry)
    {
        public bool IsSuccess => Status == ConsumeStatus.Success && Entry is not null;
    }

    /// <summary>
    ///     为已独占并成功换码的 callback 签发 128-bit 一次性证明。
    ///     返回 null 表示 pending state 已失效、租约不属于调用者或 generation 已被新绑定 supersede。
    /// </summary>
    public static string? Issue(
        DivingFishPendingAuth.PendingEntry pending,
        string sub,
        string username,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (string.IsNullOrWhiteSpace(sub)) throw new ArgumentException("水鱼 sub 不能为空", nameof(sub));
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("scope 不能为空", nameof(scope));

        CleanupExpiredIfDue();

        if (!DivingFishPendingAuth.IsCurrentGeneration(pending.Qq, pending.Generation)) return null;

        var expiresAt = DateTimeOffset.UtcNow.Add(ProofLifetime);
        var proof = new ProofEntry(
            pending.Qq,
            pending.GroupId,
            sub,
            username ?? "",
            pending.Game,
            scope,
            pending.Generation,
            expiresAt);

        string code;
        string codeHash;
        do
        {
            code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            codeHash = Sha256Hex(code);
        } while (!Store.TryAdd(codeHash, proof));

        if (!DivingFishPendingAuth.TryComplete(pending, expiresAt) ||
            !DivingFishPendingAuth.IsCurrentGeneration(proof.Qq, proof.Generation))
        {
            TryRemoveExact(Store, codeHash, proof);
            return null;
        }

        return code;
    }

    /// <summary>
    ///     原子消费证明。只要 hash 命中就先 TryRemove 烧毁，任何后续校验失败都不可重放。
    /// </summary>
    public static ConsumeResult Consume(string code, long senderQq, long groupId)
    {
        if (string.IsNullOrWhiteSpace(code)) return new ConsumeResult(ConsumeStatus.NotFound, null);

        // Issue 返回大写十六进制；确认命令允许用户输入大小写混合的十六进制，
        // 因此先规范化再哈希，避免合法 proof 因大小写不同被当成 NotFound。
        var codeHash = Sha256Hex(code.Trim().ToUpperInvariant());
        if (!Store.TryRemove(codeHash, out var proof))
        {
            CleanupExpiredIfDue();
            return new ConsumeResult(ConsumeStatus.NotFound, null);
        }

        // proof 已在上面原子烧毁；下面任何失败均不得放回 Store。
        var status = DateTimeOffset.UtcNow >= proof.ExpiresAt
            ? ConsumeStatus.Expired
            : proof.Qq != senderQq
                ? ConsumeStatus.SenderMismatch
                : proof.GroupId != groupId
                    ? ConsumeStatus.GroupMismatch
                    : !DivingFishPendingAuth.IsCurrentGeneration(proof.Qq, proof.Generation)
                        ? ConsumeStatus.Superseded
                        : ConsumeStatus.Success;

        CleanupExpiredIfDue();
        return new ConsumeResult(status, status == ConsumeStatus.Success ? proof : null);
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
            // Timer callback 中的异常不能逃逸并终止进程；下次操作仍会再次清理。
        }
    }

    private static void CleanupExpired(DateTimeOffset now)
    {
        foreach (var pair in Store)
        {
            var proof = pair.Value;
            if (now >= proof.ExpiresAt ||
                !DivingFishPendingAuth.IsCurrentGeneration(proof.Qq, proof.Generation))
            {
                TryRemoveExact(Store, pair.Key, proof);
            }
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
