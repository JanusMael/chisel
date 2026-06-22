namespace Attributes;

[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkerAttribute : Attribute
{
    public MarkerAttribute(Type target)
    {
        Target = target;
    }

    public Type Target { get; }

    public Type[] Extras { get; set; } = Array.Empty<Type>();
}
