using Kairos.Engine.Share;
using Microsoft.Web.WebView2.Core;

namespace Kairos.Ui;

/// <summary>
/// Kairos's own browser windows for X, TikTok and Instagram. One sign-in profile on this PC
/// (%LOCALAPPDATA%\Kairos\WebView), so each site asks to sign in once; "sign out" deletes it.
/// </summary>
public static class ShareSites
{
    public static string ProfileFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "WebView");

    /// <summary>What the last sign-in check found (see <see cref="SiteAccounts.FormatKnown"/>).</summary>
    public static string KnownFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "signed-in.txt");

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

    static readonly Dictionary<SharePlatform, ShareSiteWindow> Windows = [];
    static Task<CoreWebView2Environment>? _environment;
    static Task? _signingOut;

    /// <summary>The Edge WebView2 runtime is installed (always on Windows 11; without it the normal browser opens).</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The limits of Kairos's site browser: no developer tools and nothing a page can reach in Kairos, no downloads, no
    /// camera, microphone, location or notifications, and no other app opened from a page (an ms-settings: or other
    /// app link). Signing in and posting need none of them.
    /// </summary>
    public static void Lock(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
    }

    /// <summary>
    /// The pages are asked for in English: the publishing steps read a few button labels, and a site that
    /// follows the browser's language then shows the labels they expect.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetEnvironment()
    {
        if (_signingOut != null)
            await _signingOut;
        return await (_environment ??= CoreWebView2Environment.CreateAsync(null, ProfileFolder,
            new CoreWebView2EnvironmentOptions { Language = "en-US", AdditionalBrowserArguments = SiteCaches.BrowserArguments }));
    }

    /// <summary>
    /// Posts the clip through the site's own page in an off-screen window. The window comes up only to sign in,
    /// or stays up (<see cref="PublishResult.NeedsUser"/>) when the user has to finish the post.
    /// </summary>
    public static async Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status,
        CancellationToken ct)
    {
        var window = Open(platform);
        window.Show();
        var outcome = await window.Publish(file, caption, status, ct);
        if (outcome.Result != PublishResult.NeedsUser)
        {
            // The sites' caches go (sign-ins stay), once no other post is still running in the same profile.
            if (Windows.Values.All(w => w == window))
                await window.ClearCaches();
            window.Close();
        }
        return outcome;
    }

    /// <summary>
    /// Each site's sign-in, read from the profile's cookies with an invisible browser hosted in
    /// <paramref name="host"/> (a window of Kairos's). Without a profile nobody is signed in.
    /// </summary>
    public static async Task<IReadOnlyDictionary<SharePlatform, bool>> SignedIn(IntPtr host)
    {
        if (_signingOut is { IsCompleted: false } || !Directory.Exists(ProfileFolder))
            return SiteAccounts.Sites.ToDictionary(p => p, _ => false);
        return await WithBrowser(host, async core =>
        {
            var state = await SiteSessions.SignedIn(core);
            // Each look also clears the sites' caches (sign-ins stay), unless a post is running in the profile.
            if (Windows.Count == 0)
                await SiteCaches.Clear(core.Profile);
            return state;
        });
    }

    /// <summary>Shows the site's sign-in page in Kairos's window; true once signed in, false when closed first.</summary>
    public static async Task<bool> SignIn(SharePlatform platform)
    {
        var window = Open(platform);
        window.Show();
        return await window.SignIn();
    }

    /// <summary>Signs out of one site; the others stay signed in.</summary>
    public static async Task SignOut(SharePlatform platform, IntPtr host)
    {
        if (Windows.Remove(platform, out var window))
            window.Close();
        if (!Directory.Exists(ProfileFolder))
            return;
        await WithBrowser(host, async core =>
        {
            await SiteSessions.SignOut(core, platform);
            return true;
        });
    }

    static async Task<T> WithBrowser<T>(IntPtr host, Func<CoreWebView2, Task<T>> work)
    {
        var environment = await GetEnvironment();
        var controller = await environment.CreateCoreWebView2ControllerAsync(host);
        try
        {
            controller.IsVisible = false;
            return await work(controller.CoreWebView2);
        }
        finally
        {
            controller.Close();
        }
    }

    /// <summary>A fresh window for the site (an older one is closed).</summary>
    static ShareSiteWindow Open(SharePlatform platform)
    {
        if (Windows.Remove(platform, out var old))
            old.Close();
        var window = new ShareSiteWindow(platform);
        Windows[platform] = window;
        window.Closed += (_, _) =>
        {
            if (Windows.TryGetValue(platform, out var w) && w == window)
                Windows.Remove(platform);
        };
        return window;
    }

    /// <summary>Shows the site window left open for the user (after a hand-over).</summary>
    public static void Reveal(SharePlatform platform)
    {
        if (Windows.TryGetValue(platform, out var window))
            window.Reveal();
    }

    /// <summary>Closes the site windows and deletes the sign-in profile (retrying while the browser lets go of it).</summary>
    public static Task SignOut() => _signingOut = SignOutCore();

    static async Task SignOutCore()
    {
        foreach (var window in Windows.Values.ToList())
            window.Close();
        _environment = null;
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
}
