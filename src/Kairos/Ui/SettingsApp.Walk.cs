using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>
/// Kairos.exe --perf walk &lt;fixtures&gt; &lt;report.txt&gt; [lang]: every page, the Library's player and picture viewer, the
/// share panel and the editor, with every button, switch, choice, list and slider in them pressed once (and set back),
/// in a window off screen and muted. A made-up engine answers on a pipe of its own (the running Kairos never hears it),
/// the clips are copies in the data folder (KAIROS_DATA), and whatever would open, show or copy something outside
/// (<see cref="SideEffects"/>, the share sites, the accounts) is written down instead. The report lists each control and
/// what happened; errors, broken bindings and the longest wait of the window's thread are counted per area.
/// </summary>
public static partial class SettingsApp
{
    sealed record WalkLine(string Area, string Control, string Kind, string Result);

    static readonly List<WalkLine> Walked = [];
    static readonly List<string> WalkHeld = [], WalkErrors = [], WalkBindings = [], WalkCommands = [];

    static int PerfWalk(string fixtures, string reportPath, string language)
    {
        var started = Stopwatch.StartNew();
        // The made-up engine's pipe, before anything reads Ipc.PipeName.
        Environment.SetEnvironmentVariable("KAIROS_PIPE", $"KairosWalk{Environment.ProcessId}");
        string clips = Path.Combine(SettingsStore.AppDataDir, "clips");
        var (video, picture) = WalkClips(fixtures, clips);
        string spares = Path.Combine(SettingsStore.AppDataDir, "spares");
        Directory.CreateDirectory(spares);
        File.Copy(video, Path.Combine(spares, "video.mp4"), true);
        File.Copy(picture, Path.Combine(spares, "picture.png"), true);
        void Restore()
        {
            if (!File.Exists(video))
                File.Copy(Path.Combine(spares, "video.mp4"), video);
            if (!File.Exists(picture))
                File.Copy(Path.Combine(spares, "picture.png"), picture);
        }
        var settings = new AppSettings();
        settings.Output.Folder = clips;
        settings.General.Language = language;
        settings.Normalize();
        SettingsStore.Save(SettingsStore.DefaultPath, settings);
        Text.Language = language;

        string status = JsonSerializer.Serialize(new StatusDto(
            new EngineStatus(true, false, 60, 0, 0, 1000, 300, 300, "obs_nvenc_av1_tex", 2560, 1440, 700, 600, clips, false, null,
                QuietAudioRows: []),
            [], video, null, false, false, AppInfo.Version, LastShot: picture), Ipc.Json);
        string caps = JsonSerializer.Serialize(new CapsDto(
            [new MonitorInfo(@"\\?\DISPLAY1", "Display 1", 2560, 1440, true)],
            [new("Roblox", "Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe"), new("Discord", "Discord:Chrome_WidgetWin_1:Discord.exe")],
            [new("Default", "default"), new("Microphone (Shure MV7)", "mv7")], [new("Default", "default")],
            ["obs_nvenc_av1_tex", "obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2",
            [new("Spotify", "Spotify"), new("Discord", "Discord")], "default", 144), Ipc.Json);
        using var engine = new PipeServer(command =>
        {
            lock (WalkCommands)
                WalkCommands.Add(command.Length > 60 ? command[..60] + "…" : command);
            return command switch { "caps" => caps, "cameras" => "[]", _ => status };
        }, () => [("game", -18.0), ("mic", -30.0)], Ipc.PipeName);

        string current = "start";
        SideEffects.Recorder = what =>
        {
            lock (WalkHeld)
                WalkHeld.Add($"{current}: {what}");
        };
        var app = CreateApplication();
        // As under the window's own message loop: what a press starts comes back to this thread after an await.
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        app.DispatcherUnhandledException += (_, e) =>
        {
            WalkErrors.Add($"{current}: {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            lock (WalkErrors)
                WalkErrors.Add($"{current} (task): {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };
        int uiThread = Environment.CurrentManagedThreadId;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is InvalidOperationException ioe && ioe.Message.Contains("different thread"))
                Console.WriteLine($"WALK cross-thread on thread {Environment.CurrentManagedThreadId} (window {uiThread}), " +
                                  $"context {SynchronizationContext.Current?.GetType().Name ?? "none"} during {current}: {Environment.StackTrace}");
        };
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new WalkBindingListener(() => current));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        Edit.PlayerSession.Muted = true;
        ClipTranscoder.RequireLoaded();

        var vm = new SettingsViewModel();
        var share = new WalkShareHost(new WpfShareHost(vm.RememberShare, () => vm.EffectiveFolder), Path.Combine(SettingsStore.AppDataDir, "share"),
            what => SideEffects.Held(what));
        var window = WindowPlacement.OffScreen(new MainWindow(vm, share, new WalkAccountsHost()));
        bool closing = false;
        window.Closing += (_, e) =>
        {
            if (closing)
                return;
            e.Cancel = true;
            SideEffects.Held("close the window");
        };
        window.Show();
        Settle();
        vm.Start();
        WaitUntil(() => vm.EngineOnline, 5);
        Console.WriteLine($"WALK engine online={vm.EngineOnline} clips={clips}");

        var probes = new Dictionary<string, double>();
        void Area(string name, Func<FrameworkElement?> root, Action? reset = null)
        {
            current = name;
            using var probe = new StallProbe();
            int before = Walked.Count;
            WalkArea(name, root, reset, a => current = a);
            probes[name] = probe.LongestMs;
            Console.WriteLine(FormattableString.Invariant($"WALK {name}: {Walked.Count - before} controls, thread busy up to {probe.LongestMs:F0} ms"));
            if (vm.IsDirty)
                vm.RevertCommand.Execute(null);
            Settle();
        }

        // The pages (the Library last: its cards are deleted there).
        foreach (string page in PerfPages.Where(p => p != "clips"))
        {
            window.ShowPage(page);
            Settle();
            Area($"page {page}", () => window.FindName("PageHost") as FrameworkElement);
        }

        // The player and the picture viewer, opened from the Library.
        window.ShowPage("clips");
        Settle();
        WaitUntil(() => window.Library.Items.Count > 0, 10);
        if (window.Library.Items.FirstOrDefault(i => i.Kind == ClipKind.Video) is { } clipItem)
        {
            window.ShowVideo(clipItem);
            Settle();
            Area("player", () => window.VideoLayer, () => { if (!window.VideoLayer.IsVisible) window.ShowVideo(clipItem); });
            window.VideoLayer.Hide();
        }
        if (window.Library.Items.FirstOrDefault(i => i.Kind == ClipKind.Picture) is { } shotItem)
        {
            window.ShowPicture(shotItem);
            Settle();
            Area("picture viewer", () => window.PictureLayer, () => { if (!window.PictureLayer.IsVisible) window.ShowPicture(shotItem); });
            window.PictureLayer.Hide();
        }

        // The share panel, then the editor on the clip and on the screenshot.
        Restore();
        window.Library.Refresh();
        Settle();
        ShareViewModel? panel = null;
        void OpenShare() => panel = window.ShowShare(video);
        OpenShare();
        WaitUntil(() => panel!.Filmstrip.Count > 0, 10);
        Area("share panel", () => window.FindName("ShareHost") as FrameworkElement,
            () => { if (window.FindName("ShareHost") is UIElement { IsVisible: false }) OpenShare(); });
        panel?.Close();
        Settle();
        current = "share scenarios";
        Restore();
        ShareScenarios(window, video, clips, s => current = s);
        foreach (var (what, path, isPicture) in new[] { ("editor (clip)", video, false), ("editor (screenshot)", picture, true) })
        {
            Restore();
            Edit.EditorView? Open() => window.OpenEditor(path, isPicture, Edit.EditorMode.Library, new RenderCaptionMaker(true, false));
            if (Open() == null)
            {
                Walked.Add(new WalkLine(what, "(open)", "editor", "FAIL did not open"));
                continue;
            }
            Settle();
            Area(what, () => window.FindName("EditorLayer") as FrameworkElement,
                () => { if (window.FindName("EditorLayer") is UIElement { IsVisible: false }) Open(); });
            window.CloseEditorNow();
            Settle();
        }
        current = "editor scenario";
        Restore();
        EditorScenario(window, video, clips);

        Restore();
        window.ShowPage("clips");
        Settle();
        Area("page clips", () => window.FindName("ClipsHost") as FrameworkElement, () =>
        {
            if (window.VideoLayer.IsVisible) window.VideoLayer.Hide();
            if (window.PictureLayer.IsVisible) window.PictureLayer.Hide();
            if (window.FindName("EditorLayer") is UIElement { IsVisible: true }) window.CloseEditorNow();
            panel?.Close();
        });

        // Save: the settings written and the engine told (the made-up one).
        current = "save";
        vm.MaxFolderGb = 25;
        int sent = WalkCommands.Count;
        vm.ApplyCommand.Execute(null);
        WaitUntil(() => WalkCommands.Count > sent, 3);
        bool saved = SettingsStore.Load(SettingsStore.DefaultPath).Output.MaxFolderGb == 25;
        Walked.Add(new WalkLine("save", "Save", "Button", saved ? "ok: written and the engine told" : "FAIL not written"));

        // The window's own buttons and the menu.
        current = "navigation";
        foreach (var nav in Descendants(window.FindName("Nav") as DependencyObject).OfType<RadioButton>())
        {
            Act(nav, "navigation", null);
            Settle();
        }

        vm.Stop();
        closing = true;
        window.Close();
        SideEffects.Recorder = null;
        WriteWalk(reportPath, probes, started.Elapsed);
        return WalkErrors.Count == 0 && !Walked.Any(w => w.Result.StartsWith("FAIL", StringComparison.Ordinal)) ? 0 : 1;
    }

    /// <summary>Copies of the test clips (a clip with its camera file, other games, a screenshot) in the data folder.</summary>
    static (string Video, string Picture) WalkClips(string fixtures, string clips)
    {
        if (Directory.Exists(clips))
            Directory.Delete(clips, recursive: true);
        string Put(string fixture, string game, string time, string ext = ".mp4")
        {
            string target = Path.Combine(clips, game, $"{game} 2026-10-20 {time}{ext}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(fixtures, fixture), target);
            return target;
        }
        string video = Put("clip-h264.mp4", "Valorant", "10-00-00");
        File.Copy(video, Engine.ClipLibrary.CameraOf(video));
        Put("clip-h264-5tracks.mp4", "Valorant", "11-00-00");
        Put("clip-av1-2tracks.mp4", "Roblox", "12-00-00");
        for (int i = 0; i < 4; i++)
            Put("clip-h264.mp4", "Minecraft", $"13-0{i}-00");
        string picture = Path.Combine(clips, "Desktop", "Desktop 2026-10-20 14-00-00.png");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        using (var bitmap = new System.Drawing.Bitmap(1280, 720))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.SteelBlue);
            g.FillEllipse(System.Drawing.Brushes.Orange, 400, 160, 480, 400);
            bitmap.Save(picture, System.Drawing.Imaging.ImageFormat.Png);
        }
        return (video, picture);
    }

    /// <summary>
    /// Every control under <paramref name="root"/>, pressed once: a pass over what shows, then again over what the first
    /// pass brought out (a switch that opens a section), up to four passes. <paramref name="reset"/> brings the area back
    /// when a control closed it.
    /// </summary>
    static void WalkArea(string area, Func<FrameworkElement?> root, Action? reset, Action<string> setCurrent)
    {
        var seen = new HashSet<DependencyObject>();
        for (int pass = 0; pass < 4; pass++)
        {
            // What closes the area or deletes its file goes last, so the rest is pressed first.
            var controls = Controls(root()).Where(c => !seen.Contains(c)).OrderBy(Closes).ToList();
            if (controls.Count == 0)
                break;
            foreach (var control in controls)
            {
                seen.Add(control);
                if (!control.IsVisible || !control.IsEnabled || PresentationSource.FromVisual(control) == null)
                    continue;
                setCurrent($"{area} / {Label(control)}");
                Console.WriteLine($"WALK > {area} / {Label(control)}");
                Act(control, area, root, seen);
                reset?.Invoke();
                Settle();
            }
        }
    }

    /// <summary>The controls a user can press under <paramref name="root"/> (not the inner parts of lists, sliders and scroll bars).</summary>
    static IEnumerable<FrameworkElement> Controls(FrameworkElement? root) =>
        Descendants(root).OfType<FrameworkElement>()
            .Where(c => c is ButtonBase or ComboBox or Slider or HotkeyBox && c.IsVisible && c.IsEnabled)
            .Where(c => !Ancestors(c).Skip(1).Any(a => a is ComboBox or ScrollBar or Slider or TextBoxBase or DatePicker))
            .Where(c => c.TemplatedParent is not (ComboBox or ScrollBar or Slider));

    static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null)
            yield break;
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (DependencyObject? n = node; n != null; n = VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
            yield return n;
    }

    /// <summary>What a person would call the control: its accessible name, text, tooltip or command.</summary>
    static string Label(FrameworkElement c)
    {
        string? name = AutomationProperties.GetName(c);
        if (string.IsNullOrWhiteSpace(name))
            name = c switch
            {
                ContentControl { Content: string s } => s,
                ContentControl { Content: TextBlock t } => t.Text,
                _ => null,
            };
        if (string.IsNullOrWhiteSpace(name) && c.ToolTip is string tip)
            name = tip;
        string? command = BindingOperations.GetBindingExpression(c, ButtonBase.CommandProperty)?.ParentBinding.Path?.Path;
        string? bound = BindingOperations.GetBindingExpression(c, c switch
        {
            ToggleButton => ToggleButton.IsCheckedProperty,
            Selector => Selector.SelectedValueProperty,
            RangeBase => RangeBase.ValueProperty,
            _ => FrameworkElement.TagProperty,
        })?.ParentBinding.Path?.Path;
        string extra = command ?? bound ?? (string.IsNullOrEmpty(c.Name) ? "" : c.Name);
        if (string.IsNullOrWhiteSpace(name))
            name = extra.Length > 0 ? extra : c.GetType().Name;
        else if (extra.Length > 0 && extra != name)
            name += $" [{extra}]";
        return name.Replace('\n', ' ').Trim();
    }

    static readonly System.Text.RegularExpressions.Regex Closing = new(
        "Close|Back|Cancel|Delete|Recycle|Discard|إغلاق|رجوع|إلغاء|المحذوفات|حذف|تجاهل", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>1 for a control that closes its area or deletes its file (pressed last), else 0.</summary>
    static int Closes(FrameworkElement c) => Closing.IsMatch(Label(c)) ? 1 : 0;

    /// <summary>
    /// Presses one control the way UI Automation does (as a screen reader would), and writes down what happened. A switch
    /// or choice that brings out a section has that section pressed too before it is set back.
    /// </summary>
    static void Act(FrameworkElement c, string area, Func<FrameworkElement?>? root, HashSet<DependencyObject>? seen = null)
    {
        var before = root != null && seen != null && c is ToggleButton ? Controls(root()).ToHashSet() : null;
        void Revealed()
        {
            if (before == null)
                return;
            foreach (var shown in Controls(root!()).Where(x => !before.Contains(x) && !seen!.Contains(x)).OrderBy(Closes).ToList())
            {
                seen!.Add(shown);
                if (shown.IsVisible && shown.IsEnabled && PresentationSource.FromVisual(shown) != null)
                {
                    Console.WriteLine($"WALK >> {area} / {Label(c)} / {Label(shown)}");
                    Act(shown, $"{area} / {Label(c)}", null);
                }
            }
        }
        int errors = WalkErrors.Count, held = WalkHeld.Count, commands = WalkCommands.Count, bindings = WalkBindings.Count;
        string kind = c.GetType().Name;
        string result;
        try
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(c);
            switch (c)
            {
                case RadioButton radio:
                    var group = Descendants(root?.Invoke()).OfType<RadioButton>().Where(r => r.GroupName == radio.GroupName && r.IsChecked == true).ToList();
                    (peer?.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider)?.Select();
                    Settle();
                    Revealed();
                    foreach (var r in group)
                        if (r != radio)
                            r.IsChecked = true;
                    result = "chosen and set back";
                    break;
                case ToggleButton toggle:
                    var toggler = peer?.GetPattern(PatternInterface.Toggle) as IToggleProvider;
                    bool? was = toggle.IsChecked;
                    toggler?.Toggle();
                    Settle();
                    bool changed = toggle.IsChecked != was;
                    Revealed();
                    if (toggle.IsChecked != was)
                        toggle.IsChecked = was;
                    result = changed ? "switched and set back" : "pressed (no change)";
                    break;
                case ButtonBase:
                    (peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
                    Settle();
                    WaitUntil(() => false, 0.15);
                    result = "pressed";
                    break;
                case ComboBox combo:
                    int original = combo.SelectedIndex, tried = 0;
                    for (int i = 0; i < combo.Items.Count && tried < 3; i++)
                    {
                        if (i == original)
                            continue;
                        combo.SelectedIndex = i;
                        Settle();
                        tried++;
                    }
                    if (combo.IsVisible && combo.SelectedIndex != original)
                        combo.SelectedIndex = original;
                    result = $"{tried} of {combo.Items.Count} choices tried";
                    break;
                case HotkeyBox box:
                    string keys = box.Hotkey;
                    box.Focus();
                    Settle();
                    box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(box)!, Environment.TickCount, System.Windows.Input.Key.F7)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    Settle();
                    string typed = box.Hotkey;
                    box.Hotkey = keys;
                    System.Windows.Input.Keyboard.ClearFocus();
                    result = typed == keys ? $"F7 not taken (kept {keys})" : $"F7 typed ({typed}) and set back";
                    break;
                case Slider slider:
                    double value = slider.Value;
                    slider.Value = slider.Minimum;
                    Settle();
                    slider.Value = slider.Maximum;
                    Settle();
                    slider.Value = value;
                    result = "moved to both ends and back";
                    break;
                default:
                    result = "skipped";
                    break;
            }
        }
        catch (Exception e)
        {
            result = $"FAIL {e.GetType().Name}: {e.Message}";
        }
        Settle();
        if (WalkErrors.Count > errors)
            result = $"FAIL {WalkErrors[^1]}";
        else
        {
            if (WalkHeld.Count > held)
                result += $"; held: {string.Join("; ", WalkHeld.Skip(held).Select(h => h[(h.IndexOf(": ", StringComparison.Ordinal) + 2)..]))}";
            if (WalkCommands.Count > commands)
                result += $"; engine told: {string.Join(", ", WalkCommands.Skip(commands).Where(x => !x.StartsWith("status", StringComparison.Ordinal)).Distinct().Take(4))}";
            if (WalkBindings.Count > bindings)
                result += $"; {WalkBindings.Count - bindings} binding error(s)";
        }
        Walked.Add(new WalkLine(area, Label(c), kind, result.TrimEnd(';', ' ').Replace("engine told: ;", "")));
    }

    /// <summary>The share panel end to end: a cut at Discord's 10 MB, a post on X and on every site.</summary>
    static void ShareScenarios(MainWindow window, string video, string clips, Action<string> setCurrent)
    {
        string shared = ShareFiles.SharedIn(clips);
        void Line(string control, bool ok, string detail) =>
            Walked.Add(new WalkLine("share scenarios", control, "scenario", (ok ? "ok: " : "FAIL ") + detail));
        ShareViewModel Open()
        {
            var panel = window.ShowShare(video);
            WaitUntil(() => panel.Filmstrip.Count > 0, 10);
            Settle();
            return panel;
        }

        // Copy: the whole clip as it is; a cut kept in Shared (copied as it is, from the key frame at or before its start, so
        // 1-4 s may be 0-4 s); the cut at Discord's 10 MB (one that already fits is the same file).
        var share = Open();
        foreach (var (what, mb, cut) in new[] { ("Copy the whole clip", 0, false), ("Copy a cut", 0, true), ("Copy a cut at 10 MB", 10, true) })
        {
            setCurrent($"share scenarios / {what}");
            int held = WalkHeld.Count;
            if (cut)
            {
                share.TrimStart = 1;
                share.TrimEnd = 4;
            }
            Settle();
            share.CopyAtCommand.Execute(mb); // Copy pressed, the size picked in its list
            WaitUntil(() => WalkHeld.Skip(held).Any(h => h.Contains("clipboard")) || WalkErrors.Count > 0, 90);
            string? file = WalkHeld.Skip(held).Where(h => h.Contains("clipboard ")).Select(h => h[(h.IndexOf("clipboard ", StringComparison.Ordinal) + 10)..]).LastOrDefault();
            bool inShared = file != null && file.StartsWith(shared, StringComparison.OrdinalIgnoreCase);
            long bytes = file != null && File.Exists(file) ? new FileInfo(file).Length : 0;
            double seconds = file != null && File.Exists(file) ? ClipTranscoder.Probe(file).Duration : 0;
            bool ok = file != null && bytes > 0 && (cut ? inShared && seconds is > 2.5 and < 4.2 : file == video) && (mb == 0 || bytes <= mb * 1024L * 1024);
            Line(what, ok, file == null ? "nothing put on the clipboard"
                : FormattableString.Invariant($"{Path.GetFileName(file)}{(inShared ? " (in Shared)" : "")}, {bytes / 1024} KB, {seconds:F1} s, on the clipboard (held)"));
        }
        share.CopyMb = 0;
        share.Close();
        Settle();

        foreach (var platform in new[] { SharePlatform.X, SharePlatform.All })
        {
            setCurrent($"share scenarios / post on {platform}");
            share = Open();
            int held = WalkHeld.Count;
            share.Platform = platform;
            Settle();
            share.PrepareCommand.Execute(null);
            int sites = platform == SharePlatform.All ? 3 : 1;
            WaitUntil(() => WalkHeld.Skip(held).Count(h => h.Contains("post on")) >= sites || WalkErrors.Count > 0, 180);
            var posts = WalkHeld.Skip(held).Where(h => h.Contains("post on")).Select(h => h[h.IndexOf("post on", StringComparison.Ordinal)..]).ToList();
            Line($"Post on {platform}", posts.Count >= sites, posts.Count > 0 ? string.Join("; ", posts) : "nothing posted");
            share.Close();
            Settle();
        }
    }

    /// <summary>The editor end to end: split, half the clip at double speed, a text, a zoom, saved as a new copy that plays.</summary>
    static void EditorScenario(MainWindow window, string video, string clips)
    {
        void Line(string control, bool ok, string detail) =>
            Walked.Add(new WalkLine("editor scenario", control, "scenario", (ok ? "ok: " : "FAIL ") + detail));
        var editor = window.OpenEditor(video, false, Edit.EditorMode.Library, new RenderCaptionMaker(true, false));
        if (editor == null)
        {
            Line("open", false, "the editor did not open");
            return;
        }
        Settle();
        var m = editor.Model;
        double length = m.Project.OutputDuration;
        m.PlayheadOutput = length / 2;
        m.SplitCommand.Execute(null);
        Settle();
        Line("Split", m.Project.Kept.Count == 2, $"{m.Project.Kept.Count} parts");
        m.PlayheadOutput = length / 4;
        Settle();
        m.PartSpeed = 2;
        Settle();
        // Half the clip at double speed: the clip three quarters as long.
        Line("Speed x2 on the first part", Math.Abs(m.Project.OutputDuration - length * 0.75) < 0.05,
            FormattableString.Invariant($"length {length:F1} s -> {m.Project.OutputDuration:F1} s"));
        m.AddTextCommand.Execute(null);
        Settle();
        Line("Add text", m.Project.Items.Any(i => i is Engine.Edit.TextItem), $"{m.Project.Items.Count(i => i is Engine.Edit.TextItem)} text(s)");
        m.ZoomCommand.Execute(null);
        Settle();
        m.ZoomTo(new Rect(0.25, 0.25, 0.5, 0.5));
        m.ModeDoneCommand.Execute(null);
        Settle();
        bool zoomed = m.Project.Kept.Any(k => k.Zoom != null);
        Line("Zoom", zoomed, zoomed ? "a part zooms" : "no zoom");
        double expected = m.Project.OutputDuration;
        var before = Directory.GetFiles(clips, "*.mp4", SearchOption.AllDirectories).ToHashSet();
        m.PrimaryCommand.Execute(null);
        Settle();
        if (editor.FindName("NewCopyButton") is Button newCopy)
            (UIElementAutomationPeer.CreatePeerForElement(newCopy).GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
        Settle();
        WaitUntil(() => m.SaveDone || m.HasSaveError, 180);
        string? saved = Directory.GetFiles(clips, "*.mp4", SearchOption.AllDirectories).FirstOrDefault(f => !before.Contains(f));
        double got = saved != null ? ClipTranscoder.Probe(saved).Duration : 0;
        Line("Save as a new copy", m.SaveDone && saved != null && Math.Abs(got - expected) < 0.5,
            m.HasSaveError ? m.SaveError
            : saved == null ? "no new file"
            : FormattableString.Invariant($"{Path.GetFileName(saved)}, {got:F1} s (expected {expected:F1} s)"));
        window.CloseEditorNow();
        Settle();
    }

    static void WriteWalk(string path, Dictionary<string, double> probes, TimeSpan took)
    {
        var lines = new List<string>
        {
            $"Kairos {AppInfo.Version} walk check, {DateTime.Now:yyyy-MM-dd HH:mm}, {took.TotalSeconds:F0} s",
            $"controls pressed: {Walked.Count}, failures: {Walked.Count(w => w.Result.StartsWith("FAIL", StringComparison.Ordinal))}, " +
            $"errors: {WalkErrors.Count}, binding errors: {WalkBindings.Count}, held (not done): {WalkHeld.Count}",
            "",
        };
        foreach (var group in Walked.GroupBy(w => w.Area))
        {
            lines.Add(FormattableString.Invariant($"== {group.Key} ({group.Count()} controls, window thread busy up to {probes.GetValueOrDefault(group.Key):F0} ms)"));
            lines.AddRange(group.Select(w => $"  {w.Kind,-12} {w.Control} -> {w.Result}"));
            lines.Add("");
        }
        lines.Add("== errors");
        lines.AddRange(WalkErrors.Select(e => "  " + e));
        lines.Add("== binding errors");
        lines.AddRange(WalkBindings.Distinct().Select(b => "  " + b));
        lines.Add("== held (written down instead of done)");
        lines.AddRange(WalkHeld.Select(h => "  " + h));
        lines.Add("== told to the engine");
        lines.AddRange(WalkCommands.Where(c => !c.StartsWith("status", StringComparison.Ordinal)).GroupBy(c => c).Select(g => $"  {g.Key} x{g.Count()}"));
        File.WriteAllLines(path, lines);
        Console.WriteLine($"WALK report: {path}");
        Console.WriteLine(lines[1]);
    }

    /// <summary>Broken bindings (a path that is not there, a value of the wrong kind), as WPF reports them.</summary>
    sealed class WalkBindingListener(Func<string> where) : TraceListener
    {
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message != null)
                lock (WalkBindings)
                    WalkBindings.Add($"{where()}: {message}");
        }
    }

    /// <summary>The real share host, with whatever leaves the window (the clipboard, the browser, the sites, Explorer) written down.</summary>
    sealed class WalkShareHost(IShareHost inner, string folder, Func<string, bool> held) : IShareHost
    {
        public string SharedFolder => inner.SharedFolder;
        public string Folder => folder;
        public MediaInfo Probe(string clip) => inner.Probe(clip);
        public SharePicture? ReadPicture(string path) => inner.ReadPicture(path);
        public ShareResult MakeJpeg(string source, string target, long maxBytes) => inner.MakeJpeg(source, target, maxBytes);
        public void CopyPicture(string path) => held($"copy picture {path}");
        public ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double> progress, CancellationToken ct) =>
            inner.Transcode(source, target, plan, maxBytes, progress, ct);
        public ShareResult Cut(string source, string target, double start, double end, IProgress<double> progress, CancellationToken ct) =>
            inner.Cut(source, target, start, end, progress, ct);
        public void CopyToClipboard(string? file, string? text) => held($"clipboard {file ?? text}");
        public void Launch(string target) => held($"open {target}");
        public Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct)
        {
            held($"post on {platform}: {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024} KB)");
            return Task.FromResult(new PublishOutcome(PublishResult.Posted));
        }
        public void RevealSite(SharePlatform platform) => held($"show the {platform} window");
        public void ShowInFolder(string path) => held($"show in File Explorer {path}");
        public void Remember(ShareSettings prefs) => inner.Remember(prefs);
        public void Post(Action action) => inner.Post(action);
        public void Run(Action work) => inner.Run(work);
        public void GrabFrames(string clip, double[] times, int width, bool exact, Action<int, BitmapSource?> done) =>
            inner.GrabFrames(clip, times, width, exact, done);
        public void ReleaseFrames() => inner.ReleaseFrames();
        public BitmapSource? Play(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended) =>
            inner.Play(clip, from, to, width, height, position, ended);
        public void StopPlaying() => inner.StopPlaying();
        public void SetPlaybackVolume(double volume) => inner.SetPlaybackVolume(volume);
        public void After(TimeSpan delay, Action action) => inner.After(delay, action);
    }

    /// <summary>Made-up sign-ins (X connected); signing in or out is written down.</summary>
    sealed class WalkAccountsHost : IAccountsHost
    {
        public bool Available => true;
        public Task<IReadOnlyDictionary<SharePlatform, bool>> SignedIn() => Task.FromResult<IReadOnlyDictionary<SharePlatform, bool>>(
            new Dictionary<SharePlatform, bool> { [SharePlatform.X] = true, [SharePlatform.TikTok] = false, [SharePlatform.Instagram] = false });
        public Task<bool> SignIn(SharePlatform platform) => Task.FromResult(!SideEffects.Held($"sign in to {platform}"));
        public Task SignOut(SharePlatform platform)
        {
            SideEffects.Held($"sign out of {platform}");
            return Task.CompletedTask;
        }
        public Task SignOutAll()
        {
            SideEffects.Held("sign out of every site");
            return Task.CompletedTask;
        }
        public IReadOnlyDictionary<SharePlatform, bool>? LastKnown() => null;
        public void Remember(IReadOnlyDictionary<SharePlatform, bool> state) { }
    }
}
