using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>Publishing for real in a hidden WebView2 on local pages shaped like each site (no network, nothing on screen).</summary>
public static class PublishRunnerTests
{
    static void Run(string mode, string caption)
    {
        string video = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share", "clip-h264.mp4");
        string data = Path.Combine(Path.GetTempPath(), "kairos-tests", "webview-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(null, PublishRunner.SelfTest(mode, video, caption, data), mode);
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public static void X_posts_from_the_dialog_after_the_upload_finishes_and_gives_the_post_address() =>
        Run("x", "#EldenRing #gaming");

    [Test]
    public static void TikTok_closes_its_tip_replaces_the_file_name_and_confirms_post_now() =>
        Run("tiktok", "تجربة من Kairos #gaming");

    [Test]
    public static void Instagram_opens_create_keeps_the_original_size_writes_the_caption_and_shares() =>
        Run("instagram", "#EldenRing #gaming");

    [Test]
    public static void An_X_premium_account_is_recognised_from_its_page() =>
        Run("xpremium", "#gaming");

    [Test]
    public static void A_sign_in_page_asks_the_user_then_the_post_continues_by_itself() =>
        Run("login", "#gaming");

    [Test]
    public static void When_a_step_never_happens_the_window_is_handed_to_the_user() =>
        Run("stuck", "#gaming");

    [Test]
    public static void The_site_browser_opens_no_developer_tools_downloads_or_permissions() =>
        Run("locked", "#gaming");
}
