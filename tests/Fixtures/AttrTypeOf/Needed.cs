namespace AttrTypeOf;

// Referenced ONLY via typeof(Needed) inside the attribute on Annotated. If attribute arguments are
// read through the symbol API and that NREs, this dependency would be lost; the syntax recovery
// path must still collect it.
public sealed class Needed
{
    public int Value { get; set; }
}
