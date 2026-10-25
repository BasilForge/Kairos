using System.Windows;
using System.Windows.Controls;

namespace Kairos.Ui;

/// <summary>Small name dialog for rename / new folder (Enter confirms, Escape cancels).</summary>
public static class NamePrompt
{
    public static string? Ask(string title, string label, string initial = "")
    {
        var box = new TextBox
        {
            Text = initial,
            FontSize = 14,
            Padding = new Thickness(10, 8, 10, 8),
            MinWidth = 280,
        };
        var ok = new Button
        {
            Content = App.Text.Get("clips.confirm"),
            Style = (Style)Application.Current.FindResource("Primary"),
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(16, 8, 16, 8),
        };
        var cancel = new Button
        {
            Content = App.Text.Get("clips.cancel"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            IsCancel = true,
            Padding = new Thickness(16, 8, 16, 8),
        };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg"),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Margin = new Thickness(0, 0, 0, 8),
                        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary"),
                    },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { ok, cancel },
                    },
                },
            },
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text.Trim();
            if (result.Length == 0)
                return;
            window.DialogResult = true;
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        box.SelectAll();
        box.Focus();
        return window.ShowDialog() == true ? result : null;
    }
}
