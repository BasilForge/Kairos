namespace Kairos.Engine.Share;

/// <summary>A cookie saved in Kairos's site browser (only what deciding sign-ins needs).</summary>
public sealed record SiteCookie(string Name, string Domain, string Value);

/// <summary>
/// The sites Kairos posts to as accounts: which cookie a signed-in session keeps, which addresses are the site's own
/// (to sign out of one site and leave the others), and where to sign in.
/// </summary>
public static class SiteAccounts
{
    public static readonly IReadOnlyList<SharePlatform> Sites = [SharePlatform.X, SharePlatform.TikTok, SharePlatform.Instagram];

    /// <summary>The last sign-in check, one "Site=1|0" line per site (kept so the next look shows it at once).</summary>
    public static string FormatKnown(IReadOnlyDictionary<SharePlatform, bool> state) =>
        string.Join("\n", Sites.Select(p => $"{p}={(state.TryGetValue(p, out bool on) && on ? 1 : 0)}"));

    /// <summary>Reads <see cref="FormatKnown"/>; null when no site is in it (a site missing from it is signed out).</summary>
    public static IReadOnlyDictionary<SharePlatform, bool>? ParseKnown(string text)
    {
        var found = new Dictionary<SharePlatform, bool>();
        foreach (string line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && Enum.TryParse(line[..eq], out SharePlatform site) && Sites.Contains(site))
                found[site] = line[(eq + 1)..] == "1";
        }
        return found.Count == 0 ? null : Sites.ToDictionary(p => p, p => found.TryGetValue(p, out bool on) && on);
    }

    /// <summary>The site's domains: its cookies are on these or their subdomains.</summary>
    public static IReadOnlyList<string> Domains(SharePlatform platform) => platform switch
    {
        SharePlatform.X => ["x.com", "twitter.com"],
        SharePlatform.TikTok => ["tiktok.com"],
        SharePlatform.Instagram => ["instagram.com"],
        _ => [],
    };

    /// <summary>Pages whose stored data (beyond cookies) goes with the sign-in.</summary>
    public static IReadOnlyList<string> Origins(SharePlatform platform) => platform switch
    {
        SharePlatform.X => ["https://x.com", "https://twitter.com"],
        SharePlatform.TikTok => ["https://www.tiktok.com", "https://tiktok.com"],
        SharePlatform.Instagram => ["https://www.instagram.com", "https://instagram.com"],
        _ => [],
    };

    public static string SignInUrl(SharePlatform platform) => platform switch
    {
        SharePlatform.X => "https://x.com/i/flow/login",
        SharePlatform.TikTok => "https://www.tiktok.com/login",
        SharePlatform.Instagram => "https://www.instagram.com/accounts/login/",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>The cookie each site keeps while signed in (gone or emptied once signed out).</summary>
    static string SessionCookie(SharePlatform platform) => platform == SharePlatform.X ? "auth_token" : "sessionid";

    /// <summary>The cookie domain is the site's (".x.com", "api.x.com"), not a lookalike ("max.com").</summary>
    public static bool Owns(SharePlatform platform, string cookieDomain) => OnAny(cookieDomain, Domains(platform));

    /// <summary>
    /// The sign-in services the sites offer ("Continue with Google", Apple, Facebook, LINE, Kakao; Google's sign-in also
    /// sets youtube.com). Left signed in, anyone at this PC could sign in to a site again with one click.
    /// </summary>
    static readonly string[] SignInServices = ["google.com", "youtube.com", "apple.com", "facebook.com", "line.me", "kakao.com"];

    /// <summary>Signing out of <paramref name="platform"/> deletes this cookie: the site's own, or a sign-in service's (the other sites keep theirs).</summary>
    public static bool SignOutRemoves(SharePlatform platform, string cookieDomain) => Owns(platform, cookieDomain) || OnAny(cookieDomain, SignInServices);

    static bool OnAny(string cookieDomain, IEnumerable<string> domains)
    {
        string host = cookieDomain.TrimStart('.').ToLowerInvariant();
        return domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    public static bool IsSignedIn(SharePlatform platform, IEnumerable<SiteCookie> cookies) =>
        cookies.Any(c => c.Name == SessionCookie(platform) && c.Value.Length > 0 && Owns(platform, c.Domain));
}
