namespace BodyPackage;

public sealed class Service
{
    // Newtonsoft.Json is referenced ONLY inside this method body — the signature uses BCL types
    // (object/string) only. A signatures-only slice must still record the package so the copied
    // file's body can compile.
    public string Dump(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);
}
