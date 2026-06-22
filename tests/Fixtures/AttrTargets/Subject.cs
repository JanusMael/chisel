namespace AttrTargets;

[OnClass]
[WithValue(Priority.High)]
public sealed class Subject
{
    [OnField]
    public int Field;

    [OnProperty]
    public int Property { get; set; }

    public Color Kind { get; set; }

    [OnMethod]
    public void Do([OnParameter] int value)
    {
    }
}
