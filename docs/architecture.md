# Internal architecture

Public namespace: `Alxarafe.Arp.Dictionary`.

- `SourceSnapshot` / `SourceFormat`: explicit, defensively owned byte inputs.
- `CatalogBuilder` / `CatalogLimits`: local parser/working maps for every build;
  complete catalog or immutable rejection issues, never partial publication.
- `Catalog`: frozen ordinal dictionaries, immutable enumeration, resolution and
  validation against one snapshot, separate provenance.
- `ElementDescriptor`: validated immutable metadata using cloned JsonElement
  constraints and ImmutableArray literals; full projection and exact JSON export.
- `BuildResult`, `ResolutionResult`, `ValidationResult`: immutable typed outcomes
  with internally controlled construction and success-only normalized values.
- `DictionaryIssue`, `DictionaryErrorCode`, `Provenance`, `ScalarType`: typed
  vocabulary, exact portable error names, explicit diagnostic separation.

Internal collaborators are not extension APIs:

- `SourceParser`: strict UTF-8, iterative JSON token validation, duplicate mapping
  key checks, controlled YAML events and safe JSON model construction. No object
  deserializer, reflection-based constructor selection, external references or I/O.
- `UnicodeText`: surrogate pairing, scalar lengths, strict raw JSON string decoding.
  System.Text.Json convenience conversions must not erase an invalid-surrogate
  distinction, so intake checks the raw string tokens before accessing the DOM.
- `DefinitionReader`: closed shapes, identities/types, applicability, exact bounds,
  paired decimal geometry, length consistency, pattern grammar and ENUM literal
  self-consistency. General satisfiability solving is deliberately out of scope.
- `Numbers` / `ExactNumber`: grammar and integer domains, canonical arbitrary-precision
  coefficient/exponent representation, exact comparisons, remainder and metadata
  integrality. Exponents are BigInteger values, not a platform-scale integer.
- `PortablePattern`: immutable ASCII atoms and O(atoms × Unicode points) position
  matching. All working arrays are local to a validation call.
- `ScalarValidator`: type/lexical prerequisites, ordered constraints, normalization,
  exact UTC tick arithmetic, Unicode, UUID and strict Base64.
- `JsonProjection`: detached immutable JSON projections preserving raw numeric kinds.
- `Rejection`: only recognized contract/input failures; unexpected resource and
  programming exceptions propagate rather than being mislabeled as validation.

A failing build stops at its first definition defect, as the contract permits.
Only scalar constraint issues have a mandated global order. Source diagnostics
include trusted identities and JSON Pointer paths where known.

Applications own active source selection and catalog publication. No global
application/tenant registry, discovery convention, ERP loader, HTTP adapter,
persistence mapper, ORM, CRUD or UI component is included. The .NET class library
references Microsoft.NETCore.App and YamlDotNet, not ASP.NET.
