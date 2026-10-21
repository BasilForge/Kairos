using Kairos.Engine.Share;
using Kairos.Site;
using Microsoft.Web.WebView2.Core;

namespace Kairos.Ui;

/// <summary>
/// Kairos's own browser windows for X, TikTok and Instagram: a Chromium-family browser (Brave, Chrome, Chromium, Edge) on a
/// profile of its own (~/.local/share/Kairos/WebView), so each site asks to sign in once and nothing touches the
/// everyday browser; "sign out" deletes the profile. The same publishing steps as on Windows run on the page through its
/// DevTools port.
/// </summary>
public static class ShareSites
{
    public static string ProfileFolder { get; } = Path.Combine(Settings.SettingsStore.AppDataDir, "WebView");

    /// <summary>What the last sign-in check found (see <see cref="SiteAccounts.FormatKnown"/>).</summary>
    public static string KnownFile { get; } = Path.Combine(Settings.SettingsStore.AppDataDir, "signed-in.txt");

    public static IReadOnlyDictionary<SharePlatform, bool>? LastKnown()
    {
        try
        {
            return File.Exists(KnownFile) ? SiteAccounts.ParseKnown(File.ReadAllText(KnownFile)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Remember(IReadOnlyDictionary<SharePlatform, bool> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KnownFile)!);
            File.WriteAllText(KnownFile, SiteAccounts.FormatKnown(state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only a head start for the next look; the page checks again anyway.
        }
    }

    /// <summary>A Chromium-family browser is installed (without one, the clip is only prepared and the site opens in the normal browser).</summary>
    public static bool Available => ChromiumProcess.Find() != null;

    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Dictionary<SharePlatform, SiteWindow> Windows = [];
    static ChromiumProcess? _browser;
    static bool _headless;
    static int _users;
    static Task? _signingOut;

    /// <summary>The running browser (started, or restarted with a window when only the windowless one runs).</summary>
    static async Task<ChromiumProcess> Acquire(bool headless)
    {
        if (_signingOut != null)
            await _signingOut;
        await Gate.WaitAsync();
        try
        {
            if (_browser is { HasExited: false } running && (headless || !_headless))
            {
                _users++;
                return running;
            }
            if (_browser is { } old)
            {
                await old.Close();
                old.Dispose();
                _browser = null;
            }
            _browser = await ChromiumProcess.Start(ProfileFolder, headless, CancellationToken.None);
            _headless = headless;
            _users++;
            return _browser;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A user of the browser is done; the last one to go closes it.</summary>
    static async Task Release()
    {
        await Gate.WaitAsync();
        try
        {
            if (--_users > 0 || Windows.Count > 0 || _browser == null)
                return;
            await _browser.Close();
            _browser.Dispose();
            _browser = null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Posts the clip through the site's own page in a minimised window. The window comes up only to sign in, or stays up
    /// (<see cref="PublishResult.NeedsUser"/>) when the user has to finish the post.
    /// </summary>
    public static async Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct)
    {
        var browser = await Acquire(headless: false);
        SiteWindow? window = null;
        try
        {
            if (Windows.Remove(platform, out var old))
                await old.Close();
            window = new SiteWindow(platform, await browser.OpenPage("about:blank", 1000, 760, ct));
            Windows[platform] = window;
            window.Page.Closed.Register(() => Forget(platform, window));
            await window.Page.Hide();
            var outcome = await window.Publish(file, caption, status, ct);
            if (outcome.Result != PublishResult.NeedsUser)
            {
                // The sites' caches go (sign-ins stay), once no other post is still running in the same profile.
                if (Windows.Values.All(w => w == window))
                    await window.ClearCaches();
                Windows.Remove(platform);
                await window.Close();
            }
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return new PublishOutcome(PublishResult.Cancelled);
        }
        finally
        {
            await Release();
        }
    }

    static void Forget(SharePlatform platform, SiteWindow window)
    {
        if (Windows.TryGetValue(platform, out var known) && known == window)
            Windows.Remove(platform);
    }

    /// <summary>Each site's sign-in, read from the profile's cookies with a window-less browser (or the running one).</summary>
    public static async Task<IReadOnlyDictionary<SharePlatform, bool>> SignedIn()
    {
        if (_signingOut is { IsCompleted: false } || !Directory.Exists(ProfileFolder) || !Available)
            return SiteAccounts.Sites.ToDictionary(p => p, _ => false);
        var browser = await Acquire(headless: true);
        DevToolsPage? page = null;
        try
        {
            page = await browser.OpenPage("about:blank", 400, 300, CancellationToken.None);
            var core = new CoreWebView2(page);
            var state = await SiteSessions.SignedIn(core);
            // Each look also clears the sites' caches (sign-ins stay), unless a post is running in the profile.
            if (Windows.Count == 0)
                await SiteCaches.Clear(core.Profile);
            return state;
        }
        finally
        {
            if (page != null)
                await page.Close();
            await Release();
        }
    }

    /// <summary>Shows the site's sign-in page in the browser's window; true once signed in, false when closed first.</summary>
    public static async Task<bool> SignIn(SharePlatform platform)
    {
        var browser = await Acquire(headless: false);
        try
        {
            if (Windows.Remove(platform, out var old))
                await old.Close();
            var window = new SiteWindow(platform, await browser.OpenPage(SiteAccounts.SignInUrl(platform), 1000, 760, CancellationToken.None));
            Windows[platform] = window;
            window.Page.Closed.Register(() => Forget(platform, window));
            return await window.SignIn();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            Windows.Remove(platform);
            await Release();
        }
    }

    /// <summary>Signs out of one site; the others stay signed in.</summary>
    public static async Task SignOut(SharePlatform platform)
    {
        if (Windows.Remove(platform, out var window))
            await window.Close();
        if (!Directory.Exists(ProfileFolder) || !Available)
            return;
        var browser = await Acquire(headless: true);
        DevToolsPage? page = null;
        try
        {
            page = await browser.OpenPage("about:blank", 400, 300, CancellationToken.None);
            await SiteSessions.SignOut(new CoreWebView2(page), platform);
        }
        finally
        {
            if (page != null)
                await page.Close();
            await Release();
        }
    }

    /// <summary>Shows the site window left open for the user (after a hand-over).</summary>
    public static void Reveal(SharePlatform platform)
    {
        if (Windows.TryGetValue(platform, out var window))
            _ = window.Page.Reveal();
    }

    /// <summary>Closes the site windows and the browser, and deletes the sign-in profile (retrying while the browser lets go of it).</summary>
    public static Task SignOut() => _signingOut = SignOutCore();

    static async Task SignOutCore()
    {
        await Gate.WaitAsync();
        try
        {
            Windows.Clear();
            if (_browser is { } running)
            {
                await running.Close();
                running.Dispose();
                _browser = null;
                _users = 0;
            }
        }
        finally
        {
            Gate.Release();
        }
        for (int attempt = 0; attempt < 40 && Directory.Exists(ProfileFolder); attempt++)
        {
            try
            {
                Directory.Delete(ProfileFolder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }
    }

    /// <summary>Stops everything at exit: windows closed, browser gone (the profile stays).</summary>
    public static void Shutdown()
    {
        try
        {
            _browser?.Close().Wait(TimeSpan.FromSeconds(5));
            _browser?.Dispose();
        }
        catch (Exception e) when (e is AggregateException or InvalidOperationException)
        {
        }
        _browser = null;
    }
}

/// <summary>The site (X, TikTok or Instagram) in one window of the sharing browser; minimised unless the user is needed.</summary>
internal sealed class SiteWindow(SharePlatform platform, DevToolsPage page)
{
    public DevToolsPage Page { get; } = page;

    public Task ClearCaches() => SiteCaches.Clear(new CoreWebView2(Page).Profile);

    public Task Close() => Page.Close();

    /// <summary>Publishes the clip; the window stays out of the way unless the user is needed.</summary>
    public async Task<PublishOutcome> Publish(string file, string caption, Action<string> status, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Page.Closed);
        try
        {
            var recipe = PublishRecipe.For(platform, caption, Kairos.Engine.ClipOrganizer.IsPicture(file));
            var runner = new PublishRunner(new CoreWebView2(Page), recipe, file, () => Load(recipe.StartUrl, linked.Token));
            runner.StatusChanged += key => status(key);
            runner.LoginNeeded += needed => _ = needed ? Page.Reveal() : Page.Hide();
            var outcome = await runner.Run(caption, linked.Token);
            if (outcome.Result == PublishResult.NeedsUser)
                await Page.Reveal();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return new PublishOutcome(PublishResult.Cancelled);
        }
    }

    /// <summary>The site's sign-in page, on screen: true once the site's session cookie appears (the window then closes), false when the user closes it first.</summary>
    public async Task<bool> SignIn()
    {
        try
        {
            await Page.Reveal();
            while (true)
            {
                await Task.Delay(1500, Page.Closed);
                var cookies = await Page.Cookies();
                if (SiteAccounts.IsSignedIn(platform, cookies.Select(c => new SiteCookie(c.Name, c.Domain, c.Value))))
                {
                    // A moment for the site to finish its own redirects before the window goes.
                    await Task.Delay(1200, Page.Closed);
                    await Page.Close();
                    return true;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    async Task Load(string url, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource();
        void Done() => loaded.TrySetResult();
        Page.Loaded += Done;
        try
        {
            await Page.Navigate(url);
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            Page.Loaded -= Done;
        }
    }
}
