using System.Text.Json;

namespace Kairos.Engine.Share;

public enum StepKind
{
    /// <summary>Wait until <see cref="RecipeStep.Js"/> (a boolean expression) is true.</summary>
    WaitFor,
    /// <summary>Give the file to the upload box matching the CSS selector in <see cref="RecipeStep.Js"/>.</summary>
    Attach,
    /// <summary>Type the caption into the element <see cref="RecipeStep.Js"/> evaluates to.</summary>
    Type,
    /// <summary>Select everything in the element first (TikTok fills it with the file name), then type the caption.</summary>
    ReplaceText,
    /// <summary>Click the element <see cref="RecipeStep.Js"/> evaluates to, once it is there and enabled.</summary>
    Click,
    /// <summary>Run <see cref="RecipeStep.Js"/> every few seconds until <see cref="RecipeStep.Until"/> is true.</summary>
    Repeat,
    /// <summary>The post went out when <see cref="RecipeStep.Js"/> is true.</summary>
    Done,
}

/// <param name="Js">JavaScript run with the <see cref="PublishRecipe.Helpers"/> (named K) in scope.</param>
public sealed record RecipeStep(string Name, StepKind Kind, string Js, TimeSpan Timeout, bool Optional = false, string? Until = null,
    string? StatusKey = null);

/// <summary>
/// How to publish on one site through its own web page, step by step. Selectors come from the live pages
/// (2026-10-13): X's data-testid attributes, TikTok's data-e2e, Instagram's labels (English page, set by Kairos).
/// </summary>
/// <param name="Interrupts">Element expressions clicked whenever they show up (tips, "post now" confirmations).</param>
/// <param name="PostUrlJs">String expression giving the new post's address once posted, or null.</param>
/// <param name="AccountJs">X: true when the account has Premium, false when it does not, null when the page cannot tell.</param>
public sealed record PublishRecipe(SharePlatform Platform, string StartUrl, IReadOnlyList<RecipeStep> Steps,
    IReadOnlyList<string> Interrupts, string PostUrlJs, string? AccountJs = null)
{
    /// <summary>
    /// X shows a verified mark next to the account name for Premium, and a "Premium" sign-up link in the side menu
    /// only to accounts without it (checked on x.com 2026-10-13). A Premium account that hides its mark still has no
    /// sign-up link.
    /// </summary>
    public const string XPremiumJs =
        "(() => { const sw = document.querySelector('[data-testid=SideNav_AccountSwitcher_Button]'); if (!sw) return null;" +
        " if (sw.querySelector('[data-testid=icon-verified]')) return true; return !document.querySelector('a[href=\"/i/premium_sign_up\"]'); })()";

    static readonly TimeSpan Short = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Page = TimeSpan.FromSeconds(60);
    /// <summary>Uploading and processing a long 1080p clip on a slow connection.</summary>
    static readonly TimeSpan Upload = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Shared helpers: fire (a full pointer and mouse click, which the sites' scripts accept), button lookup by
    /// visible text, and an "enabled" check covering disabled, aria-disabled and TikTok's data-loading.
    /// </summary>
    public const string Helpers =
        "const K = {" +
        " fire(el) { const r = el.getBoundingClientRect(); const o = { bubbles: true, cancelable: true, view: window, clientX: r.x + r.width / 2, clientY: r.y + r.height / 2, button: 0 };" +
        "  for (const t of ['pointerdown','mousedown','pointerup','mouseup','click']) el.dispatchEvent(t.startsWith('pointer') ? new PointerEvent(t, { ...o, pointerId: 1, pointerType: 'mouse', isPrimary: true }) : new MouseEvent(t, o)); }," +
        " visible(el) { const r = el && el.getBoundingClientRect(); return !!r && r.width > 0 && r.height > 0; }," +
        " byText(words, root, sel) { return [...(root || document).querySelectorAll(sel || '[role=button],button')].find(b => words.includes(b.innerText.trim()) && K.visible(b)); }," +
        " spanButton(words, root) { const s = [...(root || document).querySelectorAll('span')].find(s => s.childElementCount === 0 && words.includes(s.textContent.trim()) && K.visible(s)); return s && s.closest('[role=button],button,a'); }," +
        " enabled(el) { return !!el && !el.disabled && el.getAttribute('aria-disabled') !== 'true' && el.getAttribute('data-loading') !== 'true'; }," +
        " dialog() { return document.querySelector('[role=dialog]'); }" +
        "};";

    /// <summary>A sign-in page (or a security check) is showing instead of the site.</summary>
    public const string LoginJs =
        "!!document.querySelector('input[type=password]') || /\\/login|\\/i\\/flow\\/|\\/i\\/jf\\/|onboarding|accounts\\/login|two_step|challenge|checkpoint|recaptcha/.test(location.href)";

    public static string Wrap(string expression) => $"(() => {{ {Helpers} return ({expression}); }})()";

    /// <summary>
    /// The post's address read from the page, when it is an https page of that site itself; null otherwise (the site's
    /// home opens instead). Windows opens whatever it is given, so a file, a network folder or another app's link is
    /// never passed on.
    /// </summary>
    public static string? PostAddress(SharePlatform platform, string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
               && uri.UserInfo.Length == 0 && SiteAccounts.Owns(platform, uri.Host)
            ? url
            : null;
    }

    /// <param name="picture">A screenshot: X waits for an attached picture, TikTok uses its Photos tab.</param>
    public static PublishRecipe For(SharePlatform platform, string caption, bool picture = false) => platform switch
    {
        SharePlatform.X => new(platform, SharePlanner.OpenTarget(SharePlatform.X, caption),
        [
            // The caption arrives with the link. Two composers exist (timeline and dialog); the dialog is ours.
            new("box", StepKind.WaitFor, "!!document.querySelector('[role=dialog] input[data-testid=fileInput]')", Page, StatusKey: "share.pub.opening"),
            new("attach", StepKind.Attach, "[role=dialog] input[data-testid=fileInput]", Short, StatusKey: "share.pub.uploading"),
            new("uploaded", StepKind.WaitFor,
                $"!!document.querySelector('[role=dialog] [data-testid=attachments] {(picture ? "img" : "video")}') && K.enabled(document.querySelector('[role=dialog] [data-testid=tweetButton]'))",
                Upload, StatusKey: "share.pub.uploading"),
            new("post", StepKind.Click, "document.querySelector('[role=dialog] [data-testid=tweetButton]')", Short, StatusKey: "share.pub.posting"),
            new("sent", StepKind.Done,
                "!!document.querySelector('[data-testid=toast] a[href*=\"/status/\"]') || !document.querySelector('[role=dialog] [data-testid=tweetTextarea_0]')",
                Page, StatusKey: "share.pub.posting"),
        ], [], "(document.querySelector('[data-testid=toast] a[href*=\"/status/\"]') || {}).href || null", XPremiumJs),

        // Photos: the upload page's Photos tab takes JPG, PNG and WebP, up to 50 MB a photo (seen 2026-10-15).
        SharePlatform.TikTok => new(platform, "https://www.tiktok.com/tiktokstudio/upload" + (picture ? "?tab=photo" : ""),
        [
            new("box", StepKind.WaitFor, $"!!document.querySelector('input[type=file][accept*={(picture ? "image" : "video")}]')", Page, StatusKey: "share.pub.opening"),
            new("attach", StepKind.Attach, $"input[type=file][accept*={(picture ? "image" : "video")}]", Short, StatusKey: "share.pub.uploading"),
            new("editor", StepKind.WaitFor, "!!document.querySelector('.public-DraftEditor-content')", Page, StatusKey: "share.pub.uploading"),
            new("caption", StepKind.ReplaceText, "document.querySelector('.public-DraftEditor-content')", Short, StatusKey: "share.pub.caption"),
            // Post can be clickable before the upload ends; TikTok says "Uploaded" when it has the whole file.
            // If that word ever changes, a minute of an enabled button is taken as done.
            new("uploaded", StepKind.WaitFor,
                "(() => { const b = document.querySelector('button[data-e2e=post_video_button]'); if (!K.enabled(b)) { window.__kairosReady = 0; return false; }" +
                " window.__kairosReady = window.__kairosReady || Date.now(); return /\\bUploaded\\b/.test(document.body.innerText) || Date.now() - window.__kairosReady > 60000; })()",
                Upload, StatusKey: "share.pub.uploading"),
            new("post", StepKind.Click, "document.querySelector('button[data-e2e=post_video_button]')", Short, StatusKey: "share.pub.posting"),
            new("sent", StepKind.Done,
                "location.href.includes('/tiktokstudio/content') || /Your (video|post|photos?) (has been|have been|is being) (uploaded|posted)|Manage your posts/i.test(document.body.innerText)",
                Upload, StatusKey: "share.pub.posting"),
        ],
        [
            "K.byText(['Got it', 'حسنًا', 'حسناً'])",
            // Posting while TikTok's own checks still run asks to confirm.
            "K.byText(['Post now', 'انشر الآن'], K.dialog())",
            // "Turn on automatic content checks?": leave the account's setting as it is.
            "(() => { const d = [...document.querySelectorAll('[role=dialog]')].find(x => /automatic content checks/i.test(x.innerText)); return d && K.byText(['Cancel', 'إلغاء'], d); })()",
        ], "null"),

        SharePlatform.Instagram => new(platform, "https://www.instagram.com/",
        [
            new("create", StepKind.Repeat, OpenCreateScript, Page, Until: "!!document.querySelector('[role=dialog] input[type=file]')", StatusKey: "share.pub.opening"),
            new("attach", StepKind.Attach, "[role=dialog] input[type=file]", Short, StatusKey: "share.pub.uploading"),
            // Keep the clip's own shape (9:16) instead of Instagram's square crop.
            new("crop", StepKind.Click, "(K.dialog() && K.dialog().querySelector('svg[aria-label=\"Select crop\"]') || {closest: () => null}).closest('[role=button],button')",
                Short, Optional: true, StatusKey: "share.pub.steps"),
            new("original", StepKind.Click, "K.spanButton(['Original', 'الأصلي'], K.dialog())", TimeSpan.FromSeconds(8), Optional: true, StatusKey: "share.pub.steps"),
            new("next1", StepKind.Click, "K.byText(['Next', 'التالي'], K.dialog())", Short, StatusKey: "share.pub.steps"),
            new("edit", StepKind.WaitFor, "!K.dialog() || !K.dialog().querySelector('svg[aria-label=\"Select crop\"]')", Short, StatusKey: "share.pub.steps"),
            new("next2", StepKind.Click, "K.byText(['Next', 'التالي'], K.dialog())", Short, StatusKey: "share.pub.steps"),
            new("captionBox", StepKind.WaitFor, "!!document.querySelector('[role=dialog] [contenteditable=true][role=textbox]')", Short, StatusKey: "share.pub.caption"),
            new("caption", StepKind.Type, "document.querySelector('[role=dialog] [contenteditable=true][role=textbox]')", Short, StatusKey: "share.pub.caption"),
            new("share", StepKind.Click, "K.byText(['Share', 'مشاركة'], K.dialog())", Short, StatusKey: "share.pub.posting"),
            new("sent", StepKind.Done,
                "/has been shared|Reel shared|Post shared|تمت مشاركة|تمت المشاركة/.test((K.dialog() || document.body).innerText) || !!document.querySelector('img[alt=\"Animated checkmark\"]')",
                Upload, StatusKey: "share.pub.posting"),
        ], [], "null"),

        _ => throw new ArgumentOutOfRangeException(nameof(platform), "only X, TikTok and Instagram are published from a page"),
    };

    /// <summary>Instagram's "Create" entry in the side menu, in the languages its labels come in.</summary>
    const string CreateEntryJs =
        "[...document.querySelectorAll('svg[aria-label]')].find(s => ['New post','Create','منشور جديد','إنشاء'].includes(s.getAttribute('aria-label')))";

    /// <summary>
    /// Instagram: opens Create, then picks "Post" in the menu that appears next to it. The text sits several levels
    /// inside the menu link, so the link around it is clicked. The search never reaches a container holding feed
    /// posts (<c>article</c>), whose own "Post" buttons are for comments.
    /// </summary>
    public static string OpenCreateScript =>
        "(() => {" +
        $"const entry = {CreateEntryJs};" +
        "if (!entry) return 'no create entry';" +
        "const words = ['Post','منشور'];" +
        "let box = entry;" +
        "for (let i = 0; i < 8 && box && !box.querySelector('article'); i++, box = box.parentElement) {" +
        " const span = [...box.querySelectorAll('span')].find(s => s.childElementCount === 0 && words.includes(s.textContent.trim()) && K.visible(s));" +
        " const item = span && span.closest('a,[role=link],[role=button]');" +
        " if (item && box.contains(item) && item.querySelector('svg') && !item.contains(entry)) { K.fire(item); return 'post'; }" +
        "}" +
        "K.fire(entry.closest('a,[role=link],[role=button]') || entry);" +
        "return 'create'; })()";

    internal static string Js(string s) => JsonSerializer.Serialize(s);
}

public enum PublishState { Running, NeedsLogin, Posted, NeedsUser }

/// <summary>Where a publish stands: the current step, its time limit, and the sign-in pause. No page access.</summary>
public sealed class PublishFlow
{
    readonly PublishRecipe _recipe;
    readonly string _caption;
    int _index;
    TimeSpan? _stepStarted;
    bool _restart;

    public PublishFlow(PublishRecipe recipe, string caption)
    {
        _recipe = recipe;
        _caption = caption;
        SkipEmptyCaption();
    }

    public PublishState State { get; private set; } = PublishState.Running;
    public RecipeStep? Current => _index < _recipe.Steps.Count ? _recipe.Steps[_index] : null;
    public string? FailedStep { get; private set; }
    public string Caption => _caption;

    public string StatusKey => State switch
    {
        PublishState.NeedsLogin => "share.pub.login",
        PublishState.NeedsUser => "share.pub.manual",
        PublishState.Posted => "share.pub.posted",
        _ => Current?.StatusKey ?? "share.pub.opening",
    };

    /// <summary>The page shows (or no longer shows) a sign-in. After signing in, everything starts again.</summary>
    public void LoginSeen(bool loginShowing)
    {
        if (State is PublishState.Posted or PublishState.NeedsUser)
            return;
        if (loginShowing && State == PublishState.Running)
            State = PublishState.NeedsLogin;
        else if (!loginShowing && State == PublishState.NeedsLogin)
        {
            State = PublishState.Running;
            _index = 0;
            _stepStarted = null;
            _restart = true;
            SkipEmptyCaption();
        }
    }

    /// <summary>True once after a sign-in: the runner loads the start page again.</summary>
    public bool TakeRestart()
    {
        bool r = _restart;
        _restart = false;
        return r;
    }

    /// <summary>The result of trying the current step at <paramref name="now"/>.</summary>
    public void StepResult(bool succeeded, TimeSpan now)
    {
        if (State != PublishState.Running || Current is not { } step)
            return;
        _stepStarted ??= now;
        if (succeeded)
        {
            Advance();
            return;
        }
        if (now - _stepStarted.Value < step.Timeout)
            return;
        if (step.Optional)
            Advance();
        else
        {
            FailedStep = step.Name;
            State = PublishState.NeedsUser;
        }
    }

    void Advance()
    {
        if (Current?.Kind == StepKind.Done)
        {
            State = PublishState.Posted;
            return;
        }
        _index++;
        _stepStarted = null;
        SkipEmptyCaption();
        if (Current == null)
            State = PublishState.Posted;
    }

    void SkipEmptyCaption()
    {
        while (_caption.Length == 0 && Current?.Kind is StepKind.Type or StepKind.ReplaceText)
            _index++;
    }
}
