using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ResxStudio.Models;

public enum DiskChange { None, Modified, Deleted, Created }

/// <summary>
/// One .resx file. Edits are applied to the original XML tree so the header, schema,
/// comments, non-string resources and key order are all preserved on save.
/// Also remembers what the file looked like on disk, to notice changes made elsewhere.
/// </summary>
public sealed class ResxDocument
{
    private static readonly XName SpaceAttribute = XNamespace.Xml + "space";

    private readonly XDocument _document;
    private readonly bool _hasBom;
    private readonly HashSet<string> _duplicateKeys = new(StringComparer.Ordinal);
    private FileFingerprint? _onDisk;

    private sealed record FileFingerprint(long Length, DateTime LastWriteUtc, string Hash);

    private ResxDocument(string filePath, XDocument document, bool hasBom, FileFingerprint? onDisk)
    {
        FilePath = filePath;
        _document = document;
        _hasBom = hasBom;
        _onDisk = onDisk;
    }

    public string FilePath { get; }

    /// <summary>Keys that appear more than once in the file (found by the last ReadStrings call).</summary>
    public IReadOnlyCollection<string> DuplicateKeys => _duplicateKeys;

    public static ResxDocument Load(string path)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        XDocument document;
        using (var stream = new MemoryStream(bytes))
            document = XDocument.Load(stream, LoadOptions.None);

        if (document.Root?.Name.LocalName != "root")
            throw new InvalidDataException($"{Path.GetFileName(path)} doesn't look like a .resx file.");

        return new ResxDocument(path, document, hasBom, new FileFingerprint(bytes.Length, lastWrite, Hash(bytes)));
    }

    /// <summary>Creates an empty resx (same header and schema as the template) for a new language.</summary>
    public static ResxDocument CreateFromTemplate(ResxDocument template, string newPath)
    {
        var copy = new XDocument(template._document);
        copy.Root!.Elements().Where(e => e.Name == "data" || e.Name == "metadata").Remove();
        return new ResxDocument(newPath, copy, template._hasBom, onDisk: null);
    }

    /// <summary>Reads all plain string entries in file order.</summary>
    public List<KeyValuePair<string, string>> ReadStrings()
    {
        _duplicateKeys.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<KeyValuePair<string, string>>();
        foreach (var element in _document.Root!.Elements("data"))
        {
            if (!IsStringEntry(element)) continue;
            var name = (string?)element.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            if (!seen.Add(name)) _duplicateKeys.Add(name);
            result.Add(new KeyValuePair<string, string>(name, element.Element("value")?.Value ?? ""));
        }
        return result;
    }

    /// <summary>
    /// Makes the string entries in the XML match <paramref name="desired"/> (in that order).
    /// Existing entries are updated in place, missing ones are removed, new ones are
    /// inserted after the previous key so sorted files stay sorted. Duplicates are collapsed.
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

        _duplicateKeys.Clear();
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

        var bytes = File.ReadAllBytes(FilePath);
        _onDisk = new FileFingerprint(bytes.Length, File.GetLastWriteTimeUtc(FilePath), Hash(bytes));
    }

    /// <summary>
    /// Has the file on disk changed since we loaded or saved it? Size and time are checked first;
    /// the content hash decides, so a tool that only touches the timestamp isn't reported.
    /// </summary>
    public DiskChange CheckDisk()
    {
        try
        {
            var exists = File.Exists(FilePath);
            if (_onDisk == null) return exists ? DiskChange.Created : DiskChange.None;
            if (!exists) return DiskChange.Deleted;

            var lastWrite = File.GetLastWriteTimeUtc(FilePath);
            var length = new FileInfo(FilePath).Length;
            if (length == _onDisk.Length && lastWrite == _onDisk.LastWriteUtc) return DiskChange.None;

            var bytes = File.ReadAllBytes(FilePath);
            var hash = Hash(bytes);
            if (hash == _onDisk.Hash)
            {
                _onDisk = new FileFingerprint(bytes.Length, lastWrite, hash);
                return DiskChange.None;
            }
            return DiskChange.Modified;
        }
        catch (IOException)
        {
            // Another program is writing it right now; look again next time.
            return DiskChange.None;
        }
        catch (UnauthorizedAccessException)
        {
            return DiskChange.None;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsStringEntry(XElement element)
    {
        if (element.Attribute("mimetype") != null) return false;
        var type = (string?)element.Attribute("type");
        return type == null || type.StartsWith("System.String", StringComparison.Ordinal);
    }
}
