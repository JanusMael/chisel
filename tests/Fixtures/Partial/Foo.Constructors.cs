namespace Partial;

// One of three files declaring `partial class Foo`. Slicing on Partial.Foo must
// collect every declaring file via DeclaringSyntaxReferences.
public partial class Foo
{
    public Foo()
    {
        Name = "default";
    }
}
