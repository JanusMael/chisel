namespace AttrTypeOf;

// A params constructor: applying it as [Marker(typeof(Needed), null)] makes the trailing bare
// `null` ambiguous against the params array, which trips Roslyn's AttributeData.ConstructorArguments
// NRE — the same shape as MSTest's [DataRow(null)].
[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkerAttribute : Attribute
{
    public MarkerAttribute(Type type, params object?[]? extra)
    {
        Type = type;
    }

    public Type Type { get; }
}
