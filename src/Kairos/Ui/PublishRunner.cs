using System.Diagnostics;
using System.Text.Json;
using Kairos.Engine.Share;
using Microsoft.Web.WebView2.Core;

namespace Kairos.Ui;

public enum PublishResult { Posted, NeedsUser, Cancelled }

/// <param name="PostUrl">The new post's address when the site shows it (X), else null.</param>
/// <param name="Detail">For <see cref="PublishResult.NeedsUser"/>: the step that did not happen.</param>
/// <param name="XPremium">X: whether the account has Premium, as its page showed (null when not seen).</param>
public sealed record PublishOutcome(PublishResult Result, string? PostUrl = null, string? Detail = null, bool? XPremium = null);

/// <summary>Runs a <see cref="PublishRecipe"/> on a page: signs-in pause, tips dismissed, each step tried four times a second.</summary>
public sealed partial class PublishRunner(CoreWebView2 core, PublishRecipe recipe, string file, Func<Task> load)
{
    /// <summary>Instagram's Create menu opens in well under a second; one retry gap covers a slow one.</summary>
    static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(1.5);

    /// <summary>Status text key, whenever it changes.</summary>
    public event Action<string>? StatusChanged;
    /// <summary>True: a sign-in page is showing (show the window). False: signed in, back to the background.</summary>
    public event Action<bool>? LoginNeeded;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(250);

    public async Task<PublishOutcome> Run(string caption, CancellationToken ct)
    {
        var flow = new PublishFlow(recipe, caption);
        var clock = Stopwatch.StartNew();
        string? status = null;
        string? repeatStep = null;
        bool? premium = null;
        bool accountRead = recipe.AccountJs == null;
        TimeSpan lastRepeat = TimeSpan.Zero;
        await load();
        while (true)
        {
            await Task.Delay(Interval, ct);
            var now = clock.Elapsed;
            var before = flow.State;
            flow.LoginSeen(await Bool(PublishRecipe.LoginJs));
            if (flow.State != before && (flow.State == PublishState.NeedsLogin || before == PublishState.NeedsLogin))
                LoginNeeded?.Invoke(flow.State == PublishState.NeedsLogin);
            if (flow.TakeRestart())
            {
                await load();
                continue;
            }
            if (flow.StatusKey != status)
                StatusChanged?.Invoke(status = flow.StatusKey);
            if (flow.State == PublishState.NeedsLogin || flow.Current is not { } step)
                continue;

            foreach (string interrupt in recipe.Interrupts)
                await Bool($"(() => {{ const el = {interrupt}; if (!el || !K.visible(el)) return false; K.fire(el); return true; }})()");

            bool done;
            if (step.Kind == StepKind.Repeat)
            {
                if (repeatStep != step.Name || now - lastRepeat >= RepeatEvery)
                {
                    repeatStep = step.Name;
                    lastRepeat = now;
                    await Eval(step.Js);
                }
                done = await Bool(step.Until!);
            }
            else
                done = await Perform(step, flow.Caption);
            flow.StepResult(done, now);
            if (!accountRead && done)
            {
                // Read once the page is past its first step (signed in, the side menu is there).
                accountRead = true;
                string seen = await Eval(recipe.AccountJs!);
                premium = seen == "true" ? true : seen == "false" ? false : null;
            }

            if (flow.State == PublishState.Posted)
            {
                StatusChanged?.Invoke(flow.StatusKey);
                string? url = JsonSerializer.Deserialize<string?>(await Eval(recipe.PostUrlJs));
                return new PublishOutcome(PublishResult.Posted, url, XPremium: premium);
            }
            if (flow.State == PublishState.NeedsUser)
            {
                StatusChanged?.Invoke(flow.StatusKey);
                return new PublishOutcome(PublishResult.NeedsUser, Detail: flow.FailedStep, XPremium: premium);
            }
        }
    }

    async Task<bool> Perform(RecipeStep step, string caption) => step.Kind switch
    {
        StepKind.WaitFor or StepKind.Done => await Bool(step.Js),
        StepKind.Attach => await Attach(step.Js),
        StepKind.Type or StepKind.ReplaceText => await Type(step.Js, caption, step.Kind == StepKind.ReplaceText),
        StepKind.Click => await Bool($"(() => {{ const el = {step.Js}; if (!el || !K.enabled(el)) return false; K.fire(el); return true; }})()"),
        _ => false,
    };

    /// <summary>Gives the site's upload box the file, the same way browser test tools do (DOM.setFileInputFiles).</summary>
    async Task<bool> Attach(string selector)
    {
        try
        {
            var doc = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0}"));
            int root = doc.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            var found = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",
                JsonSerializer.Serialize(new { nodeId = root, selector })));
            int node = found.RootElement.GetProperty("nodeId").GetInt32();
            if (node == 0)
                return false;
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new { files = new[] { file }, nodeId = node }));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException or KeyNotFoundException)
        {
            return false; // the page changed under us; tried again next round
        }
    }

    /// <summary>
    /// Focuses the box, types like a keyboard (the sites' editors ignore text set from script), then takes the focus
    /// away again as a person clicking the Post button would. Instagram only saves the caption when its box loses
    /// focus: without this its post went out with an empty caption although the box showed the text.
    /// </summary>
    async Task<bool> Type(string element, string text, bool replace)
    {
        string focus = $"(() => {{ const e = {element}; if (!e) return false; e.scrollIntoView({{ block: 'center' }}); e.focus(); " +
                       (replace ? "document.execCommand('selectAll'); " : "") + "return true; })()";
        if (!await Bool(focus))
            return false;
        await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
        await Eval($"(() => {{ const e = {element}; if (e) e.blur(); return true; }})()");
        return true;
    }

    async Task<string> Eval(string expression)
    {
        try
        {
            return await core.ExecuteScriptAsync(PublishRecipe.Wrap(expression));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return "null"; // the page is between loads
        }
    }

    async Task<bool> Bool(string expression) => await Eval(expression) == "true";
}
