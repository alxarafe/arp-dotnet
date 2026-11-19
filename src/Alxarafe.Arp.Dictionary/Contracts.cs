using System.Collections.Immutable;
using System.Text.Json;

namespace Alxarafe.Arp.Dictionary;

public enum ScalarType { Boolean, String, Text, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, Decimal, Uuid, Date, Time, DateTime, Enum, Binary }
public enum SourceFormat { Json, Yaml }
public enum DictionaryErrorCode { DocumentInvalid, FormatUnsupported, ElementInvalid, DuplicateElement, ElementNotFound, ValueType, ValueConstraint, LimitExceeded }

public sealed record Provenance(string ProviderId, string SourceId, string Path);

public sealed record DictionaryIssue(DictionaryErrorCode Code, string? ElementId = null, string? Constraint = null, Provenance? Provenance = null)
{
    public string ContractCode => Code switch
    {
        DictionaryErrorCode.DocumentInvalid => "DICTIONARY_DOCUMENT_INVALID",
        DictionaryErrorCode.FormatUnsupported => "DICTIONARY_FORMAT_UNSUPPORTED",
        DictionaryErrorCode.ElementInvalid => "DICTIONARY_ELEMENT_INVALID",
        DictionaryErrorCode.DuplicateElement => "DICTIONARY_DUPLICATE_ELEMENT",
        DictionaryErrorCode.ElementNotFound => "DICTIONARY_ELEMENT_NOT_FOUND",
        DictionaryErrorCode.ValueType => "DICTIONARY_VALUE_TYPE",
        DictionaryErrorCode.ValueConstraint => "DICTIONARY_VALUE_CONSTRAINT",
        DictionaryErrorCode.LimitExceeded => "DICTIONARY_LIMIT_EXCEEDED",
        _ => throw new InvalidOperationException("Unknown error code")
    };

    public JsonElement ToProjection()
    {
        var projection = new Dictionary<string, object?> { ["code"] = ContractCode };
        if (ElementId is not null)
            projection["elementId"] = ElementId;
        if (Constraint is not null)
            projection["constraint"] = Constraint;
        return JsonSerializer.SerializeToElement(projection);
    }
}

public sealed record CatalogLimits
{
    public static CatalogLimits Default { get; } = new();
    public int MaxDocumentBytes { get; }
    public int MaxElementsPerSource { get; }
    public int MaxDepth { get; }
    public CatalogLimits(int maxDocumentBytes = 1_048_576, int maxElementsPerSource = 2048, int maxDepth = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxElementsPerSource, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        MaxDocumentBytes = maxDocumentBytes;
        MaxElementsPerSource = maxElementsPerSource;
        MaxDepth = maxDepth;
    }
}

public sealed record BuildResult
{
    public Catalog? Catalog { get; }
    public ImmutableArray<DictionaryIssue> Issues { get; }
    public bool IsValid => Catalog is not null;
    internal BuildResult(Catalog? catalog, IEnumerable<DictionaryIssue> issues) { Catalog = catalog; Issues = [.. issues]; }
    public JsonElement ToProjection() => JsonProjection.Outcome(IsValid, Issues);
}

public sealed record ResolutionResult
{
    public ElementDescriptor? Descriptor { get; }
    public ImmutableArray<DictionaryIssue> Issues { get; }
    public bool IsValid => Descriptor is not null;
    internal ResolutionResult(ElementDescriptor? descriptor, IEnumerable<DictionaryIssue> issues) { Descriptor = descriptor; Issues = [.. issues]; }
    public JsonElement ToProjection() => JsonProjection.Outcome(IsValid, Issues);
}

public sealed record ValidationResult
{
    public object? NormalizedValue { get; }
    public ImmutableArray<DictionaryIssue> Issues { get; }
    public bool IsValid => Issues.IsEmpty;
    internal ValidationResult(object? value, IEnumerable<DictionaryIssue> issues) { NormalizedValue = value; Issues = [.. issues]; }
    public JsonElement ToProjection() => IsValid
        ? JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["valid"] = true, ["normalizedValue"] = NormalizedValue, ["issues"] = Array.Empty<object>() })
        : JsonProjection.Outcome(false, Issues);
}

internal sealed class Rejection(DictionaryErrorCode code) : Exception(code.ToString())
{
    internal DictionaryErrorCode Code { get; } = code;
    internal static Rejection Document() => new(DictionaryErrorCode.DocumentInvalid);
    internal static Rejection Element() => new(DictionaryErrorCode.ElementInvalid);
    internal static Rejection Type() => new(DictionaryErrorCode.ValueType);
    internal static Rejection Limit() => new(DictionaryErrorCode.LimitExceeded);
}
