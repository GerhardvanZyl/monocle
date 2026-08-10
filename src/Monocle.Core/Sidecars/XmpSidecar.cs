using System.Globalization;
using System.Text;
using System.Xml;
using Monocle.Core.Model;

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
    private const string NsCrs = "http://ns.adobe.com/camera-raw-settings/1.0/";

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

        // Adobe/Lightroom/exiftool often split properties across several rdf:Description siblings
        // (one per namespace), so read across all of them rather than just the first.
        var descriptions = doc.SelectNodes("//rdf:Description", ns);
        if (descriptions is null || descriptions.Count == 0)
            return data;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords = new List<string>();
        foreach (XmlNode desc in descriptions)
        {
            if (data.Rating is null && int.TryParse(SelectText(desc, "xmp:Rating", ns), out var rating))
                data.Rating = rating;
            data.Label ??= SelectText(desc, "xmp:Label", ns);
            if (data.Orientation is null && int.TryParse(SelectText(desc, "tiff:Orientation", ns), out var orientation))
                data.Orientation = orientation;
            data.Crop ??= ReadCrop(desc, ns);
            data.Description ??= SelectLangAlt(desc, "dc:description", ns);
            foreach (XmlNode li in desc.SelectNodes("dc:subject/rdf:Bag/rdf:li", ns) ?? Empty())
                if (!string.IsNullOrWhiteSpace(li.InnerText) && seen.Add(li.InnerText.Trim()))
                    keywords.Add(li.InnerText.Trim());
        }
        data.Keywords = keywords;

        return data;
    }

    /// <summary>
    /// Merge <paramref name="data"/> into the sidecar for <paramref name="imagePath"/> and save.
    /// Only the fields Monocle manages are overwritten; everything else is preserved. A field
    /// <paramref name="data"/> leaves null is not authored at all — see <see cref="XmpData"/> — so a
    /// caller that has nothing to say about the rating cannot destroy one written elsewhere.
    /// </summary>
    public static void Write(string imagePath, XmpData data)
    {
        var path = PathFor(imagePath);

        // Serialize the whole read-modify-write: the app and the spawned MCP server can both
        // write the same sidecar, and a half-merged interleave would lose one writer's edits.
        using var _ = SidecarLock.Acquire(path);

        var doc = File.Exists(path) ? LoadExisting(path) : NewDocument();
        var ns = NsManager(doc);
        var desc = EnsureDescription(doc, ns);

        // The rating, its colour label and the managed keyword flags are one unit: a write either
        // authors all three or leaves all three exactly as the file has them, so a save that has
        // nothing to say about the rating cannot destroy one another application made (#11).
        if (data.WritesRatingFields)
        {
            if (data.Rating is { } r)
                SetSimple(doc, desc, NsXmp, "xmp", "Rating", r.ToString());

            if (!string.IsNullOrEmpty(data.Label))
                SetSimple(doc, desc, NsXmp, "xmp", "Label", data.Label);
            else
                RemoveProperty(desc, "xmp", "Label", ns);

            // Merge, never clobber: keep any keywords On1/Lightroom wrote and re-apply Monocle's
            // managed set (user keywords + current Pick/reject), dropping stale managed flags.
            var merged = MergeKeywords(ReadBag(desc, ns), data.Keywords);
            if (merged.Count > 0)
                SetBag(doc, desc, ns, merged);
            else
                RemoveChild(desc, "dc:subject", ns);
        }

        if (data.Orientation is { } orientation)
            SetSimple(doc, desc, NsTiff, "tiff", "Orientation", orientation.ToString());

        WriteCrop(doc, desc, ns, data.Crop);

        // Only write dc:description when Monocle actually has something to say; null means "leave
        // the existing caption alone" so we never wipe an On1/LR caption. An explicit empty string
        // is different: it means the caller knows the description was empty before the edit it is
        // undoing, so leaving the undone verdict behind would be wrong.
        if (!string.IsNullOrEmpty(data.Description))
            SetLangAlt(doc, desc, data.Description);
        else if (data.Description is "")
            RemoveChild(desc, "dc:description", ns);

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
        ns.AddNamespace("crs", NsCrs);
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
        RemoveProperty(desc, prefix, local, ns);
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
            li.InnerText = SanitizeXmlText(v);
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
        li.InnerText = SanitizeXmlText(value);
        alt.AppendChild(li);
        description.AppendChild(alt);
        desc.AppendChild(description);
    }

    private static void RemoveChild(XmlElement desc, string xpath, XmlNamespaceManager ns)
    {
        if (desc.SelectSingleNode(xpath, ns) is XmlNode node)
            desc.RemoveChild(node);
    }

    /// <summary>
    /// Remove a simple property in <em>both</em> serializations: the child-element form
    /// (<c>&lt;xmp:Rating&gt;</c>) and the RDF-attribute shorthand (<c>xmp:Rating="…"</c>) that
    /// On1/Lightroom use, so a re-write never leaves a stale duplicate next to the new value.
    /// </summary>
    private static void RemoveProperty(XmlElement desc, string prefix, string local, XmlNamespaceManager ns)
    {
        RemoveChild(desc, $"{prefix}:{local}", ns);
        var uri = ns.LookupNamespace(prefix);
        if (uri is not null && desc.HasAttribute(local, uri))
            desc.RemoveAttribute(local, uri);
    }

    /// <summary>Read the dc:subject keyword bag (child-element form; the only form Adobe uses for it).</summary>
    private static List<string> ReadBag(XmlNode desc, XmlNamespaceManager ns)
    {
        var bag = new List<string>();
        foreach (XmlNode li in desc.SelectNodes("dc:subject/rdf:Bag/rdf:li", ns) ?? Empty())
            if (!string.IsNullOrWhiteSpace(li.InnerText))
                bag.Add(li.InnerText.Trim());
        return bag;
    }

    /// <summary>
    /// Union the keywords already on disk with Monocle's managed set. Existing keywords are
    /// preserved (On1/LR data), except the Monocle-managed flags (Pick/reject and the technical-reason
    /// tags — see <see cref="MonocleKeywords"/>), which are dropped from the existing set so the
    /// current rating's flags (carried in <paramref name="managed"/>) win rather than accumulating.
    /// </summary>
    private static List<string> MergeKeywords(List<string> existing, IEnumerable<string> managed)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var k in existing)
            if (!MonocleKeywords.IsManaged(k) && seen.Add(k))
                result.Add(k);
        foreach (var k in managed)
            if (!string.IsNullOrWhiteSpace(k) && seen.Add(k))
                result.Add(k);

        return result;
    }

    /// <summary>
    /// Strip characters that are illegal in XML 1.0 (NUL and most C0 controls) so a control char
    /// pasted into a note doesn't make <see cref="XmlWriter"/> throw and abort the entire save.
    /// Tab/LF/CR and all printable text are preserved; valid surrogate pairs (emoji, astral-plane
    /// characters) are kept intact, but an *unpaired* surrogate — which would itself make XmlWriter
    /// throw, the very abort this guards against — is dropped.
    /// </summary>
    private static string SanitizeXmlText(string s)
    {
        if (string.IsNullOrEmpty(s) || IsClean(s))
            return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;            // consumed the pair
                }
                // else: lone high surrogate — drop it
            }
            else if (!char.IsLowSurrogate(c) && IsLegalXmlChar(c))
            {
                sb.Append(c);       // a lone low surrogate falls through and is dropped
            }
        }
        return sb.ToString();
    }

    private static bool IsClean(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return false;       // lone high surrogate
            }
            if (char.IsLowSurrogate(c) || !IsLegalXmlChar(c))
                return false;       // lone low surrogate or illegal char
        }
        return true;
    }

    private static bool IsLegalXmlChar(char c) =>
        c is '\t' or '\n' or '\r' || (c >= ' ' && c != '￾' && c != '￿');

    private static CropRect? ReadCrop(XmlNode desc, XmlNamespaceManager ns)
    {
        if (!bool.TryParse(SelectText(desc, "crs:HasCrop", ns), out var hasCrop) || !hasCrop)
            return null;
        var okL = TryD(SelectText(desc, "crs:CropLeft", ns), out var l);
        var okT = TryD(SelectText(desc, "crs:CropTop", ns), out var t);
        var okR = TryD(SelectText(desc, "crs:CropRight", ns), out var r);
        var okB = TryD(SelectText(desc, "crs:CropBottom", ns), out var b);
        return okL && okT && okR && okB ? CropRect.FromEdges(l, t, r, b) : null;
    }

    private static void WriteCrop(XmlDocument doc, XmlElement desc, XmlNamespaceManager ns, CropRect? crop)
    {
        foreach (var field in new[] { "HasCrop", "CropLeft", "CropTop", "CropRight", "CropBottom" })
            RemoveProperty(desc, "crs", field, ns);
        if (crop is not { } c)
            return;
        SetSimple(doc, desc, NsCrs, "crs", "HasCrop", "True");
        SetSimple(doc, desc, NsCrs, "crs", "CropLeft", D(c.Left));
        SetSimple(doc, desc, NsCrs, "crs", "CropTop", D(c.Top));
        SetSimple(doc, desc, NsCrs, "crs", "CropRight", D(c.Right));
        SetSimple(doc, desc, NsCrs, "crs", "CropBottom", D(c.Bottom));
    }

    private static string D(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool TryD(string? s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static string? SelectText(XmlNode desc, string xpath, XmlNamespaceManager ns)
    {
        // Adobe serializes simple properties either as a child element (<xmp:Rating>3</xmp:Rating>)
        // or as an attribute on rdf:Description (xmp:Rating="3"). On1 and Lightroom routinely use
        // the attribute form, so fall back to it when the element is absent.
        if (desc.SelectSingleNode(xpath, ns)?.InnerText is { } elementText)
            return elementText;

        if (desc is XmlElement el && xpath.Split(':') is [var prefix, var local]
            && ns.LookupNamespace(prefix) is { } uri && el.HasAttribute(local, uri))
            return el.GetAttribute(local, uri);

        return null;
    }

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
        try
        {
            using (var writer = XmlWriter.Create(tmp, settings))
                doc.Save(writer);
            AtomicFile.Replace(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    private static XmlNodeList Empty() => new XmlDocument().ChildNodes;
}
