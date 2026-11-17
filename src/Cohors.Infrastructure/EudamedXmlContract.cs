using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Cohors.Domain;

namespace Cohors.Infrastructure;

internal sealed class EudamedXmlContract
{
    internal static readonly XNamespace Base = "https://ec.europa.eu/tools/eudamed/dtx/datamodel/Entity/Vigilance";
    internal static readonly XNamespace V1 = Base.NamespaceName + "/v1";
    internal static readonly XNamespace Xsi = XmlSchema.InstanceNamespace;
    internal static readonly Lazy<EudamedXmlContract> Snapshot = new(() => new());
    private readonly XmlSchemaSet schemas;
    private readonly Dictionary<string, HashSet<string>> enumerations = new(StringComparer.Ordinal);
    internal string SchemaFingerprint { get; }
    internal string EnumerationFingerprint { get; }
    internal string RuntimeSchemaFingerprint { get; }

    private EudamedXmlContract()
    {
        var assembly = typeof(EudamedXmlContract).Assembly;
        using var manifest = JsonDocument.Parse(Read(assembly, "Cohors.Eudamed.Provenance.json"));
        if (manifest.RootElement.GetProperty("sha256").GetString() != "caeb50507b578078853ebb34430e1e075e43381f054e14c7ee6692166be4cf43")
            throw new InvalidOperationException("Unpinned EUDAMED schema archive.");
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Cohors.Eudamed/", StringComparison.Ordinal))
            .ToDictionary(n => n["Cohors.Eudamed/".Length..].Replace('\\', '/'), n => Read(assembly, n), StringComparer.Ordinal);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in manifest.RootElement.GetProperty("entries").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            if (!name.EndsWith(".xsd", StringComparison.Ordinal)) continue;
            if (!resources.TryGetValue(name, out var bytes) || Hash(bytes) != item.GetProperty("sha256").GetString())
                throw new InvalidOperationException("EUDAMED schema provenance mismatch: " + name);
            hashes.Add(name, Hash(bytes));
        }
        if (hashes.Count != resources.Count) throw new InvalidOperationException("Unlisted EUDAMED schema resource.");
        SchemaFingerprint = Canonical.Hash(hashes);
        const string elements = "data/Entity/Vigilance/v1/vigilance-elements.xsd";
        resources[elements] = DotNetKeyScopes(resources[elements]);
        RuntimeSchemaFingerprint = Canonical.Hash(new { EudamedContracts.SchemaRuntime, SchemaFingerprint, Elements = Hash(resources[elements]) });
        var resolver = new EmbeddedResolver(resources);
        schemas = new XmlSchemaSet { XmlResolver = resolver };
        foreach (var path in new[] { "data/Entity/Vigilance/vigilance.xsd",
            "data/Entity/Vigilance/v1/vigilance-commons.xsd", "data/Entity/Vigilance/v1/vigilance-elements.xsd" })
        {
            using var stream = new MemoryStream(resources[path], writable: false);
            using var reader = XmlReader.Create(stream, SafeReader(), "cohors-contract:///" + path);
            schemas.Add(null, reader);
        }
        schemas.Compile();
        var enumHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[] { "provenance.json", "additional-provenance.json" })
        {
            using var provenance = JsonDocument.Parse(Read(assembly, "Cohors.Eudamed.Enum." + file));
            if (!provenance.RootElement.GetProperty("success").GetBoolean())
                throw new InvalidOperationException("Incomplete EUDAMED enumeration retrieval.");
            foreach (var item in provenance.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                var bytes = Read(assembly, "Cohors.Eudamed.Enum." + item.GetProperty("file").GetString());
                if (Hash(bytes) != item.GetProperty("sha256").GetString())
                    throw new InvalidOperationException("EUDAMED enumeration provenance mismatch: " + name);
                using var values = JsonDocument.Parse(bytes);
                enumerations.Add(name, values.RootElement.EnumerateArray().Where(v => v.GetProperty("active").GetBoolean())
                    .Select(v => v.GetProperty("value").GetString()!).ToHashSet(StringComparer.Ordinal));
                enumHashes.Add(name, Hash(bytes));
            }
        }
        EnumerationFingerprint = Canonical.Hash(enumHashes);
    }

    internal string[] Validate(string xml, EudamedDossierKind kind)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(xml) || Encoding.UTF8.GetByteCount(xml) > 2 * 1024 * 1024)
            return ["EUDAMED XML fehlt oder ueberschreitet die lokale2MiB-Grenze."];
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), SafeReader());
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var root = document.Root;
            var data = root?.Element(Base + "Data")?.Elements().ToArray() ?? [];
            if (root is null || root.Name != Base + "Dossier" || data.Length != 1 || data[0].Name != V1 + TypeName(kind))
                return ["Falscher Dossier-Root oder Dossiertyp; keine beliebige XSD-gueltige Nachricht."];
            document.Validate(schemas, (_, args) =>
            {
                if (errors.Count < 32) errors.Add("XSD: " + args.Message);
            }, addSchemaInfo: true);
            if (errors.Count != 0) return errors.ToArray();
            foreach (var element in root.DescendantsAndSelf())
            {
                var info = element.GetSchemaInfo();
                if (info?.SchemaElement?.Annotation is { } annotation && annotation.Items.OfType<XmlSchemaDocumentation>()
                    .Any(d => d.Markup?.Any(m => m is not null && m.InnerText.Contains("Output only", StringComparison.OrdinalIgnoreCase)) == true))
                    errors.Add("Output-only-Feld darf nicht vom Hersteller gesetzt werden: " + element.Name.LocalName);
                var name = info?.SchemaType?.Name;
                if (name?.StartsWith("ENUM_", StringComparison.Ordinal) == true)
                {
                    if (name.EndsWith("_ORDINAL", StringComparison.Ordinal)) name = name[..^"_ORDINAL".Length];
                    if (!enumerations.TryGetValue(name, out var values))
                        errors.Add("Keine lokal gebundene Werteliste fuer " + name + ".");
                    else if (!values.Contains(element.Value))
                        errors.Add("Wert ausserhalb des gebundenen aktiven Katalogs " + name + " bei " + element.Name.LocalName + ".");
                }
                if (element.Attributes().Any(a => a.Name == Xsi + "schemaLocation" || a.Name == Xsi + "noNamespaceSchemaLocation"))
                    errors.Add("Schema-Ort darf nicht durch die Nachricht vorgegeben werden.");
            }
        }
        catch (XmlException exception) { errors.Add("XML nicht sicher lesbar: " + exception.Message); }
        catch (XmlSchemaValidationException exception) { errors.Add("XML-Vertragsfehler: " + exception.Message); }
        return errors.Distinct(StringComparer.Ordinal).Take(32).ToArray();
    }

    internal static string TypeName(EudamedDossierKind kind) => kind switch
    {
        EudamedDossierKind.MIR => "mir_2",
        EudamedDossierKind.FSCA => "fsca_2",
        EudamedDossierKind.MTR => "mtr",
        EudamedDossierKind.PSR => "psr",
        EudamedDossierKind.PSRP => "psrp",
        EudamedDossierKind.PSUR => "psur",
        EudamedDossierKind.FSN => "fsn",
        _ => throw new DomainException("eudamed_kind", "Unbekannter Dossiertyp.")
    };

    private static XmlReaderSettings SafeReader() => new()
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 };

    private static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing pinned EUDAMED resource: " + name);
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] DotNetKeyScopes(byte[] original)
    {
        // .NET cannot resolve these descendant key tables. Hoist only the four pinned,
        // singleton-path keys, preserving selectors, types, uniqueness and every keyref.
        if (Hash(original) != "da52b4d8145b65a441cb741ea86804ed1381e1a937a45edca7dc51cb55f5677c")
            throw new InvalidOperationException("Key-scope projection requires the exact published EUDAMED schema.");
        using var reader = XmlReader.Create(new MemoryStream(original, writable: false), SafeReader());
        var schema = XDocument.Load(reader);
        XNamespace xs = XmlSchema.Namespace;
        var moves = new[]
        {
            ("fsca_2Type", "fsca_2", "affectedCountryKey", "generalInformationSection", "vig:generalInformation/vig:generalInformationSection"),
            ("fsca_2Type", "fsca_2", "generalSubactionDescriptionKey", "subactionsSection", "vig:fscaCorrectiveActionDescription/vig:subactionsSection"),
            ("fsnType", "fsnDeviceSection", "linkedFSCAFormDetailsKey", "refNumbersFSCASection", "vig:refNumbersFSCASection"),
            ("fsnType", "fsn", "affectedCountryKey-1", "deviceSection", "vig:fsnDeviceSection/vig:deviceSection")
        };
        foreach (var (type, targetName, keyName, sourceName, prefix) in moves)
        {
            var definition = schema.Root!.Elements(xs + "complexType").Single(e => (string?)e.Attribute("name") == type);
            var target = definition.Descendants(xs + "element").Single(e => (string?)e.Attribute("name") == targetName);
            var key = definition.Descendants(xs + "key").Single(e => (string?)e.Attribute("name") == keyName);
            if ((string?)key.Parent?.Attribute("name") != sourceName)
                throw new InvalidOperationException("Unexpected source key scope.");
            var selector = key.Element(xs + "selector")!.Attribute("xpath")!;
            selector.Value = prefix + "/" + selector.Value;
            key.Remove();
            target.Add(key);
        }
        return Encoding.UTF8.GetBytes(schema.ToString(SaveOptions.DisableFormatting));
    }

    private sealed class EmbeddedResolver(Dictionary<string, byte[]> resources) : XmlResolver
    {
        public override ICredentials? Credentials { set => throw new NotSupportedException("No XML network credentials."); }
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            if (absoluteUri.Scheme != "cohors-contract" || absoluteUri.Host.Length != 0 || absoluteUri.Query.Length != 0 ||
                absoluteUri.Fragment.Length != 0 || !resources.TryGetValue(absoluteUri.AbsolutePath.TrimStart('/'), out var bytes))
                throw new XmlException("Only locally pinned EUDAMED schema imports are permitted.");
            return new MemoryStream(bytes, writable: false);
        }
    }
}
