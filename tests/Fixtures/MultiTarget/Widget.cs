namespace MultiTarget;

public class Widget
{
    public string Name { get; set; } = "";

    public WidgetKind Kind { get; set; }
}

public enum WidgetKind
{
    Small,
    Large,
}
