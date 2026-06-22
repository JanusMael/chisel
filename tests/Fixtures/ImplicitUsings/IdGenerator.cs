namespace ImplicitUsings;

// This file compiles ONLY because <ImplicitUsings>enable</ImplicitUsings> supplies the
// implicit `global using System;`. There is deliberately no `using System;` directive,
// so Guid/DateTime resolve via implicit usings. The generated Slice.csproj must therefore
// also set <ImplicitUsings>enable</ImplicitUsings> to build.
public class IdGenerator
{
    public Guid NewId() => Guid.NewGuid();

    public string Stamp() => DateTime.UtcNow.ToString("O");
}
