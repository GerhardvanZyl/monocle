using System.Xml;

namespace Monocle.Core.Sidecars;

/// <summary>
/// Reads, merges and writes standard Adobe XMP sidecar files (<c>&lt;name&gt;.xmp</c>).
/// Writing preserves any fields Monocle does not manage, backs the file up to
/// <c>.xmp.bak</c> before the first edit, and never touches the proprietary
/// <c>.on1</c> file (#11 safe-writes). On1 and Lightroom read these back directly (#13).
/// </summary>
public static class XmpSidecar
{
    private const string NsX = "adobe:ns:meta/";
    private const string NsRdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string NsDc = "http://purl.org/dc/elements/1.1/";
    private const string NsXmp = "http://ns.adobe.com/xap/1.0/";
    private const string NsTiff = "http://ns.adobe.com/tiff/1.0/";

    /// <summary>The sidecar path for a given image file: replaces the extension with .xmp.</summary>
    public static string PathFor(string imagePath) =>
        Path.ChangeExtension(imagePath, ".xmp");

    public static bool Exists(string imagePath) => File.Exists(PathFor(imagePath));

    /// <summary>Read existing XMP metadata, or an empty <see cref="XmpData"/> if there is none.</summary>
    public static XmpData Read(string imagePath)
    {
        var path = PathFor(imagePath);
        var data = new XmpData();
        if (!File.Exists(path))
            return data;

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);
        var ns = NsManager(doc);
        var desc = doc.SelectSingleNode("//rdf:Description", ns);
        if (desc is null)
            return data;

        if (int.TryParse(SelectText(desc, "xmp:Rating", ns), out var rating))
            data.Rating = rating;
        data.Label = SelectText(desc, "xmp:Label", ns);
        if (int.TryParse(SelectText(desc, "tiff:Orientation", ns), out var orientation))
            data.Orientation = orientation;
        data.Description = SelectLangAlt(desc, "dc:description", ns);
        foreach (XmlNode li in desc.SelectNodes("dc:subject/rdf:Bag/rdf:li", ns) ?? Empty())
            if (!string.IsNullOrWhiteSpace(li.InnerText))
                data.Keywords.Add(li.InnerText);

        return data;
    }

    /// <summary>
    /// Merge <paramref name="data"/> into the sidecar for <paramref name="imagePath"/> and save.
    /// Only the fields Monocle manages are overwritten; everything else is preserved.
    /// </summary>
    public static void Write(string imagePath, XmpData data)
    {
        var path = PathFor(imagePath);
        var doc = File.Exists(path) ? LoadExisting(path) : NewDocument();
        var ns = NsManager(doc);
        var desc = EnsureDescription(doc, ns);

        if (data.Rating is { } r)
            SetSimple(doc, desc, NsXmp, "xmp", "Rating", r.ToString());

        if (!string.IsNullOrEmpty(data.Label))
            SetSimple(doc, desc, NsXmp, "xmp", "Label", data.Label);
        else
            RemoveChild(desc, "xmp:Label", ns);

        if (data.Orientation is { } orientation)
            SetSimple(doc, desc, NsTiff, "tiff", "Orientation", orientation.ToString());

        if (data.Keywords.Count > 0)
            SetBag(doc, desc, ns, data.Keywords);

        if (data.Description is not null)
            SetLangAlt(doc, desc, data.Description);

        BackupOnce(path);
        SaveAtomic(doc, path);
    }

    // ---- helpers --------------------------------------------------------

    private static XmlDocument LoadExisting(string path)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);
        return doc;
    }

    private static XmlDocument NewDocument()
    {
        var doc = new XmlDocument();
        var xmpmeta = doc.CreateElement("x", "xmpmeta", NsX);
        xmpmeta.SetAttribute("xmptk", NsX, "Monocle");
        doc.AppendChild(xmpmeta);
        var rdf = doc.CreateElement("rdf", "RDF", NsRdf);
        xmpmeta.AppendChild(rdf);
        var desc = doc.CreateElement("rdf", "Description", NsRdf);
        desc.SetAttribute("about", NsRdf, "");
        rdf.AppendChild(desc);
        return doc;
    }

    private static XmlNamespaceManager NsManager(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("x", NsX);
        ns.AddNamespace("rdf", NsRdf);
        ns.AddNamespace("dc", NsDc);
        ns.AddNamespace("xmp", NsXmp);
        ns.AddNamespace("tiff", NsTiff);
        return ns;
    }

    private static XmlElement EnsureDescription(XmlDocument doc, XmlNamespaceManager ns)
    {
        if (doc.SelectSingleNode("//rdf:Description", ns) is XmlElement existing)
            return existing;

        var rdf = doc.SelectSingleNode("//rdf:RDF", ns) as XmlElement
                  ?? throw new InvalidDataException("XMP file has no rdf:RDF element.");
        var desc = doc.CreateElement("rdf", "Description", NsRdf);
        desc.SetAttribute("about", NsRdf, "");
        rdf.AppendChild(desc);
        return desc;
    }

    private static void SetSimple(XmlDocument doc, XmlElement desc, string nsUri, string prefix, string local, string value)
    {
        var ns = NsManager(doc);
        RemoveChild(desc, $"{prefix}:{local}", ns);
        var el = doc.CreateElement(prefix, local, nsUri);
        el.InnerText = value;
        desc.AppendChild(el);
    }

    private static void SetBag(XmlDocument doc, XmlElement desc, XmlNamespaceManager ns, IEnumerable<string> values)
    {
        RemoveChild(desc, "dc:subject", ns);
        var subject = doc.CreateElement("dc", "subject", NsDc);
        var bag = doc.CreateElement("rdf", "Bag", NsRdf);
        foreach (var v in values)
        {
            var li = doc.CreateElement("rdf", "li", NsRdf);
            li.InnerText = v;
            bag.AppendChild(li);
        }
        subject.AppendChild(bag);
        desc.AppendChild(subject);
    }

    private static void SetLangAlt(XmlDocument doc, XmlElement desc, string value)
    {
        var ns = NsManager(doc);
        RemoveChild(desc, "dc:description", ns);
        var description = doc.CreateElement("dc", "description", NsDc);
        var alt = doc.CreateElement("rdf", "Alt", NsRdf);
        var li = doc.CreateElement("rdf", "li", NsRdf);
        li.SetAttribute("xml:lang", "x-default");
        li.InnerText = value;
        alt.AppendChild(li);
        description.AppendChild(alt);
        desc.AppendChild(description);
    }

    private static void RemoveChild(XmlElement desc, string xpath, XmlNamespaceManager ns)
    {
        if (desc.SelectSingleNode(xpath, ns) is XmlNode node)
            desc.RemoveChild(node);
    }

    private static string? SelectText(XmlNode desc, string xpath, XmlNamespaceManager ns) =>
        desc.SelectSingleNode(xpath, ns)?.InnerText;

    private static string? SelectLangAlt(XmlNode desc, string field, XmlNamespaceManager ns) =>
        desc.SelectSingleNode($"{field}/rdf:Alt/rdf:li", ns)?.InnerText
        ?? desc.SelectSingleNode(field, ns)?.InnerText;

    private static void BackupOnce(string path)
    {
        var bak = path + ".bak";
        if (File.Exists(path) && !File.Exists(bak))
            File.Copy(path, bak);
    }

    private static void SaveAtomic(XmlDocument doc, string path)
    {
        var tmp = path + ".tmp";
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(false),
            OmitXmlDeclaration = false,
        };
        using (var writer = XmlWriter.Create(tmp, settings))
            doc.Save(writer);
        File.Move(tmp, path, overwrite: true);
    }

    private static XmlNodeList Empty() => new XmlDocument().ChildNodes;
}
