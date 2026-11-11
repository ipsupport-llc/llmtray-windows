using System.IO.Compression;
using System.Xml;

namespace LLMTray.Core.ProjectFiles;

/// <summary>A document that can't be read, with the reason the user is shown.</summary>
public sealed class ExtractException(string reason) : Exception(reason);

/// <summary>
/// A zip container (docx, xlsx, pptx, odt) read with caps (Mac adr/0012):
/// entry count, bytes per part and in all, compression ratio, no nested
/// archives. A zip bomb is refused before it's inflated past the cap.
/// </summary>
public sealed class SafeZip : IDisposable
{
    public const int MaxEntries = 10_000;
    public const long MaxPart = 64L << 20;
    public const long MaxTotal = 256L << 20;
    public const int MaxRatio = 200;

    readonly ZipArchive zip;
    long total;

    public SafeZip(Stream stream)
    {
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > MaxEntries) throw new ExtractException("too many parts in the file");
        }
        catch (InvalidDataException)
        {
            throw new ExtractException("the file is damaged (not a readable zip container)");
        }
    }

    public bool Has(string name) => zip.GetEntry(name) is not null;

    public IEnumerable<string> Names => zip.Entries.Select(e => e.FullName);

    /// <summary>A part's bytes, counted as they inflate (the sizes in the directory can lie).</summary>
    public byte[]? Read(string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        if (entry.Length > MaxPart) throw new ExtractException("a part of the file is too large");
        if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > MaxRatio && entry.Length > 1 << 20)
        {
            throw new ExtractException("the file looks like a zip bomb");
        }
        try
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                total += n;
                if (output.Length > MaxPart) throw new ExtractException("a part of the file is too large");
                if (total > MaxTotal) throw new ExtractException("the file unpacks to too much data");
                if (entry.CompressedLength > 0 && output.Length > 1 << 20 && output.Length / Math.Max(1, entry.CompressedLength) > MaxRatio)
                {
                    throw new ExtractException("the file looks like a zip bomb");
                }
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new ExtractException("the file is damaged (a part doesn't unpack)");
        }
    }

    /// <summary>
    /// An XML part for reading: no DTD (any DOCTYPE is refused, so no entity
    /// expansion and nothing fetched), nesting capped.
    /// </summary>
    public XmlReader? Xml(string name) => Read(name) is { } bytes ? SafeXml.Reader(bytes) : null;

    public void Dispose() => zip.Dispose();
}

public static class SafeXml
{
    public const int MaxDepth = 256;

    public static XmlReader Reader(byte[] bytes) => new DepthCapped(XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = SafeZip.MaxPart,
    }));

    /// <summary>Wraps a reader: past <see cref="MaxDepth"/> it throws (a part nested 200k levels deep).</summary>
    sealed class DepthCapped(XmlReader inner) : XmlReader
    {
        public override bool Read()
        {
            bool more;
            try
            {
                more = inner.Read();
            }
            catch (XmlException e)
            {
                throw new ExtractException("the file is damaged (" + e.Message + ")");
            }
            if (inner.Depth > MaxDepth) throw new ExtractException("the file is nested too deeply");
            return more;
        }

        public override int AttributeCount => inner.AttributeCount;
        public override string BaseURI => inner.BaseURI;
        public override int Depth => inner.Depth;
        public override bool EOF => inner.EOF;
        public override bool IsEmptyElement => inner.IsEmptyElement;
        public override string LocalName => inner.LocalName;
        public override string NamespaceURI => inner.NamespaceURI;
        public override XmlNameTable NameTable => inner.NameTable;
        public override XmlNodeType NodeType => inner.NodeType;
        public override string Prefix => inner.Prefix;
        public override ReadState ReadState => inner.ReadState;
        public override string Value => inner.Value;
        public override string GetAttribute(int i) => inner.GetAttribute(i);
        public override string? GetAttribute(string name) => inner.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
        public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
        public override bool MoveToElement() => inner.MoveToElement();
        public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
        public override bool ReadAttributeValue() => inner.ReadAttributeValue();
        public override void ResolveEntity() => inner.ResolveEntity();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
