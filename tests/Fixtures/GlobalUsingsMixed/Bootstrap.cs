// A MIXED file: it carries an authored `global using` AND declares a type. The type-graph walk
// from the seed never reaches Bootstrap, so the directive would be lost unless it is harvested
// from this file specifically (the CORRECT-7 hole).
global using System.Text;

namespace GlobalUsingsMixed;

public static class Bootstrap
{
    public static string Name => "bootstrap";
}
