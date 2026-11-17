using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Cohors.Tests;

public sealed class DomibusContractTests : IDisposable
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Ws = "http://eu.domibus.wsplugin/";
    private static readonly XNamespace Eb = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/";
    private readonly string userVariable = "COHORS_CONTRACT_USER_" + Guid.NewGuid().ToString("N");
    private readonly string passwordVariable = "COHORS_CONTRACT_PASSWORD_" + Guid.NewGuid().ToString("N");
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("cohors-domibus-contract-");
    private const string TestCredential = "not-a-live-credential";
    private static readonly DomibusProfile Profile = new("stub", "plugin", "v1", "", "", "test-domain",
        "sender-ap", "receiver-ap", "urn:test:party", "sender", "receiver", "test-service", "test-type",
        "test-action", "original-sender", "final-recipient", "");

    public DomibusContractTests()
    {
        Environment.SetEnvironmentVariable(userVariable, "synthetic-plugin-user");
        Environment.SetEnvironmentVariable(passwordVariable, TestCredential);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(userVariable, null);
        Environment.SetEnvironmentVariable(passwordVariable, null);
        directory.Delete(true);
    }

    [Fact]
    public async Task RoleQualifiedOperationsUsePinnedBodyNamesAndAuthentication()
    {
        using var handler = new ContractHandler(request =>
        {
            Assert.Equal("application/soap+xml", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("\"\"", Assert.Single(request.Content.Headers.ContentType.Parameters, p => p.Name == "action").Value);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-plugin-user:" + TestCredential)),
                request.Headers.Authorization?.Parameter);
            return Task.FromResult(Envelope(new XElement(Ws + "getStatusResponse", "ACKNOWLEDGED")));
        });
        using var http = new HttpClient(handler);
        var client = Client(http);
        var result = await client.Status(Profile, "message@test.invalid", "SENDING");
        Assert.Equal("ACKNOWLEDGED", result.Status);
        var body = Assert.Single(handler.Documents).Root!.Element(Soap + "Body")!.Elements().Single();
        Assert.Equal(Ws + "statusRequestWithAccessPointRole", body.Name);
        Assert.Equal("SENDING", body.Element("accessPointRole")!.Value);
        Assert.All(body.Elements(), e => Assert.Equal(XNamespace.None, e.Name.Namespace));
        Assert.DoesNotContain("BusinessAccepted", Encoding.UTF8.GetString(result.OriginalTransport), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrieveNeverDownloadsImplicitlyAndKeepsExactOriginal()
    {
        var expected = Envelope(new XElement(Ws + "retrieveMessageResponse", Payload("cid:reply", [0, 0xff, 1])),
            Header("reply@test.invalid", "conversation", "request@test.invalid"));
        using var handler = new ContractHandler(_ => Task.FromResult(expected));
        using var http = new HttpClient(handler);
        var result = await Client(http).Retrieve(Profile, "reply@test.invalid");
        Assert.Equal(new byte[] { 0, 0xff, 1 }, Assert.Single(result.Payloads).Bytes);
        Assert.Equal("request@test.invalid", result.RefToMessageId);
        var request = Assert.Single(handler.Documents).Root!.Element(Soap + "Body")!.Elements().Single();
        Assert.Equal(Ws + "retrieveMessageRequest", request.Name);
        Assert.Equal("false", request.Attribute("markAsDownloaded")!.Value);
        Assert.Null(request.Element("markAsDownloaded"));
        Assert.Equal(handler.ResponseBytes, result.OriginalTransport);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MtomPayloadIsResolvedByCidWithoutLosingTransportBytes()
    {
        var xml = Envelope(new XElement(Ws + "retrieveMessageResponse",
            new XElement("payload", new XAttribute("payloadId", "cid:reply"), new XAttribute("contentType", "application/xml"),
                new XElement("value", new XElement(XName.Get("Include", "http://www.w3.org/2004/08/xop/include"), new XAttribute("href", "cid:binary"))))),
            Header("reply@test.invalid", "conversation", "request@test.invalid"));
        var wire = "--synthetic-boundary\r\nContent-Type: application/xop+xml; charset=utf-8; type=\"application/soap+xml\"\r\nContent-ID: <root>\r\n\r\n" +
            xml + "\r\n--synthetic-boundary\r\nContent-Type: application/octet-stream\r\nContent-ID: <binary>\r\n\r\n<synthetic/>\r\n--synthetic-boundary--\r\n";
        using var handler = new ContractHandler(_ => Task.FromResult(wire))
        { ContentType = "multipart/related; boundary=\"synthetic-boundary\"; start=\"<root>\"; type=\"application/xop+xml\"" };
        using var http = new HttpClient(handler);
        var result = await Client(http).Retrieve(Profile, "reply@test.invalid");
        Assert.Equal("<synthetic/>", Encoding.UTF8.GetString(Assert.Single(result.Payloads).Bytes));
        Assert.Equal(Encoding.UTF8.GetBytes(wire), result.OriginalTransport);
    }

    [Fact]
    public async Task SubmitPreservesCallerMessageIdAndDoesNotInventPluginTimestamp()
    {
        using var handler = new ContractHandler(_ => Task.FromResult(Envelope(new XElement(Ws + "submitResponse",
            new XElement("messageID", "request@test.invalid"), new XElement("messageEntityID", 23)))));
        using var http = new HttpClient(handler);
        var result = await Client(http).Submit(Profile, new("request@test.invalid", "conversation", null,
            [new("cid:body", "application/xml", Encoding.UTF8.GetBytes("<synthetic/>"))]));
        Assert.Equal("request@test.invalid", result.MessageId);
        Assert.Equal(23, result.MessageEntityId);
        var request = Assert.Single(handler.Documents);
        Assert.Empty(request.Descendants(Eb + "Timestamp"));
        Assert.Equal("request@test.invalid", Assert.Single(request.Descendants(Eb + "MessageId")).Value);
        Assert.Equal("cid:body", Assert.Single(request.Descendants(Eb + "PartInfo")).Attribute("href")!.Value);
        Assert.Equal("<synthetic/>", Encoding.UTF8.GetString(Convert.FromBase64String(Assert.Single(request.Descendants("value")).Value)));
    }

    [Fact]
    public async Task ExplicitDownloadAndErrorQueryUseActualContractWithoutFaultTextLeakage()
    {
        using var handler = new ContractHandler(_ => Task.FromResult(Envelope(new XElement(Ws + "markMessageAsDownloadedResponse",
            new XElement("messageID", "reply@test.invalid"), new XElement("messageEntityID", 23)))));
        using var http = new HttpClient(handler);
        var client = Client(http);
        await client.ConfirmDownload(Profile, "reply@test.invalid");
        Assert.Equal(Ws + "markMessageAsDownloadedRequest", handler.Documents[0].Root!.Element(Soap + "Body")!.Elements().Single().Name);
        handler.Reply = _ => Task.FromResult(Envelope(new XElement(Ws + "getMessageErrorsResponse",
            new XElement("item", new XElement("domibusErrorCode", "EBMS_0001"),
                new XElement("errorDetail", TestCredential), new XElement("messageInErrorId", "reply@test.invalid"),
                new XElement("mshRole", "RECEIVING")))));
        var errors = await client.Errors(Profile, "reply@test.invalid", "RECEIVING");
        Assert.Equal("EBMS_0001", Assert.Single(errors.Errors).Code);
        Assert.DoesNotContain(TestCredential, Canonical.Serialize(errors.Errors), StringComparison.Ordinal);
        Assert.Contains(TestCredential, Encoding.UTF8.GetString(errors.OriginalTransport), StringComparison.Ordinal);
        Assert.Equal(Ws + "getErrorsRequestWithAccessPointRole", handler.Documents[1].Root!.Element(Soap + "Body")!.Elements().Single().Name);
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///never-read'>]><x>&x;</x>", "invalid_response")]
    [InlineData("<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope'><s:Body><wrong/></s:Body></s:Envelope>", "response_contract")]
    public async Task InvalidXmlIsNotAcceptedOrRetried(string response, string code)
    {
        using var handler = new ContractHandler(_ => Task.FromResult(response));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomibusException>(() => Client(http).Status(Profile, "request@test.invalid", "SENDING"));
        Assert.Equal(code, error.Code);
        Assert.False(error.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UncertainSubmitAndDownloadDoNotRetryAndDoNotExposeInnerErrors()
    {
        using var handler = new ContractHandler(_ => throw new HttpRequestException(TestCredential));
        using var http = new HttpClient(handler);
        var client = Client(http);
        var submit = await Assert.ThrowsAsync<DomibusException>(() => client.Submit(Profile,
            new("request@test.invalid", "conversation", null, [new("cid:body", "application/xml", [1])])));
        var download = await Assert.ThrowsAsync<DomibusException>(() => client.ConfirmDownload(Profile, "reply@test.invalid"));
        Assert.True(submit.OutcomeUnknown);
        Assert.True(download.OutcomeUnknown);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain(TestCredential, submit.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestCredential, download.ToString(), StringComparison.Ordinal);
        Assert.Equal(nameof(HttpRequestException), submit.CauseType);
    }

    [Fact]
    public async Task UnapprovedEndpointOrMissingCredentialProducesZeroRequests()
    {
        using var handler = new ContractHandler(_ => throw new InvalidOperationException("No network call expected."));
        using var http = new HttpClient(handler);
        var client = Client(http);
        await Assert.ThrowsAsync<DomibusException>(() => client.Check(Profile with { EndpointReference = "unapproved" }));
        Environment.SetEnvironmentVariable(passwordVariable, null);
        await Assert.ThrowsAsync<DomibusException>(() => client.Check(Profile));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ProtectedTransportOriginalIsReversibleOnlyWithItsSeparatePurpose()
    {
        var protection = new EphemeralDataProtectionProvider();
        var objects = new LocalObjectStore(directory.FullName);
        var original = Encoding.UTF8.GetBytes("Synthetic transport containing " + TestCredential);
        var hash = await new ProtectedTransportEvidence(objects, protection).Protect(original);
        var protectedBytes = await objects.ReadBytes(hash);
        Assert.NotEqual(original, protectedBytes);
        Assert.Equal(original, protection.CreateProtector(ProtectedTransportEvidence.Purpose).Unprotect(protectedBytes));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            protection.CreateProtector("NotTheTransportPurpose").Unprotect(protectedBytes));
    }

    private DomibusGateway Client(HttpClient http) => new(http,
        new([new("stub", "https://domibus.invalid/wsplugin", true, "BackendStub")],
            [new("plugin", "v1", userVariable, passwordVariable)], true));

    private static XElement Payload(string id, byte[] bytes) => new("payload", new XAttribute("payloadId", id),
        new XAttribute("contentType", "application/xml"), new XElement("value", Convert.ToBase64String(bytes)));
    private static string Envelope(XElement body, XElement? header = null) =>
        new XDocument(new XElement(Soap + "Envelope", new XElement(Soap + "Header", header),
            new XElement(Soap + "Body", body))).ToString(SaveOptions.DisableFormatting);
    private static XElement Header(string id, string conversation, string reference) =>
        new(Eb + "Messaging", new XElement(Eb + "UserMessage",
            new XElement(Eb + "MessageInfo", new XElement(Eb + "MessageId", id), new XElement(Eb + "RefToMessageId", reference)),
            new XElement(Eb + "PartyInfo",
                new XElement(Eb + "From", new XElement(Eb + "PartyId", "receiver-ap"), new XElement(Eb + "Role", "receiver")),
                new XElement(Eb + "To", new XElement(Eb + "PartyId", "sender-ap"), new XElement(Eb + "Role", "sender"))),
            new XElement(Eb + "CollaborationInfo", new XElement(Eb + "Service", "test-service"),
                new XElement(Eb + "Action", "test-action"), new XElement(Eb + "ConversationId", conversation)),
            new XElement(Eb + "PayloadInfo", new XElement(Eb + "PartInfo", new XAttribute("href", "cid:reply")))));

    private sealed class ContractHandler(Func<HttpRequestMessage, Task<string>> reply) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<string>> Reply { get; set; } = reply;
        public string ContentType { get; init; } = "application/soap+xml";
        public int Calls { get; private set; }
        public List<XDocument> Documents { get; } = [];
        public byte[]? ResponseBytes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Documents.Add(XDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            ResponseBytes = Encoding.UTF8.GetBytes(await Reply(request));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ResponseBytes) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
            return response;
        }
    }
}
