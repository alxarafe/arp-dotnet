using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Reads;

/// <summary>Trusted context; identity must include every principal/tenant/scope binding.</summary>
public sealed record ReadContext(string Identity, string PolicyRevision);

/// <summary>Null equalities means unsupported policy; an empty map explicitly permits every row.</summary>
public sealed record ReadAccess(bool Allowed, ImmutableHashSet<string> Fields,
    ImmutableDictionary<string, string>? Equalities);

/// <summary>The application registers only active, dependency-validated compositions.</summary>
public sealed record ReadTarget
{
    public string Projection
    {
        get;
    }
    public int DefaultLimit
    {
        get;
    }
    public int MaxLimit
    {
        get;
    }
    public ImmutableHashSet<string> Fields
    {
        get;
    }

    public ReadTarget(string projection, int defaultLimit, int maxLimit, IEnumerable<string> fields)
    {
        if (defaultLimit < 1 || defaultLimit > maxLimit || maxLimit > 100)
            throw new ArgumentOutOfRangeException(nameof(defaultLimit));
        Projection = projection;
        DefaultLimit = defaultLimit;
        MaxLimit = maxLimit;
        Fields = fields.ToImmutableHashSet(StringComparer.Ordinal);
    }
}

/// <summary>Application-owned read invocation boundary. No result cache or independent metadata generation.</summary>
public sealed class ListGetHost
{
    private readonly object gate = new();
    private readonly IReadCatalog catalog;
    private readonly IRecordReader reader;
    private readonly Func<string, string, ReadContext, ReadAccess> authorize;
    private ImmutableDictionary<string, ReadTarget> active;
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private long accessEpoch;

    public ListGetHost(IReadCatalog catalog, IRecordReader reader,
        IReadOnlyDictionary<string, ReadTarget> targets, Func<string, string, ReadContext, ReadAccess> authorize)
    {
        this.catalog = catalog;
        this.reader = reader;
        this.authorize = authorize;
        active = targets.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>Every authoritative policy or active-composition change, including ABA, must use this gate.</summary>
    public void ChangeAccess(IReadOnlyDictionary<string, ReadTarget> targets, Action policyMutation)
    {
        lock (gate) {
            accessEpoch++;
            active = targets.ToImmutableDictionary(StringComparer.Ordinal);
            policyMutation();
        }
    }

    public JsonElement Invoke(ReadOnlyMemory<byte> request, ReadContext context)
    {
        JsonElement parsed;
        try {
            parsed = CdsReadCodec.ParseRequest(request);
        } catch (Exception) {
            return Failure("INPUT_SHAPE");
        }
        try {
            return InvokeCore(JsonNode.Parse(parsed.GetRawText()), context);
        } catch (ReadFault fault) {
            return Failure(fault.Code);
        } catch (InvalidOperationException) {
            return Failure("GENERATION_CHANGED");
        } catch (Exception) {
            return Failure("READ_FAILED");
        }
    }

    private JsonElement InvokeCore(JsonNode? request, ReadContext context)
    {
        Need(request is JsonObject, "INPUT_SHAPE");
        string target = Text(request!["target"]), operation = Text(request["operation"]);
        ReadTarget registration;
        ReadAccess initial;
        long epoch;
        lock (gate) {
            Need(active.ContainsKey(target) && operation is "GET" or "LIST", "UNAVAILABLE");
            registration = active[target];
            initial = Access(target, operation, context, registration.Fields);
            epoch = accessEpoch;
        }
        Need(Keys(request!, "target", "operation", "inputs"), "INPUT_SHAPE");
        var snapshot = catalog.Resolve();
        var document = JsonNode.Parse(snapshot.ToJson())!;
        if (!snapshot.IsValid) {
            string family = Text(document["issues"]![0]!["family"]);
            throw new ReadFault(family switch {
                "execution-failure" => "READ_FAILED",
                "physical-incompatible" => "INCOMPATIBLE_STORAGE",
                _ => "INVALID_DEFINITION"
            });
        }
        var projection = Find(document["projections"]!, registration.Projection);
        Need(projection["operations"]!.AsArray().Any(n => Text(n) == operation), "UNAVAILABLE");
        var fields = projection["fields"]!.AsArray();
        string[] names = fields.Select(f => Text(f!["name"])).ToArray();
        Need(registration.Fields.SetEquals(names), "INVALID_DEFINITION");
        Need(initial.Equalities is not null, "CAPABILITY_UNAVAILABLE");
        var prepared = reader.Prepare(snapshot, registration.Projection, initial.Equalities!);
        string[] keys = prepared.PrimaryKey.Select(k => k.Name).ToArray();
        // This is the capacity of this host's protected evidence, not a storage-engine limit.
        Need(keys.Length is >= 1 and <= 7, "CAPABILITY_UNAVAILABLE");
        var keyTypes = prepared.PrimaryKey.Select(k => k.Type).ToList();
        Need(prepared.Columns.Select(c => c.Name).SequenceEqual(names), "INVALID_DEFINITION");
        long generation = catalog.PublishRead(snapshot, () => catalog.Generation);
        var binding = new Binding(target, registration.Projection, context, epoch, generation);
        var inputs = request["inputs"];
        Need(inputs is JsonObject, "INPUT_SHAPE");
        bool get = operation == "GET", opaque = !keys.All(names.Contains), absent = false;
        int limit = registration.DefaultLimit;
        string[]? position = null;
        if (get) {
            Need(Keys(inputs!, "key"), "INPUT_SHAPE");
            var key = inputs!["key"];
            Need(key is JsonObject, "INVALID_KEY");
            if (opaque) {
                Need(Keys(key!, "reference") && key!["reference"] is JsonValue v
                    && v.TryGetValue<string>(out _), "INVALID_KEY");
                string reference = Text(key!["reference"]);
                Need(reference.Length is >= 1 and <= 128, "INVALID_KEY");
                var token = Open(reference, binding, "GET", keys.Length);
                absent = token is null;
                position = token?.Position;
            } else {
                Need(Keys(key!, "components") && key!["components"] is JsonArray, "INVALID_KEY");
                var components = key!["components"]!.AsArray();
                Need(components.Count == keys.Length, "INVALID_KEY");
                position = new string[keys.Length];
                for (int i = 0; i < keys.Length; i++) {
                    var component = components[i];
                    Need(component is JsonObject && Keys(component, "field", "type", "value")
                        && Text(component["field"]) == keys[i]
                        && Text(component["type"]) == keyTypes[i].ToString().ToUpperInvariant(), "INVALID_KEY");
                    var decoded = CdsReadCodec.Decode(keyTypes[i], Text(component!["value"]));
                    Need(component["value"] is JsonValue value && value.TryGetValue<string>(out _)
                        && decoded.IsValid, "INVALID_KEY");
                    position[i] = (string) decoded.NormalizedValue!;
                }
            }
        } else {
            Need(inputs!.AsObject().All(p => p.Key is "limit" or "continuation"), "INPUT_SHAPE");
            if (inputs.AsObject().ContainsKey("limit")) {
                int? requested = CdsReadCodec.PageLimit(JsonSerializer.SerializeToElement(inputs["limit"]));
                Need(requested.HasValue, "INPUT_SHAPE");
                limit = Math.Min(requested!.Value, registration.MaxLimit);
            }
            if (inputs.AsObject().ContainsKey("continuation")) {
                Need(inputs["continuation"] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_CURSOR");
                string cursor = Text(inputs["continuation"]);
                Need(cursor.Length is >= 1 and <= 128, "INVALID_CURSOR");
                var token = Open(cursor, binding, "LIST", keys.Length);
                Need(token is not null && token.Limit == limit, "INVALID_CURSOR");
                position = token!.Position;
            }
        }
        JsonElement Publish(IReadOnlyList<ReadRow> rows, Action commit)
        {
            foreach (var row in rows) {
                for (int i = 0; i < keys.Length; i++)
                    Need(CdsReadCodec.Decode(keyTypes[i], row.Key[i]).IsValid, "ROW_IDENTITY_INVALID");
            }
            lock (gate) {
                Need(active.TryGetValue(target, out var current) && ReferenceEquals(current, registration),
                    "GENERATION_CHANGED");
                var final = Access(target, operation, context, registration.Fields);
                Need(Same(initial, final), "UNAVAILABLE");
                Need(accessEpoch == epoch, "GENERATION_CHANGED");
                return catalog.PublishRead(snapshot, () => {
                    object output;
                    if (get) {
                        Need(rows.Count <= 1, "ROW_IDENTITY_INVALID");
                        output = rows.Count == 0 ? new {
                            found = false
                        } : (object) new {
                            found = true,
                            record = Record(rows[0], keys, keyTypes, opaque, binding)
                        };
                    } else {
                        bool more = rows.Count > limit;
                        var visible = rows.Take(limit).ToArray();
                        output = new {
                            records = visible.Select(r => Record(r, keys, keyTypes, opaque, binding)).ToArray(),
                            page = new {
                                limit,
                                hasMore = more,
                                next = more ? Seal(binding, "LIST", visible[^1].Key, limit) : null
                            }
                        };
                    }
                    var response = JsonSerializer.SerializeToElement(new {
                        status = "success",
                        diagnostics = Array.Empty<object>(),
                        output
                    });
                    commit();
                    // Completion may invoke reentrant application/driver callbacks. Check again before output escapes.
                    Need(active.TryGetValue(target, out var completed) && ReferenceEquals(completed, registration),
                        "GENERATION_CHANGED");
                    Need(Same(initial, Access(target, operation, context, registration.Fields)), "UNAVAILABLE");
                    Need(accessEpoch == epoch, "GENERATION_CHANGED");
                    return catalog.PublishRead(snapshot, () => response);
                });
            }
        }
        bool Current()
        {
            lock (gate) {
                return epoch == accessEpoch && active.TryGetValue(target, out var current)
                    && ReferenceEquals(current, registration) && Same(initial, authorize(target, operation, context))
                    && ReferenceEquals(catalog.Current, snapshot);
            }
        }
        if (absent) {
            return Publish([], () => { });
        }
        using var attempt = reader.Open(new ReadPlan(prepared,
            position is null ? null : [.. position], get, limit), Current);
        return Publish(attempt.Rows, attempt.Complete);
    }

    private ReadAccess Access(string target, string operation, ReadContext context, IEnumerable<string> fields)
    {
        var access = authorize(target, operation, context);
        Need(access.Allowed && access.Fields.IsSupersetOf(fields), "UNAVAILABLE");
        return access;
    }

    private static bool Same(ReadAccess a, ReadAccess b) => a.Allowed == b.Allowed && a.Fields.SetEquals(b.Fields)
        && (a.Equalities is null ? b.Equalities is null : b.Equalities is not null
            && a.Equalities.Count == b.Equalities.Count
            && a.Equalities.All(p => b.Equalities.TryGetValue(p.Key, out string? v) && v == p.Value));

    private object Record(ReadRow row, string[] keys, List<ScalarType> types, bool opaque, Binding binding)
    {
        object key = opaque ? new {
            reference = Seal(binding, "GET", row.Key, 0)
        } : (object) new {
            components = keys.Select((name, i) => new {
                field = name,
                type = types[i].ToString().ToUpperInvariant(),
                value = row.Key[i]
            }).ToArray()
        };
        return new {
            key,
            values = row.Values
        };
    }

    private sealed record Binding(string Target, string Projection, ReadContext Context, long Epoch, long Generation);
    private sealed record Token(string[] Position, int Limit);
    private static byte[] Associated(Binding b, string purpose) =>
        JsonSerializer.SerializeToUtf8Bytes(new {
            purpose,
            b.Target,
            b.Projection,
            b.Context.Identity,
            b.Context.PolicyRevision,
            b.Epoch
        });

    private string Seal(Binding binding, string purpose, IReadOnlyList<string> key, int limit)
    {
        byte[] payload = new byte[12 + key.Count * 8];
        BinaryPrimitives.WriteInt64BigEndian(payload, binding.Generation);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), limit);
        for (int i = 0; i < key.Count; i++)
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(12 + i * 8),
                long.Parse(key[i], CultureInfo.InvariantCulture));
        byte[] result = new byte[12 + payload.Length + 16];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(secret, 16);
        aes.Encrypt(result.AsSpan(0, 12), payload, result.AsSpan(12, payload.Length),
            result.AsSpan(12 + payload.Length, 16), Associated(binding, purpose));
        string encoded = Convert.ToBase64String(result).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Need(encoded.Length <= 128, "CAPABILITY_UNAVAILABLE");
        return encoded;
    }

    private Token? Open(string text, Binding binding, string purpose, int count)
    {
        byte[] payload;
        try {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            if (bytes.Length != 40 + count * 8)
                return null;
            payload = new byte[12 + count * 8];
            using var aes = new AesGcm(secret, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(12, payload.Length),
                bytes.AsSpan(12 + payload.Length, 16), payload, Associated(binding, purpose));
        } catch (FormatException) {
            return null;
        } catch (CryptographicException) {
            return null;
        }
        Need(BinaryPrimitives.ReadInt64BigEndian(payload) == binding.Generation, "GENERATION_EXPIRED");
        string[] position = Enumerable.Range(0, count).Select(i =>
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(12 + i * 8))
                .ToString(CultureInfo.InvariantCulture)).ToArray();
        return new Token(position, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(8)));
    }

    private static JsonNode Find(JsonNode array, string id) =>
        array.AsArray().FirstOrDefault(n => Text(n!["id"]) == id) ?? throw new ReadFault("INVALID_DEFINITION");
    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static bool Keys(JsonNode node, params string[] keys) =>
        node is JsonObject obj && obj.Count == keys.Length && keys.All(obj.ContainsKey);
    private static void Need(bool condition, string code)
    {
        if (!condition)
            throw new ReadFault(code);
    }
    private static JsonElement Failure(string code)
    {
        bool failed = code is "READ_FAILED" or "ROW_DECODE_FAILED" or "ROW_IDENTITY_INVALID" or "GENERATION_CHANGED";
        string family = failed ? "execution-failure" : code switch {
            "INPUT_SHAPE" or "INVALID_KEY" or "INVALID_CURSOR" => "invalid-data",
            "INVALID_DEFINITION" => "definition-invalid",
            "INCOMPATIBLE_STORAGE" => "physical-incompatible",
            _ => "operation-not-allowed"
        };
        return JsonSerializer.SerializeToElement(new {
            status = failed ? "failed" : "rejected",
            diagnostics = new[] { new { family, code = "CDS_LISTGET_" + code } }
        });
    }
}