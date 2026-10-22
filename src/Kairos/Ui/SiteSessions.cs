using System.Text.Json;
using Kairos.Engine.Share;
using Microsoft.Web.WebView2.Core;

namespace Kairos.Ui;

/// <summary>Reads and removes the site sign-ins kept in a WebView2 profile.</summary>
public static class SiteSessions
{
    static async Task<List<CoreWebView2Cookie>> AllCookies(CoreWebView2 core) =>
        // No address: every cookie in the profile.
        await core.CookieManager.GetCookiesAsync(null);

    public static async Task<IReadOnlyDictionary<SharePlatform, bool>> SignedIn(CoreWebView2 core)
    {
        var cookies = (await AllCookies(core)).Select(c => new SiteCookie(c.Name, c.Domain, c.Value)).ToList();
        return SiteAccounts.Sites.ToDictionary(p => p, p => SiteAccounts.IsSignedIn(p, cookies));
    }

    /// <summary>
    /// Signs out of one site: its cookies, the data its pages stored and any sign-in service's cookies go; the other sites
    /// stay signed in.
    /// </summary>
    public static async Task SignOut(CoreWebView2 core, SharePlatform platform)
    {
        foreach (var cookie in await AllCookies(core))
        {
            if (SiteAccounts.SignOutRemoves(platform, cookie.Domain))
                core.CookieManager.DeleteCookie(cookie);
        }
        foreach (string origin in SiteAccounts.Origins(platform))
            await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                JsonSerializer.Serialize(new { origin, storageTypes = "all" }));
    }
}
