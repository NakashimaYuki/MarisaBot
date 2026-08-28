using System;
using Marisa.Database.Entity;
using Realms;

namespace Marisa.Database.Entity.Plugin.DivingFish;

/// <summary>
///     已确认的水鱼 OAuth 身份映射。只持久化 QQ → subject/sub 与审计元数据，不保存用户令牌。
///     Subject 为 sub:&lt;id&gt;（授权码确认）或 ref:&lt;sha256&gt;（Developer-Token 存量迁移）。
/// </summary>
public partial class DivingFishOAuthBind : IRealmObject, IHaveId
{
    public const string VerifiedStatus = "verified";
    public const string UnverifiedStatus = "unverified";

    [PrimaryKey]
    public long Id { get; set; }

    /// <summary>QQ 号；应用层保证每个 QQ 只有一条有效绑定。</summary>
    [Indexed]
    public long Qq { get; set; }

    /// <summary>OBO 使用的 subject，只允许 sub: 或 ref:。</summary>
    [Indexed]
    public string Subject { get; set; } = "";

    /// <summary>userinfo 返回的水鱼用户 ID；ref: 存量迁移尚不知道 sub 时为空。</summary>
    [Indexed]
    public string Sub { get; set; } = "";

    /// <summary>userinfo 返回的展示名；ref: 存量迁移时为空。</summary>
    public string Username { get; set; } = "";

    /// <summary>已实测成功的 scope，空格分隔。</summary>
    public string Scopes { get; set; } = "";

    /// <summary>状态：verified / unverified；TokenStore 只使用 verified。</summary>
    public string Status { get; set; } = UnverifiedStatus;

    /// <summary>本地身份映射最近一次完成确认或迁移的时间。</summary>
    public DateTimeOffset VerifiedAt { get; set; }
}
