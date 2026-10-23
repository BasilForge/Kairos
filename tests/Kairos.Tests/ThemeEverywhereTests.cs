using System.Drawing;
using System.Windows.Media;
using Kairos.App;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>
/// Everything Kairos shows wears its one theme and speaks the chosen language: drop-down lists, pop-up menus and
/// tooltips, the tray menu, and the option lists after a language switch.
/// </summary>
public static class ThemeEverywhereTests
{
    static void OnSta(Action test)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error == null, error ?? "");
    }

    static System.Windows.Media.Color Rgb(uint rgb) => System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    [Test]
    public static void Drop_down_lists_menus_and_tooltips_use_the_warm_theme_not_windows_grey() => OnSta(() =>
    {
        var theme = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/Kairos;component/Ui/Theme.xaml", UriKind.Relative));
        System.Windows.Media.Color Of(string key) => ((SolidColorBrush)theme[key]).Color;
        var surface = Of("Surface");
        var stroke = Of("Stroke");
        foreach (string key in new[] { "ComboBoxDropDownBackground", "ContextMenuBackground", "FlyoutBackground", "ToolTipBackground" })
            Assert.Equal(surface, Of(key), $"{key}: the cards' warm brown (Fluent's own is grey #2C2C2C)");
        foreach (string key in new[] { "ComboBoxDropDownBorderBrush", "ContextMenuBorderBrush", "FlyoutBorderBrush", "ToolTipBorderBrush" })
            Assert.Equal(stroke, Of(key), $"{key}: the cards' hairline");
        var hover = Of("ComboBoxItemBackgroundSelected");
        Assert.True(hover.A == 255 && hover.R > hover.B && hover != surface, $"a highlighted entry is a warm shade of the list ({hover})");

        var r = new System.Windows.ResourceDictionary();
        ThemeAccent.Apply(r);
        Assert.Equal(Rgb(ThemeColors.Accent), ((SolidColorBrush)r["ComboBoxItemPillFillBrush"]).Color,
            "the bar beside the chosen entry is rose (Fluent's own is grey #606060)");
    });

    [Test]
    public static void The_tray_menu_is_drawn_in_the_app_theme_not_white() => OnSta(() =>
    {
        using var menu = TrayMenu.Create(rightToLeft: false);
        menu.Items.Add("Save clip");
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit");
        var size = menu.GetPreferredSize(System.Drawing.Size.Empty);
        menu.Size = size;
        using var bmp = new Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(bmp, new Rectangle(System.Drawing.Point.Empty, size));

        var counts = new Dictionary<int, int>();
        int light = 0, white = 0;
        for (int x = 0; x < bmp.Width; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                var p = bmp.GetPixel(x, y);
                counts[p.ToArgb() & 0xFFFFFF] = counts.GetValueOrDefault(p.ToArgb() & 0xFFFFFF) + 1;
                if (p.R > 0xE0 && p.G > 0xD0)
                    light++;
                if (p.R > 0xF8 && p.G > 0xF8 && p.B > 0xF8)
                    white++;
            }
        int background = counts.MaxBy(c => c.Value).Key;
        Assert.Equal(ThemeColors.Background, (uint)background, $"the menu's fill is the window's brown, not #{background:X6}");
        Assert.True(light > 20, "with light text on it");
        Assert.True(white < light / 4, $"nothing white-on-white Windows look ({white} pure white pixels)");
    });

    [Test]
    public static void The_tray_tooltip_is_just_the_name()
    {
        foreach (string language in new[] { "en", "ar" })
        {
            Text.Language = language;
            Assert.Equal("Kairos", TrayController.Tooltip, $"{language}: no state text");
        }
        Text.Language = "en";
    }

    [Test]
    public static void Switching_the_language_relabels_every_list_in_the_window() => OnSta(() =>
    {
        Text.Language = "ar";
        var vm = new SettingsViewModel();
        Assert.Equal("بلا حد", vm.FolderLimits[0].Label, "built in Arabic");

        vm.ApplyLanguage("en");
        Assert.Equal("en", Text.Language, "the language");
        Assert.Equal("No limit", vm.FolderLimits[0].Label, "folder size limit");
        Assert.Equal(Text.Format("unit.seconds", 10), vm.QuickOptions[0].Label, "quick save length");
        Assert.True(!vm.QuickOptions[0].Label.Any(c => c is >= '\u0600' and <= '\u06FF'), vm.QuickOptions[0].Label);
        Assert.Equal("30 fps", vm.FpsOptions[0].Label, "no right-to-left marks left in English");
        Assert.Equal("128 kbps", vm.AudioBitrates[0].Label, "audio bitrate");
        Assert.Equal("100 GB", vm.FolderLimits[3].Label, "sizes");
        Assert.Equal(Text.Get("audio.default"), vm.Speakers[0].Label, "the default device");
        Assert.True(vm.Resolutions.Count == 0 || vm.Resolutions[0].Label.StartsWith(Text.Get("quality.native")), "native resolution");

        vm.ApplyLanguage("fr");
        Assert.Equal("30 i/s", vm.FpsOptions[0].Label, "French frame rate");
        Assert.Equal("128 kbit/s", vm.AudioBitrates[0].Label, "French audio bitrate");
        Assert.Equal("100 Go", vm.FolderLimits[3].Label, "French sizes");
        Assert.Equal("1 To", vm.FolderLimits[^1].Label, "French terabytes");

        vm.ApplyLanguage("ar");
        Assert.Equal("بلا حد", vm.FolderLimits[0].Label, "and back");
        Text.Language = "en";
    });
}
