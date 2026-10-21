using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Kairos.App;
using Kairos.Engine;

namespace Kairos.Ui;

public sealed class ClipItem : INotifyPropertyChanged
{
    BitmapSource? _thumbnail;
    bool _selected;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string App { get; init; }
    public required DateTime Time { get; init; }
    public required long Size { get; init; }
    public ClipKind Kind { get; init; }

    /// <summary>The day heading the card is listed under ("Today", "Yesterday"…).</summary>
    public string GroupLabel
    {
        get => _groupLabel;
        set
        {
            if (_groupLabel == value)
                return;
            _groupLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupLabel)));
        }
    }
    string _groupLabel = "";
    /// <summary>A screenshot (or any picture): opened in Kairos's viewer, shown with a camera badge.</summary>
    public bool IsPicture => Kind == ClipKind.Picture;
    public string Meta => $"{Time:yyyy-MM-dd HH:mm}  •  {SettingsViewModel.FormatSize(Size)}";

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Saved while the Clips page was open (shown with a "New" mark until the page is opened again).</summary>
    public bool IsNew
    {
        get => _new;
        set
        {
            if (_new == value)
                return;
            _new = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNew)));
        }
    }

    bool _new;
    public Action? SelectionChanged { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One entry of the Clips page's folder list. It stays the same object while its count changes, so the list keeps
/// showing what was chosen when clips are added or removed.
/// </summary>
public sealed class FolderChoice(string value, string label) : INotifyPropertyChanged
{
    string _label = label;

    public string Value { get; } = value;

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
                return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Every video and picture in the clips folder and its subfolders, newest first, shown a page at a time (more as the
/// list is scrolled down), with thumbnails, selection and deletion; filtered by folder and by kind.
/// </summary>
public sealed class ClipsViewModel : INotifyPropertyChanged
{
    /// <summary>Cards shown first (a screenful: quick to lay out), then added at a time; thumbnails only for cards shown.</summary>
    const int FirstPage = 24, PageSize = 36;
    readonly Func<string> _folder;
    readonly Func<string> _saveHotkey;
    readonly Func<string> _shotHotkey;
    List<ClipItem> _all = [];
    List<ClipItem> _filtered = [];
    string _filter = "";
    string _kind = "";
    bool _confirmingPermanent;

    public ClipsViewModel(Func<string> folder, Func<string> saveHotkey, Func<string>? shotHotkey = null)
    {
        _folder = folder;
        _saveHotkey = saveHotkey;
        _shotHotkey = shotHotkey ?? (() => "");
        RefreshCommand = new RelayCommand(_ => Refresh());
        PlayCommand = new RelayCommand(p =>
        {
            if (p is ClipItem { IsPicture: true } picture)
                PictureRequested?.Invoke(picture);
            else if (p is ClipItem video && VideoRequested != null)
                VideoRequested(video); // Kairos's own player (with Edit)
            else
                Open((p as ClipItem)?.Path);
        });
        ShowCommand = new RelayCommand(p => ShowInFolder((p as ClipItem)?.Path));
        DeleteCommand = new RelayCommand(p => { if (p is ClipItem c) Remove([c], permanent: false); });
        ShareCommand = new RelayCommand(p => { if (p is ClipItem c) ShareRequested?.Invoke(c); });
        SelectAllCommand = new RelayCommand(_ => SetAll(!AllSelected));
        ClearSelectionCommand = new RelayCommand(_ => SetAll(false));
        RecycleSelectedCommand = new RelayCommand(_ => Remove(Selected, permanent: false));
        AskPermanentCommand = new RelayCommand(_ => { _confirmingPermanent = true; Raise(); });
        ConfirmPermanentCommand = new RelayCommand(_ => Remove(Selected, permanent: true));
        CancelPermanentCommand = new RelayCommand(_ => { _confirmingPermanent = false; Raise(); });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>The share button on a clip card: the window opens the share panel for it.</summary>
    public event Action<ClipItem>? ShareRequested;
    /// <summary>A picture card was opened: the window shows it in its own viewer.</summary>
    public event Action<ClipItem>? PictureRequested;
    /// <summary>A video to play in Kairos's own player.</summary>
    public event Action<ClipItem>? VideoRequested;
    void Raise(string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public ObservableCollection<ClipItem> Items { get; } = [];
    public ObservableCollection<FolderChoice> Apps { get; } = [];
    /// <summary>All / Videos / Screenshots, with counts; the same entries stay while the counts change.</summary>
    public ObservableCollection<FolderChoice> Kinds { get; } = [];

    /// <summary>The picture <paramref name="step"/> places on from <paramref name="from"/> among what is shown, skipping videos.</summary>
    public ClipItem? NextPicture(ClipItem from, int step)
    {
        int at = _filtered.IndexOf(from);
        if (at < 0 || step == 0)
            return null;
        for (int i = at + Math.Sign(step); i >= 0 && i < _filtered.Count; i += Math.Sign(step))
            if (_filtered[i].IsPicture)
                return _filtered[i];
        return null;
    }

    /// <summary>"" (everything), "video" or "picture".</summary>
    public string Kind
    {
        get => _kind;
        set
        {
            // Null comes from the list itself while it is rebuilt: not a choice.
            if (value == null || value == _kind)
                return;
            _kind = value;
            ApplyFilter();
        }
    }
    public string Filter
    {
        get => _filter;
        set
        {
            // Null comes from the folder list itself while its items are being replaced: not a choice.
            if (value == null || value == _filter)
                return;
            _filter = value;
            ApplyFilter();
        }
    }

    // What is chosen among the clips shown (also those whose cards are not loaded yet: Select all takes them too).
    List<ClipItem> Selected => _filtered.Where(c => c.IsSelected).ToList();
    public int SelectedCount => _filtered.Count(c => c.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool AllSelected => _filtered.Count > 0 && _filtered.All(c => c.IsSelected);
    /// <summary>The chosen clips' size, beside the page's title (empty with nothing chosen).</summary>
    public string SelectedSizeText => HasSelection ? Text.Ltr(SettingsViewModel.FormatSize(Selected.Sum(c => c.Size))) : "";
    public bool ConfirmingPermanent => _confirmingPermanent && HasSelection;
    public string SelectedText => Text.Format("clips.selected", SelectedCount);
    public string SelectAllLabel => Text.Get(AllSelected ? "clips.clearSelection" : "clips.selectAll");
    public string ConfirmText => Text.Format("clips.confirmForever", Text.Count("count.files", SelectedCount));
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText => _kind == "picture" ? Text.Format("clips.emptyShots", _shotHotkey()) : Text.Format("clips.empty", _saveHotkey());

    public string CountText
    {
        get
        {
            int pictures = _all.Count(c => c.IsPicture), videos = _all.Count - pictures;
            var parts = new List<string>();
            if (videos > 0 || pictures == 0)
                parts.Add(Text.Count("count.clips", videos));
            if (pictures > 0)
                parts.Add(Text.Count("count.shots", pictures));
            parts.Add(Text.Ltr(SettingsViewModel.FormatSize(_all.Sum(c => c.Size))));
            return string.Join(" • ", parts);
        }
    }

    public bool HasMore => Items.Count < _filtered.Count;

    /// <summary>The next page of cards (called as the list nears its end).</summary>
    public void LoadMore()
    {
        if (!HasMore)
            return;
        foreach (var clip in _filtered.Skip(Items.Count).Take(Items.Count == 0 ? FirstPage : PageSize))
        {
            Items.Add(clip);
            if (clip.Thumbnail == null)
            {
                var target = clip;
                ShellThumbnail.Request(clip.Path, image =>
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => target.Thumbnail = image));
            }
        }
        Raise();
    }

    public ICommand RefreshCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand ShowCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand RecycleSelectedCommand { get; }
    public ICommand AskPermanentCommand { get; }
    public ICommand ConfirmPermanentCommand { get; }
    public ICommand CancelPermanentCommand { get; }

    /// <param name="live">A clip was saved while the page is open: what is new is marked so.</param>
    public void Refresh(bool live = false)
    {
        var library = ClipLibrary.Scan(_folder());
        // Clips that did not change keep their card (and its picture): coming back to the page rebuilds nothing.
        var known = _all.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var next = library
            .Select(c => known.TryGetValue(c.Path, out var old) && old.Time == c.Time && old.Size == c.Size && old.App == c.Group
                ? old
                : new ClipItem
                {
                    Path = c.Path,
                    Name = System.IO.Path.GetFileNameWithoutExtension(c.Path),
                    App = c.Group,
                    Time = c.Time,
                    Size = c.Size,
                    Kind = c.Kind,
                    IsNew = live,
                })
            .ToList();
        if (!live)
            foreach (var clip in next)
                clip.IsNew = false; // the page was opened again: nothing is "new" any more
        // Every folder, with how many videos it has (built from all of them, not just the cards shown); the lists are
        // brought up to date in place on every refresh, so they are there for an empty library too.
        UpdateChoices(Apps, [("", $"{Text.Get("clips.all")} ({next.Count})"),
            .. ClipLibrary.Groups(library).Select(g => (g.Group, $"{g.Group} ({g.Count})"))]);
        int pictureCount = next.Count(c => c.IsPicture);
        UpdateChoices(Kinds, [("", Text.Format("clips.kind.all", next.Count)), ("video", Text.Format("clips.kind.videos", next.Count - pictureCount)),
            ("picture", Text.Format("clips.kind.pictures", pictureCount))]);
        if (_filter.Length > 0 && !Apps.Any(a => a.Value == _filter))
        {
            _filter = ""; // its folder is gone
            Raise(nameof(Filter));
        }
        if (next.Count == _all.Count && next.Zip(_all).All(p => ReferenceEquals(p.First, p.Second)))
        {
            if (_confirmingPermanent)
            {
                _confirmingPermanent = false;
                Raise();
            }
            return;
        }
        _all = next;
        foreach (var clip in _all)
            clip.SelectionChanged = () =>
            {
                _confirmingPermanent = false;
                if (!_choosingAll)
                    Raise();
            };
        _confirmingPermanent = false;
        ApplyFilter();
    }

    /// <summary>Brings a choice list to <paramref name="wanted"/> in place: same entries, new counts, gone ones out.</summary>
    static void UpdateChoices(ObservableCollection<FolderChoice> list, List<(string Value, string Label)> wanted)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (!wanted.Any(w => w.Value == list[i].Value))
                list.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            var (value, label) = wanted[i];
            int at = -1;
            for (int j = 0; j < list.Count; j++)
                if (list[j].Value == value)
                    at = j;
            if (at < 0)
                list.Insert(i, new FolderChoice(value, label));
            else
            {
                if (at != i)
                    list.Move(at, i);
                list[i].Label = label;
            }
        }
    }

    void ApplyFilter()
    {
        Items.Clear();
        _filtered = _all.Where(c => (_filter.Length == 0 || string.Equals(c.App, _filter, StringComparison.OrdinalIgnoreCase))
                                    && (_kind.Length == 0 || c.IsPicture == (_kind == "picture"))).ToList();
        var now = DateTime.Now;
        foreach (var clip in _filtered)
            clip.GroupLabel = DayGroup(clip.Time, now);
        LoadMore();
        Raise();
    }

    /// <summary>Today, Yesterday, This week, This month or Older.</summary>
    static string DayGroup(DateTime time, DateTime now)
    {
        var day = time.Date;
        var today = now.Date;
        return Text.Get(day >= today ? "clips.groupToday"
            : day == today.AddDays(-1) ? "clips.groupYesterday"
            : day > today.AddDays(-7) ? "clips.groupWeek"
            : day > today.AddDays(-30) ? "clips.groupMonth"
            : "clips.groupOlder");
    }

    bool _choosingAll;

    void SetAll(bool selected)
    {
        _choosingAll = true; // one update for the page, not one per clip
        try
        {
            foreach (var c in _filtered)
                c.IsSelected = selected;
        }
        finally
        {
            _choosingAll = false;
        }
        Raise();
    }

    void Remove(List<ClipItem> clips, bool permanent)
    {
        if (clips.Count == 0)
            return;
        ClipDeleter.Delete(clips.Select(c => c.Path), permanent, ClipDeleter.Recycle);
        Refresh();
    }

    static void Open(string? path)
    {
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    static void ShowInFolder(string? path)
    {
        if (path != null && File.Exists(path))
            Process.Start(App.ShellPaths.ShowInFolder(path));
    }
}
