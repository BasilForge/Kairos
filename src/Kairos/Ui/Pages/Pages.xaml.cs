using System.Windows.Controls;

namespace Kairos.Ui.Pages;

public partial class StatusPage : UserControl { public StatusPage() => InitializeComponent(); }

public partial class ClipsPage : UserControl
{
    ScrollViewer? _scroller;

    public ClipsPage()
    {
        InitializeComponent();
        // The page sits in the window's scroller: near its end, the next page of clips is added.
        Loaded += (_, _) =>
        {
            _scroller = FindScroller(this);
            if (_scroller != null)
                _scroller.ScrollChanged += OnScroll;
        };
        Unloaded += (_, _) =>
        {
            if (_scroller != null)
                _scroller.ScrollChanged -= OnScroll;
            _scroller = null;
        };
    }

    /// <summary>Each card's picture can be dragged out as its file (the card it shows when the drag starts).</summary>
    void OnCardInitialized(object? sender, EventArgs e)
    {
        if (sender is System.Windows.FrameworkElement card)
            FileDrag.Attach(card,
                // Null from the Library (an HEVC clip's H.264 copy still to make) means nothing to drag yet, never the clip.
                () => card.DataContext is ClipItem clip ? DataContext is ClipsViewModel library ? library.DragFile(clip) : clip.Path : null,
                () =>
                {
                    if (card.DataContext is ClipItem clip)
                        (DataContext as ClipsViewModel)?.PrepareDrag(clip);
                });
    }

    void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        // Only when the user scrolls down near the end, or the cards do not fill the window yet; never while hidden
        // behind another page (the scroller is shared) or while the page is being laid out again.
        if (!IsVisible || DataContext is not ClipsViewModel { HasMore: true } vm || _scroller is not { } s)
            return;
        bool nearEnd = s.VerticalOffset + s.ViewportHeight >= s.ExtentHeight - 600;
        bool notFilled = s.ExtentHeight <= s.ViewportHeight + 1;
        if ((nearEnd && e.VerticalChange > 0) || notFilled)
            vm.LoadMore();
    }

    static ScrollViewer? FindScroller(System.Windows.DependencyObject from)
    {
        for (var d = System.Windows.Media.VisualTreeHelper.GetParent(from); d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer sv)
                return sv;
        return null;
    }
}

public partial class CapturePage : UserControl { public CapturePage() => InitializeComponent(); }

public partial class QualityPage : UserControl { public QualityPage() => InitializeComponent(); }

public partial class AudioPage : UserControl
{
    public AudioPage() => InitializeComponent();

    void OnAddSource(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            AddSourceMenu(vm, (Button)sender).IsOpen = true;
    }

    /// <summary>"+ Add source": the apps open now, input devices, then playback devices (each list only when it has any).</summary>
    internal static ContextMenu AddSourceMenu(SettingsViewModel vm, System.Windows.FrameworkElement under)
    {
        var menu = new ContextMenu { PlacementTarget = under, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Group(string title, IReadOnlyList<Option> options, System.Windows.Input.ICommand command)
        {
            if (options.Count == 0)
                return;
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = App.Text.Get(title), IsEnabled = false });
            foreach (var o in options)
                menu.Items.Add(new MenuItem { Header = o.Label, Command = command, CommandParameter = o.Value });
        }
        Group("audio.addApps", vm.AddAudioApps, vm.AddAudioAppCommand);
        Group("audio.addDevices", vm.AddAudioDevices, vm.AddAudioDeviceCommand);
        Group("audio.addOutputs", vm.AddAudioOutputs, vm.AddAudioOutputCommand);
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = App.Text.Get("audio.addNone"), IsEnabled = false });
        return menu;
    }
}

public partial class HotkeysPage : UserControl { public HotkeysPage() => InitializeComponent(); }

public partial class SmartSavePage : UserControl { public SmartSavePage() => InitializeComponent(); }

public partial class ConnectionsPage : UserControl { public ConnectionsPage() => InitializeComponent(); }

public partial class GeneralPage : UserControl { public GeneralPage() => InitializeComponent(); }
