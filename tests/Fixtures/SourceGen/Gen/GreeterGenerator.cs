using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Gen;

[Generator]
public sealed class GreeterGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Always emit a type, regardless of user input, so the fixture is deterministic.
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("GeneratedGreeter.g.cs", SourceText.From(
                """
                namespace Generated
                {
                    public class GeneratedGreeter
                    {
                        public string Greet() => "hello from generator";
                    }
                }
                """,
                Encoding.UTF8));
        });
    }
}
