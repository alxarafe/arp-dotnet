using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Alxarafe.Arp.Cds.Reads;
using Xunit;

namespace Alxarafe.Arp.Dictionary.Tests;

public sealed class ReadHostTests
{
    private sealed class Catalog : IReadCatalog
    {
        private readonly CdsReadResult snapshot = new(JsonSerializer.SerializeToElement(new
        {
            valid = true,
            projections = new[] { new { id = "test", entity = "model",
                operations = new[] { "LIST", "GET" }, fields = new[] { new { name = "id", type = "INT64" } } } }
        }));
        public bool Valid = true;
        public bool Publishing;
        public CdsReadResult Resolve() => snapshot;
        public CdsReadResult? Current => Valid ? snapshot : null;
        public long Generation => 1;
        public T PublishRead<T>(CdsReadResult expected, Func<T> publish)
        {
            if (!Valid || !ReferenceEquals(snapshot, expected))
                throw new InvalidOperationException();
            bool prior = Publishing;
            Publishing = true;
            try
            {
                return publish();
            } finally { Publishing = prior; }
        }
    }

    private sealed class Prepared() : PreparedRead([new ReadColumn("id", ScalarType.Int64, false)],
        [new ReadColumn("id", ScalarType.Int64, false)], ImmutableDictionary<string, string>.Empty);

    private sealed class Reader(Catalog catalog, Action complete) : IRecordReader
    {
        public bool Completed, Disposed;
        public PreparedRead Prepare(CdsReadResult snapshot, string projection,
            ImmutableDictionary<string, string> equalities) => new Prepared();
        public IReadAttempt Open(ReadPlan plan, Func<bool> current)
        {
            Assert.True(current());
            return new Attempt(this, catalog, complete);
        }
        private sealed class Attempt(Reader owner, Catalog catalog, Action complete) : IReadAttempt
        {
            public IReadOnlyList<ReadRow> Rows
            {
                get;
            } = new[] {
                new ReadRow(["2"], ImmutableDictionary<string, object?>.Empty.Add("id", "2")) };
            public void Complete()
            {
                Assert.True(catalog.Publishing);
                complete();
                owner.Completed = true;
            }
            public void Dispose() => owner.Disposed = true;
        }
    }

    [Fact]
    public void CoreHasNoSqlOrProviderAssemblyDependency()
    {
        var references = typeof(ListGetHost).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(references, n => n.Contains("PostgreSql", StringComparison.OrdinalIgnoreCase)
            || n == "Alxarafe.Arp.Cds.Sql" || n.StartsWith("System.Data", StringComparison.Ordinal));
        Assert.Equal(typeof(ListGetHost).Assembly,
            typeof(Alxarafe.Arp.Cds.Physical.PhysicalCatalog).Assembly);
        Assert.Equal(typeof(ListGetHost).Assembly,
            typeof(Alxarafe.Arp.Cds.Physical.IPhysicalObservationProvider).Assembly);
        Assert.Equal(typeof(ListGetHost).Assembly,
            typeof(Alxarafe.Arp.Cds.Physical.IInvalidatablePhysicalProvider).Assembly);
        var parameters = typeof(ListGetHost).GetConstructors().Single().GetParameters();
        Assert.Equal(typeof(IReadCatalog), parameters[0].ParameterType);
        Assert.Equal(typeof(IRecordReader), parameters[1].ParameterType);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("commit-failure")]
    [InlineData("invalidate")]
    [InlineData("revoke")]
    public void NeutralReaderCompletesInsidePublicationAndCannotReleaseStaleOutput(string mode)
    {
        var catalog = new Catalog();
        bool authorized = true;
        var reader = new Reader(catalog, () =>
        {
            if (mode == "commit-failure")
                throw new ReadFault("READ_FAILED");
            if (mode == "invalidate")
                catalog.Valid = false;
            if (mode == "revoke")
                authorized = false;
        });
        var host = new ListGetHost(catalog, reader,
            new Dictionary<string, ReadTarget> { ["test"] = new("test", 2, 3, ["id"]) },
            (_, _, _) => new ReadAccess(authorized, ImmutableHashSet.Create("id"),
                ImmutableDictionary<string, string>.Empty));
        var response = host.Invoke(Encoding.UTF8.GetBytes(
            "{\"target\":\"test\",\"operation\":\"LIST\",\"inputs\":{}}"), new ReadContext("test", "1"));
        Assert.True(reader.Disposed);
        if (mode == "success")
        {
            Assert.True(reader.Completed);
            Assert.Equal("success", response.GetProperty("status").GetString());
            Assert.Equal("2", response.GetProperty("output").GetProperty("records")[0]
                .GetProperty("values").GetProperty("id").GetString());
        } else
        {
            Assert.False(response.TryGetProperty("output", out _));
            string code = mode switch
            {
                "commit-failure" => "READ_FAILED",
                "invalidate" => "GENERATION_CHANGED",
                _ => "UNAVAILABLE"
            };
            Assert.Equal("CDS_LISTGET_" + code, response.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        }
    }
}
