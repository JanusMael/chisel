using System.Collections.Generic;
using System.Linq;
using Contracts;
using Geometry;
using Primitives;

namespace Composite;

public sealed class Group : IShape
{
    private readonly List<IShape> _children = new();

    public Group()
    {
        // Concrete cross-project types referenced ONLY inside this body — exercises the
        // method-body walker pulling Circle (Primitives) and Rectangle (Primitives) in.
        _children.Add(new Circle(new Point(0, 0), 1));
        _children.Add(new Rectangle(new Point(1, 1), new Size(2, 2)));
    }

    public Point Center => new Point(0, 0);

    public double Area() => _children.Sum(c => c.Area());

    public Size BoundingSize()
    {
        var width = _children.Max(c => c.BoundingSize().Width);
        var height = _children.Max(c => c.BoundingSize().Height);
        return new Size(width, height);
    }
}
