namespace Attributes;

// Payload appears only as a typeof() constructor argument; ExtraA/ExtraB only as a typeof()
// array in a named argument. All three must be discovered via attribute-argument walking.
[Marker(typeof(Payload), Extras = new[] { typeof(ExtraA), typeof(ExtraB) })]
public sealed class Widget
{
}
