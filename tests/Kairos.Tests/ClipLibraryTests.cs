using Kairos.App;
using Kairos.Engine;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The Clips page over any folder (Kairos's own or Windows' Videos): every video in it and its subfolders.</summary>
public static class ClipLibraryTests
{
    static readonly DateTime T0 = new(2026, 10, 14, 20, 0, 0);

    static string TempRoot(string name = "Videos")
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-library-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(root);
        return root;
    }

    static string Put(string root, string relative, int minutes)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2]);
        File.SetLastWriteTime(path, T0.AddMinutes(minutes));
        return path;
    }

    static void Clean(string root) => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);

    [Test]
    public static void Every_mp4_in_the_folder_and_its_subfolders_is_listed_newest_first()
    {
        string root = TempRoot();
        try
        {
            string top = Put(root, "2026-05-12 03-11-53.mp4", 1);
            string apex = Put(root, @"Apex Legends\Apex Legends 2026.05.07.mp4", 3);
            string deep = Put(root, @"Apex Legends\Highlights\kill.mp4", 2);
            Put(root, @".preview_cache\junk.mp4", 9);
            string hidden = Put(root, @"Hidden\secret.mp4", 8);
            File.SetAttributes(Path.GetDirectoryName(hidden)!, FileAttributes.Directory | FileAttributes.Hidden);
            Put(root, "notes.txt", 7);
            Put(root, "other.mkv", 6);

            var clips = ClipLibrary.Scan(root);
            Assert.Equal(string.Join(" | ", new[] { apex, deep, top }), string.Join(" | ", clips.Select(c => c.Path)),
                "subfolders at any depth, newest first; no hidden or dot folders, no other files");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void Screenshots_are_listed_beside_the_videos_with_their_kind()
    {
        string root = TempRoot();
        try
        {
            string clip = Put(root, @"Elden Ring\Elden Ring 2026-10-15 21-00-00.mp4", 1);
            string shot = Put(root, @"Elden Ring\Elden Ring 2026-10-15 21-30-00.png", 4);
            string gameBar = Put(root, @"Captures\Elden Ring 10_15_2026.jpg", 3);
            string jpeg = Put(root, "photo.JPEG", 2);
            Put(root, "notes.txt", 9);
            Put(root, "anim.gif", 8);
            Put(root, @".thumbs\cache.png", 7);

            var found = ClipLibrary.Scan(root);
            Assert.Equal(string.Join(" | ", new[] { shot, gameBar, jpeg, clip }), string.Join(" | ", found.Select(c => c.Path)),
                "videos and pictures, newest first; no other files, no dot folders");
            Assert.Equal("Picture Picture Picture Video", string.Join(" ", found.Select(c => c.Kind)), "each with its kind");
            Assert.Equal(ClipKind.Picture, ClipLibrary.KindOf(@"C:\a\b.Png"), "any case");
            Assert.Equal(null, ClipLibrary.KindOf(@"C:\a\b.webm"), "not ours");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void The_clips_page_filters_videos_or_screenshots_inside_a_folder()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            Put(root, @"Apex Legends\Apex Legends 2026-10-15 20-00-00.mp4", 1);
            Put(root, @"Apex Legends\Apex Legends 2026-10-15 20-01-00.png", 2);
            Put(root, @"Apex Legends\Apex Legends 2026-10-15 20-02-00.png", 3);
            Put(root, @"Desktop\Desktop 2026-10-15 20-03-00.mp4", 4);
            Put(root, @"Desktop\Desktop 2026-10-15 20-04-00.png", 5);
            var vm = new ClipsViewModel(() => root, () => "Alt+F10", () => "Alt+F12");
            vm.Refresh();

            Assert.True(vm.CountText.StartsWith("2 clips • 3 screenshots"), vm.CountText);
            Assert.Equal("All (5) | Videos (2) | Screenshots (3)", string.Join(" | ", vm.Kinds.Select(k => k.Label)), "the kind list with counts");
            vm.Kind = "picture";
            Assert.Equal(3, vm.Items.Count, "screenshots only");
            Assert.True(vm.Items.All(c => c.IsPicture), "all pictures");
            vm.Filter = "Apex Legends";
            Assert.Equal(2, vm.Items.Count, "screenshots of that game");
            vm.Kind = "video";
            Assert.Equal(1, vm.Items.Count, "its videos");
            vm.Kind = null!;
            Assert.Equal("video", vm.Kind, "null from the list itself is not a choice");
            vm.Kind = "";
            Assert.Equal(3, vm.Items.Count, "everything of that game");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void The_last_clip_is_the_newest_video_not_a_newer_screenshot()
    {
        string root = TempRoot();
        try
        {
            string clip = Put(root, @"Game\Game 2026-10-15 21-00-00.mp4", 1);
            Put(root, @"Game\Game 2026-10-15 21-30-00.png", 5);
            Assert.Equal(clip, ClipLibrary.NewestVideo(root), "the Status page's last clip is a video");
            Assert.Equal(null, ClipLibrary.NewestVideo(Path.Combine(root, "none")), "no folder, no clip");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void Counts_use_the_right_form_of_the_word()
    {
        Text.Language = "en";
        Assert.Equal("1 clip | 2 clips | 1 screenshot | 5 screenshots | 1 file | 3 files",
            string.Join(" | ", Text.Count("count.clips", 1), Text.Count("count.clips", 2), Text.Count("count.shots", 1),
                Text.Count("count.shots", 5), Text.Count("count.files", 1), Text.Count("count.files", 3)), "English: one and the rest");
        Text.Language = "ar";
        // Formal Arabic: the number follows the word for one, the dual stands alone, 3–10 take the plural, 11–99 the singular
        // with tanween, and 100, 200… the plain singular.
        Assert.Equal("مقطع واحد | مقطعان | 3 مقاطع | 10 مقاطع | 11 مقطعًا | 99 مقطعًا | 100 مقطع | 103 مقاطع | 111 مقطعًا",
            string.Join(" | ", new[] { 1, 2, 3, 10, 11, 99, 100, 103, 111 }.Select(n => Text.Count("count.clips", n))), "Arabic: one, two, 3–10, 11–99, and 100");
        Assert.Equal("لقطة شاشة واحدة | لقطتا شاشة | 4 لقطات شاشة | 25 لقطة شاشة",
            string.Join(" | ", new[] { 1, 2, 4, 25 }.Select(n => Text.Count("count.shots", n))), "screenshots");
        Assert.Equal("ثانية واحدة | ثانيتين | 10 ثوانٍ | 30 ثانية", string.Join(" | ", new[] { 1, 2, 10, 30 }.Select(n => Text.Count("count.seconds", n))), "seconds");
        Text.Language = "en";
    }

    [Test]
    public static void The_clips_page_counts_one_clip_and_one_screenshot_in_the_singular()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            Put(root, @"Desktop\Desktop 2026-10-15 20-00-00.mp4", 1);
            Put(root, @"Desktop\Desktop 2026-10-15 20-01-00.png", 2);
            var vm = new ClipsViewModel(() => root, () => "Alt+F10", () => "Alt+F12");
            vm.Refresh();
            Assert.True(vm.CountText.StartsWith("1 clip • 1 screenshot • ", StringComparison.Ordinal), vm.CountText);
            File.Delete(Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories).Single());
            vm.Refresh();
            Assert.True(vm.CountText.StartsWith("1 screenshot • ", StringComparison.Ordinal), $"no \"0 clips\": {vm.CountText}");
            vm.Items[0].IsSelected = true;
            Assert.Equal("Permanently delete 1 file? This cannot be undone.", vm.ConfirmText, "the delete question, whatever is selected");
        }
        finally
        {
            Clean(root);
        }
    }

    /// <summary>Select all chooses every clip shown, also the cards not loaded yet, and the title tells their size.</summary>
    [Test]
    public static void Select_all_chooses_every_clip_and_the_title_tells_their_size()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            for (int i = 0; i < 30; i++)
                File.WriteAllBytes(Put(root, $@"Desktop\Desktop 2026-10-15 20-{i:00}-00.mp4", i), new byte[1 << 20]);
            var vm = new ClipsViewModel(() => root, () => "Alt+F10", () => "Alt+F12");
            vm.Refresh();
            Assert.True(vm.Items.Count < 30, "only the first cards are loaded");
            Assert.Equal("", vm.SelectedSizeText, "nothing chosen: no size");
            vm.Items[0].IsSelected = true;
            Assert.Equal(Text.Ltr("1 MB"), vm.SelectedSizeText, "one clip");
            vm.SelectAllCommand.Execute(null);
            Assert.Equal(30, vm.SelectedCount, "every clip, also those not loaded yet");
            Assert.True(vm.AllSelected, "all chosen");
            Assert.Equal(Text.Ltr("30 MB"), vm.SelectedSizeText, "their size");
            vm.LoadMore();
            Assert.True(vm.Items.All(c => c.IsSelected), "cards loaded later show as chosen");
            vm.ClearSelectionCommand.Execute(null);
            Assert.Equal(0, vm.SelectedCount, "cleared");
            Assert.Equal("", vm.SelectedSizeText, "no size again");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void An_empty_library_still_lists_all_zero_in_both_filters()
    {
        // A first install: the clips folder is new and empty, and the two boxes above the page must not be blank.
        string root = TempRoot();
        try
        {
            var vm = new ClipsViewModel(() => root, () => "Alt+F10", () => "Alt+F12");
            vm.Refresh();
            Assert.Equal(1, vm.Apps.Count, "the folder box has its 'All' entry");
            Assert.True(vm.Apps[0].Value == "" && vm.Apps[0].Label.EndsWith("(0)"), $"All, counting nothing: {vm.Apps[0].Label}");
            Assert.Equal("|video|picture", string.Join("|", vm.Kinds.Select(k => k.Value)), "All, videos and screenshots");
            Assert.True(vm.Kinds.All(k => k.Label.Contains('0')), "each counts nothing: " + string.Join(", ", vm.Kinds.Select(k => k.Label)));
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void Playing_a_screenshot_opens_it_in_kairos_not_in_another_app()
    {
        // A picture that is not there: if it went to the shell, nothing would open (and nothing shows during tests).
        var vm = new ClipsViewModel(() => @"C:\nowhere", () => "Alt+F10", () => "Alt+F12");
        var picture = new ClipItem { Path = @"C:\nowhere\Game 2026-10-15 21-30-00.png", Name = "x", App = "Game", Time = T0, Size = 1, Kind = ClipKind.Picture };
        ClipItem? asked = null;
        vm.PictureRequested += c => asked = c;
        vm.PlayCommand.Execute(picture);
        Assert.True(ReferenceEquals(picture, asked), "the window shows it in its own viewer");
    }

    [Test]
    public static void Clips_are_grouped_by_their_first_folder_or_by_the_app_in_their_name()
    {
        const string root = @"D:\Media\Videos";
        Assert.Equal("Apex Legends", ClipLibrary.GroupOf(root, root + @"\Apex Legends\x.mp4"), "its folder");
        Assert.Equal("Apex Legends", ClipLibrary.GroupOf(root, root + @"\Apex Legends\Highlights\y.mp4"), "a folder inside counts for the game");
        Assert.Equal("Desktop", ClipLibrary.GroupOf(root, root + @"\Desktop 2026-10-14 23-17-00.mp4"), "Kairos's own name says the app");
        Assert.Equal("Desktop", ClipLibrary.GroupOf(root, root + @"\Desktop 2026-10-14 23-17-00 (2).mp4"), "a second clip that second");
        Assert.Equal("Videos", ClipLibrary.GroupOf(root, root + @"\2026-05-12 03-11-53.mp4"), "anything else: the folder's own name");
    }

    [Test]
    public static void The_folder_list_has_every_group_with_its_count()
    {
        var clips = new[] { "Desktop", "Apex Legends", "Desktop", "Videos", "Apex Legends", "Apex Legends" }
            .Select((g, i) => new LibraryClip($@"C:\v\{i}.mp4", g, T0, 1)).ToList();
        Assert.Equal("Apex Legends 3, Desktop 2, Videos 1",
            string.Join(", ", ClipLibrary.Groups(clips).Select(g => $"{g.Group} {g.Count}")), "alphabetical, with counts");
    }

    [Test]
    public static void The_clips_page_loads_every_video_a_page_at_a_time()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            for (int i = 0; i < 131; i++)
                Put(root, $"2026-05-12 03-{i / 60:00}-{i % 60:00}.mp4", i);
            for (int i = 0; i < 48; i++)
                Put(root, $@"Apex Legends\Apex {i}.mp4", -100 - i);
            var vm = new ClipsViewModel(() => root, () => "Ctrl+F8");
            vm.Refresh();
            Assert.Equal(24, vm.Items.Count, "the first page: a screenful, quick to show");
            Assert.True(vm.HasMore, "more to load");
            Assert.True(vm.CountText.StartsWith("179 clips"), vm.CountText);
            Assert.Equal("All folders (179) | Apex Legends (48) | Videos (131)", string.Join(" | ", vm.Apps.Select(a => a.Label)), "every folder, even the older ones");
            while (vm.HasMore)
                vm.LoadMore();
            Assert.Equal(179, vm.Items.Count, "all of them, down to the oldest");
            Assert.True(!vm.HasMore, "nothing left");
            vm.Filter = "Apex Legends";
            Assert.Equal(24, vm.Items.Count, "one folder: its first page");
            while (vm.HasMore)
                vm.LoadMore();
            Assert.Equal(48, vm.Items.Count, "one folder: all of it");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void Coming_back_to_the_page_keeps_the_cards_and_their_pictures()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            for (int i = 0; i < 30; i++)
                Put(root, $@"Desktop\Desktop {i:00}.mp4", i);
            var vm = new ClipsViewModel(() => root, () => "Ctrl+F8");
            vm.Refresh();
            var first = vm.Items.ToList();
            first[0].Thumbnail = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
            var changes = 0;
            vm.Items.CollectionChanged += (_, _) => changes++;

            vm.Refresh();
            Assert.Equal(0, changes, "nothing changed: the cards stay as they are");
            Assert.True(vm.Items.SequenceEqual(first), "the same cards");
            Assert.True(vm.Items[0].Thumbnail != null, "with their pictures");

            string newest = Put(root, @"Desktop\Desktop new.mp4", 60);
            vm.Refresh();
            Assert.Equal(newest, vm.Items[0].Path, "a new clip shows first");
            Assert.True(ReferenceEquals(first[0], vm.Items[1]) && vm.Items[1].Thumbnail != null, "the others keep their cards and pictures");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void A_clip_saved_while_the_page_is_open_shows_at_once_marked_new()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            for (int i = 0; i < 5; i++)
                Put(root, $@"Desktop\Desktop {i}.mp4", i);
            var vm = new ClipsViewModel(() => root, () => "Ctrl+F8");
            vm.Refresh();
            Assert.True(vm.Items.All(c => !c.IsNew), "nothing is new when the page opens");

            string saved = Put(root, @"Desktop\Desktop saved.mp4", 10);
            vm.Refresh(live: true);
            Assert.Equal(saved, vm.Items[0].Path, "the clip just saved, first");
            Assert.True(vm.Items[0].IsNew && vm.Items.Skip(1).All(c => !c.IsNew), "only it is marked new");

            vm.Refresh();
            Assert.True(vm.Items.All(c => !c.IsNew), "coming back to the page: no longer new");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void The_folder_list_keeps_showing_its_choice_when_the_clips_change()
    {
        Text.Language = "en";
        string root = TempRoot();
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 3; i++)
                    Put(root, $@"Desktop\Desktop {i}.mp4", i);
                var vm = new ClipsViewModel(() => root, () => "Ctrl+F8");
                vm.Refresh();
                // As on the Clips page.
                var box = new System.Windows.Controls.ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", DataContext = vm };
                box.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Apps"));
                box.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new System.Windows.Data.Binding("Filter") { Mode = System.Windows.Data.BindingMode.TwoWay });
                Assert.Equal("All folders (3)", (box.SelectedItem as FolderChoice)?.Label, "at first");

                Put(root, @"Desktop\Desktop 3.mp4", 5);
                vm.Refresh(live: true);
                Assert.Equal("All folders (4)", (box.SelectedItem as FolderChoice)?.Label, "still shown after a new clip");

                box.SelectedValue = "Desktop";
                Put(root, @"Desktop\Desktop 4.mp4", 6);
                vm.Refresh(live: true);
                Assert.Equal(("Desktop (5)", "Desktop"), ((box.SelectedItem as FolderChoice)?.Label, vm.Filter), "a folder chosen stays chosen");
                Assert.Equal(5, vm.Items.Count, "with its clips");
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Clean(root);
        Assert.True(error == null, error ?? "");
    }

    [Test]
    public static void A_clips_camera_file_is_part_of_the_clip()
    {
        string root = TempRoot();
        string clip = Path.Combine(root, "Valorant 2026-10-20 03-00-00.mp4");
        File.WriteAllText(clip, "x");
        File.WriteAllText(Path.ChangeExtension(clip, null) + ".camera.mp4", "x");
        File.WriteAllText(Path.Combine(root, "Valorant 2026-10-20 03-05-00.mp4"), "x");
        var found = ClipLibrary.Scan(root);
        Assert.Equal(2, found.Count, "the camera file is not a card of its own");
        Assert.Equal(Path.ChangeExtension(clip, null) + ".camera.mp4", found.Single(c => c.Path == clip).Camera, "it goes with its clip");
        Assert.Equal(null, found.Single(c => c.Path != clip).Camera, "a clip without one");
        var recycled = new List<string>();
        ClipDeleter.Delete([clip], permanent: false, recycled.Add);
        Assert.Equal(2, recycled.Count, "deleting the clip takes its camera file too");
    }
}
