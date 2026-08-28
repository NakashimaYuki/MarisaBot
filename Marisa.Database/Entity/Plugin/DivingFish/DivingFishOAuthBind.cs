using System;
using Marisa.Database.Entity;
using Realms;

namespace Marisa.Database.Entity.Plugin.DivingFish;

public partial class DivingFishOAuthBind : IRealmObject, IHaveId
{
    public const string VerifiedStatus = "verified";
    public const string UnverifiedStatus = "unverified";

    [PrimaryKey]
    public long Id { get; set; }

    [Indexed]
    public long Qq { get; set; }

    [Indexed]
    public string Subject { get; set; } = "";

    [Indexed]
    public string Sub { get; set; } = "";

    public string Username { get; set; } = "";

    public string Scopes { get; set; } = "";

    public string Status { get; set; } = UnverifiedStatus;

    public DateTimeOffset VerifiedAt { get; set; }
}
