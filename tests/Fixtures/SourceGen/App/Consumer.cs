namespace SourceGenApp;

// Depends on Generated.GeneratedGreeter, which only exists as source-generator output.
public class Consumer
{
    public string Run() => new Generated.GeneratedGreeter().Greet();
}
