using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Kairos.App;
using Kairos.Engine.Share;
using Microsoft.Web.WebView2.Core;

namespace Kairos.Ui;

/// <summary>
/// The site (X, TikTok or Instagram) in Kairos's own browser. It works off screen: it only comes up to sign in
/// (once) or when a step did not happen and the user finishes the post here.
/// </summary>
public partial class ShareSiteWindow : Window
{
    readonly SharePlatform _platform;
    readonly CancellationTokenSource _closed = new();
    string _file = "";
    bool _onScreen;

    public ShareSiteWindow(SharePlatform platform)
    {
        _platform = platform;
        InitializeComponent();
        if (MainWindow.AppIcon is { } icon)
            Icon = icon;
        Title = $"{Text.Get("app")} – {ShareViewModel.PlatformName(platform)}";
        FlowDirection = Text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Web.FlowDirection = FlowDirection.LeftToRight; // the site lays itself out
        DragText.Text = Text.Get("share.site.drag");
        // The file by hand, for finishing a post here when a site changed.
        FileDrag.Attach(FileTile, () => _file);
        ShowButton.ToolTip = Text.Get("share.showFile");
        WindowStartupLocation = WindowStartupLocation.Manual;
        MoveOffScreen();
        ShowActivated = false;
        Closed += (_, _) => _closed.Cancel();
        Web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
                ShareSites.Lock(Web.CoreWebView2);
        };
    }

    /// <summary>Clears the sites' caches in the shared profile (sign-ins stay).</summary>
    public Task ClearCaches() => Web.CoreWebView2 is { } core ? SiteCaches.Clear(core.Profile) : Task.CompletedTask;

    /// <summary>Publishes the clip; the window stays off screen unless the user is needed.</summary>
    public async Task<PublishOutcome> Publish(string file, string caption, Action<string> status, CancellationToken ct)
    {
        _file = file;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        try
        {
            await Web.EnsureCoreWebView2Async(await ShareSites.GetEnvironment());
            var core = Web.CoreWebView2;
            // A page that asks "leave site?" must not stop the work; other dialogs are dismissed.
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.ScriptDialogOpening += (_, e) =>
            {
                if (e.Kind is CoreWebView2ScriptDialogKind.Beforeunload or CoreWebView2ScriptDialogKind.Alert)
                    e.Accept();
            };
            var recipe = PublishRecipe.For(_platform, caption, Kairos.Engine.ClipOrganizer.IsPicture(file));
            var runner = new PublishRunner(core, recipe, file, () => Load(core, recipe.StartUrl, linked.Token));
            runner.StatusChanged += key =>
            {
                ShowStatus(key);
                status(key);
            };
            runner.LoginNeeded += needed =>
            {
                if (needed)
                    Reveal();
                else
                    MoveOffScreen();
            };
            var outcome = await runner.Run(caption, linked.Token);
            if (outcome.Result == PublishResult.NeedsUser)
                Reveal();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return new PublishOutcome(PublishResult.Cancelled);
        }
    }

    /// <summary>
    /// The site's sign-in page, on screen. Closes by itself once the site's session cookie appears (true), or
    /// returns false when the user closes it first.
    /// </summary>
    public async Task<bool> SignIn()
    {
        FileTile.Visibility = Visibility.Collapsed;
        ShowButton.Visibility = Visibility.Collapsed;
        ShowStatus("accounts.signInHint");
        Reveal();
        try
        {
            await Web.EnsureCoreWebView2Async(await ShareSites.GetEnvironment());
            var core = Web.CoreWebView2;
            core.Navigate(SiteAccounts.SignInUrl(_platform));
            while (true)
            {
                await Task.Delay(1500, _closed.Token);
                var cookies = await core.CookieManager.GetCookiesAsync(null);
                if (SiteAccounts.IsSignedIn(_platform, cookies.Select(c => new SiteCookie(c.Name, c.Domain, c.Value))))
                {
                    ShowStatus("accounts.signedIn");
                    // A moment for the site to finish its own redirects before the window goes.
                    await Task.Delay(1200, _closed.Token);
                    Close();
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static async Task Load(CoreWebView2 core, string url, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource();
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
        core.NavigationCompleted += Done;
        try
        {
            core.Navigate(url);
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            core.NavigationCompleted -= Done;
        }
    }

    /// <summary>Brings the window on screen (sign in, or finish the post by hand).</summary>
    public void Reveal()
    {
        if (!_onScreen)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            ShowInTaskbar = true;
            _onScreen = true;
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    void MoveOffScreen()
    {
        Left = -32000;
        Top = -32000;
        ShowInTaskbar = false;
        _onScreen = false;
    }

    void ShowStatus(string key)
    {
        Status.Text = Text.Get(key);
        Dot.Fill = key switch
        {
            "share.pub.posted" or "accounts.signedIn" => (Brush)FindResource("Success"),
            "share.pub.manual" or "share.pub.login" => (Brush)FindResource("Warning"),
            _ => (Brush)FindResource("Accent"),
        };
    }

    void OnShowFile(object sender, RoutedEventArgs e) => App.ShellPaths.ShowInExplorer(_file);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
