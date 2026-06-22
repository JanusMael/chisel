namespace GlobalUsingsMixed;

// No local `using System.Text;` and ImplicitUsings is disabled — StringBuilder binds ONLY via the
// `global using System.Text;` authored in Bootstrap.cs (a file this seed does not otherwise reach).
public sealed class Report
{
    private readonly StringBuilder _sb = new();

    public Report Add(string line)
    {
        _sb.AppendLine(line);
        return this;
    }

    public override string ToString() => _sb.ToString();
}
