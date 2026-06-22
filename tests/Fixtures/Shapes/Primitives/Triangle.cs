using Geometry;

namespace Primitives;

// Deliberately does NOT implement IShape and is referenced by nothing in the IShape graph.
// It must be excluded from a slice seeded on IShape.
public sealed class Triangle
{
    public Point A { get; set; }
    public Point B { get; set; }
    public Point C { get; set; }
}
