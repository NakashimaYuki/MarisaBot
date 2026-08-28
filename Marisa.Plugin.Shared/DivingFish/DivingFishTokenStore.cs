using System.Collections.Concurrent;
using Marisa.Database;
using Marisa.Database.Entity.Plugin.DivingFish;

namespace Marisa.Plugin.Shared.DivingFish;

/// <summary>
///     水鱼 OBO access token 缓存。正式绑定只保存 QQ → subject/sub；短期 token 按 (subject, game)
///     single-flight 换取并缓存，不保存 refresh token，也不允许 username: OBO。
/// </summary>
public static class DivingFishTokenStore
{
    private static readonly ConcurrentDictionary<(string Subject, string Game), DivingFishToken> Cache = new();
    private static readonly ConcurrentDictionary<
        (string Subject, string Game),
        Lazy<Task<DivingFishToken?>>> InFlightFetches = new();
    // CommitBinding 会在数据库更新后调用 Invalidate。额外保留进程内 QQ → 曾使用 subject 索引，
    // 这样重绑后的清理仍能找到刚被新 subject 覆盖的旧缓存。
    private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> KnownSubjectsByQq = new();

    /// <summary>
    ///     获取 QQ 对应的短期 token。优先使用 verified subject；若尚无本地绑定，则仅尝试该 QQ 的
    ///     ref: 存量迁移。ref OBO 成功后必须先把映射持久化，再向调用方返回 token。
    /// </summary>
    public static async Task<DivingFishToken?> GetValidToken(long qq, string game)
    {
        game = NormalizeGame(game);
        var scope = DivingFishOAuth.ScopeOf(game);

        // 最多重试两次并发重绑。每次远程取票后都重新读取当前绑定，防止 Invalidate 与
        // in-flight OBO 交错时让旧 subject 的 token 被重新写回并返回给调用方。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var binding = ReadVerifiedBinding(qq);
            if (binding != null)
            {
                RememberSubject(qq, binding.Subject);
                var token = await GetOrFetch(binding.Subject, game);
                if (!IsCurrentVerifiedSubject(qq, binding.Subject))
                {
                    Cache.TryRemove((binding.Subject, game), out _);
                    continue;
                }

                if (token != null) RecordGrantedScope(qq, binding.Subject, scope);
                return token;
            }

            // Developer-Token 迁移只允许由真实 sender QQ 推导 ref，绝不接受群命令中的 username。
            var refSubject = DivingFishOAuth.SubjectForQq(qq);
            RememberSubject(qq, refSubject);
            var migratedToken = await GetOrFetch(refSubject, game);
            if (migratedToken == null)
            {
                // OBO 期间可能刚完成浏览器确认；此时按新 verified subject 重试。
                if (ReadVerifiedBinding(qq) != null) continue;
                return null;
            }

            string effectiveSubject;
            try
            {
                effectiveSubject = PersistMigratedBinding(qq, refSubject, scope);
            }
            catch
            {
                Cache.TryRemove((refSubject, game), out _);
                throw;
            }

            // 探测 ref 期间若另一个确认事务刚写入 sub: 绑定，不能让旧 ref 覆盖新绑定。
            if (!effectiveSubject.Equals(refSubject, StringComparison.Ordinal) ||
                !IsCurrentVerifiedSubject(qq, refSubject))
            {
                Cache.TryRemove((refSubject, game), out _);
                if (DivingFishOAuth.IsAllowedSubject(effectiveSubject)) RememberSubject(qq, effectiveSubject);
                continue;
            }

            return migratedToken;
        }

        throw new InvalidOperationException("水鱼绑定在取票期间反复变化，请稍后重试");
    }

    /// <summary>按 QQ 清掉其当前绑定及可推导 ref 的所有游戏缓存；确认重绑前后均可安全调用。</summary>
    public static void Invalidate(long qq)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        if (KnownSubjectsByQq.TryRemove(qq, out var knownSubjects))
        {
            subjects.UnionWith(knownSubjects.Keys);
        }

        using (var realm = BotDbContext.OpenRealm())
        {
            foreach (var bind in realm.All<DivingFishOAuthBind>().Where(x => x.Qq == qq).ToList())
            {
                var subject = ResolveStoredSubject(bind, false);
                if (subject != null) subjects.Add(subject);
            }
        }

        if (DivingFishOAuth.IsConfigured) subjects.Add(DivingFishOAuth.SubjectForQq(qq));
        foreach (var subject in subjects) InvalidateSubject(subject);
    }

    /// <summary>兼容现有 401 处理：只清除该 QQ 当前 subject 的指定游戏 token。</summary>
    public static void RemoveToken(long qq, string game)
    {
        game = NormalizeGame(game);
        var binding = ReadVerifiedBinding(qq);
        if (binding != null) Cache.TryRemove((binding.Subject, game), out _);

        // 尚未持久化完成的 ref migration 也可能已有缓存。
        if (DivingFishOAuth.IsConfigured)
        {
            Cache.TryRemove((DivingFishOAuth.SubjectForQq(qq), game), out _);
        }
    }

    /// <summary>本地解绑：事务删除该 QQ 的所有映射并清除相关缓存。</summary>
    public static void RemoveBinding(long qq)
    {
        Invalidate(qq);
        using var realm = BotDbContext.OpenRealm();
        realm.Write(() =>
        {
            foreach (var bind in realm.All<DivingFishOAuthBind>().Where(x => x.Qq == qq).ToList())
            {
                realm.Remove(bind);
            }
        });
    }

    private static async Task<DivingFishToken?> GetOrFetch(string subject, string game)
    {
        if (!DivingFishOAuth.IsAllowedSubject(subject))
        {
            throw new InvalidOperationException("本地水鱼绑定 subject 无效，请重新绑定");
        }

        var key = (subject, game);
        if (TryGetCached(key, out var cached)) return cached;

        var fetch = InFlightFetches.GetOrAdd(key, _ =>
            new Lazy<Task<DivingFishToken?>>(
                () => FetchAndCache(key),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await fetch.Value;
        }
        finally
        {
            RemoveExact(InFlightFetches, key, fetch);
        }
    }

    private static async Task<DivingFishToken?> FetchAndCache((string Subject, string Game) key)
    {
        if (TryGetCached(key, out var cached)) return cached;

        try
        {
            var token = await DivingFishOAuth.FetchToken(key.Subject, key.Game);
            Cache[key] = token;
            _ = EvictCachedToken(key, token);
            return token;
        }
        catch (DivingFishNotBoundException)
        {
            Cache.TryRemove(key, out _);
            return null;
        }
    }

    private static async Task EvictCachedToken(
        (string Subject, string Game) key,
        DivingFishToken token)
    {
        // OBO 票正常只有五分钟。即使上游误报了极长 expires_in，也不让票在内存缓存超过十分钟。
        var evictionAt = token.ExpiresAt < DateTime.UtcNow.AddMinutes(10)
            ? token.ExpiresAt
            : DateTime.UtcNow.AddMinutes(10);
        var delay = evictionAt - DateTime.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        RemoveExact(Cache, key, token);
    }

    private static bool TryGetCached((string Subject, string Game) key, out DivingFishToken? token)
    {
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.ExpiresAt.AddSeconds(-30))
        {
            token = cached;
            return true;
        }

        Cache.TryRemove(key, out _);
        token = null;
        return false;
    }

    private static BindingSnapshot? ReadVerifiedBinding(long qq)
    {
        using var realm = BotDbContext.OpenRealm();
        var bind = realm.All<DivingFishOAuthBind>()
            .Where(x => x.Qq == qq && x.Status == DivingFishOAuthBind.VerifiedStatus)
            .ToList()
            .OrderByDescending(x => x.VerifiedAt)
            .FirstOrDefault();
        if (bind == null) return null;

        var subject = ResolveStoredSubject(bind, true);
        if (subject == null) return null;

        // 兼容本分支旧 schema：已有 Sub 但还没有 Subject 时，首次读取即回填 sub: subject。
        if (string.IsNullOrWhiteSpace(bind.Subject))
        {
            realm.Write(() => bind.Subject = subject);
        }

        return new BindingSnapshot(subject);
    }

    private static bool IsCurrentVerifiedSubject(long qq, string expectedSubject)
    {
        var current = ReadVerifiedBinding(qq);
        return current != null && current.Subject.Equals(expectedSubject, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ref OBO 成功后原子持久化。若并发确认已经写入另一个 verified subject，返回新 subject，
    ///     调用方必须丢弃 ref token 并按新 subject 重新取票。
    /// </summary>
    private static string PersistMigratedBinding(long qq, string refSubject, string scope)
    {
        lock (DivingFishBindingService.WriteGate)
        {
            return PersistMigratedBindingCore(qq, refSubject, scope);
        }
    }

    private static string PersistMigratedBindingCore(long qq, string refSubject, string scope)
    {
        var effectiveSubject = refSubject;
        using var realm = BotDbContext.OpenRealm();
        realm.Write(() =>
        {
            var sameQq = realm.All<DivingFishOAuthBind>().Where(x => x.Qq == qq).ToList();
            var verified = sameQq
                .Where(x => x.Status == DivingFishOAuthBind.VerifiedStatus)
                .OrderByDescending(x => x.VerifiedAt)
                .FirstOrDefault();

            if (verified != null)
            {
                var currentSubject = ResolveStoredSubject(verified, true);
                if (currentSubject != null)
                {
                    effectiveSubject = currentSubject;
                    if (currentSubject.Equals(refSubject, StringComparison.Ordinal))
                    {
                        verified.Subject = refSubject;
                        verified.Scopes = MergeScopes(verified.Scopes, scope);
                    }
                    else if (string.IsNullOrWhiteSpace(verified.Subject))
                    {
                        verified.Subject = currentSubject;
                    }
                    return;
                }
            }

            var target = sameQq.OrderByDescending(x => x.VerifiedAt).FirstOrDefault();
            if (target == null)
            {
                target = realm.AddWithAutoId(new DivingFishOAuthBind { Qq = qq });
            }

            target.Subject = refSubject;
            target.Sub = "";
            target.Username = "";
            target.Scopes = MergeScopes(target.Scopes, scope);
            target.Status = DivingFishOAuthBind.VerifiedStatus;
            target.VerifiedAt = DateTimeOffset.UtcNow;

            foreach (var duplicate in sameQq.Where(x => x.Id != target.Id))
            {
                realm.Remove(duplicate);
            }
        });
        return effectiveSubject;
    }

    private static void RecordGrantedScope(long qq, string subject, string scope)
    {
        using var realm = BotDbContext.OpenRealm();
        realm.Write(() =>
        {
            var bind = realm.All<DivingFishOAuthBind>()
                .FirstOrDefault(x =>
                    x.Qq == qq &&
                    x.Status == DivingFishOAuthBind.VerifiedStatus &&
                    x.Subject == subject);
            if (bind != null) bind.Scopes = MergeScopes(bind.Scopes, scope);
        });
    }

    private static string? ResolveStoredSubject(DivingFishOAuthBind bind, bool throwOnInvalid)
    {
        if (!string.IsNullOrWhiteSpace(bind.Subject))
        {
            if (!DivingFishOAuth.IsAllowedSubject(bind.Subject))
            {
                if (throwOnInvalid) throw new InvalidOperationException("本地水鱼绑定 subject 无效，请重新绑定");
                return null;
            }

            if (bind.Subject.StartsWith("sub:", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(bind.Sub) &&
                !bind.Subject[4..].Equals(bind.Sub, StringComparison.Ordinal))
            {
                if (throwOnInvalid) throw new InvalidOperationException("本地水鱼绑定 sub 与 subject 不一致，请重新绑定");
                return null;
            }
            return bind.Subject;
        }

        if (string.IsNullOrWhiteSpace(bind.Sub)) return null;
        try
        {
            return DivingFishOAuth.SubjectForSub(bind.Sub);
        }
        catch (ArgumentException) when (!throwOnInvalid)
        {
            return null;
        }
    }

    private static void InvalidateSubject(string subject)
    {
        foreach (var key in Cache.Keys.Where(x => x.Subject.Equals(subject, StringComparison.Ordinal)))
        {
            Cache.TryRemove(key, out _);
        }
    }

    private static void RememberSubject(long qq, string subject)
    {
        while (true)
        {
            var subjects = KnownSubjectsByQq.GetOrAdd(qq, _ => new ConcurrentDictionary<string, byte>());
            subjects[subject] = 0;
            if (KnownSubjectsByQq.TryGetValue(qq, out var current) && ReferenceEquals(subjects, current)) return;
        }
    }

    private static void RemoveExact<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key,
        TValue value) where TKey : notnull
    {
        ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(new KeyValuePair<TKey, TValue>(key, value));
    }

    private static string NormalizeGame(string game)
    {
        if (string.Equals(game, "maimai", StringComparison.OrdinalIgnoreCase)) return "maimai";
        if (string.Equals(game, "chunithm", StringComparison.OrdinalIgnoreCase)) return "chunithm";
        throw new ArgumentOutOfRangeException(nameof(game), game, "仅支持 maimai 或 chunithm");
    }

    private static string NormalizeScopes(string scopes)
    {
        return string.Join(' ', scopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string MergeScopes(string current, string added)
    {
        return NormalizeScopes($"{current} {added}");
    }

    private sealed record BindingSnapshot(string Subject);
}
