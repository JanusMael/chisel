using Contracts;
using Geometry;

namespace Primitives;

public sealed class Circle : IShape
{
    public Circle(Point center, double radius)
    {
        Center = center;
        Radius = radius;
    }

    public Point Center { get; }

    public double Radius { get; }

    public double Area() => System.Math.PI * Radius * Radius;

    public Size BoundingSize() => new Size(Radius * 2, Radius * 2);
}
