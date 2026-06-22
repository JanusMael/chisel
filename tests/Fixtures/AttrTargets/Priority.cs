namespace AttrTargets;

// Reachable ONLY as an attribute argument value on Subject (via an object-typed attribute
// parameter), so it exercises the TypedConstant.Type path specifically.
public enum Priority
{
    Low,
    High,
}
