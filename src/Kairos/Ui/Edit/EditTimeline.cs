using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairos.App;
using Kairos.Engine.Edit;
using Kairos.Engine.Share;

namespace Kairos.Ui.Edit;

/// <summary>
/// The edited clip from left to right in rows: its name on the left of each row, a ruler on top, the clip's parts with
/// frames (each part's speed and sound shown on it), then a row per kind of item with one bar per item and its words.
/// A press moves the playhead and chooses what is under it (Shift or Ctrl adds it); a row's name chooses the whole row;
/// a bar drags in time, its ends trim it. Ctrl+wheel zooms, the wheel scrolls sideways when zoomed. Time runs left to
/// right in both languages; nothing scrolls up or down (rows show only when they hold something).
/// </summary>
public sealed class EditTimeline : FrameworkElement
{
    const int Frames = 16;
    const double Radius = 6, BarRadius = 5;

    readonly EditorViewModel _vm;
    readonly Brush _surface, _stroke, _accent, _accentSoft, _text, _text2;
    readonly Pen _playhead, _chosen, _rowLine, _tick, _cut;
    readonly Typeface _font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    readonly Typeface _plain = new("Segoe UI");
    readonly Typeface _icons = new("Segoe Fluent Icons");
    readonly SortedList<double, BitmapSource> _frames = []; // source time -> picture
    double _scroll;
    Gesture _gesture;
    Point _pressAt;
    bool _moved;
    readonly List<EditItem> _originals = [];
    EditItem? _edgeItem;
    bool _edgeStart;
    double _pressOutput;

    enum Gesture { None, Seek, Edge, Move, Box }

    /// <summary>The edit and the chosen items when a drag started (every move is worked out from them).</summary>
    EditProject? _pressProject;
    /// <summary>A box being dragged over the timeline (to choose the bars it touches), and what was chosen before it.</summary>
    Rect? _box;
    readonly List<EditItem> _boxBase = [];

    /// <summary>
    /// The playhead in a layer of its own over the drawing (clipped to the lane like it): while the clip plays it only
    /// moves, and the timeline is drawn again only when something on it changes.
    /// </summary>
    readonly ContainerVisual _headLayer = new();
    readonly DrawingVisual _head = new();
    readonly TranslateTransform _headAt = new();
    double _headHeight = -1;
    Rect _headLane;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _headLayer : throw new ArgumentOutOfRangeException(nameof(index));

    public EditTimeline(EditorViewModel vm)
    {
        _vm = vm;
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = true;
        // Clicked, it takes the keyboard from a slider used before, so the arrow keys move through the clip.
        Focusable = true;
        FocusVisualStyle = null;
        _surface = Find("Surface", Brushes.DimGray);
        _stroke = Find("Stroke", Brushes.Gray);
        _accent = Find("Accent", Brushes.HotPink);
        _accentSoft = Find("AccentSoft", Brushes.DarkMagenta);
        _text = Find("TextPrimary", Brushes.White);
        _text2 = Find("TextSecondary", Brushes.LightGray);
        _playhead = Freeze(new Pen(_accent, 2));
        _chosen = Freeze(new Pen(_accent, 2));
        _rowLine = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));
        _tick = Freeze(new Pen(_text2, 1));
        _cut = Freeze(new Pen(_accent, 1.5) { DashStyle = DashStyles.Dash, DashCap = PenLineCap.Flat });
        _head.Transform = _headAt;
        _headLayer.Children.Add(_head);
        AddVisualChild(_headLayer);
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(EditorViewModel.PlayheadOutput):
                    double scrolled = _scroll;
                    if (_gesture == Gesture.None)
                        Follow();
                    // Only the playhead moves, unless the view scrolled to keep it in sight (then all is drawn again).
                    if (Math.Abs(_scroll - scrolled) > 1e-9 || _headHeight < 0)
                        InvalidateVisual();
                    else
                        _headAt.X = Layout().XOf(_vm.PlayheadOutput);
                    break;
                case nameof(EditorViewModel.TimelineZoom):
                    _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Layout().MaxScroll));
                    Follow();
                    InvalidateVisual();
                    break;
                case nameof(EditorViewModel.Project):
                    InvalidateMeasure(); // a row grows a layer when items come to overlap
                    InvalidateVisual();
                    break;
                case nameof(EditorViewModel.SelectedItems) or nameof(EditorViewModel.SelectedParts):
                    InvalidateVisual();
                    break;
            }
        };
        Loaded += (_, _) => LoadFrames();
    }

    static Brush Find(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>The most height the timeline may take (its MaxHeight, set by the page): rows grow up to it, never past.</summary>
    double _maxHeight = double.PositiveInfinity;

    double _fillHeight;

    /// <summary>The height the page offers the timeline (a share of the editor's): a taller editor, taller rows.</summary>
    public double FillHeight
    {
        get => _fillHeight;
        set
        {
            if (Math.Abs(value - _fillHeight) < 0.5)
                return;
            _fillHeight = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    TimelineLayout Layout() => new(_vm.Project, _vm.Rows, ActualWidth, _vm.TimelineZoom, _scroll, _maxHeight, _fillHeight);

    protected override Size MeasureOverride(Size available)
    {
        _maxHeight = available.Height;
        return new(double.IsInfinity(available.Width) ? 600 : available.Width, new TimelineLayout(_vm.Project, _vm.Rows, 600, maxHeight: _maxHeight, fillHeight: _fillHeight).Height);
    }

    /// <summary>Zoomed in, the playhead stays on screen while it moves.</summary>
    void Follow()
    {
        var layout = Layout();
        double scroll = TimelineLayout.ScrollToShow(layout, _vm.PlayheadOutput);
        if (Math.Abs(scroll - _scroll) > 0.5)
            _scroll = scroll;
    }

    /// <summary>A few key frames along the whole clip, read once in the background (the editor only).</summary>
    void LoadFrames()
    {
        string source = _vm.Project.Source;
        double duration = _vm.Project.Duration;
        if (duration <= 0 || _frames.Count > 0)
            return;
        Task.Run(() =>
        {
            try
            {
                using var grabber = new FrameGrabber(source);
                for (int i = 0; i < Frames; i++)
                {
                    double at = duration * (i + 0.5) / Frames;
                    if (grabber.Grab(at, 160, exact: false) is not { } frame)
                        continue;
                    var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Width * 4);
                    bitmap.Freeze();
                    Dispatcher.BeginInvoke(() =>
                    {
                        _frames[at] = bitmap;
                        InvalidateVisual();
                    });
                }
            }
            catch (Exception e) when (e is ShareException or IOException or InvalidOperationException)
            {
                // No frames: the parts show plain.
            }
        });
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>How many times the timeline was drawn (tests: moving the playhead does not draw it again).</summary>
    internal int Renders { get; private set; }

    protected override void OnRender(DrawingContext dc)
    {
        Renders++;
        var p = _vm.Project;
        var layout = Layout();
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || p.OutputDuration <= 0)
        {
            _headLayer.Opacity = 0;
            _headHeight = -1;
            return;
        }
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height)); // the whole area takes clicks
        var rows = _vm.Rows;

        // Row lines and names
        for (int i = 0; i < rows.Count; i++)
        {
            double top = layout.RowTop(i), h = layout.RowHeightOf(i);
            dc.DrawLine(_rowLine, new Point(0, top), new Point(width, top));
            DrawHead(dc, rows[i], new Rect(0, top, TimelineLayout.HeadWidth, h));
        }
        dc.DrawLine(_rowLine, new Point(TimelineLayout.HeadWidth, 0), new Point(TimelineLayout.HeadWidth, height));

        // Everything right of the names is clipped to the lane (zoomed in, it runs past both sides).
        dc.PushClip(new RectangleGeometry(new Rect(TimelineLayout.HeadWidth, 0, Math.Max(0, width - TimelineLayout.HeadWidth), height)));
        DrawRuler(dc, layout);
        var parts = layout.PartBoxes();
        for (int i = 0; i < parts.Count; i++)
            DrawPart(dc, parts[i], p.Kept[i], _vm.IsPartChosen(i), i);
        // Where one part ends and the next begins: a cut mark through the gap, so the parts are told apart at a glance.
        double partsTop = layout.RowTop(0), partsBottom = partsTop + layout.RowHeightOf(0);
        for (int i = 1; i < parts.Count; i++)
        {
            double x = Math.Round(parts[i].Left - TimelineLayout.PartGap / 2) + 0.5;
            if (x < TimelineLayout.HeadWidth || x > width)
                continue;
            dc.DrawLine(_cut, new Point(x, partsTop + 2), new Point(x, partsBottom - 2));
            dc.PushTransform(new TranslateTransform(x, partsTop));
            dc.DrawGeometry(_accent, null, CutKnob);
            dc.Pop();
        }
        for (int row = 1; row < rows.Count; row++)
            foreach (var (bar, box) in layout.Bars(row))
                DrawBar(dc, bar.Item, box, _vm.IsChosen(bar.Item));

        if (_box is { } choosing)
            dc.DrawRectangle(BoxFill, BoxEdge, choosing);

        dc.Pop();
        PlaceHead(layout, width, height);
    }

    /// <summary>The playhead layer: clipped to the lane, drawn again only when the height changes, moved to the playhead.</summary>
    void PlaceHead(TimelineLayout layout, double width, double height)
    {
        var lane = new Rect(TimelineLayout.HeadWidth, 0, Math.Max(0, width - TimelineLayout.HeadWidth), height);
        if (lane != _headLane)
        {
            _headLane = lane;
            var clip = new RectangleGeometry(lane);
            clip.Freeze();
            _headLayer.Clip = clip;
        }
        if (height != _headHeight)
        {
            _headHeight = height;
            using var dc = _head.RenderOpen();
            dc.DrawLine(_playhead, new Point(0, 4), new Point(0, height));
            var knob = new StreamGeometry();
            using (var g = knob.Open())
            {
                g.BeginFigure(new Point(-6, 2), true, true);
                g.LineTo(new Point(6, 2), true, false);
                g.LineTo(new Point(6, 9), true, false);
                g.LineTo(new Point(0, 14), true, false);
                g.LineTo(new Point(-6, 9), true, false);
            }
            knob.Freeze();
            dc.DrawGeometry(_accent, null, knob);
        }
        _headLayer.Opacity = 1;
        _headAt.X = layout.XOf(_vm.PlayheadOutput);
    }

    void DrawHead(DrawingContext dc, TimelineRow row, Rect box)
    {
        bool chosen = _vm.IsRowChosen(row);
        if (chosen)
        {
            dc.DrawRectangle(_accentSoft, null, box);
            dc.DrawRectangle(_accent, null, new Rect(box.Left, box.Top, 3, box.Height));
        }
        var brush = chosen ? _text : _text2;
        var icon = Formatted(RowGlyph(row), _icons, 13, brush);
        dc.DrawText(icon, new Point(box.Left + 10, box.Top + (box.Height - icon.Height) / 2));
        var name = Formatted(Text.Get($"editor.row.{row.ToString().ToLowerInvariant()}"), _plain, 11.5, brush);
        name.MaxTextWidth = box.Width - 52;
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(name, new Point(box.Left + 30, box.Top + (box.Height - name.Height) / 2));
        int count = row == TimelineRow.Video ? _vm.Project.Kept.Count : _vm.Project.Items.Count(i => EditorViewModel.RowOf(i) == row);
        if (count == 0)
            return; // an empty row says nothing (it is there to be filled)
        var number = Formatted(count.ToString(CultureInfo.InvariantCulture), _plain, 10, _text2);
        dc.DrawText(number, new Point(box.Right - 10 - number.Width, box.Top + (box.Height - number.Height) / 2));
    }

    internal static string RowGlyph(TimelineRow row) => row switch
    {
        TimelineRow.Video => "",
        TimelineRow.Text => "",
        TimelineRow.Draw => "",
        _ => "",
    };

    /// <summary>Ticks with times, as far apart as the zoom allows (at least 70 px between labels).</summary>
    void DrawRuler(DrawingContext dc, TimelineLayout layout)
    {
        double duration = _vm.Project.OutputDuration;
        double perSecond = layout.ContentWidth / duration;
        double step = new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800 }.FirstOrDefault(s => s * perSecond >= 70, 3600);
        double first = Math.Floor(layout.OutputAt(TimelineLayout.HeadWidth) / step) * step;
        for (double t = first; t <= duration + 1e-9; t += step)
        {
            double x = layout.XOf(t);
            if (x > ActualWidth)
                break;
            dc.DrawLine(_tick, new Point(x, TimelineLayout.RulerHeight - 5), new Point(x, TimelineLayout.RulerHeight));
            var label = Formatted(Clock(t, step < 1), _plain, 10, _text2);
            dc.DrawText(label, new Point(x + 3, 3));
        }
    }

    static string Clock(double seconds, bool tenths)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : tenths ? @"m\:ss\.f" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The small triangle over a cut, its tip on the Video row's top edge.</summary>
    static readonly Geometry CutKnob = Freeze(Geometry.Parse("M -4,-5 L 4,-5 L 0,0 Z"));

    void DrawPart(DrawingContext dc, Rect box, ClipSpan part, bool chosen, int index)
    {
        if (box.Right < TimelineLayout.HeadWidth || box.Left > ActualWidth)
            return;
        dc.PushClip(new RectangleGeometry(box, Radius, Radius));
        dc.DrawRectangle(_surface, null, box);
        if (_frames.Count > 0)
        {
            // Frames side by side at the strip's height, each the nearest one read to its place.
            double frameWidth = box.Height * 16 / 9;
            double from = Math.Max(box.Left, TimelineLayout.HeadWidth - frameWidth);
            from = box.Left + Math.Floor((from - box.Left) / frameWidth) * frameWidth;
            for (double x = from; x < Math.Min(box.Right, ActualWidth); x += frameWidth)
            {
                double share = (x + frameWidth / 2 - box.Left) / Math.Max(1, box.Width);
                double source = part.Start + Math.Clamp(share, 0, 1) * part.Length;
                var nearest = _frames.MinBy(f => Math.Abs(f.Key - source)).Value;
                dc.DrawImage(nearest, new Rect(x, box.Top, frameWidth, box.Height));
            }
        }
        // Its number when the clip is in parts, then what is special about it: its speed on the left, its sound on the right.
        double y = box.Top + Math.Max(0, (box.Height - BadgeHeight) / 2), left = box.Left + 4;
        if (_vm.Project.Kept.Count > 1)
            left += Badge(dc, (index + 1).ToString(CultureInfo.InvariantCulture), null, new Point(left, y), alignRight: false) + 4;
        if (Math.Abs(part.Speed - 1) > 1e-9)
            left += Badge(dc, part.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×", null, new Point(left, y), alignRight: false) + 4;
        if (part.Look is { } own) // a colour look of its own
            left += Badge(dc, Text.Get($"editor.look.{own.ToString().ToLowerInvariant()}"), "\uE790", new Point(left, y), alignRight: false) + 4;
        double right = box.Right - 4;
        if (_vm.HasSound)
        {
            // Each of its own tracks set apart from the clip's (a one-track clip: its whole sound), the first leftmost.
            var tracks = _vm.PartTracks;
            for (int j = tracks.Count - 1; j >= 0; j--)
                if (Math.Abs(part.Levels[j] - 1) > 1e-9)
                    right -= Badge(dc, EditorViewModel.Percent(part.Levels[j]), tracks[j].Glyph, new Point(right, y), alignRight: true) + 4;
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, chosen ? _chosen : new Pen(_stroke, 1), box, Radius, Radius);
    }

    static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)));

    /// <summary>Badge words shaped once (numbers, speeds, looks, levels: a handful), drawn again on every redraw.</summary>
    readonly Dictionary<string, FormattedText> _badgeWords = [];

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _badgeWords.Clear();
        base.OnDpiChanged(oldDpi, newDpi);
    }
    const double BadgeHeight = 14;

    /// <summary>A small dark label on a part; returns its width.</summary>
    double Badge(DrawingContext dc, string text, string? glyph, Point at, bool alignRight)
    {
        if (!_badgeWords.TryGetValue(text, out var words))
            _badgeWords[text] = words = Formatted(text, _font, 9.5, Brushes.White);
        var icon = glyph == null ? null : Formatted(glyph, _icons, 9.5, Brushes.White);
        double w = words.Width + 10 + (icon == null ? 0 : icon.Width + 3), h = BadgeHeight;
        double x = alignRight ? at.X - w : at.X;
        dc.DrawRoundedRectangle(BadgeFill, null, new Rect(x, at.Y, w, h), 4, 4);
        double tx = x + 5;
        if (icon != null)
        {
            dc.DrawText(icon, new Point(tx, at.Y + (h - icon.Height) / 2));
            tx += icon.Width + 3;
        }
        dc.DrawText(words, new Point(tx, at.Y + (h - words.Height) / 2));
        return w;
    }

    void DrawBar(DrawingContext dc, EditItem item, Rect box, bool chosen)
    {
        if (box.Right < TimelineLayout.HeadWidth || box.Left > ActualWidth)
            return;
        var fill = BarColor(item);
        double luma = fill.R * 0.299 + fill.G * 0.587 + fill.B * 0.114;
        // Chosen: a white ring with a dark edge inside, seen on any colour (a white text's bar too).
        if (chosen)
            dc.DrawRoundedRectangle(null, ChosenRing, Grown(box, 2), BarRadius + 2, BarRadius + 2);
        dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(fill)), luma < 70 ? DarkEdge : chosen ? ChosenInner : null, box, BarRadius, BarRadius);
        dc.PushClip(new RectangleGeometry(box, BarRadius, BarRadius));
        if (IsStriped(item))
            for (double x = box.Left - box.Height; x < box.Right; x += 6)
                dc.DrawLine(Stripe, new Point(x, box.Bottom), new Point(x + box.Height, box.Top)); // blur: hatched, "hidden here"
        var ink = luma > 140 ? DarkText : Brushes.White;
        double left = box.Left + 4;
        if (box.Width >= 16 && box.Height >= 9)
        {
            var icon = Formatted(BarGlyph(item), _icons, Math.Min(11, box.Height - 2), ink);
            dc.DrawText(icon, new Point(left, box.Top + (box.Height - icon.Height) / 2));
            left += icon.Width + 4;
        }
        string label = item switch
        {
            TextItem t => t.Text,
            StrokeItem => Text.Get("editor.page.line"),
            ShapeItem s => Text.Get(s.Kind switch { ShapeKind.Arrow => "editor.arrow", ShapeKind.Ellipse => "editor.ellipse", _ => "editor.rectangle" }),
            _ => Text.Get("editor.page.blur"),
        };
        if (box.Right - left >= 14 && box.Height >= 13 && label.Length > 0)
        {
            var words = Formatted(label, _font, 10, ink);
            words.MaxTextWidth = Math.Max(1, box.Right - left - 4);
            words.MaxLineCount = 1;
            words.Trimming = TextTrimming.CharacterEllipsis;
            words.FlowDirection = HasArabic(label) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            dc.DrawText(words, new Point(left, box.Top + (box.Height - words.Height) / 2)); // Arabic sits at the right end of that width
        }
        dc.Pop();
    }

    static Rect Grown(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    static readonly Brush DarkText = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x1C, 0x1B)));
    static readonly Brush BoxFill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xF4, 0x8B, 0xA4)));
    static readonly Pen BoxEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x8B, 0xA4))), 1.25) { DashStyle = DashStyles.Dash });
    static readonly Pen ChosenRing = Freeze(new Pen(Brushes.White, 2));
    static readonly Pen ChosenInner = Freeze(new Pen(DarkText, 1));
    static readonly Pen DarkEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x78, 0x75))), 1));
    static readonly Pen Stripe = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x2E, 0x27, 0x26))), 2));

    static bool HasArabic(string s) => s.Any(c => c is >= '\u0600' and <= '\u06FF');

    /// <summary>
    /// A bar is the colour of its item (a text made green shows green, each line its own ink), so things are told apart
    /// from afar; a blur box has none of its own and is grey.
    /// </summary>
    internal static Color BarColor(EditItem item)
    {
        string? hex = item switch
        {
            TextItem t => t.ColorHex,
            StrokeItem s => s.ColorHex,
            ShapeItem s => s.ColorHex,
            _ => null,
        };
        return hex != null ? (Color)ColorConverter.ConvertFromString(hex) : Color.FromRgb(0xA8, 0x9C, 0x99);
    }

    /// <summary>The icon on a bar: what kind of thing it is (Segoe Fluent Icons).</summary>
    internal static string BarGlyph(EditItem item) => item switch
    {
        TextItem { Group: not null } => "\uE7F0", // a caption
        TextItem => "\uE8D2",
        StrokeItem => "\uEDFB",
        ShapeItem { Kind: ShapeKind.Arrow } => "\uE72A",
        ShapeItem { Kind: ShapeKind.Ellipse } => "\uEA3A",
        ShapeItem => "\uE739",
        _ => "\uED1A",
    };

    /// <summary>Blur boxes are hatched, so they never look like a shape of the same grey.</summary>
    internal static bool IsStriped(EditItem item) => item is BlurItem;

    FormattedText Formatted(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    // ---------------------------------------------------------------- the pointer

    /// <summary>
    /// What a click does here, as the pointer shows it: the arrow on a row's name or a part (choose it), a two-way arrow on
    /// an item's end (drag its time), four arrows on an item (drag it along), a line elsewhere (move the playhead).
    /// </summary>
    internal static Cursor CursorAt(TimelineLayout layout, Point at)
    {
        int row = layout.RowAt(at.Y);
        if (at.X < TimelineLayout.HeadWidth)
            return Cursors.Arrow; // a row's name (no hand anywhere in Kairos)
        if (row == 0)
            return layout.PartAt(at.X) >= 0 ? Cursors.Arrow : Cursors.IBeam;
        return layout.BarAt(at) switch
        {
            { Edge: true } => Cursors.SizeWE,
            { } => Cursors.SizeAll,
            null => Cursors.IBeam,
        };
    }

    // ---------------------------------------------------------------- gestures

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var at = e.GetPosition(this);
        var layout = Layout();
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        int row = layout.RowAt(at.Y);
        _pressAt = at;
        _moved = false;
        _pressProject = _vm.Project;
        e.Handled = true;

        if (at.X < TimelineLayout.HeadWidth)
        {
            if (row >= 0 && row < layout.Rows.Count)
                _vm.SelectRow(layout.Rows[row], add: ctrl || shift); // Ctrl: the row joins what is chosen
            return;
        }
        if (row > 0 && row < layout.Rows.Count && layout.BarAt(at) is { } hit)
        {
            if (ctrl)
            {
                _vm.Select(hit.Bar.Item, add: true);
                return;
            }
            if (shift)
            {
                _vm.SelectRange(hit.Bar.Item);
                return;
            }
            // A press on one of the chosen drags (or trims) them all; on another, that one alone.
            if (!_vm.IsChosen(hit.Bar.Item))
                _vm.Select(hit.Bar.Item);
            _originals.Clear();
            _originals.AddRange(_vm.SelectedItems);
            if (hit.Edge)
            {
                _edgeItem = hit.Bar.Item;
                _edgeStart = hit.StartEdge;
                _gesture = Gesture.Edge;
            }
            else
            {
                _pressOutput = layout.OutputAt(at.X);
                _gesture = Gesture.Move;
                Seek(layout.OutputAt(at.X)); // the playhead goes there, so it shows on the picture
            }
            CaptureMouse();
            return;
        }
        if (row == 0 && layout.PartAt(at.X) is var part and >= 0)
        {
            if (shift)
            {
                _vm.SelectPartRange(part);
                return;
            }
            _vm.SelectPart(part, ctrl);
            if (ctrl)
                return;
        }
        else if (row > 0 && row < layout.Rows.Count)
        {
            // Empty room among the items: a drag chooses with a box, a click moves the playhead.
            _boxBase.Clear();
            if (ctrl || shift)
                _boxBase.AddRange(_vm.SelectedItems);
            _gesture = Gesture.Box;
            CaptureMouse();
            return;
        }
        else if (!(ctrl || shift))
            _vm.ClearChoice();
        _gesture = Gesture.Seek;
        _vm.BeginSeek(); // playing waits until the mouse is let go, then plays on from there
        _vm.PlayheadOutput = layout.OutputAt(at.X);
        CaptureMouse();
    }

    void Seek(double output)
    {
        _vm.BeginSeek();
        _vm.PlayheadOutput = output;
        _vm.EndSeek();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        var layout = Layout();
        switch (_gesture)
        {
            case Gesture.None:
                Cursor = CursorAt(layout, at);
                return;
            case Gesture.Seek:
                _vm.PlayheadOutput = layout.OutputAt(at.X);
                break;
            case Gesture.Edge when _edgeItem is { } item && _pressProject is { } pressed:
                // Every chosen bar's same end moves by as much as this one's.
                var edged = pressed;
                foreach (var changed in TimelineMath.WithBarEdges(pressed, _originals, item, _edgeStart, layout.OutputAt(at.X)))
                    edged = edged.With(changed);
                _vm.Amend("time:" + string.Join(",", _originals.Select(o => o.Id)), _vm.Project with { Items = edged.Items });
                break;
            case Gesture.Box:
                if (!_moved && (at - _pressAt).Length < 4)
                    return;
                _moved = true;
                _box = new Rect(_pressAt, at);
                _vm.SetChoice(_boxBase.Concat(layout.ItemsIn(_box.Value)));
                InvalidateVisual();
                break;
            case Gesture.Move:
                if (!_moved && Math.Abs(at.X - _pressAt.X) < 4)
                    return; // a click, not a drag yet
                _moved = true;
                double delta = layout.OutputAt(at.X) - _pressOutput;
                var start = _vm.Project;
                // Moved from where they were when pressed, all by the same time.
                var moved = start;
                foreach (var original in _originals)
                    moved = moved.With(TimelineMath.MoveBar(start, original, delta));
                _vm.Amend("move:" + string.Join(",", _originals.Select(o => o.Id)), moved);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        EndGesture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndGesture();
    }

    void EndGesture()
    {
        var was = _gesture;
        _gesture = Gesture.None;
        _edgeItem = null;
        _originals.Clear();
        _pressProject = null;
        if (was == Gesture.Seek)
            _vm.EndSeek();
        if (was == Gesture.Box)
        {
            if (!_moved)
            {
                // Just a click on empty room: the playhead goes there, nothing chosen (Ctrl or Shift keep the choice).
                if (_boxBase.Count == 0)
                    _vm.ClearChoice();
                Seek(Layout().OutputAt(_pressAt.X));
            }
            _box = null;
            _boxBase.Clear();
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var layout = Layout();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Zoom round the pointer: the time under it stays under it.
            double x = e.GetPosition(this).X, under = layout.OutputAt(x);
            _vm.TimelineZoom *= e.Delta > 0 ? 1.25 : 0.8;
            var zoomed = Layout();
            double content = _vm.Project.OutputDuration <= 0 ? 0 : under / _vm.Project.OutputDuration * zoomed.ContentWidth;
            _scroll = Math.Clamp(content - (x - TimelineLayout.HeadWidth), 0, Math.Max(0, zoomed.MaxScroll));
            InvalidateVisual();
            e.Handled = true;
        }
        else if (layout.MaxScroll > 0)
        {
            _scroll = Math.Clamp(_scroll - e.Delta * 0.5, 0, layout.MaxScroll);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
