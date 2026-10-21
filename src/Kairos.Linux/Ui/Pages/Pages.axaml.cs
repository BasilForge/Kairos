using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Kairos.Ui.Pages;

public partial class StatusPage : UserControl { public StatusPage() => InitializeComponent(); }

public partial class CapturePage : UserControl { public CapturePage() => InitializeComponent(); }

public partial class QualityPage : UserControl { public QualityPage() => InitializeComponent(); }

public partial class AudioPage : UserControl { public AudioPage() => InitializeComponent(); }

public partial class HotkeysPage : UserControl { public HotkeysPage() => InitializeComponent(); }

public partial class SmartSavePage : UserControl { public SmartSavePage() => InitializeComponent(); }

public partial class GeneralPage : UserControl { public GeneralPage() => InitializeComponent(); }

/// <summary>The clips of one day (or all of them, with no heading, when sorted by size).</summary>
public sealed record ClipGroup(string Name, IReadOnlyList<ClipItem> Items);

public partial class ClipsPage : UserControl
{
    ScrollViewer? _scroller;
    ClipsViewModel? _vm;
    bool _regroup;

    public ClipsPage()
    {
        InitializeComponent();
        // The page sits in the window's scroller: near its end, the next page of clips is added.
        AttachedToVisualTree += (_, _) =>
        {
            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller != null)
                _scroller.ScrollChanged += OnScroll;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_scroller != null)
                _scroller.ScrollChanged -= OnScroll;
            _scroller = null;
        };
        DataContextChanged += (_, _) => Follow(DataContext as ClipsViewModel);
    }

    /// <summary>
    /// WPF groups the list in a CollectionViewSource; here the groups are rebuilt (once per burst of changes) whenever the list
    /// changes, in the order of the list.
    /// </summary>
    void Follow(ClipsViewModel? vm)
    {
        if (_vm != null)
            _vm.Items.CollectionChanged -= OnItems;
        _vm = vm;
        if (vm == null)
            return;
        vm.Items.CollectionChanged += OnItems;
        Regroup();
    }

    void OnItems(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_regroup)
            return;
        _regroup = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Regroup, Avalonia.Threading.DispatcherPriority.Background);
    }

    void Regroup()
    {
        _regroup = false;
        GroupList.ItemsSource = _vm?.Items
            .GroupBy(c => c.GroupLabel)
            .Select(g => new ClipGroup(g.Key, g.ToList()))
            .ToList();
    }

    void OnScroll(object? sender, ScrollChangedEventArgs e)
    {
        // Only when the user scrolls down near the end, or the cards do not fill the window yet; never while hidden
        // behind another page (the scroller is shared) or while the page is being laid out again.
        if (!IsVisible || DataContext is not ClipsViewModel { HasMore: true } vm || _scroller is not { } s)
            return;
        bool nearEnd = s.Offset.Y + s.Viewport.Height >= s.Extent.Height - 600;
        bool notFilled = s.Extent.Height <= s.Viewport.Height + 1;
        if ((nearEnd && e.OffsetDelta.Y > 0) || notFilled)
            vm.LoadMore();
    }
}

public partial class ConnectionsPage : UserControl { public ConnectionsPage() => InitializeComponent(); }
