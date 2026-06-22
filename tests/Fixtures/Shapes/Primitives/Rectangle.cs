using Contracts;
using Geometry;

namespace Primitives;

public sealed class Rectangle : IShape
{
    public Rectangle(Point center, Size size)
    {
        Center = center;
        Size = size;
    }

    public Point Center { get; }

    public Size Size { get; }

    public double Area() => Size.Width * Size.Height;

    public Size BoundingSize() => Size;
}
