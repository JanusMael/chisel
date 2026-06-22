namespace GlobalUsings;

// No local `using System.Text;` — StringBuilder binds ONLY via the authored global using.
public sealed class ReportBuilder
{
    private readonly StringBuilder _sb = new();

    public ReportBuilder Add(string line)
    {
        _sb.AppendLine(line);
        return this;
    }

    public override string ToString() => _sb.ToString();
}
