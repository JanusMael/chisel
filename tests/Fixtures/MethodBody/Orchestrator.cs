namespace MethodBody;

public class Orchestrator
{
    // ConcreteHelper is NOT referenced in any public/private signature on this type.
    // It appears only inside the Run() body. The walker must discover it via
    // SemanticModel walking of the body.
    public void Run()
    {
        var helper = new ConcreteHelper();
        helper.DoWork();
    }
}
