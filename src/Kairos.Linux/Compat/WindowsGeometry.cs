// The shared engine uses WPF's Rect, Point, Size and Int32Rect for fractions of a picture; WPF does not exist on Linux,
// so the same names are defined here with the members the engine uses.
namespace System.Windows;

public readonly record struct Vector(double X, double Y)
{
    public static implicit operator Avalonia.Vector(Vector v) => new(v.X, v.Y);

    public static implicit operator Vector(Avalonia.Vector v) => new(v.X, v.Y);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static Vector operator *(Vector v, double k) => new(v.X * k, v.Y * k);

    public static Vector operator -(Vector v) => new(-v.X, -v.Y);
}

public readonly record struct Point(double X, double Y)
{
    public static implicit operator Avalonia.Point(Point p) => new(p.X, p.Y);

    public static implicit operator Point(Avalonia.Point p) => new(p.X, p.Y);

    public static Vector operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    public static Point operator +(Point p, Vector v) => new(p.X + v.X, p.Y + v.Y);

    public static Point operator -(Point p, Vector v) => new(p.X - v.X, p.Y - v.Y);
}

public readonly record struct Size(double Width, double Height)
{
    public static implicit operator Avalonia.Size(Size s) => new(s.Width, s.Height);

    public static implicit operator Size(Avalonia.Size s) => new(s.Width, s.Height);
}

public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public static implicit operator Avalonia.Rect(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    public static implicit operator Rect(Avalonia.Rect r) => new(r.X, r.Y, r.Width, r.Height);

    public Point TopLeft => new(X, Y);

    public Point TopRight => new(Right, Y);

    public Point BottomLeft => new(X, Bottom);

    public Point BottomRight => new(Right, Bottom);

    public Size Size => new(Width, Height);

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    /// <summary>WPF's Rect.Empty has a negative width; one made so is "no box".</summary>
    public static Rect Empty { get; } = new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => Width < 0;

    public Rect(Point from, Point to) : this(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y))
    {
    }

    public Rect Inflate(double by) => Inflate(by, by);

    public Rect Inflate(double width, double height) => new(X - width, Y - height, Width + 2 * width, Height + 2 * height);

    public bool IntersectsWith(Rect other) => other.X <= Right && other.Right >= X && other.Y <= Bottom && other.Bottom >= Y;

    public bool Contains(Point p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

public readonly record struct Int32Rect(int X, int Y, int Width, int Height);
