using System.Text.Json;
using Kairos.Engine.Share;
using Kairos.Site;
using Kairos.Ui;
using Microsoft.Web.WebView2.Core;

namespace Kairos;

/// <summary>
/// kairos --selftest-share-site &lt;x|tiktok|instagram|login|stuck|xpremium&gt; &lt;clip&gt;: publishes on a local page shaped like the
/// site (<see cref="PublishMocks"/>) in a window-less browser with a throwaway profile; nothing on screen, no network.
/// </summary>
static class SiteSelfTest
{
    public static int Run(string[] args)
    {
        string mode = args.Length > 1 ? args[1] : "x";
        string video = args.Length > 2 ? args[2] : throw new ArgumentException("usage: --selftest-share-site <mode> <clip>");
        string profile = Path.Combine(Path.GetTempPath(), "kairos-site-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? failure = Check(mode, video, "Kairos test caption", profile).GetAwaiter().GetResult();
            Console.WriteLine(failure == null ? $"share-site {mode}: ok" : $"share-site {mode}: FAILED {failure}");
            return failure == null ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }

    static async Task<string?> Check(string mode, string video, string caption, string profile)
    {
        using var browser = await ChromiumProcess.Start(profile, headless: true, CancellationToken.None);
        using var page = await browser.OpenPage("about:blank", 1000, 760, CancellationToken.None);
        var core = new CoreWebView2(page);
        var platform = mode switch { "tiktok" => SharePlatform.TikTok, "instagram" => SharePlatform.Instagram, _ => SharePlatform.X };
        var recipe = PublishRecipe.For(platform, caption, Kairos.Engine.ClipOrganizer.IsPicture(video));
        if (mode == "stuck")
            recipe = recipe with { Steps = recipe.Steps.Select(s => s with { Timeout = TimeSpan.FromSeconds(3) }).ToList() };

        int loads = 0;
        async Task Load()
        {
            string html = mode switch
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
            void Done() => loaded.TrySetResult();
            page.Loaded += Done;
            await page.Navigate("data:text/html;charset=utf-8," + Uri.EscapeDataString(html));
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            page.Loaded -= Done;
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

        string title = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.title")) ?? "";
        if (title.StartsWith("WRONG"))
            return title;
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
