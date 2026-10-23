using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ResxStudio.Models;

/// <summary>
/// One .resx file. Edits are applied to the original XML tree so the header, schema,
/// comments, non-string resources and key order are all preserved on save.
/// </summary>
public sealed class ResxDocument
{
    private static readonly XName SpaceAttribute = XNamespace.Xml + "space";

    private readonly XDocument _document;
    private readonly bool _hasBom;

    private ResxDocument(string filePath, XDocument document, bool hasBom)
    {
        FilePath = filePath;
        _document = document;
        _hasBom = hasBom;
    }

    public string FilePath { get; }

    public static ResxDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        XDocument document;
        using (var stream = new MemoryStream(bytes))
            document = XDocument.Load(stream, LoadOptions.None);

        if (document.Root?.Name.LocalName != "root")
            throw new InvalidDataException($"{Path.GetFileName(path)} doesn't look like a .resx file.");

        return new ResxDocument(path, document, hasBom);
    }

    /// <summary>Creates an empty resx (same header and schema as the template) for a new language.</summary>
    public static ResxDocument CreateFromTemplate(ResxDocument template, string newPath)
    {
        var copy = new XDocument(template._document);
        copy.Root!.Elements().Where(e => e.Name == "data" || e.Name == "metadata").Remove();
        return new ResxDocument(newPath, copy, template._hasBom);
    }

    /// <summary>Reads all plain string entries in file order.</summary>
    public List<KeyValuePair<string, string>> ReadStrings()
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var element in _document.Root!.Elements("data"))
        {
            if (!IsStringEntry(element)) continue;
            var name = (string?)element.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            result.Add(new KeyValuePair<string, string>(name, element.Element("value")?.Value ?? ""));
        }
        return result;
    }

    /// <summary>
    /// Makes the string entries in the XML match <paramref name="desired"/> (in that order).
    /// Existing entries are updated in place, missing ones are removed, new ones are
    /// inserted after the previous key so sorted files stay sorted.
    /// </summary>
    public void Apply(IReadOnlyList<KeyValuePair<string, string>> desired, IReadOnlyDictionary<string, string> renames)
    {
        var root = _document.Root!;
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in desired) wanted[pair.Key] = pair.Value;

        var elements = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in root.Elements("data").Where(IsStringEntry).ToList())
        {
            var name = (string?)element.Attribute("name") ?? "";
            if (renames.TryGetValue(name, out var renamed))
            {
                name = renamed;
                element.SetAttributeValue("name", renamed);
            }

            // Not wanted any more, or a duplicate: drop it.
            if (!wanted.TryGetValue(name, out var value) || elements.ContainsKey(name))
            {
                element.Remove();
                continue;
            }

            elements[name] = element;
            var valueElement = element.Element("value");
            if (valueElement == null)
            {
                valueElement = new XElement("value");
                element.AddFirst(valueElement);
            }
            if (valueElement.Value != value)
            {
                valueElement.Value = value;
                element.SetAttributeValue(SpaceAttribute, "preserve");
            }
        }

        XElement? previous = null;
        foreach (var pair in desired)
        {
            if (elements.TryGetValue(pair.Key, out var existing))
            {
                previous = existing;
                continue;
            }

            var created = new XElement("data",
                new XAttribute("name", pair.Key),
                new XAttribute(SpaceAttribute, "preserve"),
                new XElement("value", pair.Value));

            if (previous != null) previous.AddAfterSelf(created);
            else if (root.Elements("data").FirstOrDefault() is { } first) first.AddBeforeSelf(created);
            else root.Add(created);

            elements[pair.Key] = created;
            previous = created;
        }
    }

    /// <summary>Writes the file the way Visual Studio does (2-space indent, CRLF), via a temp file.</summary>
    public void Save()
    {
        var folder = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(_hasBom),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
        };

        var tempPath = FilePath + ".tmp";
        using (var writer = XmlWriter.Create(tempPath, settings))
            _document.Save(writer);

        File.Move(tempPath, FilePath, overwrite: true);
    }

    private static bool IsStringEntry(XElement element)
    {
        if (element.Attribute("mimetype") != null) return false;
        var type = (string?)element.Attribute("type");
        return type == null || type.StartsWith("System.String", StringComparison.Ordinal);
    }
}
