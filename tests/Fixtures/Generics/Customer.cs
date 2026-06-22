namespace Generics;

// A concrete IEntity implementation. It is NOT referenced from Repository<T> directly;
// it is pulled in because DerivedTypeFinder expands IEntity to its implementations
// (the default ExpandDerived=true behavior).
public class Customer : IEntity
{
    public int Id { get; set; }

    public string Name { get; set; }
}
