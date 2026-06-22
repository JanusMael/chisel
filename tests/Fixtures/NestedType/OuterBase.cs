namespace NestedType;

// Lives in a SEPARATE file from Outer. Slicing on the nested type Outer.Inner must still pull
// this in, because Inner is compiled in the context of Outer, and Outer : OuterBase.
public class OuterBase
{
    public int BaseValue { get; set; }
}
