using Newtonsoft.Json.Linq;

namespace ExternalPackage;

// JObject lives in the Newtonsoft.Json NuGet package (out of source). The walker must
// treat it as a leaf: record the package in references.json and emit a <PackageReference>
// in Slice.csproj, but NOT collect any Newtonsoft .cs source.
public class MyClass
{
    public JObject Convert(string s) => JObject.Parse(s);
}
