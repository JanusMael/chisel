namespace AttrTargets;

// Each custom attribute lives here; the slice must pull this file in when any of them is applied
// to a collected type or member.
[AttributeUsage(AttributeTargets.Class)]
public sealed class OnClassAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class OnPropertyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Field)]
public sealed class OnFieldAttribute : Attribute;

[AttributeUsage(AttributeTargets.Enum)]
public sealed class OnEnumAttribute : Attribute;

[AttributeUsage(AttributeTargets.Field)]
public sealed class OnEnumValueAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnMethodAttribute : Attribute;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class OnParameterAttribute : Attribute;

// object-typed parameter: an enum passed here is reachable only via the argument's declared type,
// not via this attribute's own signature.
[AttributeUsage(AttributeTargets.Class)]
public sealed class WithValueAttribute(object value) : Attribute
{
    public object Value { get; } = value;
}
