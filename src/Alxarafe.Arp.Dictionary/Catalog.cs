using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Alxarafe.Arp.Dictionary;

/// <summary>A completely built immutable catalog, safe for concurrent readers.</summary>
public sealed class Catalog
{
    private readonly FrozenDictionary<string, ElementDescriptor> elements;
    private readonly FrozenDictionary<string, Provenance> provenance;
    public ImmutableArray<string> ElementIds { get; }
    internal Catalog(IDictionary<string, ElementDescriptor> elements, IDictionary<string, Provenance> provenance)
    {
        this.elements = elements.ToFrozenDictionary(StringComparer.Ordinal);
        this.provenance = provenance.ToFrozenDictionary(StringComparer.Ordinal);
        ElementIds = [.. elements.Keys.Order(StringComparer.Ordinal)];
    }
    public ResolutionResult Resolve(string elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        return elements.TryGetValue(elementId, out var descriptor) ? new(descriptor, []) : new(null, Missing(elementId));
    }
    public ValidationResult Validate(string elementId, object? portableValue)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        return elements.TryGetValue(elementId, out var descriptor) ? ScalarValidator.Validate(descriptor, portableValue) : new(null, Missing(elementId));
    }
    public Provenance? GetProvenance(string elementId) => provenance.GetValueOrDefault(elementId);
    private static DictionaryIssue[] Missing(string id) => [new(DictionaryErrorCode.ElementNotFound, id)];
}
