using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Cohors.Infrastructure;

public sealed record DomibusEndpoint(string Id, string Uri, bool LocalContractTest, string EvidenceLevel);
public sealed record DomibusSecretReference(string Id, string Version, string UserNameVariable, string PasswordVariable);
public sealed record DomibusGatewayOptions(DomibusEndpoint[] Endpoints, DomibusSecretReference[] Secrets, bool LocalAllowed)
{
    public DomibusReferenceChoice[] ModuleKeys { get; init; } = [];
    public string[] OnboardingEvidence { get; init; } = [];
}

public sealed class ProtectedTransportEvidence(IObjectStore objects, IDataProtectionProvider protection) : ITransportEvidenceStore
{
    public const string Purpose = "Cohors.Domibus.TransportEvidence.v1";
    private readonly IDataProtector protector = protection.CreateProtector(Purpose);
    public Task<string> Protect(ReadOnlyMemory<byte> original, CancellationToken ct = default) =>
        objects.PutBytes(protector.Protect(original.ToArray()), ct);
}

public sealed class DomibusGateway(HttpClient http, DomibusGatewayOptions options) : IDomibusGateway
{
    private const int MaxTransportBytes = 4 * 1024 * 1024;
    private const int MaxPayloads = 16;
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Ws = "http://eu.domibus.wsplugin/";
    private static readonly XNamespace Eb = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/";
    private static readonly XNamespace Xop = "http://www.w3.org/2004/08/xop/include";
    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string[] AllowedEndpointReferences => options.Endpoints.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToArray();
    public DomibusReferenceCatalog ReferenceCatalog => new(AllowedEndpointReferences,
        options.Secrets.Select(s => new DomibusReferenceChoice(s.Id, s.Version)).Distinct().ToArray(),
        options.ModuleKeys, options.OnboardingEvidence);

    public DomibusAvailability Availability(DomibusProfile profile)
    {
        var errors = new List<string>();
        var endpoint = options.Endpoints.SingleOrDefault(e => e.Id == profile.EndpointReference);
        if (endpoint is null) errors.Add("Backend-Endpunkt ist nicht in der betreiberseitigen Allowlist.");
        else if (!Uri.TryCreate(endpoint.Uri, UriKind.Absolute, out var address) || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0 ||
            address.Scheme != "https" && !(options.LocalAllowed && endpoint.LocalContractTest && address.IsLoopback && address.Scheme == "http"))
            errors.Add("Backend-Endpunkt verlangt HTTPS ohne Benutzerinfo/Query; HTTP ist nur fuer einen expliziten lokalen Vertragstest erlaubt.");
        var secret = options.Secrets.SingleOrDefault(s => s.Id == profile.SecretReference && s.Version == profile.SecretVersion);
        if (secret is null || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(secret.UserNameVariable)) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(secret.PasswordVariable)))
            errors.Add("Versionierte Plugin-Benutzer-/Passwortreferenz ist serverseitig nicht verfuegbar. EU Login ist kein Pluginzugang.");
        if (profile.Domain is null || profile.Domain.Length > 120 || profile.Domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            errors.Add("Ungueltige Domibus-Domaenenkennung.");
        return new(errors.Count == 0, errors.ToArray(), endpoint is null ? "None" : endpoint.LocalContractTest ? "BackendStub" : "BackendConnectionOnly");
    }

    public async Task Check(DomibusProfile profile, CancellationToken ct = default) =>
        _ = await Call(profile, new XElement(Ws + "listPendingMessagesRequest",
            new XElement("messageId", "cohors-connectivity-probe")), "listPendingMessagesResponse", null, false, ct);

    public async Task<DomibusSubmitReceipt> Submit(DomibusProfile profile, DomibusMessage message, CancellationToken ct = default)
    {
        RequireId(message.MessageId);
        RequireId(message.ConversationId);
        Guard.Against(message.Payloads.Length is < 1 or > MaxPayloads ||
            message.Payloads.Sum(p => (long)p.Bytes.Length) > MaxTransportBytes / 2 ||
            message.Payloads.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != message.Payloads.Length,
            "domibus_payload", "Ein bis16 eindeutige, insgesamt hoechstens2MiB grosse Payloads erforderlich.");
        foreach (var payload in message.Payloads)
        {
            RequireId(payload.Id);
            Guard.Against(payload.ContentType is not ("application/xml" or "application/zip" or "application/pdf" or "text/plain"),
                "domibus_content_type", "Nicht zugelassener Payloadtyp.");
        }
        var info = new XElement(Eb + "MessageInfo", new XElement(Eb + "MessageId", message.MessageId));
        if (message.RefToMessageId is { } reference) { RequireId(reference); info.Add(new XElement(Eb + "RefToMessageId", reference)); }
        var header = new XElement(Eb + "Messaging", new XElement(Eb + "UserMessage", info,
            new XElement(Eb + "PartyInfo",
                Party("From", profile.AccessPointPartyId, profile.FromRole, profile.PartyIdType),
                Party("To", profile.ReceiverPartyId, profile.ToRole, profile.PartyIdType)),
            new XElement(Eb + "CollaborationInfo",
                new XElement(Eb + "Service", new XAttribute("type", profile.ServiceType), profile.Service),
                new XElement(Eb + "Action", profile.Action), new XElement(Eb + "ConversationId", message.ConversationId)),
            new XElement(Eb + "MessageProperties",
                new XElement(Eb + "Property", new XAttribute("name", "originalSender"), profile.OriginalSender),
                new XElement(Eb + "Property", new XAttribute("name", "finalRecipient"), profile.FinalRecipient)),
            new XElement(Eb + "PayloadInfo", message.Payloads.Select(p =>
                new XElement(Eb + "PartInfo", new XAttribute("href", p.Id),
                    new XElement(Eb + "PartProperties", new XElement(Eb + "Property", new XAttribute("name", "MimeType"), p.ContentType)))))));
        var body = new XElement(Ws + "submitRequest", message.Payloads.Select(p => new XElement("payload",
            new XAttribute("payloadId", p.Id), new XAttribute("contentType", p.ContentType),
            new XElement("value", Convert.ToBase64String(p.Bytes)))));
        var response = await Call(profile, body, "submitResponse", header, true, ct);
        var ids = response.Body.Elements("messageID").Select(e => e.Value).ToArray();
        if (ids.Length != 1 || ids[0] != message.MessageId) throw new DomibusException("submission_correlation", true);
        var entity = response.Body.Elements("messageEntityID").SingleOrDefault()?.Value;
        if (entity is not null && !long.TryParse(entity, out _)) throw new DomibusException("submission_entity", true);
        return new(ids[0], entity is null ? null : long.Parse(entity, System.Globalization.CultureInfo.InvariantCulture), response.Raw);
    }

    public async Task<DomibusStatusResult> Status(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default)
    {
        var response = await Call(profile, RoleRequest("statusRequestWithAccessPointRole", messageId, accessPointRole),
            "getStatusResponse", null, false, ct);
        if (string.IsNullOrWhiteSpace(response.Body.Value)) throw new DomibusException("empty_status", false, originalTransport: response.Raw);
        return new(response.Body.Value, response.Raw);
    }

    public async Task<DomibusErrorsResult> Errors(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default)
    {
        var response = await Call(profile, RoleRequest("getErrorsRequestWithAccessPointRole", messageId, accessPointRole),
            "getMessageErrorsResponse", null, false, ct);
        return new(response.Body.Elements("item").Select(e => new DomibusError(e.Element("domibusErrorCode")?.Value,
            e.Element("messageInErrorId")?.Value, e.Element("mshRole")?.Value)).ToArray(), response.Raw);
    }

    public async Task<string[]> Pending(DomibusProfile profile, CancellationToken ct = default)
    {
        var response = await Call(profile, new XElement(Ws + "listPendingMessagesRequest",
            new XElement("fromPartyId", profile.ReceiverPartyId)), "listPendingMessagesResponse", null, false, ct);
        var ids = response.Body.Elements("messageID").Select(e => e.Value).ToArray();
        if (ids.Length > 1000 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new DomibusException("pending_limit_or_duplicates", false);
        foreach (var id in ids) RequireId(id);
        return ids.Take(50).ToArray();
    }

    public async Task<DomibusRetrievedMessage> Retrieve(DomibusProfile profile, string messageId, CancellationToken ct = default)
    {
        RequireId(messageId);
        var response = await Call(profile, new XElement(Ws + "retrieveMessageRequest",
            new XAttribute("markAsDownloaded", "false"), new XElement("messageID", messageId)),
            "retrieveMessageResponse", null, false, ct);
        var user = response.Document.Root?.Element(Soap + "Header")?.Element(Eb + "Messaging")?.Element(Eb + "UserMessage")
            ?? throw new DomibusException("message_header_missing", false);
        var receivedId = user.Element(Eb + "MessageInfo")?.Element(Eb + "MessageId")?.Value;
        var conversation = user.Element(Eb + "CollaborationInfo")?.Element(Eb + "ConversationId")?.Value;
        var from = user.Element(Eb + "PartyInfo")?.Element(Eb + "From")?.Element(Eb + "PartyId")?.Value;
        var to = user.Element(Eb + "PartyInfo")?.Element(Eb + "To")?.Element(Eb + "PartyId")?.Value;
        if (receivedId != messageId || string.IsNullOrWhiteSpace(conversation) || from != profile.ReceiverPartyId || to != profile.AccessPointPartyId)
            throw new DomibusException("message_correlation_or_party", false);
        var payloads = response.Body.Elements().Select(p => new DomibusPayload(
            p.Attribute("payloadId")?.Value ?? "", p.Attribute("contentType")?.Value ?? "application/octet-stream",
            Convert.FromBase64String(p.Element("value")?.Value ?? ""))).ToArray();
        if (payloads.Length is < 1 or > MaxPayloads || payloads.Any(p => string.IsNullOrWhiteSpace(p.Id)) ||
            payloads.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != payloads.Length)
            throw new DomibusException("message_payload_binding", false);
        var parts = user.Element(Eb + "PayloadInfo")?.Elements(Eb + "PartInfo").Select(p => p.Attribute("href")?.Value).ToArray() ?? [];
        if (parts.Length != payloads.Length || payloads.Any(p => !parts.Contains(p.Id, StringComparer.Ordinal)))
            throw new DomibusException("message_payload_manifest", false);
        return new(messageId, conversation, user.Element(Eb + "MessageInfo")?.Element(Eb + "RefToMessageId")?.Value,
            from!, to!, payloads, response.Raw);
    }

    public async Task<byte[]> ConfirmDownload(DomibusProfile profile, string messageId, CancellationToken ct = default)
    {
        RequireId(messageId);
        var response = await Call(profile, new XElement(Ws + "markMessageAsDownloadedRequest", new XElement("messageID", messageId)),
            "markMessageAsDownloadedResponse", null, true, ct);
        if (response.Body.Element("messageID")?.Value != messageId) throw new DomibusException("download_correlation", true);
        return response.Raw;
    }

    private async Task<(XDocument Document, XElement Body, byte[] Raw)> Call(DomibusProfile profile, XElement body,
        string expectedResponse, XElement? header, bool changesRemote, CancellationToken ct)
    {
        if (!Availability(profile).Configured) throw new DomibusException("configuration_missing", false);
        var endpoint = options.Endpoints.Single(e => e.Id == profile.EndpointReference);
        var secret = options.Secrets.Single(s => s.Id == profile.SecretReference && s.Version == profile.SecretVersion);
        var document = new XDocument(new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XElement(Soap + "Header", header), new XElement(Soap + "Body", body)));
        Validate(document, outcomeUnknown: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Uri)
        {
            Content = new ByteArrayContent(Utf8.GetBytes(document.ToString(SaveOptions.DisableFormatting)))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", "\"\""));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Utf8.GetBytes(
            Environment.GetEnvironmentVariable(secret.UserNameVariable) + ":" + Environment.GetEnvironmentVariable(secret.PasswordVariable))));
        if (profile.Domain.Length != 0) request.Headers.Add("Domain", profile.Domain);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        byte[]? original = null;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400) throw new DomibusException("redirect_forbidden", changesRemote);
            if (response.Content.Headers.ContentLength > MaxTransportBytes) throw new DomibusException("response_limit", changesRemote);
            var raw = await ReadLimited(await response.Content.ReadAsStreamAsync(timeout.Token), MaxTransportBytes, timeout.Token);
            original = raw;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new DomibusException("authentication_or_authorization", false, originalTransport: raw);
            var parsed = await Parse(raw, response.Content.Headers.ContentType, timeout.Token);
            var responseBody = parsed.Root?.Element(Soap + "Body");
            if (responseBody?.Element(Soap + "Fault") is not null)
                throw new DomibusException("soap_fault", changesRemote);
            if (!response.IsSuccessStatusCode) throw new DomibusException("http_failure", changesRemote);
            if (parsed.Root?.Name != Soap + "Envelope" || responseBody?.Elements().Count() != 1 ||
                responseBody.Element(Ws + expectedResponse) is not { } result)
                throw new DomibusException("response_contract", changesRemote);
            Validate(parsed, changesRemote);
            return (parsed, result, raw);
        }
        catch (DomibusException exception) when (exception.OriginalTransport is null && original is not null)
        { throw new DomibusException(exception.Code, exception.OutcomeUnknown, exception, original); }
        catch (OperationCanceledException exception) { throw new DomibusException("timeout_or_cancelled", changesRemote, exception, original); }
        catch (HttpRequestException exception) { throw new DomibusException("connection_failure", changesRemote, exception, original); }
        catch (IOException exception) { throw new DomibusException("incomplete_response", changesRemote, exception, original); }
        catch (Exception exception) when (exception is XmlException or FormatException or InvalidDataException or DecoderFallbackException)
        { throw new DomibusException("invalid_response", changesRemote, exception, original); }
    }

    private static async Task<XDocument> Parse(byte[] raw, MediaTypeHeaderValue? contentType, CancellationToken ct)
    {
        byte[] xml = raw;
        var attachments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (contentType?.MediaType == "multipart/related")
        {
            var boundary = contentType.Parameters.SingleOrDefault(p => p.Name == "boundary")?.Value?.Trim('"');
            var start = contentType.Parameters.SingleOrDefault(p => p.Name == "start")?.Value?.Trim('"', '<', '>');
            if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 200) throw new InvalidDataException("Invalid MIME boundary.");
            var reader = new MultipartReader(boundary, new MemoryStream(raw))
            { HeadersCountLimit = 16, HeadersLengthLimit = 8192, BodyLengthLimit = MaxTransportBytes };
            byte[]? root = null;
            var count = 0;
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (++count > MaxPayloads + 1) throw new InvalidDataException("Too many MIME parts.");
                var id = section.Headers?["Content-ID"].ToString().Trim('<', '>') ?? "";
                var bytes = await ReadLimited(section.Body, MaxTransportBytes, ct);
                if (id.Length == 0 || !attachments.TryAdd(id, bytes)) throw new InvalidDataException("Missing or repeated MIME identity.");
                if (start is null && count == 1 || id == start) root = bytes;
            }
            xml = root ?? throw new InvalidDataException("Missing MIME root.");
        }
        else if (contentType?.MediaType is not ("application/soap+xml" or "application/xml"))
            throw new InvalidDataException("Unsupported SOAP content type.");
        using var xmlReader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxTransportBytes, IgnoreProcessingInstructions = true });
        var document = XDocument.Load(xmlReader);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var include in document.Descendants(Xop + "Include").ToArray())
        {
            var href = include.Attribute("href")?.Value;
            if (include.Parent?.Name != "value" || include.Parent.Elements().Count() != 1 ||
                href?.StartsWith("cid:", StringComparison.Ordinal) != true ||
                !attachments.TryGetValue(href[4..], out var bytes) || !used.Add(href[4..]))
                throw new InvalidDataException("Unbound XOP payload.");
            include.ReplaceWith(new XText(Convert.ToBase64String(bytes)));
        }
        if (attachments.Count != 0 && attachments.Count != used.Count + 1)
            throw new InvalidDataException("Unreferenced MIME payload.");
        return document;
    }

    private static async Task<byte[]> ReadLimited(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Response limit exceeded.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static XElement RoleRequest(string name, string messageId, string role)
    {
        RequireId(messageId);
        Guard.Against(role is not ("SENDING" or "RECEIVING"), "domibus_role", "Explizite AccessPointRole erforderlich.");
        return new(Ws + name, new XElement("messageID", messageId), new XElement("accessPointRole", role));
    }

    private static XElement Party(string name, string id, string role, string type) =>
        new(Eb + name, new XElement(Eb + "PartyId", new XAttribute("type", type), id), new XElement(Eb + "Role", role));

    private static void RequireId(string value) =>
        Guard.Against(string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Any(char.IsControl),
            "domibus_message_id", "Explizite, begrenzte Nachrichten-/Payloadkennung erforderlich.");

    private static void Validate(XDocument document, bool outcomeUnknown)
    {
        var invalid = false;
        document.Validate(Schemas.Value, (_, _) => invalid = true);
        if (invalid) throw new DomibusException("schema_contract", outcomeUnknown);
    }

    private static XmlSchemaSet LoadSchemas()
    {
        var assembly = typeof(DomibusGateway).Assembly;
        using var manifestStream = assembly.GetManifestResourceStream("Cohors.Domibus.provenance.json")
            ?? throw new InvalidDataException("Pinned Domibus provenance missing.");
        using var manifest = System.Text.Json.JsonDocument.Parse(manifestStream);
        var schemas = new XmlSchemaSet { XmlResolver = null };
        foreach (var name in new[] { "xml.xsd", "xmlmime.xsd", "envelope.xsd", "webservicePlugin-header.xsd", "webservicePlugin-body.xsd", "WebServicePlugin.wsdl" })
        {
            using var source = assembly.GetManifestResourceStream("Cohors.Domibus." + name)
                ?? throw new InvalidDataException("Pinned Domibus contract missing.");
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            var expected = manifest.RootElement.GetProperty("files").EnumerateArray().Single(e => e.GetProperty("file").GetString() == name)
                .GetProperty("sha256").GetString();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())) != expected ||
                name == "WebServicePlugin.wsdl" && expected != EudamedContracts.WsdlSha256)
                throw new InvalidDataException("Pinned Domibus contract changed.");
            if (!name.EndsWith(".xsd", StringComparison.Ordinal)) continue;
            bytes.Position = 0;
            using var reader = XmlReader.Create(bytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            schemas.Add(null, reader);
        }
        schemas.Compile();
        return schemas;
    }
}
