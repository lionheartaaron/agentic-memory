namespace AgenticMemory.CodeIndex;

/// <summary>
/// Structured symbol-shape detail records — the P1 "flat strings -> structured records" layer
/// from PrincipalEngineer1milliondollar-report-and-recommendations2.md.
///
/// These are mutable POCOs (parameterless ctor, get/set) on purpose: LiteDB 5.x cannot round-trip
/// positional records with init-only properties, and the same types are reused both as the
/// provider-facing DTO sub-objects (on SymbolInfo) and the persisted storage sub-objects
/// (on SymbolRecord), so the ingestion mapper can assign them by reference with no translation.
/// </summary>
public sealed class ParameterRecord
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Ordinal { get; set; }
    public bool IsOptional { get; set; }
    public string? DefaultValue { get; set; }
    public string RefKind { get; set; } = "none";          // none / ref / out / in / refreadonly
    public bool IsParams { get; set; }
    public string? NullableAnnotation { get; set; }         // annotated / notannotated / none
    public List<AttributeRecord> Attributes { get; set; } = []; // populated in P2 (validation rules)
}

public sealed class EnumMemberRecord
{
    public string Name { get; set; } = "";
    public long? Value { get; set; }
    public string? ExplicitExpression { get; set; }         // string-enum members / non-numeric initializers
}

public sealed class AttributeRecord
{
    public string Name { get; set; } = "";                  // attribute class short name, e.g. "HttpGet"
    public List<string> ConstructorArgs { get; set; } = [];
    public Dictionary<string, string> NamedArgs { get; set; } = [];
}

/// <summary>A validation rule (DataAnnotations / class-validator) attached to a property or parameter.</summary>
public sealed class ValidationRuleRecord
{
    public string Member { get; set; } = "";                // property or parameter name
    public string Rule { get; set; } = "";                  // e.g. "Required", "Range", "StringLength"
    public Dictionary<string, string> Args { get; set; } = [];
}

/// <summary>A declared generic type parameter with its constraints (P4).</summary>
public sealed class TypeParameterRecord
{
    public string Name { get; set; } = "";
    public List<string> Constraints { get; set; } = [];     // "class","struct","new()", + constraint type names
    public string? Variance { get; set; }                   // "in" | "out" | null
}
