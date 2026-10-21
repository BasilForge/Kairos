using Kairos.Site;

// The shared publishing code (PublishRunner, SiteSessions, SiteCaches) was written against WebView2; this is the part of it
// they use, over the sharing browser's DevTools connection (Chromium speaks the same protocol WebView2 exposes).
namespace Microsoft.Web.WebView2.Core
{
    [Flags]
    public enum CoreWebView2BrowsingDataKinds
    {
        DiskCache = 1,
        CacheStorage = 2,
    }

    public sealed class CoreWebView2Cookie(string name, string domain, string value, string path)
    {
        public string Name { get; } = name;
        public string Domain { get; } = domain;
        public string Value { get; } = value;
        public string Path { get; } = path;
    }

    public sealed class CoreWebView2CookieManager(DevToolsPage page)
    {
        /// <summary>Every cookie of the profile (the address is not used: the page's connection sees them all).</summary>
        public async Task<List<CoreWebView2Cookie>> GetCookiesAsync(string? uri) =>
            (await page.Cookies()).Select(c => new CoreWebView2Cookie(c.Name, c.Domain, c.Value, c.Path)).ToList();

        public void DeleteCookie(CoreWebView2Cookie cookie) => _ = page.DeleteCookie(cookie.Name, cookie.Domain, cookie.Path);
    }

    public sealed class CoreWebView2Profile(DevToolsPage page)
    {
        public Task ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds kinds) => page.ClearCaches();
    }

    public sealed class CoreWebView2
    {
        readonly DevToolsPage _page;

        public CoreWebView2(DevToolsPage page)
        {
            _page = page;
            CookieManager = new CoreWebView2CookieManager(page);
            Profile = new CoreWebView2Profile(page);
        }

        public CoreWebView2CookieManager CookieManager { get; }
        public CoreWebView2Profile Profile { get; }

        public Task<string> ExecuteScriptAsync(string script) => _page.Evaluate(script);

        public Task<string> CallDevToolsProtocolMethodAsync(string method, string parameters) => _page.Call(method, parameters);
    }
}
