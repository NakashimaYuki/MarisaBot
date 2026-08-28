using Marisa.Database;
using Marisa.Database.Entity.Plugin.Chunithm;
using Marisa.Database.Entity.Plugin.DivingFish;
using Marisa.Database.Entity.Plugin.MaiMaiDx;

namespace Marisa.Plugin.Shared.DivingFish;

public enum DivingFishBindingCommitResult
{
    Success,
    SubjectAlreadyBound
}

public static class DivingFishBindingService
{
    internal static readonly object WriteGate = new();

    public static DivingFishBindingCommitResult Commit(
        long qq,
        string sub,
        string username,
        string scopes,
        string game)
    {
        lock (WriteGate)
        {
            return CommitCore(qq, sub, username, scopes, game);
        }
    }

    private static DivingFishBindingCommitResult CommitCore(
        long qq,
        string sub,
        string username,
        string scopes,
        string game)
    {
        if (string.IsNullOrWhiteSpace(sub)) throw new ArgumentException("sub is required", nameof(sub));
        var requiredScope = DivingFishOAuth.ScopeOf(game);
        var grantedScopes = scopes.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!grantedScopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            throw new ArgumentException("confirmation does not contain the required game scope", nameof(scopes));
        }

        var subject = DivingFishOAuth.SubjectForSub(sub);
        using var realm = BotDbContext.OpenRealm();

        var allOauthBindings = realm.All<DivingFishOAuthBind>().ToList();
        if (allOauthBindings.Any(x =>
                x.Status == DivingFishOAuthBind.VerifiedStatus &&
                x.Qq != qq &&
                (x.Sub == sub || x.Subject == subject)))
        {
            return DivingFishBindingCommitResult.SubjectAlreadyBound;
        }

        var sameQq = allOauthBindings.Where(x => x.Qq == qq).OrderByDescending(x => x.VerifiedAt).ToList();
        var oauthBinding = sameQq.FirstOrDefault();
        var duplicateBindings = sameQq.Skip(1).ToList();

        var maiBinding = realm.All<MaiMaiDxBind>().FirstOrDefault(x => x.UId == qq);
        var chunithmBinding = realm.All<ChunithmBind>().FirstOrDefault(x => x.UId == qq);
        var mergedScopes = MergeScopes(oauthBinding?.Scopes, scopes);

        realm.Write(() =>
        {
            foreach (var duplicate in duplicateBindings) realm.Remove(duplicate);

            if (oauthBinding == null)
            {
                oauthBinding = realm.AddWithAutoId(new DivingFishOAuthBind { Qq = qq });
            }

            oauthBinding.Subject = subject;
            oauthBinding.Sub = sub;
            oauthBinding.Username = username;
            oauthBinding.Scopes = mergedScopes;
            oauthBinding.Status = DivingFishOAuthBind.VerifiedStatus;
            oauthBinding.VerifiedAt = DateTimeOffset.UtcNow;

            switch (game)
            {
                case "maimai":
                    if (maiBinding == null)
                    {
                        realm.AddWithAutoId(new MaiMaiDxBind(qq, 0) { ServerName = "DivingFish" });
                    }
                    else
                    {
                        maiBinding.ServerName = "DivingFish";
                    }
                    break;

                case "chunithm":
                    if (chunithmBinding == null)
                    {
                        realm.AddWithAutoId(new ChunithmBind(qq, "DivingFish"));
                    }
                    else
                    {
                        chunithmBinding.ServerName = "DivingFish";
                        chunithmBinding.AccessCode = "";
                    }
                    break;
            }
        });

        DivingFishTokenStore.Invalidate(qq);
        return DivingFishBindingCommitResult.Success;
    }

    private static string MergeScopes(string? current, string granted)
    {
        return string.Join(' ', $"{current} {granted}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal));
    }
}
