using System.Security.Cryptography;
using System.Text.Json;
using Alxarafe.Arp.Dictionary;

// Demo-only transport. The library remains independent of ASP.NET and HTTP.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
string origin = Environment.GetEnvironmentVariable("ARP_DEMO_ORIGIN") ?? "http://127.0.0.1:4173";
if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
    || originUri.Scheme is not ("http" or "https") || originUri.UserInfo.Length != 0
    || origin != originUri.GetLeftPart(UriPartial.Authority)) {
    throw new InvalidOperationException("ARP_DEMO_ORIGIN must be one exact http(s) origin without a trailing slash");
}
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(origin).WithMethods("GET", "POST").WithHeaders("Content-Type")));

string contractRoot = Environment.GetEnvironmentVariable("ARP_CONTRACT_PATH") ?? "/contract";
string[] paths = ["fixtures/dictionary/v1/catalog.json", "fixtures/dictionary/v1/sources/portable-equivalent.json"];
var sourceBytes = paths.Select(path => File.ReadAllBytes(Path.Combine(contractRoot, path))).ToArray();
var sources = paths.Select((path, index) =>
    new SourceSnapshot("arp-demo", path, SourceFormat.Json, sourceBytes[index]));
BuildResult build = new CatalogBuilder().BuildCatalog(sources);
if (!build.IsValid) {
    throw new InvalidOperationException("The demo dictionary sources did not produce a valid catalog");
}
Catalog catalog = build.Catalog!;
string contractRef = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract.ref")).Trim();
var app = builder.Build();
app.Use(async (context, next) => {
    try {
        await next(context);
    } catch (BadHttpRequestException exception) {
        await WriteError(context, exception.StatusCode, "DEMO_REQUEST_REJECTED");
    } catch (Exception exception) {
        app.Logger.LogError(exception, "Demo request failed");
        await WriteError(context, 500, "DEMO_EXECUTION_FAILURE");
    }
});
app.UseStatusCodePages(async status => {
    await WriteError(status.HttpContext, status.HttpContext.Response.StatusCode, "DEMO_HTTP_ERROR");
});
app.UseCors();
app.Use(async (context, next) => {
    if (context.Request.Headers.TryGetValue("Origin", out var supplied) && supplied.ToString() != origin) {
        await WriteError(context, 403, "DEMO_ORIGIN_NOT_ALLOWED");
        return;
    }
    await next(context);
});
app.MapGet("/demo/info", () => Results.Json(new {
    demoProtocol = "arp-demo-dotnet/1",
    runtime = "dotnet",
    libraryContractRef = contractRef,
    capabilities = new[] { "dictionary.list", "dictionary.describe", "dictionary.validate" },
    sources = paths.Select((path, index) => new {
        path,
        sha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes[index]))
    })
}));
app.MapGet("/demo/dictionary/elements", () => Results.Json(new {
    elements = catalog.ElementIds.Select(id => new {
        id,
        type = catalog.Resolve(id).Descriptor!.Type.ToString().ToUpperInvariant()
    })
}));
app.MapGet("/demo/dictionary/elements/{id}", (string id) => {
    ResolutionResult resolved = catalog.Resolve(id);
    return resolved.IsValid
        ? Results.Text(resolved.Descriptor!.ToJson(), "application/json")
        : Results.Json(resolved.ToProjection(), statusCode: 404);
});
app.MapPost("/demo/dictionary/elements/{id}/validate", async (string id, HttpRequest request) => {
    if (!request.HasJsonContentType()) {
        return Error(415, "DEMO_JSON_REQUIRED");
    }
    JsonDocument document;
    try {
        document = await JsonDocument.ParseAsync(
            request.Body, cancellationToken: request.HttpContext.RequestAborted);
    } catch (JsonException) {
        return Error(400, "DEMO_INVALID_JSON");
    }
    using (document) {
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) {
            return Error(400, "DEMO_INPUT_SHAPE");
        }
        // JsonDocument parses lazily: decoding escaped strings can still reject invalid Unicode.
        // Keep this catch at the input boundary; library/internal InvalidOperationExceptions remain 500.
        try {
            DecodeStrings(root);
        } catch (InvalidOperationException) {
            return Error(400, "DEMO_INVALID_JSON");
        }
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != "value") {
            return Error(400, "DEMO_INPUT_SHAPE");
        }
        JsonElement value = properties[0].Value;
        object? portable = value.ValueKind switch {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.Clone() // Real library type rejection; never coerce JSON numbers, arrays or objects.
        };
        ValidationResult result = catalog.Validate(id, portable);
        return Results.Json(result.ToProjection(), statusCode: catalog.Resolve(id).IsValid ? 200 : 404);
    }
});
app.Run();

static void DecodeStrings(JsonElement element)
{
    switch (element.ValueKind) {
        case JsonValueKind.String:
            _ = element.GetString();
            break;
        case JsonValueKind.Object:
            foreach (JsonProperty property in element.EnumerateObject()) {
                _ = property.Name;
                DecodeStrings(property.Value);
            }
            break;
        case JsonValueKind.Array:
            foreach (JsonElement item in element.EnumerateArray()) {
                DecodeStrings(item);
            }
            break;
    }
}

static IResult Error(int status, string code)
{
    return Results.Json(new {
        valid = false,
        issues = new[] { new { code } }
    }, statusCode: status);
}

static async Task WriteError(HttpContext context, int status, string code)
{
    if (context.Response.HasStarted) {
        context.Abort();
        return;
    }
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new {
        valid = false,
        issues = new[] { new { code } }
    });
}