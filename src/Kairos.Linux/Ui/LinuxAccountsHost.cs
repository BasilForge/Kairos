using Kairos.Engine.Share;

namespace Kairos.Ui;

/// <summary>The Connections page's link to Kairos's sharing browser profile.</summary>
public sealed class LinuxAccountsHost : IAccountsHost
{
    public bool Available => ShareSites.Available;
    public Task<IReadOnlyDictionary<SharePlatform, bool>> SignedIn() => ShareSites.SignedIn();
    public Task<bool> SignIn(SharePlatform platform) => ShareSites.SignIn(platform);
    public Task SignOut(SharePlatform platform) => ShareSites.SignOut(platform);
    public Task SignOutAll() => ShareSites.SignOut();
    public IReadOnlyDictionary<SharePlatform, bool>? LastKnown() => ShareSites.LastKnown();
    public void Remember(IReadOnlyDictionary<SharePlatform, bool> state) => ShareSites.Remember(state);
}
