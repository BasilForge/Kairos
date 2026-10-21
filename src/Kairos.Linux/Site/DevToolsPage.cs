using System.Text.Json;

namespace Kairos.Site;

/// <summary>One page (a window) of the sharing browser.</summary>
public sealed class DevToolsPage : IDisposable
{
    readonly ChromiumProcess _browser;
    readonly DevToolsConnection _connection;
    readonly CancellationTokenSource _closed = new();
    int? _window;

    internal DevToolsPage(ChromiumProcess browser, DevToolsConnection connection, string targetId)
    {
        _browser = browser;
        _connection = connection;
        TargetId = targetId;
        connection.Event += OnEvent;
        connection.Lost += _closed.Cancel;
    }

    public string TargetId { get; }

    /// <summary>Cancelled when the page's window closes (the user closed it, or the browser went).</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>A page finished loading.</summary>
    public event Action? Loaded;

    internal async Task Prepare()
    {
        await _connection.Call("Page.enable");
        await _connection.Call("Network.enable");
        // A page that asks "leave site?" must not stop the work, and one that thinks it is hidden must not slow down.
        await _connection.Call("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
    }

    void OnEvent(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Page.loadEventFired":
                Loaded?.Invoke();
                break;
            case "Page.javascriptDialogOpening":
                _ = _connection.Call("Page.handleJavaScriptDialog", "{\"accept\":true}");
                break;
        }
    }

    /// <summary>Runs the script and gives its value as JSON ("null" when there is none or the page threw), like WebView2.</summary>
    public async Task<string> Evaluate(string script)
    {
        var answer = await _connection.Call("Runtime.evaluate", JsonSerializer.Serialize(new { expression = script, returnByValue = true }));
        if (answer.TryGetProperty("exceptionDetails", out _))
            return "null";
        return answer.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value) ? value.GetRawText() : "null";
    }

    public async Task<string> Call(string method, string parameters) => (await _connection.Call(method, parameters)).GetRawText();

    public Task Navigate(string url) => _connection.Call("Page.navigate", JsonSerializer.Serialize(new { url }));

    /// <summary>Every cookie of the profile (name, domain, value).</summary>
    public async Task<List<(string Name, string Domain, string Value, string Path)>> Cookies()
    {
        var answer = await _connection.Call("Network.getAllCookies");
        var list = new List<(string, string, string, string)>();
        foreach (var cookie in answer.GetProperty("cookies").EnumerateArray())
            list.Add((cookie.GetProperty("name").GetString() ?? "", cookie.GetProperty("domain").GetString() ?? "",
                cookie.GetProperty("value").GetString() ?? "", cookie.GetProperty("path").GetString() ?? "/"));
        return list;
    }

    public Task DeleteCookie(string name, string domain, string path) =>
        _connection.Call("Network.deleteCookies", JsonSerializer.Serialize(new { name, domain, path }));

    /// <summary>The disk cache and the pages' own caches go; cookies and site storage stay.</summary>
    public async Task ClearCaches()
    {
        await _connection.Call("Network.clearBrowserCache");
    }

    public Task ClearSiteData(string origin) =>
        _connection.Call("Storage.clearDataForOrigin", JsonSerializer.Serialize(new { origin, storageTypes = "all" }));

    async Task<int> Window()
    {
        if (_window is { } known)
            return known;
        var answer = await _browser.Browser("Browser.getWindowForTarget", JsonSerializer.Serialize(new { targetId = TargetId }));
        return (_window = answer.GetProperty("windowId").GetInt32()).Value;
    }

    /// <summary>The window comes up on screen, in front (sign in, or finish the post by hand).</summary>
    public async Task Reveal()
    {
        try
        {
            await _browser.Browser("Browser.setWindowBounds", JsonSerializer.Serialize(new { windowId = await Window(), bounds = new { windowState = "normal" } }));
            await _browser.Browser("Target.activateTarget", JsonSerializer.Serialize(new { targetId = TargetId }));
        }
        catch (InvalidOperationException)
        {
            // The window went meanwhile.
        }
    }

    /// <summary>The window goes out of the way (minimised); the page keeps working.</summary>
    public async Task Hide()
    {
        try
        {
            await _browser.Browser("Browser.setWindowBounds", JsonSerializer.Serialize(new { windowId = await Window(), bounds = new { windowState = "minimized" } }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task Close()
    {
        try
        {
            await _browser.Browser("Target.closeTarget", JsonSerializer.Serialize(new { targetId = TargetId }));
        }
        catch (InvalidOperationException)
        {
        }
        _closed.Cancel();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _closed.Cancel();
    }
}
