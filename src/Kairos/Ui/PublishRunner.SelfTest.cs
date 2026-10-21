using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Kairos.Engine.Share;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Kairos.Ui;

// The --selftest rehearsal on the Windows WebView2 (the Linux build has its own, over its DevTools browser).
public sealed partial class PublishRunner
{
    /// <summary>
    /// Publishes on a hidden local page shaped like the site (see <see cref="PublishMocks"/>): nothing on screen,
    /// no network, a throwaway profile. Returns null when the outcome is the expected one.
    /// <paramref name="mode"/>: x, tiktok, instagram, login (X behind a sign-in), stuck (X never finishes uploading).
    /// </summary>
    public static string? SelfTest(string mode, string video, string caption, string userDataFolder)
    {
        string? result = "timed out";
        var thread = new Thread(() =>
        {
            var window = WindowPlacement.OffScreen(new Window { Width = 1000, Height = 760, WindowStyle = WindowStyle.None });
            var web = new WebView2();
            window.Content = web;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    result = await RunSelfTest(web, mode, video, caption, userDataFolder);
                }
                catch (Exception e)
                {
                    result = e.GetType().Name + ": " + e.Message;
                }
                window.Close();
            };
            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(90));
        return result;
    }

    static async Task<string?> RunSelfTest(WebView2 web, string mode, string video, string caption, string userDataFolder)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        var platform = mode switch { "tiktok" => SharePlatform.TikTok, "instagram" => SharePlatform.Instagram, _ => SharePlatform.X };
        var recipe = PublishRecipe.For(platform, caption, Kairos.Engine.ClipOrganizer.IsPicture(video));
        if (mode == "stuck")
            recipe = recipe with { Steps = recipe.Steps.Select(s => s with { Timeout = TimeSpan.FromSeconds(3) }).ToList() };

        int loads = 0;
        async Task Load()
        {
            string page = mode switch
            {
                "tiktok" => PublishMocks.TikTok,
                "instagram" => PublishMocks.Instagram,
                "stuck" => PublishMocks.X(caption, stuck: true),
                "xpremium" => PublishMocks.X(caption, stuck: false, premium: true),
                "login" when loads == 0 => PublishMocks.SignIn,
                _ => PublishMocks.X(caption, stuck: false),
            };
            loads++;
            var loaded = new TaskCompletionSource();
            void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
            core.NavigationCompleted += Done;
            core.NavigateToString(page);
            await loaded.Task;
            core.NavigationCompleted -= Done;
        }

        var runner = new PublishRunner(core, recipe, video, Load) { Interval = TimeSpan.FromMilliseconds(200) };
        bool askedToSignIn = false;
        runner.LoginNeeded += needed =>
        {
            if (!needed)
                return;
            askedToSignIn = true;
            // The "user" signs in: the sign-in form goes away.
            _ = core.ExecuteScriptAsync("document.querySelector('input[type=password]').remove()");
        };
        var outcome = await runner.Run(caption, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

        if (core.DocumentTitle.StartsWith("WRONG"))
            return core.DocumentTitle;
        if (mode == "stuck")
            return outcome.Result == PublishResult.NeedsUser && outcome.Detail == "uploaded" ? null : $"expected a hand-over at 'uploaded', got {outcome}";
        if (mode == "login" && !askedToSignIn)
            return "the sign-in page was not noticed";
        if (outcome.Result != PublishResult.Posted)
            return $"not posted: {outcome}";
        if (mode is "x" or "login" && outcome.PostUrl != "https://x.com/kairos/status/123")
            return "post address: " + outcome.PostUrl;
        if (mode == "x" && outcome.XPremium != false)
            return "X account read as " + (outcome.XPremium?.ToString() ?? "unknown") + ", expected free";
        if (mode == "xpremium" && outcome.XPremium != true)
            return "X account read as " + (outcome.XPremium?.ToString() ?? "unknown") + ", expected Premium";

        string posted = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(window.__posted || null)")) ?? "null";
        if (posted == "null")
            return "the site never received the post";
        var p = JsonDocument.Parse(posted).RootElement;
        string expectedFile = $"{Path.GetFileName(video)}:{new FileInfo(video).Length}";
        if (p.GetProperty("file").GetString() != expectedFile)
            return "file: " + p.GetProperty("file").GetString();
        if (p.GetProperty("text").GetString()?.Trim() != caption)
            return $"caption: '{p.GetProperty("text").GetString()}'";
        if (mode == "instagram" && p.GetProperty("aspect").GetString() != "original")
            return "Instagram crop: " + p.GetProperty("aspect").GetString();
        if (mode == "tiktok" && await core.ExecuteScriptAsync("!!window.__tipClosed") != "true")
            return "TikTok's tip was left open";
        return null;
    }
}
