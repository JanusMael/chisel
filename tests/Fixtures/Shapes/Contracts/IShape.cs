using Geometry;

namespace Contracts;

public interface IShape
{
    // Member signatures reference model types from the Geometry project — they must be
    // collected even though they live in a different project than the interface.
    Point Center { get; }

    double Area();

    Size BoundingSize();
}
