using System.Xml.Linq;

namespace uDrivers.Services.Sources;

/// <summary>Namespace- and case-tolerant helpers for vendor XML catalogs.</summary>
internal static class Xml
{
    public static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return XDocument.Load(stream);
    }

    public static XDocument Parse(string text) => XDocument.Parse(text);

    public static IEnumerable<XElement> All(this XContainer c, string name) =>
        c.Descendants().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<XElement> Kids(this XElement e, string name) =>
        e.Elements().Where(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static XElement? Kid(this XElement? e, string name) => e?.Kids(name).FirstOrDefault();

    /// <summary>Trimmed text of the first child element with any of the given names.</summary>
    public static string Text(this XElement? e, params string[] names)
    {
        if (e is null) return "";
        foreach (var n in names)
        {
            var k = e.Kid(n);
            if (k is not null) return k.Value.Trim();
        }
        return "";
    }

    public static string Attr(this XElement? e, string name) =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

    /// <summary>Text of a localized Display child (Dell/Lenovo style), preferring English.</summary>
    public static string Display(this XElement? e)
    {
        if (e is null) return "";
        var displays = e.Kids("Display").Concat(e.Kids("Desc")).ToList();
        var en = displays.FirstOrDefault(d => d.Attr("lang").StartsWith("en", StringComparison.OrdinalIgnoreCase)
                                              || d.Attr("id").Equals("EN", StringComparison.OrdinalIgnoreCase));
        return (en ?? displays.FirstOrDefault())?.Value.Trim() ?? e.Value.Trim();
    }

    public static string FirstXml(string folder) =>
        Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories).FirstOrDefault()
        ?? throw new InvalidOperationException("The catalog didn't contain an XML file.");
}
