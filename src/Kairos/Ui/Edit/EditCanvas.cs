using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kairos.Engine.Edit;
using TextEffect = Kairos.Engine.Edit.TextEffect;

namespace Kairos.Ui.Edit;

/// <summary>
/// The surface over the preview where items are made and changed. With no mode a click chooses an item (Shift or Ctrl
/// adds it to the chosen ones), a drag moves the chosen ones, a corner resizes the one chosen, a double click types a
/// text. Draw mode draws lines (each its own item), Shapes and Blur drag one box, Crop (pictures) the crop. Every
/// finished gesture is one step to undo. Never mirrored: the picture reads the same in Arabic.
/// </summary>
public sealed class EditCanvas : Canvas
{
    const double HandlePx = 9, HitMarginPx = 8, PointStep = 0.004, DragPx = 3;
    /// <summary>A blur box's edge: thin and dashed, so the real blur inside shows as it will be saved.</summary>
    static readonly Pen BlurEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 1.25) { DashStyle = DashStyles.Dash });
    static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));

    readonly EditorViewModel _vm;
    readonly Pen _selection, _handleEdge;
    readonly Brush _handleFill;
    Gesture _gesture;
    Point _start, _startPx;
    bool _moved;
    readonly List<EditItem> _originals = [];
    readonly List<EditItem> _transients = [];
    EditItem? _transient;
    Rect? _transientCrop;
    Corner _corner;
    readonly List<Point> _points = [];
    TextBox? _editor;
    Guid _editing;

    enum Gesture { None, Move, Resize, Draw, Box, CropResize }

    public EditCanvas(EditorViewModel vm)
    {
        _vm = vm;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        FlowDirection = FlowDirection.LeftToRight;
        Cursor = CursorFor(vm.EditMode);
        var accent = (Application.Current?.TryFindResource("Accent") as Brush) ?? Brushes.HotPink;
        _selection = new Pen(accent, 1.5) { DashStyle = DashStyles.Dash };
        _selection.Freeze();
        _handleEdge = new Pen(accent, 1.5);
        _handleEdge.Freeze();
        _handleFill = Brushes.White;
        vm.PropertyChanged += (_, e) =>
        {
            // The playhead moving (60 times a second while playing) changes the overlay only when an item comes or goes.
            if (e.PropertyName == nameof(EditorViewModel.PlayheadOutput) && !ShownChanged())
                return;
            if (e.PropertyName is nameof(EditorViewModel.Project) or nameof(EditorViewModel.PlayheadOutput) or nameof(EditorViewModel.SelectedItems)
                or nameof(EditorViewModel.EditMode) or nameof(EditorViewModel.SelectedParts))
            {
                if (e.PropertyName == nameof(EditorViewModel.EditMode))
                {
                    CloseEditor();
                    Cursor = CursorFor(_vm.EditMode);
                }
                InvalidateVisual();
            }
        };
        vm.TypeRequested += text => Dispatcher.BeginInvoke(() =>
        {
            if (_vm.Project.Items.FirstOrDefault(i => i.Id == text.Id) is TextItem now)
                OpenEditor(now);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        SizeChanged += (_, _) =>
        {
            PlaceEditor();
            InvalidateVisual();
        };
    }

    Rect Picture => EditGeometry.PictureRect(RenderSize, _vm.Aspect);

    double SourceNow => _vm.Project.ToSource(_vm.PlayheadOutput) ?? 0;

    IReadOnlyList<EditItem> Visible() =>
        _vm.Project.Items.Where(i => EditGeometry.ShowsAt(_vm.Project, i, SourceNow)).ToList();

    // ---------------------------------------------------------------- drawing

    /// <summary>The items on the picture when it was last drawn (in order): the playhead moving changes nothing else on it.</summary>
    readonly List<Guid> _shown = [];

    /// <summary>Whether the items showing at the playhead now differ from those last drawn.</summary>
    bool ShownChanged()
    {
        int i = 0;
        foreach (var item in Visible())
        {
            if (i >= _shown.Count || _shown[i] != item.Id)
                return true;
            i++;
        }
        return i != _shown.Count;
    }

    /// <summary>How many times the overlay was drawn (tests: it is not drawn again while nothing on it changes).</summary>
    internal int Renders { get; private set; }

    protected override void OnRender(DrawingContext dc)
    {
        Renders++;
        base.OnRender(dc);
        var picture = Picture;
        var p = _vm.Project;
        _shown.Clear();
        _shown.AddRange(Visible().Select(i => i.Id));
        foreach (var item in Visible())
        {
            var shown = Shown(item);
            if (shown.Id != _editing)
                DrawItem(dc, shown, picture);
        }
        if (_transient is { } fresh && p.Items.All(i => i.Id != fresh.Id))
            DrawItem(dc, fresh, picture);

        if ((_transientCrop ?? (_vm.IsZoomMode ? _vm.ZoomShown : p.Crop)) is { } crop)
            DrawCrop(dc, crop, picture);

        // Every chosen item on screen has a dashed frame; the one chosen alone has corners to resize it.
        var single = _vm.SelectedItem;
        foreach (var chosen in _vm.SelectedItems)
        {
            var shown = Shown(chosen);
            if (shown.Id == _editing || !EditGeometry.ShowsAt(p, shown, SourceNow))
                continue;
            var box = EditGeometry.ToPixels(EditGeometry.Bounds(shown), picture);
            dc.DrawRectangle(null, _selection, box);
            if (single != null && single.Id == shown.Id)
                DrawCorners(dc, box);
        }
    }

    /// <summary>An item as it is drawn now: moved or resized while the mouse is down.</summary>
    EditItem Shown(EditItem item) =>
        _transients.FirstOrDefault(t => t.Id == item.Id) ?? (_transient is { } t2 && t2.Id == item.Id ? t2 : item);

    void DrawItem(DrawingContext dc, EditItem item, Rect picture)
    {
        if (item is BlurItem blur)
        {
            // The real blur is drawn into the preview picture; here only its edge shows where it is.
            dc.DrawRectangle(null, BlurEdge, EditGeometry.ToPixels(blur.Box, picture));
            return;
        }
        dc.PushTransform(new TranslateTransform(picture.X, picture.Y));
        ItemRenderer.Draw(dc, item, picture.Size);
        dc.Pop();
    }

    void DrawCrop(DrawingContext dc, Rect crop, Rect picture)
    {
        var box = EditGeometry.ToPixels(crop, picture);
        var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
        outside.Children.Add(new RectangleGeometry(picture));
        outside.Children.Add(new RectangleGeometry(box));
        dc.DrawGeometry(Dim, null, outside);
        if (_vm.IsCropMode || _vm.IsZoomMode)
            dc.DrawRectangle(null, _selection, box);
        if (_vm.IsCropMode)
            DrawCorners(dc, box);
    }

    void DrawCorners(DrawingContext dc, Rect box)
    {
        foreach (var corner in Corners(box))
            dc.DrawRectangle(_handleFill, _handleEdge, new Rect(corner.X - HandlePx / 2, corner.Y - HandlePx / 2, HandlePx, HandlePx));
    }

    static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight];

    static Corner? CornerAt(Rect box, Point at)
    {
        var corners = Corners(box);
        for (int i = 0; i < corners.Length; i++)
            if (Math.Abs(corners[i].X - at.X) <= HandlePx && Math.Abs(corners[i].Y - at.Y) <= HandlePx)
                return (Corner)i;
        return null;
    }

    // ---------------------------------------------------------------- gestures

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editor != null && !_editor.IsMouseOver)
            CloseEditor();
        Focus();
        var picture = Picture;
        var at = e.GetPosition(this);
        _startPx = at;
        _moved = false;
        _start = EditGeometry.Normalize(at, picture);
        double margin = HitMarginPx / Math.Max(1, picture.Width);
        bool add = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;

        switch (_vm.EditMode)
        {
            case EditMode.Draw:
                _points.Clear();
                _points.Add(_start);
                _gesture = Gesture.Draw;
                break;
            case EditMode.Shapes or EditMode.Blur or EditMode.Zoom:
                _gesture = Gesture.Box;
                break;
            case EditMode.Crop:
                if (_vm.Project.Crop is { } crop && CornerAt(EditGeometry.ToPixels(crop, picture), at) is { } cropCorner)
                {
                    _corner = cropCorner;
                    _gesture = Gesture.CropResize;
                }
                else
                    _gesture = Gesture.Box;
                break;
            default:
                if (!add && _vm.SelectedItem is { } selected && EditGeometry.ShowsAt(_vm.Project, selected, SourceNow)
                    && CornerAt(EditGeometry.ToPixels(EditGeometry.Bounds(selected), picture), at) is { } corner)
                {
                    _gesture = Gesture.Resize;
                    _originals.Clear();
                    _originals.Add(selected);
                    _corner = corner;
                }
                else if (EditGeometry.HitTest(Visible(), _start, margin) is { } hit)
                {
                    if (add)
                    {
                        _vm.Select(hit, add: true);
                        e.Handled = true;
                        return;
                    }
                    if (e.ClickCount == 2 && hit is TextItem text)
                    {
                        _vm.Select(hit);
                        OpenEditor(text);
                        e.Handled = true;
                        return;
                    }
                    // A press on one of the chosen moves them all; on another item, that one alone.
                    if (!_vm.IsChosen(hit))
                        _vm.Select(hit);
                    _originals.Clear();
                    _originals.AddRange(_vm.SelectedItems.Where(i => EditGeometry.ShowsAt(_vm.Project, i, SourceNow)));
                    _gesture = Gesture.Move;
                }
                else
                {
                    if (!add)
                        _vm.ClearChoice();
                    e.Handled = true;
                    return;
                }
                break;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_gesture == Gesture.None)
        {
            Cursor = HoverAt(_vm, Picture, e.GetPosition(this));
            return;
        }
        var px = e.GetPosition(this);
        if (!_moved && (px - _startPx).Length < DragPx)
            return; // a click, not a drag yet
        _moved = true;
        var now = EditGeometry.Normalize(px, Picture);
        switch (_gesture)
        {
            case Gesture.Move:
                _transients.Clear();
                _transients.AddRange(_originals.Select(o => EditGeometry.Move(o, now - _start)));
                break;
            case Gesture.Resize:
                _transient = EditGeometry.WithBounds(_originals[0], EditGeometry.Resize(EditGeometry.Bounds(_originals[0]), _corner, now));
                break;
            case Gesture.Draw:
                if ((now - _points[^1]).Length >= PointStep)
                    _points.Add(now);
                _transient = Stroke(Guid.Empty);
                break;
            case Gesture.Box:
                if (_vm.IsCropMode)
                    _transientCrop = new Rect(_start, now);
                else if (_vm.IsZoomMode)
                    _transientCrop = VideoZoom.Fit(new Rect(_start, now)); // the video's shape as it is drawn
                else
                    _transient = BoxItem(Guid.Empty, _start, now);
                break;
            case Gesture.CropResize:
                _transientCrop = EditGeometry.Resize(_vm.Project.Crop!.Value, _corner, now);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_gesture == Gesture.None)
            return;
        var end = EditGeometry.Normalize(e.GetPosition(this), Picture);
        var p = _vm.Project;
        switch (_gesture)
        {
            case Gesture.Move when _transients.Count > 0:
                var moved = p;
                foreach (var item in _transients)
                    moved = moved.With(item);
                _vm.Apply(moved);
                break;
            case Gesture.Resize when _transient != null:
                _vm.Apply(p.With(_transient));
                break;
            case Gesture.Draw when _points.Count >= 2:
                _vm.AddStroke(_points.ToList()); // its own line: the brush stays for the next one
                break;
            case Gesture.Box when _vm.IsZoomMode:
                var drawn = new Rect(_start, end);
                if (drawn.Width >= EditGeometry.MinSize || drawn.Height >= EditGeometry.MinSize)
                    _vm.ZoomTo(drawn);
                break;
            case Gesture.Box:
                var box = new Rect(_start, end);
                if (box.Width >= EditGeometry.MinSize && box.Height >= EditGeometry.MinSize)
                {
                    if (_vm.IsCropMode)
                        _vm.Apply(p with { Crop = box });
                    else if (BoxItem(Guid.NewGuid(), _start, end) is { } item)
                        _vm.AddMade(item);
                }
                break;
            case Gesture.CropResize when _transientCrop is { } crop:
                _vm.Apply(p with { Crop = crop });
                break;
        }
        EndGesture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture != Gesture.None)
            EndGesture();
    }

    void EndGesture()
    {
        _gesture = Gesture.None;
        _transient = null;
        _transientCrop = null;
        _transients.Clear();
        _originals.Clear();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }

    StrokeItem Stroke(Guid id)
    {
        var (start, end) = EditGeometry.NewItemTime(_vm.Project, _vm.PlayheadOutput);
        return new StrokeItem(id, start, end, [_points.ToList()], _vm.PenColor, _vm.PenThickness);
    }

    EditItem? BoxItem(Guid id, Point a, Point b)
    {
        var (start, end) = EditGeometry.NewItemTime(_vm.Project, _vm.PlayheadOutput);
        if (_vm.IsBlurMode)
            return new BlurItem(id, start, end, new Rect(a, b));
        return new ShapeItem(id, start, end, _vm.ShapeKind, a, b, _vm.ShapeColor, _vm.ShapeThickness);
    }

    // ---------------------------------------------------------------- text in place

    /// <summary>The text's box fitted to its words at the picture's size, centred where it was, kept on the picture.</summary>
    TextItem Fit(TextItem text)
    {
        var size = ItemRenderer.BoxSize(text, new Size(Math.Max(1, Picture.Width), Math.Max(1, Picture.Height)));
        double w = size.Width, h = size.Height;
        double x = Math.Clamp(text.Box.X + text.Box.Width / 2 - w / 2, 0, Math.Max(0, 1 - w));
        double y = Math.Clamp(text.Box.Y, 0, Math.Max(0, 1 - h));
        return text with { Box = new Rect(x, y, w, h) };
    }

    void OpenEditor(TextItem text)
    {
        CloseEditor();
        _editing = text.Id;
        var picture = Picture;
        _editor = TextEditor(text, Math.Max(10, text.SizeRel * picture.Height));
        _editor.TextChanged += (_, _) =>
        {
            if (_vm.Project.Items.FirstOrDefault(i => i.Id == _editing) is TextItem current)
            {
                var next = Fit(current with { Text = _editor.Text });
                _vm.Amend("text:" + next.Id, _vm.Project.With(next));
                PlaceEditor();
            }
        };
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                CloseEditor();
                e.Handled = true;
            }
        };
        _editor.LostKeyboardFocus += (_, e) =>
        {
            // On to another field (the inspector's words for this text): the text stays and that field keeps the keyboard.
            bool movedOn = e.NewFocus is System.Windows.Controls.Primitives.TextBoxBase other && !ReferenceEquals(other, _editor);
            Dispatcher.BeginInvoke(() => CloseEditor(movedOn));
        };
        Children.Add(_editor);
        PlaceEditor();
        Dispatcher.BeginInvoke(() =>
        {
            _editor?.Focus();
            _editor?.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        InvalidateVisual();
    }

    /// <summary>A plain look for typing in place: no field frame, fill or clear button (the app's text fields have them).</summary>
    static readonly Style PlainEditor = MakePlainEditor();

    static Style MakePlainEditor()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.AppendChild(host);
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(TextBox)) { VisualTree = border }));
        style.Seal();
        return style;
    }

    /// <summary>The box a text is typed in: the text's own font, colour and style, so typing looks like the result.</summary>
    internal static TextBox TextEditor(TextItem text, double fontPx)
    {
        var font = TextFonts.For(text.Font);
        var editor = new TextBox
        {
            Style = PlainEditor,
            OverridesDefaultStyle = true,
            Text = text.Text,
            FontSize = fontPx,
            FontFamily = font.Family,
            FontWeight = font.Weight,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.ColorHex)),
            Background = text.Effect is TextEffect.Box or TextEffect.Pill ? new SolidColorBrush(Color.FromArgb(0xB8, 0x14, 0x10, 0x10)) : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(fontPx * 0.25, 0, fontPx * 0.25, 0),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            CaretBrush = Brushes.White,
            MinWidth = 40,
        };
        // Shadow and outline while typing: a dark halo close enough to show the style; glow and neon, a light one in the colour.
        if (text.Effect is TextEffect.Shadow or TextEffect.Outline)
            editor.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                ShadowDepth = text.Effect == TextEffect.Shadow ? Math.Max(1, fontPx * 0.07) : 0,
                BlurRadius = text.Effect == TextEffect.Outline ? Math.Max(2, fontPx * 0.12) : 0,
                Opacity = 0.95,
            };
        else if (text.Effect is TextEffect.Glow or TextEffect.Neon)
            editor.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString(text.ColorHex),
                ShadowDepth = 0,
                BlurRadius = Math.Max(4, fontPx * 0.45),
                Opacity = text.Effect == TextEffect.Neon ? 1 : 0.8,
            };
        return editor;
    }

    void PlaceEditor()
    {
        if (_editor == null || _vm.Project.Items.FirstOrDefault(i => i.Id == _editing) is not TextItem text)
            return;
        var box = EditGeometry.ToPixels(text.Box, Picture);
        double width = Math.Max(40, box.Width);
        SetLeft(_editor, box.X + box.Width / 2 - width / 2);
        SetTop(_editor, box.Y);
        _editor.Width = width;
        _editor.Height = box.Height;
    }

    /// <summary>A text is being typed on the picture now.</summary>
    internal bool Typing => _editor != null;

    /// <summary>
    /// Ends typing: an empty text is removed (with the same step that made it) and the canvas takes the keyboard back;
    /// unless the keyboard <paramref name="movedOn"/> to another field (the inspector's words), which keeps it and the text.
    /// </summary>
    internal void CloseEditor(bool movedOn = false)
    {
        if (_editor == null)
            return;
        var editor = _editor;
        _editor = null;
        Children.Remove(editor);
        var id = _editing;
        _editing = Guid.Empty;
        // Gone from the edit, it is no longer chosen either (whatever was chosen meanwhile stays chosen).
        if (!movedOn && _vm.Project.Items.FirstOrDefault(i => i.Id == id) is TextItem { Text.Length: 0 })
            _vm.Amend("text:" + id, _vm.Project.Without(id));
        InvalidateVisual();
        if (!movedOn)
            Keyboard.Focus(this);
    }

    /// <summary>The pointer over the picture: the usual arrow to choose and move; each tool its own (a pen to draw…).</summary>
    internal static Cursor CursorFor(EditMode mode) => mode switch
    {
        EditMode.Draw => ThemeCursors.Get(ThemeCursors.Shape.Pen),
        EditMode.Shapes => ThemeCursors.Get(ThemeCursors.Shape.ShapeTool),
        EditMode.Blur => ThemeCursors.Get(ThemeCursors.Shape.BlurTool),
        EditMode.Crop or EditMode.Zoom => ThemeCursors.Get(ThemeCursors.Shape.CropTool),
        _ => Cursors.Arrow,
    };

    /// <summary>
    /// What a press here would do, as the pointer shows it: slanted arrows on a corner of the chosen item (or the crop),
    /// four arrows on an item (drag it), the tool's own pointer in a mode (a pen to draw…), the arrow elsewhere.
    /// </summary>
    internal static Cursor HoverAt(EditorViewModel vm, Rect picture, Point at)
    {
        var p = vm.Project;
        double source = p.ToSource(vm.PlayheadOutput) ?? 0;
        switch (vm.EditMode)
        {
            case EditMode.Crop when p.Crop is { } crop && CornerAt(EditGeometry.ToPixels(crop, picture), at) is { } cropCorner:
                return CornerCursor(cropCorner);
            case EditMode.None:
                if (vm.SelectedItem is { } chosen && EditGeometry.ShowsAt(p, chosen, source)
                    && CornerAt(EditGeometry.ToPixels(EditGeometry.Bounds(chosen), picture), at) is { } corner)
                    return CornerCursor(corner);
                var visible = p.Items.Where(i => EditGeometry.ShowsAt(p, i, source)).ToList();
                return EditGeometry.HitTest(visible, EditGeometry.Normalize(at, picture), HitMarginPx / Math.Max(1, picture.Width)) != null
                    ? Cursors.SizeAll : Cursors.Arrow;
            default:
                return CursorFor(vm.EditMode);
        }
    }

    static Cursor CornerCursor(Corner corner) => corner is Corner.TopLeft or Corner.BottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW;

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
