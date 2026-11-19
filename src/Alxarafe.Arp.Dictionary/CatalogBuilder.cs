using System.Text.Json;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Stateless atomic construction from explicitly selected source snapshots.</summary>
public sealed class CatalogBuilder
{
    public CatalogLimits Limits { get; }
    public CatalogBuilder(CatalogLimits? limits = null) => Limits = limits ?? CatalogLimits.Default;
    public BuildResult BuildCatalog(IEnumerable<SourceSnapshot> activeSources)
    {
        ArgumentNullException.ThrowIfNull(activeSources);
        var elements = new Dictionary<string, ElementDescriptor>(StringComparer.Ordinal);
        var provenance = new Dictionary<string, Provenance>(StringComparer.Ordinal);
        var parser = new SourceParser(Limits);
        foreach (var source in activeSources)
        {
            ArgumentNullException.ThrowIfNull(source);
            string? id = null;
            string path = "";
            try
            {
                var document = parser.Parse(source);
                if (document.ValueKind != JsonValueKind.Object)
                    throw Rejection.Document();
                if (!document.TryGetProperty("apiVersion", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "arp.dictionary/v1")
                    throw new Rejection(DictionaryErrorCode.FormatUnsupported);
                if (document.EnumerateObject().Any(p => p.Name is not ("apiVersion" or "elements")) || !document.TryGetProperty("elements", out var definitions)
                    || definitions.ValueKind != JsonValueKind.Array || definitions.GetArrayLength() == 0)
                    throw Rejection.Document();
                if (definitions.GetArrayLength() > Limits.MaxElementsPerSource)
                    throw Rejection.Limit();
                int index = 0;
                foreach (var definition in definitions.EnumerateArray())
                {
                    path = "/elements/" + index++;
                    id = null;
                    if (definition.ValueKind != JsonValueKind.Object)
                        throw Rejection.Document();
                    if (definition.TryGetProperty("id", out var name) && name.ValueKind == JsonValueKind.String)
                        id = name.GetString();
                    var descriptor = DefinitionReader.Read(definition);
                    if (!elements.TryAdd(descriptor.Id, descriptor))
                        throw new Rejection(DictionaryErrorCode.DuplicateElement);
                    provenance.Add(descriptor.Id, new(source.ProviderId, source.SourceId, path));
                }
            } catch (Rejection rejection) { return new(null, [new(rejection.Code, id, Provenance: new(source.ProviderId, source.SourceId, path))]); }
        }
        return new(new Catalog(elements, provenance), []);
    }
}
