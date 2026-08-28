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

/// <summary>
///     Commits the verified OAuth identity and the selected game provider in one Realm
///     transaction. The caller must have consumed a valid one-time proof first.
/// </summary>
public static class DivingFishBindingService
{
    // Browser confirmation and automatic ref migration must share this process-local write
    // boundary. Otherwise a stale ref migration can be committed just after a newer sub:
    // confirmation and become the row selected by VerifiedAt ordering.
    internal static readonly object WriteGate = new();

    public static DivingFishBindingCommitResult Commit(
        long qq,
        string sub,
        string username,
        string scopes,
        string game)
    {
        // Confirmation messages are ordered at ingress, and this gate also makes direct
        // callers share the same one-to-one uniqueness boundary inside this process.
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
            throw new ArgumentException("proof does not contain the required game scope", nameof(scopes));
        }

        var subject = DivingFishOAuth.SubjectForSub(sub);
        using var realm = BotDbContext.OpenRealm();

        // Realm's [Indexed] attribute is not a uniqueness constraint. Enforce the
        // one-waterfish-account-to-one-QQ invariant before entering the write transaction.
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
