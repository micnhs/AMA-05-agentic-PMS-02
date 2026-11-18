# Fabric C1: bounded MCP probe client

Status: **SOURCE IMPLEMENTATION / native cases not executed**. General cost
approval and the bounded C1 execution request were received. Concrete private
targets, identities/grants, protocol observation, model/budget/retention and
cleanup bindings are still required; their IDs or permissions are not inferred
from those approvals. No resources, credentials or model calls are created by
the credential-free validation job. Historical blocked runs remain historical.

## Explicit C1 model-contract correction (v2, 2026-11-18)

The first-deployment coordination review found that `FabricC1Entry.Host` calls
`InvokeDirect`, which invokes the Fabric MCP transport directly. Neither path
calls a Foundry model. In v1, `runtime.modelDeployment` and `modelVersion` were
only nonempty-string checks, not implemented model dependencies. Creating an
unused deployment or filling those fields with invented versions is prohibited.

`cohors-fabric-c1-preparation-2` therefore binds
`runtime.aiExecution=FabricManagedDataAgent`. Its generative AI runs inside the
Fabric data agent; the Hosted container is the deterministic, bounded MCP
caller. Microsoft's [data-agent concepts](https://learn.microsoft.com/fabric/data-science/concept-data-agent)
describe that managed LLM and explicitly say it cannot be changed by the caller.
No exact internal model version is claimed from an MCP server version, agent
definition, model name in documentation, or successful response.

The two historical model fields stay `null` in v2. A new required
`runtime.fabricAiPolicyReviewSha256` binds real effective Fabric AI processing,
storage/cross-geo settings and their approved geographic/data-handling limits.
This supplements, not replaces, the existing data-handling/retention, private
path, budget and source/ontology reviews. If policy or service behavior requires
unapproved cross-geo processing, the stage is blocked: do not enable it or move
regions automatically. The C1 BOM contains no separate Foundry model deployment.

This is an explicit technical correction to the R4.1 mapping: its model
deployment output and model-version start value become inactive historical
fields, and one real AI-policy review is added. There are 39 active mappings:
8 start, 17 output, 3 discovery and 11 review, plus the fixed AI-execution value.
All other bindings, the unchanged five-row fixture/three questions,
seven native cases and acceptance thresholds remain. v1 is still readable for
offline history with unchanged canonical serialization, but cannot authorize a
new service run. Build fresh v2 preparation/execution hashes and checkpoints;
never resume an old checkpoint under the new contract. Migration cannot reset
already consumed request budgets or extend approved deadlines.

## Deliverable and exact boundary

| File | Use |
| --- | --- |
| [`inputs.template.json`](../../fabric/c1/inputs.template.json) | v2 machine-readable binding; null active fields need generated/observed/reviewed values; the two unused historical model fields must remain null |
| [`synthetic-source.json`](../../fabric/c1/synthetic-source.json) | Five synthetic rows, three fixed questions and an independent expected/forbidden-row oracle |
| `src/Cohors.Infrastructure/FabricC1ProbeClient.cs` | Network-free .NET preparation kernel: preflight, discovery/request recipes, exact tool/schema checks and bound task resume/cancel intents |
| [`execution.template.json`](../../fabric/c1/execution.template.json) | Private execution envelope: embed the filled preparation, bind expiry/access evidence, split request budgets, private addresses and exact Hosted session/caller |
| `src/Cohors.Infrastructure/FabricC1Execution.cs`, `FabricC1Transport.cs`, `FabricC1HostedClient.cs` | Explicit app credentials, bounded JSON/HTTP/SSE transport, durable original-request/task state and Hosted invocation |
| `src/Cohors.Api/FabricC1Entry.cs` | Opt-in `--fabric-c1` CLI or Hosted Invocations server; entered before normal API configuration/DI |
| `tests/Cohors.Tests/FabricC1PreparationTests.cs`, `FabricC1TransportTests.cs` | In-process checks with synthetic HTTP handlers, not real HTTP listeners or services |

The offline `Discovery` prepares the MCP `initialize`, `notifications/initialized` and
`tools/list` sequence. These are **not a batch to send without replies**: the
executable transport validates the negotiated protocol/server response.
`Call` accepts only the reviewed single-tool name/schema and a question from the
hash-bound fixture. It never picks the first available tool or argument.
`TaskAction` retains the original identity/configuration, query hash and task ID;
it returns an intent, not an implemented task wire protocol or cancellation proof.

The historical kernel is unchanged: `PreparedOffline` means complete *input
shape*, never authentic approval or service evidence; `CloudExecutionAllowed`
remains false. A separate execution envelope is required by the executable.
Normal API, persistence, agent and IQ registrations are unchanged.

The transport uses the existing .NET 10 `HttpClient`, framework `SseParser`
and locked `Azure.Identity` 1.21.0 libraries, not a new SDK dependency or a
hand-written SSE/credential parser. The earlier recommendation to add an MCP
SDK is replaced by this narrow implementation of the documented
`2025-11-25` Streamable HTTP protocol. Fabric explicitly documents this option.
It is not a general MCP SDK: no legacy SSE endpoint fallback, sampling,
elicitation, interactive authentication or automatic protocol downgrade.
The Hosted side implements the documented pass-through `invocations/2.0.0`
contract with existing ASP.NET Core rather than adding an AgentServer adapter
package. That wire implementation still requires native platform acceptance.
The automatic Foundry skill dependency script remains unexecuted because it
can install global `azd` extensions on the shared host.

## Executable entry and private run binding

The existing API executable has an explicit, mutually exclusive C1 mode.
Without that flag, application behavior and registrations are unchanged.
For example, from an allocated private runtime with approved files:

```powershell
dotnet Cohors.Api.dll --fabric-c1 check --preparation <preparation.json>
dotnet Cohors.Api.dll --fabric-c1 discover --inputs <execution.json> --fixture <synthetic-source.json> --state <absolute-private-checkpoint.json>
dotnet Cohors.Api.dll --fabric-c1 query --inputs <execution.json> --fixture <synthetic-source.json> --state <absolute-private-checkpoint.json> --probe allowed
dotnet Cohors.Api.dll --fabric-c1 resume --inputs <execution.json> --fixture <synthetic-source.json> --state <absolute-private-checkpoint.json>
dotnet Cohors.Api.dll --fabric-c1 cancel --inputs <execution.json> --fixture <synthetic-source.json> --state <absolute-private-checkpoint.json>
dotnet Cohors.Api.dll --fabric-c1 close --inputs <execution.json> --fixture <synthetic-source.json> --state <absolute-private-checkpoint.json>
```

These are instructions, not executed service evidence. The build job runs only
`check` against the null-bound public preparation (expected exit 2); all service
modes explicitly refuse `GITHUB_ACTIONS=true`. The executing binary must have
the bound Git revision in its assembly informational version.

`execution.json` embeds the private preparation as `preparation`, not a path or
automatic discovery instruction. Supply a unique run ID, source commit, absolute
expiry and verified access-review hash. `directMaxRequests`, `hostedMaxRequests`
and `hostedControlMaxRequests` are separate allocations whose sum must not exceed
the preparation's request limit. Each durable checkpoint retains its original
deadline and counter across restarts. Direct/Hosted data clients reserve three
requests for cleanup; no background poll loop, query retry or timeout extension.
Credential-service control traffic is separate from those data-plane counts;
Azure Identity retries are disabled.

Each credential reference is an explicit protected mounted file: a combined
certificate/private-key PEM, a rotating federation assertion, or a separately
approved client secret. No secret is an argument, public input or log value.
The credential kind selects exactly one Azure Identity implementation. The
Entra authority is restricted to the bound tenant on `login.microsoftonline.com`;
the Hosted control caller uses its own binding and `https://ai.azure.com/.default`.
Neither it nor a Hosted platform identity substitutes for the Fabric caller.
Token claim checks reject issuer/audience/client/object/tenant/delegation/lifetime
drift before sending a bearer token; they are not signature or service-side
identity evidence.

The exact data-plane FQDN and explicit private address allowlist are required.
Sockets connect to an address from the validated DNS result rather than resolving
again; all results must be approved RFC1918/IPv6 unique-local addresses.
Redirects, proxy inheritance, cookies and public fallback are disabled.
This only checks client-side connection binding. Private Endpoint ownership,
public-denial and all downstream hops still require independent native evidence.

For Hosted execution, start the same binary with `--fabric-c1 host` and the three
fixed file options. It serves HTTP/1.1 on 8088, `GET /readiness` and
`POST /invocations`, checks the injected project/agent/version/session variables
and bound platform caller header, and observes shutdown/request cancellation.
Configuration, credentials and checkpoints must be outside upload-writable
session `HOME`; the checkpoint must be in the approved durable private evidence
location. The owner must independently verify mount permissions, directory
ownership and absence of writable link redirections; lexical path checks are not
a native storage-isolation proof.
The surrounding Foundry endpoint must enforce its separately reviewed Entra/RBAC
authorization and fixed version routing. Container headers alone are not proof.
No forwarded user credential or model-directed tool choice is used.

The private operator calls the existing session with
`--fabric-c1 hosted --inputs ... --fixture ... --state <control-checkpoint> --operation discover`
(or `query --probe allowed`, `status`, `resume`, `cancel`, `close`). The exact URL
must match the bound project/agent Invocations route, `api-version=v1` and
`agent_session_id`; no session is created automatically. The response session,
run hash, runtime and invocation ID must match. A lost Hosted response remains
Unknown until `status` reconciles that same invocation, not a new query.

Checkpoints are exclusively locked and atomically replaced after flushing.
Request intent and count are persisted before sending; cursor/task/result
bindings are retained afterward. Raw results remain private; console output
contains only bounded status/codes/hashes. A filesystem checkpoint is not a
G01/G02 journal, durable cloud storage proof or RPO0 acknowledgement.

## Missing inputs: infrastructure owner supplies private bindings

Keep the filled file and approval evidence in the approved private evidence
location, not in this public checkout, CI secrets, logs or uploaded artifacts.
The existing infrastructure owner continues the one joint target plan; do not
open another planning session or invent resource IDs.

| Block in the input file | Required missing values and evidence |
| --- | --- |
| `identity` | Exact Entra tenant, application/client ID, service-principal object ID; approved certificate/federation/secret method and a secret-store reference **without secret bytes**; expected token issuer and actual resource audience |
| `target` | Own capacity resource ID/SKU and region; workspace, published data-agent, source-item and ontology IDs; copied exact private MCP URL; observed supported protocol; tool name, question argument, canonical input-schema hash; reviewed task protocol/SDK revision |
| `source` | Materialized item/release hash, ontology definition/binding hash and effective source-grant review hash. The fixed `ortho` scope, `C1-RELEASE-1` label and fixture hash describe synthetic intent only, not a deployed release |
| `runtime` | Allocated private direct-runner resource; Foundry project endpoint, own Hosted Agent version/image digest; `FabricManagedDataAgent` plus real Fabric AI-policy/geography review, no unused model resource |
| `controls` | Hashes of the isolated resource/region plan, exact app-only private-path review, DNS/egress and public-denial plans, data-handling/retention review, quantified budget/expiry plan and named cleanup plan; separate C1 run approval reference |
| `limits` | Approved maximum request count, total duration, response size and cleanup timeout; no unlimited/implicit retry or progress-based deadline extension |

The resource plan must bind subscription/RG, capacity SKU/quotas, billing owner,
quantified cost assumptions/currency/expiry, Fabric AI/Hosted usage, private endpoint IDs and exact
source grants. The data-handling plan must cover Fabric AI cross-geo settings,
Foundry/model responses, client evidence, diagnostics and deletion/retention.
Tenant settings may only use the explicitly approved SP security group; no
tenant-wide enabling, role expansion or other primary-environment change.

## Identity, source and protocol contract

**App-only is authenticated service access, not anonymous Cohors operation.**
The Entra user login and separate authorized Medical/RA human approvals through
the Cohors API remain unchanged. The probe has no approval or business-write
tool. A Hosted runtime identity is not assumed to be a supported Fabric caller.

For the direct **published data-agent MCP** candidate, request
`https://api.fabric.microsoft.com/.default`. The issued token's `aud` is a
separately bound resource value, not that scope string. Do not substitute the
Power BI audience from the different query-interface/OBO documentation.
Use an explicit approved SP credential, not `DefaultAzureCredential`, Azure CLI
login, interactive/device-code/OBO or a managed-identity fallback. Decode-only
JWT claims are not identity proof: collect matching Entra/service-side caller
evidence, audience/issuer checks and request correlation without retaining tokens.

Direct ontology MCP still has the documented SP limitation. An ontology-backed
data agent remains a candidate, not equivalent evidence by name. Bind the native
published definition, attached source IDs, query traces and source/ontology
release hashes independently of answer text. A model repeating requested IDs or
hashes does not prove which data it read.

The fixture has two allowed revisions (`C1-A@1`, `C1-B@2`), a pending newer
`C1-A@2`, a revoked `C1-C@1` and an unauthorized `cardio` row `C1-D@1`. The owner
must materialize allowed and denied source scopes with **actual enforceable
source permissions and release visibility**. Prompt instructions and filtering
the answer afterwards are not access control. Broad workspace Member/Contributor
rights can invalidate the negative cases; if necessary isolation cannot be
enforced with supported item/source grants, record a blocker, not a pass.
No operational case database or existing primary source is used.

The discovery recipe and transport are pinned to MCP `2025-11-25`; the target has not
been observed to support it. The current tasks-extension documentation describes
per-request capabilities and `server/discover`, while the pinned lifecycle uses
`initialize`. Do not mix task layouts or assume `tasks/result` vs. a final result
inside `tasks/get`. `taskDialect=none` enables no task augmentation.
`taskDialect=mcp-2025-11-25` implements only the versioned specification:
advertised server/tool task support, `tools/call` with `task`, `tasks/get`,
`tasks/result` with the original task binding, and `tasks/cancel` followed by
terminal readback. The later extension is explicitly unsupported until its
actual target contract is bound; no guessed mixture. `input_required` blocks.
Interrupted SSE can resume with `GET`/`Last-Event-ID`; a lost query without a
durable task/cursor remains Unknown. Discovery-only preparation can
omit tool/schema/task bindings; any query preparation requires their review.

## Native cases under the bounded execution authorization

Run the same immutable client/fixture first from the allocated private direct
runtime, then from the own Foundry Hosted Agent. The standard GitHub runner is
**not** a private service-test runner.

| Case | Required observation; failure/stop condition |
| --- | --- |
| C1-01 identity/discovery | No user session; exact SP, workspace/item, audience, issuer and protocol; complete expected tool/schema. Stop on drift, missing source grant or undocumented protocol |
| C1-02 private positive | Same approved query in direct and Hosted runtime with public data-plane access disabled; exact allowed rows plus independent source/ontology/version provenance |
| C1-03 source negatives | Wrong product, pending/newer and revoked release, missing/withdrawn source grant deny data under the actual caller. An empty answer/model refusal alone is not denial proof; pair with native authorization/query evidence |
| C1-04 identity negatives | Approved negative identity and wrong-audience token fail at the same isolated endpoint. Never send a token to a redirected/unreviewed host; never broaden grants to make the probe green |
| C1-05 network negatives | From an approved outside-network runner and with wrong DNS, no successful data-plane path. Capture resolved addresses, private endpoint, connection/egress and denial evidence; no public fallback |
| C1-06 interruption | Persist original request/config hash and task ID in approved private storage; resume that task after interruption. Unknown response without a durable ID stays Unknown, not a new `tools/call` |
| C1-07 cancellation/cleanup | Cancel the bound task, observe its terminal outcome, close session/transport, verify exact run-owned resource cleanup and cost stop. Cancellation acknowledgement alone does not prove work stopped |

A `401/403`, timeout or empty result without a healthy positive control can be a
broken setup, not a passed denial. Task `input_required` must stop unattended
execution; no user/OBO authentication or business approval fallback. Enforce all
approved limits, bounded private response retention and no automatic query retry.
Redirection, public resolution, scope/release drift, unclear provenance or
excess budget/time/data immediately stop new requests and start recorded cleanup.

The private path includes Hosted -> model, Hosted -> Fabric MCP, Fabric ->
ontology/source, necessary evidence/credential services and the relevant Cohors
API path; private ingress alone is insufficient. Enumerate unavoidable Entra/
control-plane traffic separately. Firewall/VPN products are not mandated here;
the owner must choose approved controls that enforce the required path.

## Evidence, abort and cleanup record

Retain privately: source commit/image/configuration and fixture hashes; UTC
start/end; direct/Hosted execution identity; service request/task IDs; tool/schema
and native query/source-release evidence; per-hop private and denial evidence;
each case's expected/observed result, response digest, failure code and cost.
Each observation is `NotRun`, `Blocked`, `Observed`, `Denied`, `Unknown` or
`Failed`; specification/in-process/native observation/acceptance stay distinct.
The credential-free source checks have **zero native service observations**,
no G03/G04 pass. Successful transport observations also are not automatic
capability passes: authorization controls, source provenance and private
downstream paths need their independent evidence. Client-side rejection of an
incorrect token is not the native wrong-audience case.

Before provisioning, the owner supplies an exact run-owned cleanup inventory,
responsible operator, deadline and approved retention/deletion policy. On success
or abort: stop scheduling; request task cancellation when its ID is known;
reconcile final/Unknown state; close sessions and revoke only dedicated temporary
credentials/grants; remove or suspend only approved named C1 resources; verify
deletion/suspension and remaining billable items. Unknown tasks, failed deletion,
shared-resource dependencies or retained diagnostics produce `CleanupIncomplete`
with owner/action, never a success-shaped cleanup report. Never delete primary
resources, whole subscriptions or evidence whose retention is still required.

Only small sanitized status/hash summaries belong in public hosted logs.
Raw responses, private endpoints, tenant/principal IDs, tokens, source ACLs and
private configuration do not. This does not amend G01/G02, their two protection
subscriptions, any adapter gate, full private-path/recovery proof or pilot
acceptance. No Public/OBO/Direct-SQL substitute is authorized.

## Sources and unresolved platform boundary

Reviewed 2026-11-18; documentation is not native execution:

- [Data-agent MCP](https://learn.microsoft.com/fabric/data-science/data-agent-mcp-server): streamable HTTP, handshake, published endpoint, SP token and Fabric scope.
- [Data-agent SP](https://learn.microsoft.com/fabric/data-science/data-agent-service-principal): preview, source grants, no managed-identity or SP-backed KQL support.
- [Workspace private-link support](https://learn.microsoft.com/fabric/security/security-workspace-level-private-links-support#data-agent) still excludes external Foundry/M365 integrations.
- [Foundry Fabric IQ](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/fabric-iq) describes workspace-private data agents but its private connection example forwards a user token. This is **not** proof for the chosen direct SP path; explicit path review/clarification and a separately approved experiment are required.
- [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle) and [tasks extension](https://modelcontextprotocol.io/extensions/tasks/overview): pin the actual negotiated dialect; no guessed compatibility.
- [Versioned Streamable HTTP](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) and [versioned tasks](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/tasks): implemented bounded dialect, not inferred target support.
- [Hosted runtime contract](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-contract), [Invocations REST route](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent#deploy-using-the-rest-api) and [existing sessions](https://learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions): no provisioning or endpoint rerouting by this client.

The existing [capability probes](capability-probes.md) and
[gate register](capability-gates.json) remain authoritative and open.
