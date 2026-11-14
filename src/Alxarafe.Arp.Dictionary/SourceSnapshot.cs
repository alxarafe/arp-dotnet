using System.Text;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Explicit immutable source bytes. No discovery, filesystem or network access.</summary>
public sealed class SourceSnapshot
{
    private readonly byte[] content;
    public string ProviderId { get; }
    public string SourceId { get; }
    public SourceFormat Format { get; }
    public SourceSnapshot(string providerId, string sourceId, SourceFormat format, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(providerId); ArgumentNullException.ThrowIfNull(sourceId);
        if (!System.Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        ProviderId = providerId; SourceId = sourceId; Format = format; this.content = content.ToArray();
    }
    public static SourceSnapshot FromUtf8(string providerId, string sourceId, SourceFormat format, string content) =>
        new(providerId, sourceId, format, UnicodeText.Utf8.GetBytes(content));
    public byte[] GetContent() => (byte[])content.Clone();
    internal ReadOnlyMemory<byte> Bytes => content;
}
