using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Share;

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
    /// <summary>The clip's own camera file (opened from its card), or null.</summary>
    public string? Camera { get; init; }

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

    /// <summary>Pinned: stays under Favorites at the top of the Library.</summary>
    public bool IsFavorite
    {
        get => _favorite;
        set
        {
            if (_favorite == value)
                return;
            _favorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }

    bool _favorite;

    /// <summary>Its H.264 copy is being made for a drag (<see cref="PlayableCopies"/>): the card shows a ring.</summary>
    public bool Preparing
    {
        get => _preparing;
        set
        {
            if (_preparing == value)
                return;
            _preparing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preparing)));
        }
    }

    public double PrepareProgress
    {
        get => _prepareProgress;
        set
        {
            _prepareProgress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrepareProgress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreparePercent)));
        }
    }

    public string PreparePercent => $"{(int)(_prepareProgress * 100)}%";
    bool _preparing;
    double _prepareProgress;
    public Action? SelectionChanged { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A folder on the Library home: defaults (Favorites / Videos / Screenshots) or a user album.</summary>
public sealed class LibraryFolder
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Meta { get; init; }
    public required IReadOnlyList<ClipItem> Sheets { get; init; }
    /// <summary>Built-in folder: no rename / delete / hide from the ⋮ menu.</summary>
    public bool IsDefault { get; init; } = true;
    public string SizeText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    /// <summary>Newest clip shown as the card cover.</summary>
    public ClipItem? Cover => Sheets.Count > 0 ? Sheets[0] : null;
    /// <summary>Segoe Fluent icon for the folder kind.</summary>
    public string Glyph => Id switch
    {
        "favorites" => "\uE735",
        "video" => "\uE8B2",
        "picture" => "\uE91B",
        _ => "\uE8B7",
    };
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
/// Every video and picture in the clips folder and its subfolders. Home shows Favorites / Videos / Screenshots folders;
/// opening one shows its clips a page at a time. Pinned clips stay on top inside a folder.
/// </summary>
public sealed class ClipsViewModel : INotifyPropertyChanged
{
    /// <summary>Cards shown first (a screenful: quick to lay out), then added at a time; thumbnails only for cards shown.</summary>
    const int FirstPage = 24, PageSize = 36;
    /// <summary>Newest clips shown as "Recent" on home and at the top inside a folder.</summary>
    const int RecentCount = 5;
    readonly Func<string> _folder;
    readonly Func<string> _saveHotkey;
    readonly Func<string> _shotHotkey;
    readonly ClipFavorites _favorites;
    readonly LibraryAlbums _albums;
    List<ClipItem> _all = [];
    List<ClipItem> _filtered = [];
    string _filter = "";
    /// <summary>null = Library home; favorites / video / picture / u:{albumId}.</summary>
    string? _browse;
    bool _confirmingPermanent;
    LibraryFolder? _folderPendingDelete;

    public ClipsViewModel(Func<string> folder, Func<string> saveHotkey, Func<string>? shotHotkey = null,
        ClipFavorites? favorites = null, LibraryAlbums? albums = null)
    {
        _folder = folder;
        _saveHotkey = saveHotkey;
        _shotHotkey = shotHotkey ?? (() => "");
        _favorites = favorites ?? new ClipFavorites(ClipFavorites.DefaultPath);
        _albums = albums ?? new LibraryAlbums(LibraryAlbums.DefaultPath);
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
        OpenCameraCommand = new RelayCommand(p => Open((p as ClipItem)?.Camera));
        DeleteCommand = new RelayCommand(p => { if (p is ClipItem c) Remove([c], permanent: false); });
        ShareCommand = new RelayCommand(p => { if (p is ClipItem c) ShareRequested?.Invoke(c); });
        FavoriteCommand = new RelayCommand(p => { if (p is ClipItem c) ToggleFavorite(c); });
        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is LibraryFolder folder)
                Enter(folder.Id);
        });
        BackCommand = new RelayCommand(_ => Enter(null));
        AddFolderCommand = new RelayCommand(_ => AddFolder());
        RenameFolderCommand = new RelayCommand(p => { if (p is LibraryFolder { IsDefault: false } f) RenameFolder(f); });
        HideFolderCommand = new RelayCommand(p => { if (p is LibraryFolder { IsDefault: false } f) HideFolder(f); });
        UnhideFolderCommand = new RelayCommand(p => { if (p is LibraryFolder { IsDefault: false } f) UnhideFolder(f); });
        AskDeleteFolderCommand = new RelayCommand(p =>
        {
            if (p is not LibraryFolder { IsDefault: false } f)
                return;
            _folderPendingDelete = f;
            Raise();
        });
        ConfirmDeleteFolderCommand = new RelayCommand(_ => ConfirmDeleteFolder());
        CancelDeleteFolderCommand = new RelayCommand(_ => { _folderPendingDelete = null; Raise(); });
        SelectAllCommand = new RelayCommand(_ => SetAll(!AllSelected));
        ClearSelectionCommand = new RelayCommand(_ => SetAll(false));
        RecycleSelectedCommand = new RelayCommand(_ => Remove(Selected, permanent: false));
        AskPermanentCommand = new RelayCommand(_ => { _confirmingPermanent = true; Raise(); });
        ConfirmPermanentCommand = new RelayCommand(_ => Remove(Selected, permanent: true));
        CancelPermanentCommand = new RelayCommand(_ => { _confirmingPermanent = false; Raise(); });
        // Kind list kept for counts / older tests; navigation is the glass folders.
        UpdateChoices(Kinds, [("", Text.Format("clips.kind.all", 0)), ("video", Text.Format("clips.kind.videos", 0)),
            ("picture", Text.Format("clips.kind.pictures", 0))]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>The share button on a clip card: the window opens the share panel for it.</summary>
    public event Action<ClipItem>? ShareRequested;

    /// <summary>What a card's drag hands on (set by the window: its share host's copies); null: the clip itself.</summary>
    public PlayableCopies? Copies { get; set; }

    /// <summary>The file a card's drag hands on: the clip, or an HEVC clip's H.264 copy (null while it is still to make).</summary>
    public string? DragFile(ClipItem clip)
    {
        if (clip.IsPicture || Copies == null)
            return clip.Path;
        try
        {
            return Copies.FileFor(clip.Path);
        }
        catch (Exception e) when (e is ShareException or IOException)
        {
            return clip.Path; // cannot be read: dragged as it is
        }
    }

    /// <summary>A drag with nothing to hand on yet: the H.264 copy is made, its ring on the card, for the next drag.</summary>
    public void PrepareDrag(ClipItem clip)
    {
        if (Copies is not { } copies || clip.Preparing)
            return;
        clip.Preparing = true;
        clip.PrepareProgress = 0;
        var ui = System.Windows.Application.Current?.Dispatcher;
        Task.Run(() =>
        {
            try
            {
                copies.Make(clip.Path, new InlineProgress(p => ui?.BeginInvoke(() => clip.PrepareProgress = p)), CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ObsLog.Warn($"library drag: the H.264 copy of {Path.GetFileName(clip.Path)} was not made: {e.Message}");
            }
            finally
            {
                ui?.BeginInvoke(() => clip.Preparing = false);
            }
        });
    }
    /// <summary>A picture card was opened: the window shows it in its own viewer.</summary>
    public event Action<ClipItem>? PictureRequested;
    /// <summary>A video to play in Kairos's own player.</summary>
    public event Action<ClipItem>? VideoRequested;
    void Raise(string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public ObservableCollection<ClipItem> Items { get; } = [];
    public ObservableCollection<FolderChoice> Apps { get; } = [];
    /// <summary>All / Videos / Screenshots, with counts; the same entries stay while the counts change.</summary>
    public ObservableCollection<FolderChoice> Kinds { get; } = [];
    /// <summary>Glass folders above the grid (Favorites, Videos, Screenshots when each has clips).</summary>
    public ObservableCollection<LibraryFolder> Folders { get; } = [];

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

    /// <summary>"" (everything), "video" or "picture" — opens that folder (home when cleared). Kept for tests and counts.</summary>
    public string Kind
    {
        get => _browse is "video" or "picture" ? _browse : "";
        set
        {
            if (value == null)
                return;
            Enter(value.Length == 0 ? null : value);
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
            Raise(nameof(Filter));
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
    public bool ConfirmingFolderDelete => _folderPendingDelete != null;
    public string FolderDeleteText => _folderPendingDelete == null ? ""
        : Text.Format("clips.folder.deleteConfirm", _folderPendingDelete.Label);
    /// <summary>Hidden user albums (for the + menu to show again).</summary>
    public IReadOnlyList<LibraryFolder> HiddenFolders { get; private set; } = [];
    public bool HasHiddenFolders => HiddenFolders.Count > 0;
    /// <summary>Nothing in the library at all (home empty state).</summary>
    public bool IsEmpty => _all.Count == 0;
    /// <summary>Inside a folder with no matching clips.</summary>
    public bool FolderEmpty => InsideFolder && Items.Count == 0 && !HasMore;
    public string EmptyText => _browse == "picture" ? Text.Format("clips.emptyShots", _shotHotkey()) : Text.Format("clips.empty", _saveHotkey());

    public bool AtHome => _browse == null;
    public bool InsideFolder => _browse != null;
    public bool HasFolders => AtHome && Folders.Count > 0;
    /// <summary>Folder cards + the pink add tile on Library home.</summary>
    public bool ShowFolderRow => AtHome;
    /// <summary>Clip cards are shown on home (Recent) and inside a folder.</summary>
    public bool ShowClips => Items.Count > 0 || HasMore;
    public string FolderTitle => _browse switch
    {
        "favorites" => Text.Get("clips.favorites"),
        "video" => Text.Get("clips.groupVideos"),
        "picture" => Text.Get("clips.groupPictures"),
        _ when LibraryAlbums.AlbumIdOf(_browse) is { } id => _albums.Find(id)?.Name ?? Text.Get("clips.title"),
        _ => Text.Get("clips.title"),
    };
    /// <summary>Any clip is pinned.</summary>
    public bool HasFavorites => _all.Any(c => c.IsFavorite);
    public int FavoriteCount => _all.Count(c => c.IsFavorite);

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
    public ICommand FavoriteCommand { get; }
    /// <summary>Enters a folder (Favorites / Videos / Screenshots).</summary>
    public ICommand OpenFolderCommand { get; }
    /// <summary>Returns to the Library home (folder cards).</summary>
    public ICommand BackCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RenameFolderCommand { get; }
    public ICommand HideFolderCommand { get; }
    public ICommand UnhideFolderCommand { get; }
    public ICommand AskDeleteFolderCommand { get; }
    public ICommand ConfirmDeleteFolderCommand { get; }
    public ICommand CancelDeleteFolderCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand RecycleSelectedCommand { get; }
    /// <summary>Opens a clip's camera file in the usual player.</summary>
    public ICommand OpenCameraCommand { get; }
    public ICommand AskPermanentCommand { get; }
    public ICommand ConfirmPermanentCommand { get; }
    public ICommand CancelPermanentCommand { get; }

    /// <param name="live">A clip was saved while the page is open: what is new is marked so.</param>
    public void Refresh(bool live = false)
    {
        _favorites.Prune();
        var pinned = _favorites.Load();
        var library = ClipLibrary.Scan(_folder());
        _albums.PruneMissing(library.Select(c => c.Path));
        // Clips that did not change keep their card (and its picture): coming back to the page rebuilds nothing.
        var known = _all.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        bool favChanged = false;
        var next = library
            .Select(c =>
            {
                bool fav = pinned.Contains(c.Path);
                if (known.TryGetValue(c.Path, out var old) && old.Time == c.Time && old.Size == c.Size && old.App == c.Group && old.Camera == c.Camera)
                {
                    if (old.IsFavorite != fav)
                    {
                        old.IsFavorite = fav;
                        favChanged = true;
                    }
                    return old;
                }
                return new ClipItem
                {
                    Path = c.Path,
                    Name = System.IO.Path.GetFileNameWithoutExtension(c.Path),
                    App = c.Group,
                    Time = c.Time,
                    Size = c.Size,
                    Kind = c.Kind,
                    Camera = c.Camera,
                    IsNew = live,
                    IsFavorite = fav,
                };
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
        // Folder we were in no longer has anything (or the album was removed): go home.
        if (_browse == "favorites" && next.All(c => !c.IsFavorite)
            || _browse == "video" && next.All(c => c.IsPicture)
            || _browse == "picture" && next.All(c => !c.IsPicture)
            || LibraryAlbums.AlbumIdOf(_browse) is { } gone && _albums.Find(gone) == null)
            _browse = null;
        if (next.Count == _all.Count && next.Zip(_all).All(p => ReferenceEquals(p.First, p.Second)))
        {
            // Same clips: still refresh folder cards (albums may have changed) without rebuilding every card.
            if (favChanged)
                ApplyFilter();
            else
            {
                RebuildFolders();
                foreach (var clip in Folders.SelectMany(f => f.Sheets))
                    RequestThumb(clip);
                if (_confirmingPermanent)
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

    void Enter(string? browse)
    {
        if (_browse == browse)
            return;
        _browse = browse;
        _confirmingPermanent = false;
        SetAll(false);
        ApplyFilter();
    }

    void ApplyFilter()
    {
        Items.Clear();
        RebuildFolders();
        if (_browse == null)
        {
            // Home: last few clips under Recent (folders sit above).
            _filtered = _all.OrderByDescending(c => c.Time).Take(RecentCount).ToList();
            foreach (var clip in _filtered)
                clip.GroupLabel = Text.Get("clips.groupRecent");
            LoadMore();
            foreach (var clip in Folders.SelectMany(f => f.Sheets))
                RequestThumb(clip);
            Raise();
            return;
        }

        IEnumerable<ClipItem> q = _browse switch
        {
            "favorites" => _all.Where(c => c.IsFavorite),
            "video" => _all.Where(c => !c.IsPicture),
            "picture" => _all.Where(c => c.IsPicture),
            _ when LibraryAlbums.AlbumIdOf(_browse) is { } albumId =>
                UserAlbumClips(albumId),
            _ => _all,
        };
        if (_filter.Length > 0)
            q = q.Where(c => string.Equals(c.App, _filter, StringComparison.OrdinalIgnoreCase));
        _filtered = q.OrderByDescending(c => c.Time).ToList();
        // Pinned clips stay on top; then Recent (newest few); then older by day.
        List<ClipItem> pinned = [];
        bool userAlbum = LibraryAlbums.IsUserBrowse(_browse);
        if (_browse != "favorites" && !userAlbum && _filtered.Any(c => c.IsFavorite))
        {
            pinned = _filtered.Where(c => c.IsFavorite).ToList();
            _filtered = _filtered.Where(c => !c.IsFavorite).ToList();
            foreach (var clip in pinned)
                clip.GroupLabel = Text.Get("clips.groupFavorites");
        }
        var now = DateTime.Now;
        for (int i = 0; i < _filtered.Count; i++)
            _filtered[i].GroupLabel = i < RecentCount
                ? Text.Get("clips.groupRecent")
                : DayGroup(_filtered[i].Time, now);
        if (pinned.Count > 0)
            _filtered = [.. pinned, .. _filtered];
        LoadMore();
        Raise();
    }

    void RebuildFolders()
    {
        Folders.Clear();
        static List<ClipItem> Sheets(IEnumerable<ClipItem> clips) =>
            clips.OrderByDescending(c => c.Time).Take(RecentCount).ToList();

        LibraryFolder Make(string id, string label, string meta, List<ClipItem> clips, bool isDefault, DateTime? emptyModified = null)
        {
            long size = clips.Sum(c => c.Size);
            DateTime modified = clips.Count > 0 ? clips.Max(c => c.Time) : emptyModified ?? DateTime.Now;
            return new LibraryFolder
            {
                Id = id,
                Label = label,
                Meta = meta,
                Sheets = Sheets(clips),
                IsDefault = isDefault,
                SizeText = Text.Ltr(SettingsViewModel.FormatSize(size)),
                ModifiedText = modified.ToString("yyyy-MM-dd HH:mm"),
            };
        }

        var pinned = _all.Where(c => c.IsFavorite).ToList();
        if (pinned.Count > 0)
            Folders.Add(Make("favorites", Text.Get("clips.favorites"), Text.Count("count.files", pinned.Count), pinned, true));
        var videos = _all.Where(c => !c.IsPicture).ToList();
        if (videos.Count > 0)
            Folders.Add(Make("video", Text.Get("clips.groupVideos"), Text.Count("count.clips", videos.Count), videos, true));
        var pictures = _all.Where(c => c.IsPicture).ToList();
        if (pictures.Count > 0)
            Folders.Add(Make("picture", Text.Get("clips.groupPictures"), Text.Count("count.shots", pictures.Count), pictures, true));

        var hidden = new List<LibraryFolder>();
        foreach (var album in _albums.Load())
        {
            var clips = UserAlbumClips(album.Id).ToList();
            var card = Make(LibraryAlbums.BrowseId(album.Id), album.Name,
                Text.Count("count.files", clips.Count), clips, isDefault: false, album.Created);
            if (album.Hidden)
                hidden.Add(card);
            else
                Folders.Add(card);
        }
        HiddenFolders = hidden;
    }

    IEnumerable<ClipItem> UserAlbumClips(string albumId)
    {
        var album = _albums.Find(albumId);
        if (album == null)
            return [];
        var paths = album.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _all.Where(c => paths.Contains(c.Path));
    }

    void AddFolder()
    {
        string? name = NamePrompt.Ask(Text.Get("clips.folder.new"), Text.Get("clips.folder.nameHint"));
        if (string.IsNullOrWhiteSpace(name))
            return;
        var album = _albums.Add(name);
        Enter(LibraryAlbums.BrowseId(album.Id));
    }

    void RenameFolder(LibraryFolder folder)
    {
        if (LibraryAlbums.AlbumIdOf(folder.Id) is not { } id || _albums.Find(id) is not { } album)
            return;
        string? name = NamePrompt.Ask(Text.Get("clips.folder.rename"), Text.Get("clips.folder.nameHint"), album.Name);
        if (string.IsNullOrWhiteSpace(name) || name == album.Name)
            return;
        album.Name = name.Trim();
        _albums.Update(album);
        ApplyFilter();
    }

    void HideFolder(LibraryFolder folder)
    {
        if (LibraryAlbums.AlbumIdOf(folder.Id) is not { } id || _albums.Find(id) is not { } album)
            return;
        album.Hidden = true;
        _albums.Update(album);
        if (_browse == folder.Id)
            _browse = null;
        ApplyFilter();
    }

    void UnhideFolder(LibraryFolder folder)
    {
        if (LibraryAlbums.AlbumIdOf(folder.Id) is not { } id || _albums.Find(id) is not { } album)
            return;
        album.Hidden = false;
        _albums.Update(album);
        ApplyFilter();
    }

    void ConfirmDeleteFolder()
    {
        if (_folderPendingDelete == null || LibraryAlbums.AlbumIdOf(_folderPendingDelete.Id) is not { } id)
            return;
        if (_browse == _folderPendingDelete.Id)
            _browse = null;
        _albums.Remove(id);
        _folderPendingDelete = null;
        ApplyFilter();
    }

    static void RequestThumb(ClipItem clip)
    {
        if (clip.Thumbnail != null)
            return;
        var target = clip;
        ShellThumbnail.Request(clip.Path, image =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => target.Thumbnail = image));
    }

    void ToggleFavorite(ClipItem clip)
    {
        clip.IsFavorite = _favorites.Toggle(clip.Path);
        if (_browse == "favorites" && !_all.Any(c => c.IsFavorite))
            _browse = null;
        ApplyFilter();
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
        _favorites.Remove(clips.Select(c => c.Path));
        _albums.RemovePaths(clips.Select(c => c.Path));
        ClipDeleter.Delete(clips.Select(c => c.Path), permanent, ClipDeleter.Recycle);
        Refresh();
    }

    static void Open(string? path)
    {
        if (path != null && File.Exists(path))
            App.ShellPaths.Open(path);
    }

    static void ShowInFolder(string? path) => App.ShellPaths.ShowInExplorer(path);
}
