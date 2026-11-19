# Cohors Fabric

Independent alternative based on ALT-FAB-01 v0.2 and FAB-BASELINE-01 v0.2.
This is an implementation in progress, not an accepted or deployed system.

## Own Fachagent models remain in the full pilot

The model-less **C1 MCP test is not the full Cohors application**. Its Fabric
data agent is the controlled `ExplainDomainContext` knowledge path, not a
replacement for Cohors' own assessment, assurance, authoring or embedding models.
ALT-FAB-01 v0.2 sections 9-11 and baseline X-23/X-25/X-27 still require own
.NET/Agent-Framework agents in Foundry, separately controlled model deployments,
Fabric-based retrieval/embeddings and independently executed assurance.

The isolated fork already contains the live model call in
`Cohors.Agents\AgentRunner.cs` (`AzureOpenAIClient` / `GetChatClient` /
`AsAIAgent` / `RunAsync`), six bounded drafting roles, explicit Replay/Failure
modes, `FoundryBoundary` model/version/EU-review binding and
`AgentExecutionService` invocation/input/result persistence. `Cohors.AgentHost`
provides a separate caller-authenticated `/internal/run` service. These are
retained implementation components, not deployed Fabric Hosted agents.
The inherited `infra\modules\ai-services.bicep` optional Standard/no-auto-upgrade
model and old AgentHost settings are not the Fabric delivery installer.

The full Fabric pilot still needs its own concrete model/version/SKU/region/
quota/cost/privacy binding, explicitly selected workload credentials instead of
the inherited model client's `DefaultAzureCredential` chain, private model
access, Hosted invocation/reentry integration, knowledge/embedding adapters,
separate assurance execution and native functional/operational acceptance.
The C1 v2 correction removes only unused fields from the direct MCP probe;
it neither removes these obligations nor proves any of them delivered.

### Mandatory task-model quality priority (2026-11-18)

The user's quality decision supplements the full pilot, without changing the
immutable original baseline or adding a model to the model-less C1 transport.
Medical/technical analysis, causality/risk, recommendations and independent
assurance require quality-first selection among capable strong/frontier
candidates. A cheap model is allowed only for a task whose professional
evaluation demonstrates correct, evidence-preserving execution. Intake,
extraction and triage must not drop safety signals to save tokens or money.
Missing or conflicting evidence stays visible as review work; it is not
silently rejected or automatically approved.

`FoundryTaskBinding` / `FoundryQuality.ValidateActivationReview` specify the
separate activation evidence contract: exact task/model/version/family,
prompt bytes/version, complete pipeline configuration, own workload identity,
private-path and evidence-preservation reviews, and the existing EU boundary.
Changes require a newly bound regression. Critical roles cannot select the
cost-optimized policy. No lower-cost, Global, DataZone, region or credential
fallback is authorized. A capability label alone proves no quality.

The professional reference dataset, evaluation protocol/results and qualified
review must cover the entire intake-to-retrieval-to-analysis-to-assurance chain.
FP and FN are separate, with sensitivity, precision, FPR/FNR, case/decision
denominators, rare critical cases, negation, conflict, missing evidence,
unsupported claims and fabricated citations. Sample size, coverage, uncertainty
and limits are explicit. No invented target rates, zero-error guarantee,
model-confidence threshold or AI-judge-only gold standard is supplied.
The confusion counts are measurements, not an automatic acceptance threshold.
The qualified review must define and justify the task-specific criteria.

Assurance additionally binds the actual analysis profile, separate workload
identity, independent original-read path and a reviewed independence argument,
including shared failure modes. A second call or a different/same model-family
label is insufficient. Medical and RA decisions remain distinct authorized
human approvals through the Cohors API.

This increment validates the **evidence contract only**. Reports, native
identity/private-path enforcement, separate hosting, original-read execution
and real model evaluations are not supplied or certified by a successful
validator result. The validator is not yet a hook in the inherited AgentRunner;
its reviewed integration is mandatory before the Fabric Fachagent activation.
No concrete model/version is selected and no existing deployment is activated.
Model resources for genuine evaluation may be provisioned before live-quality
approval under their separate resource/privacy/authorization gates; the
quality validator must not create a circular pre-provisioning requirement.

## C1 provisioning increment: partial source implementation

The original deployment manifest must now bind `cleanup.reservedRequests`,
`cleanup.maxDurationSeconds` and `cleanup.verificationDeadlineUtc`. The reserve
is **part of**, not added to, `maxControlRequests`; normal provisioning cannot
spend it. Its explicit verification deadline may extend only through the
original expiry plus at most the already bounded control duration. It does
not extend C1 use, allow new work after expiry, or promise a monetary cap.
This must be included in the original resource/budget/cleanup review before
starting. An older missing reserve is not manufactured on resume.

The operator command `cleanup-identities --manifest PRIVATE_FILE --journal
EXISTING_PRIVATE_FILE` is an implemented **B1-only** cleanup path. It requires
the original source-bound manifest/journal and caller, can consume its reserve
after normal expiry, fences normal provisioning, and persists its own start,
counter, delete intents and absence readbacks without resetting original
limits. Uncertain creates are reconciled read-only, not repeated. Deletion
requires exact journal/marker/identity/ownership checks; group membership and
parent application ownership are checked before removing dependent identities.
An uncertain DELETE is observed rather than repeated, a restored object is
not automatically deleted again, and the initial deployer is never targeted.

This command refuses journals containing workspace, paid-resource or Hosted
consumers: their stop/cancel/session/item/private-link/resource cleanup must
precede identity removal in the future full cleanup driver. Directory active
endpoint absence is **not** retention purge. Shared tenant effects and scoped
tenant-setting references stay explicitly inventoried for separate current
administrative review; no tenant reset occurs during identity cleanup.
The independent cloud deadline executor and full paid-resource cleanup remain
unimplemented gates. No cleanup or cloud deployment has been executed.

The shared inbound stage is distinct from scoped runtime-group settings.
`CanPrepareEmptyFoundation` reports tenant-setting prerequisites only, not a
cost/stop/private-readiness approval: a disabled `WorkspaceBlockInboundAccess`
does not prohibit empty workspace-level Private Link preparation.
`SelectedRequirementsObserved` does not prove a working Public-Deny either.
The source/data and workspace-policy methods freshly require that shared
setting to be enabled; the exact public-denial probes remain separate.

`ApplyWorkspaceInboundEnable` requires completed private-link preparation and
a separate deployment/diff-bound, current direct-user approval plus the
administrative change-window review. The approval explicitly acknowledges
tenant-wide workspace-admin configuration authority and **no automatic
disable**. A scoped-group approval cannot execute this change. Original
snapshot, exact request, approval and observed shared effect remain in the
journal. Interrupted writes reconcile by GET, never by repeating an uncertain
POST. C1's transport rejects disabling the setting or fictitious group scope
before credential acquisition. Own-resource cleanup must retain this shared
effect; a later reversal requires a separate current administrative decision,
not unchanged setting values/ETags. Neither other workspace rules,
tenant-wide `BlockPublicInternetAccess`, IP firewall/trusted resources nor
cross-geography settings are changed.

This provider stage is not yet a full-installer CLI activation path, and no
tenant setting has been changed. At the actual activation stage the operator
must ask the single narrow direct-user question, not ask early for code/CI
preparation or repeat general C1/cost approval. Allow up to 15 minutes for
setting propagation; workspace-policy propagation and native Public-Deny
remain independent evidence. Sources:
[inbound administration](https://learn.microsoft.com/fabric/security/security-workspace-enable-inbound-access-protection),
[shared rollback effect](https://learn.microsoft.com/fabric/admin/tenant-settings-index#advanced-networking),
[list contract](https://learn.microsoft.com/rest/api/fabric/admin/tenants/list-tenant-settings),
[update contract](https://learn.microsoft.com/rest/api/fabric/admin/tenants/update-tenant-setting).

The new opt-in entry is `Cohors.Api --fabric-c1-deploy plan --manifest FILE`.
Start with `fabric\c1\deployment.template.json`, not with manually created
Capacity/Workspace/App/PE IDs. The normal API and the existing `--fabric-c1`
probe entry are unchanged. `plan` is offline and returns exit 2 / `Blocked`.
Full-profile `apply --manifest FILE --journal PRIVATE_FILE` is still blocked
before credential acquisition: the complete private bootstrap driver is not
implemented yet. The explicitly limited
`apply --manifest FILE --journal PRIVATE_FILE --through identities` now runs
the unpaid identity phase, with the exact selected credential, source-build
binding and owner-only manifest/credential/state paths. It never allocates
capacity or other paid resources. Missing scopes, group permission, policy
selection or admin-read access produce a specific paused result; they are
not converted into successful deployment. This path is prohibited in the
credential-free hosted verification job and has not been run against a tenant.
This is not the requested finished automatic C1 deployment.

The current target is an infrastructure-owned **shared hub and an own Fabric
spoke**, not the earlier own Firewall/VPN deployment. The standalone templates,
operator audience implementation and R4-R6 evidence below remain historical
source/cleanup contracts. A new manifest selects `network.sharedHub`; it cannot
also select `operatorAccess` or silently fall back to those standalone stages.
See the shared-hub consumer section below. No new cloud execution follows.

Implemented source components:

- Historical standalone `infra\fabric\c1.foundation.json` describes an isolated F2 (overage disabled),
  private VM/VNet/subnets, DNS, Key Vault, private storage/ACR, and a Foundry
  account with injection at creation plus a separate project identity.
  The selected technical work profile is F2/FranceCentral, not a live quota/cost
  result. The historical `c1.network-access.json` stage owns its Standard Firewall,
  DNS proxy, two fixed default instances, active-passive VpnGw1AZ/OpenVPN
  gateway and their separate static Standard IPv4 addresses. Foundation v2
  consumes the observed firewall address before creating workloads, sets VNet
  DNS and attaches the default route only to `client` and `agents`.
  This replaces NAT in the new resource graph; it does not delete or adopt an
  earlier deployment. GatewaySubnet and AzureFirewallSubnet have neither an
  NSG nor a default route table. No public VM NIC, DNAT, TLS interception,
  shared gateway or primary peering is added.
  Vault purge protection and seven-day soft-delete retention are enabled; the
  deleted-vault/name tail is retained rather than falsely reported as purged.
  `c1.workspace-network.json` binds workspace PLS/PE/DNS from creation outputs.
  Own workspace, Vault and artifact-Blob bootstrap endpoints use the dedicated
  NSG-protected `operator-endpoints` subnet. P2S has HTTPS access only there,
  not to VM/ACR/Hosted endpoints. Workload callers retain HTTPS to both endpoint
  subnets. The service-specific identity and data grants still apply.
- Historical `FabricC1OperatorNetwork` binds nonoverlapping canonical private prefixes,
  explicit excluded workstation/other-network prefixes, exact reviewed HTTPS
  hostnames and client/address-review references. Wildcards, URL/IP aliases
  and absent private routing review are not executable inputs. Null omission
  preserves historic deployment hashes; legacy identity cleanup does not
  require retroactively inserting a VPN profile.
  `EnsureOperatorNetworkIdentity` creates a separate, credential-free custom
  Entra audience and assignment-required SP. The separate VPN-access decision
  now selects all four named pilot accounts, using their already resolved
  private object IDs. `vpnUsers` binds the exact individual set, the answered
  authorization and original directory-evidence hashes; no new directory
  search, user creation, group/wildcard grant or actor switch follows.
  Each selected user receives only the default assignment on the own VPN
  audience, not a Cohors/Fabric/administrative role. The original operator
  remains separately pinned for deployment and delegated notebook authoring.
  Missing new selection inputs block writes rather than defaulting to that
  operator. Historic null selections retain their serialized hashes and are
  accepted only for the already bound identity-cleanup path.
  Each assignment has its own intent/readback; even a selected user's
  unjournaled grant blocks further work. Unknown creates are not replayed,
  and only confirmed own-SP absence settles their dependent pending intents.
  Private user IDs and authorization evidence are excluded from ARM parameters.
  The Microsoft-registered Azure VPN Client is preauthorized for that own scope.
  It does not change the deployment client's OAuth context, grant Microsoft API
  access, create tenant consent or modify Conditional Access. Client MFA and
  connectivity still need their own real sign-in evidence.
  Identifier PATCH and operator POST are journaled before effect; uncertain
  responses reconcile without reissuing either write. Additional owners,
  assignments, credentials or scopes block continuation/identity cleanup.
  Removing this own SP/app does not delete a user or the Microsoft VPN Client.
- `FabricC1Provisioner` implements journal-before-effect creation, exact
  manifest/ownership checks, bounded inventory, unknown-create reconciliation
  without blind POST replay, ARM deployment polling, distinct ARM/Fabric
  capacity identities, Graph app/SP bootstrap, workspace/Lakehouse/DataAgent
  APIs, publication readback, and the agent-workspace-only Contributor grant.
- ARM resume also binds the actual deployment ID, Incremental mode, exact
  parameter values and first observed correlation/template hash. A replaced
  deployment is not adopted. The resource group is read again before the stage;
  no missing original deployment is silently recreated.
  Once a successful response is observed, its outputs cannot change on resume,
  even if subsequent child reconciliation has not yet finished. Explicitly
  created Private DNS VNet links receive the deployment tags in their template
  at creation; this is not later retagging of an unknown provider resource.
  Azure-generated PE NICs and the VM OS disk need not acquire fabricated
  ownership tags: their IDs must occur in the original successful parent
  outputs and complete group inventory, with matching fresh parent and child
  references in both directions. The original VM instance and each NIC/disk
  instance GUID are retained; replacing an instance, adding tags later, changing
  region or attaching a shared disk cannot bypass reconciliation.
  `c1.workspace-network.json` therefore returns its actual private endpoints
  and their generated NIC references. Missing outputs/children, unknown untagged
  resources or contradictory tags block progress; they are not absence proof.
  Child observations are retained with `ThroughRecordedParentOnly` and
  `directDeleteAuthorized=false`. No tag write or child deletion is performed.
  This is resume/inventory groundwork, not a full cleanup implementation or
  a successful Azure deployment. A failed/incomplete parent without these
  outputs still needs explicit reconciliation, not inferred child ownership.
- A policy update first reads its ETag and full body, explicitly writes both
  inbound and outbound `defaultAction=Deny`, preserves other fields, and
  checks the readback. Missing ETag, HTTP 412, foreign names, wrong
  tenant/subscription or incomplete inventories stop the operation.
- The HTTP component binds each scope to its exact authority, caller and
  tenant. Public Fabric hosts permit only the documented control operations;
  `controlTokenBindings` separately binds observed `armAudience`/`armIssuer` and
  `graphAudience`/`graphIssuer`. A `/.default` request scope is not an audience;
  URI-vs-application-ID claims are not inferred from the credential kind.
  item operations require the exact separately configured private workspace
  client. There is no default HTTP, public, OBO, SQL or other-caller fallback.
- Durable deployment intent and request budgets use a separate locked,
  bounded, atomic file journal. Generated preparation **drafts** retain
  observed IDs but leave missing credentials, ontology, reviews and runtime
  facts missing. They cannot become runnable by substituting dummy values.
- Four separate applications/SPs (runtime, Hosted control, private bootstrap
  and negative caller) and a static, non-role-assignable Fabric API group are
  generated from provider responses. The initial deployer is never reused as
  one of them. `Application.ReadWrite.OwnedBy` is accepted for the app-only
  owned-app/SP path. Group creation, read and SP membership have independent
  permission checks; `AppRegistration.Create` alone is not the whole path.
  Graph v1.0 group SP omissions are addressed with the documented expanded
  members query and the creator SP's `ownedObjects` ownership read.
  Deleting the owned non-role-assignable group uses the separate Group.ReadWrite.All
  contract, not the additional application permission needed for SP membership.
  Delegated `delegatedGroupOwnerMode` is bound by the reviewed start rights:
  `ExplicitReviewedAdministrator` or `AutomaticReviewedNonAdministrator`.
  Missing optional `wids` never implies non-admin status; the selected reviewed
  administrator binds its own owner explicitly and still needs actual readback.
- Tenant policy planning uses observed setting keys, preserves exclusions and
  delegations, refuses admin-API membership for the runtime group, and never
  enables an unscopable setting or reactivates unrelated disabled groups.
  Existing tenant-wide settings are reported, not silently rewritten.
  The scoped-update component needs an exact approved change and a separate
  administrative concurrency review: its API has no documented ETag/CAS
  contract. It is not automatically invoked by the normal identity command.
  An explicit `--admin-approval PRIVATE_FILE` after `--through identities`
  enables only that deployment-bound, unexpired change. The approval JSON uses
  `contract=cohors-fabric-c1-scoped-admin-approval-1`, `deploymentBindingHash`,
  `changeHash`, `administrativeConcurrencyReviewSha256`, and `expiresAtUtc`.
  Group membership is read again before changing policy; completed/uncertain
  substeps reconcile against the original persisted approval without POST
  replay. No separate admin identity is selected automatically.

Remaining integration gaps block the **whole** profile, not just its last
step: private operator/artifact delivery
and FQDN-filtered egress; private synthetic materialization and effective
source-grant readback; protected Hosted certificate delivery and durable
checkpoint outside upload-writable `HOME`; Hosted image/version/session
and final execution binding; an independent deadline/stop/cleanup executor.
The foundation
NSG is only a deny-by-default candidate, **not an implemented FQDN filter or
working Managed Run Command route**. Do not deploy the template alone and
leave paid resources waiting for these gaps. The later user decision supersedes
the separate Firewall/P2S topology with an infrastructure-owned shared hub,
separate primary/Fabric spokes and a technically selected shared private WAF_v2.
This is not Internet, current cost or cloud activation approval. B5 still uses
documented delegated notebook authoring through P2S to the Azure hub/spoke
network, not a VPN gateway in Fabric SaaS. Native private access is unproven.
The hub owner manages the audience and four individually selected pilot-user
assignments; the Fabric/deployment OAuth client and Cohors roles do not change.
Client prerequisites, private DNS, narrow service access, costs and stop/removal
must be bound before activation. No automatic client install or connection is
assumed. Managed Run Command, public workspace calls, an HTTP-parsing Relay or
copying the user's bearer to a worker remain invalid substitutes.

`FabricC1BlobCheckpointStore` now provides a separate versioned checkpoint
consumer, not yet the complete Hosted delivery/bootstrap path. It requires an
already existing JSON block blob under the own account's
`c1-checkpoints/{deployment-binding}/{execution-binding}/{runtime}.json`.
There is no create-on-404, file import/fallback, new task, lease break, or retry.
Its native factory uses the same explicit app-only caller with a separately
bound Storage audience/issuer and the existing private DNS/socket/TLS guard.
It refuses GitHub Actions before reading credentials or contacting a service.

The consumer holds a proposed 60-second lease, renews every 15 seconds and
conservatively fences at 50 seconds measured from the request start. Every
state write requires both that lease and the current strong ETag. Lost lease
or ambiguous writes cancel in-flight MCP/Hosted-control reads and prevent later
writes; cancellation alone does **not** prove a remote task stopped.
Original clocks, consumed MCP requests and outstanding calls survive ownership
changes. Metadata and an observed encryption scope are preserved; unexpected
index tags fail closed rather than being silently erased. Calls and JSON reads
are bounded to ten seconds or the remaining invocation allowance, whichever is
shorter. One reserved request is kept for release; a failed/unknown release is
reported, never replayed. Opening and subsequent release failures are retained
together rather than one hiding the other. A server lease can remain until expiry, and Azure
documents an additional acquisition wait after expiry; no immediate recovery
or completed cleanup is inferred.

Before **any** storage request, the driver must durably debit a one-use I/O
reservation from the original approved deployment allocation. The consumer
records successful reservations under the lease, rejects reuse or excess,
and does not refund unused quota or allocate a replacement after ambiguity.
The receipt/review hashes are references, not proof that this upstream debit
happened. The provisioner's source-only `PrepareBlobCheckpoint` now atomically
records a fixed allocation and initial seed in the original exclusive local
journal. It binds the original own Storage output, generated runtime/control
app identities, source revision and existing budget-review hash. Prepaid
allocations count against both normal and total control ceilings; ordinary
calls cannot borrow them or the cleanup reserve. `Requests` continues to mean
control attempts; prepaid requests remain separately visible in allocation
receipts, not invented HTTP traffic.

`ReserveBlobCheckpoint` durably issues individual claims from that fixed pool.
Resume returns the original seed, clock and claim, never a new allocation;
direct/Hosted/control allocations share that original execution and clock.
Unused/uncertain claims are not refunded. An ambiguous journal write fences
all further provider calls and journal writes in that process until its
original journal is reopened. The generated seed alone is a **plan**, not a
confirmed Blob create.

`MaterializeBlobCheckpointNative` now supplies the separate seed producer.
Foundation declares the own nonpublic `c1-checkpoints` container; the producer
requires that completed original output and never creates a container on a data
path 404. It charges every attempted GET/PUT to the original normal control
budget, outside prepaid consumer I/O and cleanup reserves. Only `BlobNotFound`
permits a first `If-None-Match: *` block-blob PUT, after a durable random creation
marker and original allocation/seed hashes. The subsequent bounded readback
must have the exact seed bytes, metadata and a strong ETag. Unjournaled data,
a conditional-create race, changed bytes or metadata, uncertain create followed
by 404, or uncertain journal persistence fail closed. There is no automatic
retry; only an explicit 401/403 rejection permits a later invocation to retry
that same original conditional intent. A confirmed receipt remains historical:
it does not reinitialize or attest the current runtime state. The consumer
still rejects missing state instead of importing or reconstructing it.

The native factory uses the explicit app-only/private transport and refuses
the hosted source-build runner. `ExportBlobCheckpointProfile` now exports
only the already journaled allocation and confirmed original seed receipt.
The profile pins the original clock and seed-lineage metadata, not the current
mutable ETag. `ExportBlobCheckpointAllowance` requires actual one-use reservation
receipts from that journal and binds their original invocation/operation. Hosted
control and server I/O use separate allocation receipts; neither borrows the
other's budget. Re-export is not permission to replay a used claim.

The CLI now explicitly selects Blob state with `--checkpoint-profile FILE
--allowance FILE`; the original `--state FILE` retains its file meaning and is
never a fallback. The selected profile, execution and fixture inputs must be
protected files. The Blob invocation ID comes from its original allowance,
not a new GUID on retry. For the Hosted server, startup uses only
`--checkpoint-profile FILE`, outside upload-writable `HOME`; it has no reusable
startup allowance. Every authenticated invocation carries its own Hosted
reservation. Missing/mismatched/reused claims fail closed. Readiness describes
configuration only; it does not consume I/O or prove Storage access.
Original execution hashes remain independent of these sidecar profiles, avoiding
a self-referential binding hash. Historical invocations omit the absent new
reservation property and retain their canonical hashes.

Example argument shapes for the exact source-bound binary (not native evidence):

```text
--fabric-c1 discover --inputs EXECUTION --fixture FIXTURE --checkpoint-profile DIRECT_PROFILE --allowance INVOCATION
--fabric-c1 hosted --inputs EXECUTION --fixture FIXTURE --checkpoint-profile CONTROL_PROFILE --allowance INVOCATION --operation status
--fabric-c1 host --inputs EXECUTION --fixture FIXTURE --checkpoint-profile HOSTED_PROFILE
```

Scoped RBAC/readback, protected native profile/certificate delivery and the full
orchestrated runtime driver remain **not integrated**. These hashes do not
replace a trusted delivery path or demonstrate an upstream native deployment.
The checkpoint access component now prepares and reconciles one own Azure
custom role with only Blob `read`/`write` data actions and no management,
delete or delegation actions. Its assignable scope is the own resource group;
the two assignments target only the own checkpoint container. Exact ABAC paths
separate the runtime's direct/Hosted files from the control caller's file and
exclude `Blob.List`. These are service-principal grants, not human VPN or
Cohors roles. The component verifies each current provider readback against
the original intent. It never adopts an unjournaled role/grant, replays a lost
PUT, recreates an unknown missing assignment, or widens a changed condition.
The complete original role/grant requests are persisted before the first I/O,
including the exact paths needed for later reconciliation and ordered cleanup;
their hashes alone are not substituted for recoverable original requests.
An explicit 401/403 is retained as a rejected original attempt, not retried
automatically. Ambiguous receipt saves fence this process until journal recovery.
The start-rights contract now includes role-definition/assignment read, write
and delete. Grant creation does not allocate or restart the C1 checkpoint clock.

This source path has not created a role or demonstrated effective Azure access.
Inherited grants, condition enforcement/propagation, private lease/read/write
access and cross-path denials still need native evidence. It does not prove a
checkpoint is immutable against a compromised writer or replace the separate
Ledger/WORM contract. `CleanupBlobCheckpointAccess` now removes only the two
original grants before the own role definition, using the original cleanup
reserve and deadline even after normal execution expires. It validates the
complete retained requests against the own storage/container, principals and
role identity, and obtains fresh GET absence readbacks. An unattempted or
explicitly rejected create is not ownership; a 404 after an uncertain create is
not proof that no resource was created. Unknown creates/deletes only reconcile,
never replay a write. Reappearing objects or changed observed creation metadata
block deletion. All control/checkpoint journal write failures now fence further
I/O until reopening the original journal; uncertain debits are not refunded.

This is an inventory-cleanup component, not a worker/task stop or proof of
effective authorization-cache revocation. Checkpoint data and service identities
are not deleted by it. The full driver must order runtime shutdown/cancellation
and data/identity teardown around this step; that integration and native deletion
remain unproven. Removing the resource group alone is not proof that the role
definition is gone. Extra assignments of the custom role must not be deleted
by broad enumeration; a provider refusal leaves that role visible for review.
ARM's documented PUT/DELETE contracts do not establish a conditional CAS fence
against another privileged writer between the ownership read and mutation.
Provider references: [custom role creation](https://learn.microsoft.com/rest/api/authorization/role-definitions/create-or-update?view=rest-authorization-2022-04-01),
[role assignments](https://learn.microsoft.com/rest/api/authorization/role-assignments/create?view=rest-authorization-2022-04-01),
[Blob path conditions](https://learn.microsoft.com/azure/storage/blobs/storage-auth-abac-examples#blob-container-names-or-paths)
and [lease authorization](https://learn.microsoft.com/rest/api/storageservices/lease-blob#authorization),
[assignment deletion](https://learn.microsoft.com/rest/api/authorization/role-assignments/delete?view=rest-authorization-2022-04-01)
and [role-definition deletion](https://learn.microsoft.com/rest/api/authorization/role-definitions/delete?view=rest-authorization-2022-04-01).
The existing 39 preparation mappings,
18 execution fields and seven caller fields are unchanged; these additional
storage documents are a separate implementation contract. All five full-driver
gaps and seven native `NotRun` cases remain. No live Blob/lease, permission,
private-path, budget or recovery acceptance follows from fake-transport tests.

Storage API basis: [conditional Put Blob and metadata](https://learn.microsoft.com/rest/api/storageservices/put-blob).

These are explicit **implementation gaps**, not assertions that Microsoft
lacks the corresponding APIs. Source-grant/ontology-private-link and the
custom Hosted app-only path may additionally encounter actual provider
limits during the bounded native probe; no G03/G04 pass is a prerequisite
disguised as a deployment gate. DataAgent and Ontology creation/definition
APIs exist; Ontology creation is not Ontology MCP app-only/private support.
The published `fabric-data-agent-sdk` 0.1.36a0 documents the separate
`add_staging_ontology` / `list_ontologies` / `publish_staging` authoring path.
That management path uses delegated notebook-user authentication and workload
APIs, not an app-only public REST datasource entry. Do not invent ontology
definition files or copy a user bearer to the private worker. Fabric documents
REST-triggered notebooks under the triggering identity; private access,
notebook SDK availability, authorization and effective published ontology
binding still need an integrated and observed path. This authoring limitation
does not prove an app-only inference denial.
Sources: [SDK release](https://pypi.org/project/fabric-data-agent-sdk/0.1.36a0/),
[notebook security context](https://learn.microsoft.com/fabric/data-engineering/how-to-use-notebook#security-context-of-running-notebook),
[notebook APIs](https://learn.microsoft.com/fabric/data-engineering/notebook-public-api).

R4 separates the existing probe's 40 fields into start policy/limits,
generated outputs, discovery values and real reviews. The explicit
[v2 model correction](c1-preparation.md#explicit-c1-model-contract-correction-v2-2026-11-18)
replaces its unused model-deployment output and model-version start value with
a real Fabric AI-policy review (39 active mappings: 8/17/3/11).
The C1 container calls Fabric MCP, not an additional Foundry model; it does not
invent or assert Fabric's internal model version. The selected initial
actor and actual OAuth client must both be bound; a successful user metadata
read does not prove an app-only caller's write access. An existing bootstrap
secret can be supplied only through its protected file reference, independently
of the new runtime certificate contract. A credential key ID is not a secret.
The coordinator has now selected the original observed `DelegatedAzureCli`
profile for initial setup only; its private IDs are not copied here. No user
token goes to the private runner or Hosted runtime. Fabric `user_impersonation`
is not treated as either proven named write scopes or guaranteed denial; the
authorized endpoint operation must decide. Existing broad directory rights do
not authorize global setting changes. In particular, disabled
`WorkspaceBlockInboundAccess` with `canSpecifySecurityGroups=false` cannot be
made into an allegedly group-scoped change by this installer.
That disabled setting does not prevent an Azure workspace Private Link from
being prepared. Enabling it is a separate shared tenant-wide permission for
workspace admins to restrict their own workspaces, not an action that makes
every workspace private. Before actual data setup/public-deny evidence, the
specific activation requires its direct approval; no early approval question
is needed for code, CI or resource-free preparation. Allow up to 15 minutes
for this setting's propagation, separately from workspace-policy propagation.
The [tenant-settings index](https://learn.microsoft.com/fabric/admin/tenant-settings-index#advanced-networking)
states that disabling it restores all workspaces to their earlier configuration.
Therefore **never automatically disable it during C1 cleanup**: inventory the
shared effect and require a separate current administrative decision for any
later reversal. Other workspaces, BlockPublicInternetAccess, IP-firewall,
trusted-resource and cross-geo settings remain untouched.
An observed directory role is neither copied to runtime nor silently used as
a fallback for an unbound operation-specific permission path.
Graph `AppRegistration.Create`
alone does not cover app/SP/key/group lifecycle. A dedicated group and
scoped admin-resume gate must precede paid capacity where needed; no implicit
tenant-setting, consent, provider-registration or role escalation is included
here. Contributor is restricted to the agent workspace, never the source
workspace. The narrow candidate is a separate, closed, synthetic release
workspace containing only C1-A@1/C1-B@2, with excluded records in a distinct,
present deny scope. Viewer there is not permission to use a mixed source
workspace and does not prove OneLake ReadAll, SQL policy, revocation or
productive RLS. Required SQL/OneLake grants and ontology bindings still need
implementation and native checks; prompt filtering is not enforcement.
App-only means authenticated service access, not
anonymous Cohors use. Entra user login and separate Medical/RA approval stay
unchanged.

`FabricC1DeploymentTests` exercises synthetic API responses, not Azure,
Graph, Fabric or Foundry services. It covers creation/resume, foreign
conflicts, missing rights, lost responses, policy concurrency, missing
publication and incomplete output bindings. The same isolated hosted lane
builds the Web/Solution and executes these cases and the existing regressions.
It does not prove ARM schema/provider acceptance, effective permissions,
network paths, deployment, full output completeness, cleanup or costs.
All seven real C1 cases remain `NotRun`; no SQL/Docker/browser/cloud or local
native lane is added.

### Deliberately requested deployment artifacts

The original default lane is still upload-free. The coordinator's narrow
2026-11-18 amendment allows a **single** `cohors-fabric-c1-RUN-ATTEMPT`
artifact only for an explicitly selected push on this repository's trusted
Fabric branch. Put exactly one `Cohors-C1-Package: requested` trailer in that
specific commit to request it. This avoids changing the primary/default branch
merely to register a `workflow_dispatch` workflow. PRs and ordinary pushes
cannot publish it. The same job must first finish all existing validation.

`scripts\fabric\Build-C1Package.ps1` cross-publishes self-contained win-x64 and
linux-x64 entries using the pinned SDK and unchanged locked dependencies.
The first real publish attempt found NU1004: the original generic lockfiles
have no RID targets. `CohorsC1Publish=true` therefore selects separately
versioned `packages.c1.lock.json` files for the same five projects; normal
builds keep their original locks. An explicit, mutually exclusive
`Cohors-C1-Locks: requested` source-maintenance commit can generate candidates
after validation in the same hosted job. It compares every base package/hash
against the original lock, emits bounded hash-bound source text only, and
cannot publish/upload a binary. Candidates must be reviewed and committed
before a subsequent locked artifact run; no unlocked publish fallback.
The hosted diagnostic identified .NET 10 framework-package pruning, not a
package-version upgrade, in `Cohors.Infrastructure`. C1 publishing retains
only that library's twelve previously locked ASP.NET framework references.
The existing .NET runtime pruning (including DiagnosticSource and Unsafe)
and the API's pruning remain unchanged. The full base-graph equality check
stays enforced; no lock content is synthesized by hand.
The first committed RID locks are NuGet output from source
`59d3da44e30f443ace0f016ebdc530cb2a75cf06`, hosted run
`44321528471` (attempt 1). Their import verified each emitted file's length
and SHA256 before creating the five new source files. That run uploaded
nothing and is not a native deployment result.
`scripts\fabric\c1_package.py` derives the exact binary allowlist from the
published dependency graphs; its explicit `SOURCE_ASSETS` lists the only
IaC/templates, unchanged synthetic source, license and verification-tool files.
No `.local`/`.env`, private start bindings, keys/PFX, tokens, state, logs, test
results, PDBs or unknown package files are included. Content checks include
credential-shaped strings in binary UTF-16 as well as text. Unknown entries,
unbound dependencies, path aliases/links, or an over-limit archive fail closed.
Package contract `cohors-fabric-c1-package-2` adds only the explicitly listed
`scripts/fabric/c1_private_stage.py` operator asset. Package-3 additionally lists
`infra/fabric/c1.network-access.json` for the explicitly selected isolated
Firewall/P2S path. Historical package-1 and package-2 verification retain their
original exact allowlists; neither silently gains a new file. Existing archive
hashes are not altered.

Each included file has a length and SHA256 in `manifest.json`, bound to the
source SHA, repository/branch/workflow, run/attempt, SDK and source lock hashes.
`archive.json` separately binds the actual inner ZIP and manifest digests.
The artifact service's actual outer ZIP digest/ID are recorded separately.
One upload, `retention-days: 1`, at most 256 MiB compressed including the outer
ZIP allowance; no quota increase, replacement/deletion of artifacts, new
secrets, OIDC, attestation permission or CI cloud operation.

`scripts\fabric\Get-C1Package.ps1 -RunId ID -SourceCommit SHA -Destination ABSOLUTE_NEW_DIRECTORY`
reads authenticated GitHub provenance using the existing account, checks the
exact successful run/attempt, downloads a size-bounded archive, and verifies
outer/inner digests, the manifest, every file hash and extraction paths.
It **does not execute** the payload. Cross-publish is not native Windows
verification. Optional `-RetainTransportArchive` additionally preserves the
exact verified outer bytes as `transport.zip`, never a recompressed replacement.
It performs no upload. Normal download behavior still removes the temporary ZIP.

The source-only private receiver `scripts\fabric\c1_private_stage.py --profile
PRIVATE_JSON --allowance PRIVATE_ALLOWANCE_JSON --destination NEW_PRIVATE_DIRECTORY` is a bounded **consumer**
for the already specified isolated C1 Blob storage and VM system identity:

- It accepts only the versioned private-stage profile, binding deployment hash,
  source/run/attempt, actual outer archive digest/length, both verified Python
  source hashes, VM/storage resource IDs in the same subscription/group,
  tenant/client/object/issuer, private addresses and private-route review.
  The profile also binds an absolute expiry and at most 300 seconds per invocation.
  These values must come from real provisioning and the verified operator
  package, not fabricated review hashes. The driver must cap expiry and request
  accounting by the original deployment contract; this helper does not create
  or renew that contract.
- Native invocation also requires its original `cohors-fabric-c1-stage-allowance-1`
  sidecar. Before DNS/token/Blob I/O, an exclusive, file-and-directory-fsynced
  claim consumes the profile once in
  `/var/lib/cohors-c1/{deploymentBindingHash}/artifact-stage`. Both protected
  input files belong in that directory; the new destination must be its child.
  Moving the allowance, changing the invocation ID or choosing another
  destination cannot create another claim for the same profile. A failed or
  uncertain claim/download/result remains spent, without an automatic retry.
  The overall timer includes claim and result persistence. A successful result
  is persisted separately before output and binds the profile, invocation,
  allowance and original control-journal receipt. A profile alone is no longer
  native execution authority. This is a trusted-VM state boundary, not a fence
  against a privileged administrator deleting or replacing the claim.
- Before credentials, DNS must return only reviewed RFC1918/ULA addresses.
  The socket connects to one of those addresses without a second DNS lookup,
  retains the original storage FQDN for certificate verification/SNI, requires
  TLS 1.2 or higher, and has no proxy, redirect, alternate trust file or public
  fallback. It does not configure DNS, VPN, Relay, a public endpoint or a tunnel.
- Exactly one fixed VM IMDS token request and one Blob GET are allowed by this
  invocation; there are no retries. The token must bind the VM's own managed
  identity, tenant, storage audience, issuer and lifetime; delegated `scp`
  tokens and other callers are rejected. Metadata checks are not a local
  signature/RBAC verdict: Storage must actually authorize the request.
  The receiver needs only Blob read on its exact own artifact path; the
  separate reconciler below supplies that narrower custom role/condition.
  No role is granted by this script, and VM managed identity is a
  VM-wide boundary, not per-process isolation.
- The only Blob path is
  `c1-artifacts/{deploymentBindingHash}/{sourceCommit}/{outerArchiveSha256}.zip`.
  The size-bounded stream and complete version-bound package allowlist/hashes are checked
  before extraction; downloaded tool hashes must match the running receiver
  and verifier. Neither the binaries nor packaged scripts are executed.
  Profile/destination directories are owner-only and link-free on Linux.
  An overall process timer bounds stalled network/verification work, in
  addition to socket and monotonic/absolute deadlines. Failed partial extraction
  is never reused or reported as ready; it remains a named cleanup item.
  Temporary download files are removed. Errors expose codes, not provider
  bodies, token strings or exception text.

The hosted lane tests this with synthetic files and mocked transport only;
the real worker CLI refuses GitHub Actions before reading a profile or obtaining
a token. There has been no private Blob/IMDS request, upload or native execution.
Full-driver integration of the operator upload, exact RBAC/readback, trusted bootstrap
delivery through the Guest Agent, FQDN egress, durable result reconciliation and
ordered Blob/VM cleanup are **not integrated** by this receiver. The five
whole-profile blockers therefore remain. No user bearer, GitHub credential,
storage key or SAS is accepted by or copied to this worker. This implementation
does not implement the now-selected B5 P2S connection or supply a full installer,
G03/G04 pass, independent cloud stop or monetary hard cap.
Sources: [VM IMDS identity protocol](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/how-to-use-vm-token),
[Blob GET and least-privilege authorization](https://learn.microsoft.com/rest/api/storageservices/get-blob).

The source publisher `PublishPrivateArtifactNative` now complements this
receiver without changing its identity boundary. It uses only the explicitly
bound initial deployer credential (the selected original CLI user in the current
profile), never forwards that credential/token to the VM, and refuses CI before
credential use. Foundation declares a separate nonpublic `c1-artifacts`
container in the own C1 storage account. Publication requires the protected
`transport.zip` and exact `download-provenance.json` retained by
`Get-C1Package.ps1 -RetainTransportArchive`; source/repository/branch/workflow,
run/attempt/artifact, archive digest/size and original expiry are bound.
The prior package verifier remains responsible for complete payload validation;
a provenance file is not a fresh GitHub signature or native execution proof.

The publisher hashes the retained file before service I/O, reads current own
storage/container privacy, and uses the reviewed private storage client with
unchanged FQDN/TLS and explicit storage audience/issuer checks. Only `BlobNotFound`
permits the first `PUT` with `If-None-Match: *`. A complete streamed SHA256
readback, exact original creation metadata and strong ETag are required.
Unjournaled blobs are never adopted or overwritten; a lost write only
reconciles, and a known artifact cannot silently change storage generation.
An explicit 401/403 is recorded for a later explicit same-intent invocation, not
retried automatically. Original publication time/request accounting survives
resume and cannot borrow checkpoint allocations or cleanup reserve.

These are source/fake-transport contracts, not an actual upload or B5 proof.
The native publisher now reconciles its own artifact-access intents before
publication. Two separate custom roles grant only Blob read/write to the bound
initial deployer and Blob read to the own probe VM's system identity. Both roles
are assignable only in the own resource group; the assignments target only
`c1-artifacts`, require the exact deployment/source/archive path and exclude
`Blob.List`. Neither role grants management, delete, delegation or SAS/key access.
The publisher assignment is `User` only for the already selected delegated
initial actor; it is not assigned to the other VPN users and confers no Cohors
or Fabric business rights. The VM assignment remains a distinct service
principal. Current storage/container privacy and the original VM instance,
tenant and system identity are checked before the first IAM mutation. The
retained archive is hashed first, not merely trusted because a local manifest
names it. Original target and complete role/grant requests are journaled before
provider I/O; no publication clock starts while these access prerequisites run.

The shared checkpoint/artifact reconciler preserves exact current readbacks,
unknown-write fencing and the original control budget. A same-name role/grant
with changed previously observed creation metadata is not adopted on resume.
`CleanupPrivateArtifactAccess` removes the original publisher/receiver grants
before their subscription-level role definitions, with fresh endpoint-absence
readbacks and the original cleanup reserve/deadline. It never deletes the
initial user, VM identity, archive data or unrelated grants. An uncertain delete
only reconciles; a missing uncertain create is not proof of absence. Historical
receipts survive cleanup but cannot be reused for native publication.

This specifies and exercises the source path, not native ABAC acceptance,
propagation, exclusive effective access or effective revocation. Existing or
inherited grants still require separate native positive/negative access checks;
ARM does not supply a claimed CAS fence against another privileged writer.
`PreparePrivateArtifactStage` now derives a protected delivery bundle from the
original confirmed publication, exact access inventory and retained ZIP. It
rehashes the entire outer archive, checks the provenance-bound inner ZIP and
extracts only the two size-bounded bootstrap tool entries, without trusting a
separately modified extracted copy or replacing the existing complete Python
package verifier. A current own-VM readback and one targeted GET of that VM's
system service principal bind its client/object/resource identity; no human
directory search or application-owner tenant assumption is used.

The original control journal prepays the receiver's two HTTP requests after
those two identity GETs. Its immutable profile/allowance/tool bundle and
bounded clock survive resume without another debit, new invocation or extended
expiry. Cleanup reserve and checkpoint allocations cannot be borrowed. An
uncertain journal save fences further I/O until exclusive original-journal
recovery. The profile uses ASCII canonical JSON **plus LF** for the Python wire
hash, without changing global audit serialization; the hosted source tests pass
the actual .NET bundle to the Python validator. These are synthetic transport
and filesystem checks, not a guest execution or an effective-access proof.

`AdvancePrivateArtifactDelivery` adds a source-level Managed Run Command
component for that original bundle. Before the command GET/PUT, it freshly
checks the original VM instance/system identity, explicit Linux/Guest-Agent/
extension settings, running power state and ready Guest Agent >= 2.4.0.2.
The complete inline script, VM generation and allocation receipt are persisted
before command I/O. It creates at most the one original
`runCommands/c1-stage-{invocationId}` resource through ARM 2024-07-01; it does not
use a script URI, storage key/SAS, output Blob, alternate run-as account or user
bearer. The existing original control budget and receiver deadline bound every
request, including a recheck after debit persistence. There is no separate
polling budget or automatic create retry. An unknown create plus 404 stays
pending; a matching unjournaled resource is not adopted.

The generated inline Python writes the original profile, allowance and two
hash-bound tools only to the fixed own owner-only Linux directory. Existing
files must be byte-identical, private, regular and unlinked; writes and reused
files are fsynced. The original timer also covers materialization/import before
the receiver's own one-use timer takes over. The receiver still verifies and
extracts without executing the package. Native delivery refuses CI; a source
test runs only the actual generated file-materialization function in a private
temporary test directory, not the native receiver or an Azure operation.

Provisioning `Succeeded` alone never verifies reception. Fresh expanded
instance view must report execution `Succeeded`, exit code zero, no error and
start/end within the original window. Its complete JSON output must fit the
provider's 4 KiB tail and bind all original archive/profile/allowance/invocation
hashes, the private connected/resolved addresses, exactly two HTTP requests,
zero retries and no payload execution. Duplicate, truncated, oversized,
changed or incomplete results are rejected. Only the persisted bound result
produces `VerifiedNotExecuted`; uncertain saves fence I/O until original-journal
recovery. Previously observed non-default execution start times cannot change.

`CleanupPrivateArtifactDelivery` is deliberately command-inventory-only. It
checks the original VM generation, reads the exact original command before
DELETE and requires a fresh 404 afterwards. Only the parent VM GET and that
specific command's GET/DELETE are admitted; operation-location URLs are not
followed. Unknown creates/deletes only reconcile, without repeated deletion or
adoption of a replacement. It uses the original cleanup reserve/deadline even
after the receiver window expires. ARM endpoint absence is **not** a proved
worker stop, effective permission revocation, VM/artifact-data cleanup or
complete C1 teardown. A privileged concurrent writer is not fenced by these
ARM inventory checks.

These are source/fake-provider and filesystem contracts, not actual Guest Agent
execution, private transport or B5 evidence. The publication profile is not yet
exported by a complete driver; native FQDN egress, execution/result persistence,
independent stop and ordered Blob/VM teardown still need complete-driver
integration and real readbacks. The worker still uses only its own IMDS
identity; no user bearer, GitHub credential, SAS or storage key is part of its
profile or payload. Managed Run Command does not supply delegated notebook
authoring or the selected operator P2S connection.
There is no new runnable partial paid `apply`.
References: [conditional Blob publication](https://learn.microsoft.com/rest/api/storageservices/put-blob),
[exact Blob path conditions](https://learn.microsoft.com/azure/storage/blobs/storage-auth-abac-examples#blob-container-names-or-paths)
and [conditional user/service-principal role assignments](https://learn.microsoft.com/azure/role-based-access-control/conditions-role-assignments-cli).
Managed command contracts:
[Linux requirements, execution status and output limits](https://learn.microsoft.com/azure/virtual-machines/linux/run-command-managed),
[create/update 200/201](https://learn.microsoft.com/rest/api/compute/virtual-machine-run-commands/create-or-update?view=rest-compute-2024-07-01),
[VM-scoped GET with instance view](https://learn.microsoft.com/rest/api/compute/virtual-machine-run-commands/get-by-virtual-machine?view=rest-compute-2024-07-01)
and [delete 200/202/204](https://learn.microsoft.com/rest/api/compute/virtual-machine-run-commands/delete?view=rest-compute-2024-07-01).

### Original Fabric operation polling

The shared Fabric LRO path retains provider `Retry-After` values beyond 30
seconds, rounds future HTTP-date delays upward, and durably binds the next
permitted poll to the exact original operation. Creation and definition-read
LROs resume that operation rather than repeating their POST. Cancellation,
uncertain journal writes and process restart cannot reset the original wait,
request counter, cleanup reserve or control deadline. A provider minimum that
cannot fit the original window blocks before another request; it is never
shortened to make the run appear feasible. Status/result throttling is surfaced
without an automatic retry and retains its minimum for explicit resume.

The optional journal extension is omitted when absent, preserving historical
canonical payloads. An interrupted historical LRO without its original poll
schedule is not silently assigned a new one. Only the matching completed
receipt clears that operation's schedule. Exact provider authority/path checks
are unchanged. Hosted fake-clock cases cover initial, progress and result waits,
resume, malformed/missing schedules, throttling and uncertain persistence.
This prerequisite does not implement notebook materialization or prove a native
Fabric job, B5, source permission or private transport result.
Reference: [Fabric long-running operations and polling headers](https://learn.microsoft.com/rest/api/fabric/articles/long-running-operation).

### Delegated synthetic source notebooks

`FabricC1SourceNotebook.cs` prepares one own notebook in each dedicated source
workspace, never in the runtime Contributor workspace. The build embeds the
unchanged five-row synthetic fixture. Only `C1-A@1` and `C1-B@2` go into the
Released notebook; the other three rows go into Denied. Before creating a
notebook, the original delegated bootstrap user, three distinct workspaces,
current capacity/ownership, full denied communication policy and private
Lakehouse metadata must match. Schemaless and documented `dbo` layouts are bound
from the readback; no schema upgrade or new identity is inferred.
The workspace GET itself uses the private workspace host. Only the documented
communication-policy exception and tenant control API retain their control
hosts; ordinary workspace metadata is not treated as a public-path exception.

The original source/code hashes, invocation and control window persist across
resume. A PySpark `FabricGitSource` definition binds the own default Lakehouse.
The `ipynb` definition readback must contain exactly the original code cell in
the documented `artifact.content.ipynb` part, with no remote or ambiguous
content. Shared definition LRO handling retains the same original poll schedule.
The generated program checks actual NotebookUtils workspace, Lakehouse and user
context, refuses pipeline/reference execution and existing tables/paths, writes
Delta with `errorifexists`, then reads the actual schema, bounded complete
rowset and Delta table ID. Native entry is forbidden in GitHub Actions.

`FabricC1SourceMaterialization.cs` explicitly uses the documented on-demand
Notebook workload route and its `beta=true` exit-value readback, not a generic
Core `Completed` status or scheduler identity. An original intent precedes the
one-use submission. Unknown POST outcomes never replay or adopt a latest run.
Only a returned job ID bound to the exact workspace/notebook is accepted;
canonical public Location values are identifier metadata only and are never
followed. Every job read is reconstructed on the original private workspace
client. The provider polling minimum is persisted without shortening the
original window; missing minima block, throttling is surfaced without automatic
retry, and explicit advances before the minimum perform no network request.

Completion requires the exact source/invocation hashes, actual original user,
notebook/Lakehouse/workspace IDs, full expected rows, Delta ID and bounded
provider/script timestamps. A later result cannot replace an accepted receipt.
Only separate completed Released and Denied receipts can produce the combined
materialization receipt. Neither that receipt nor the hosted fake-provider and
generated-Python cases prove effective source grants, native API/private-path
support, runtime `userId` correspondence, B5, G03/G04 or domain acceptance.
The full paid installer remains blocked; this notebook component introduces no
source grant, delegated token relay, native notebook run, new validation lane
or C1 model requirement.

References: [notebook definitions](https://learn.microsoft.com/rest/api/fabric/articles/item-management/definitions/notebook-definition),
[Lakehouse metadata and default schema](https://learn.microsoft.com/rest/api/fabric/lakehouse/items/get-lakehouse),
[runtime context](https://learn.microsoft.com/fabric/data-engineering/notebookutils/notebookutils-runtime),
[notebook workload execution and exit values](https://learn.microsoft.com/fabric/data-engineering/notebook-public-api).

### Closed-source role metadata

`FabricC1SourceAccess.cs` now prepares the source-rights metadata step under the
same original delegated user and control budget. It requires both original
materialization receipts, fresh private source/policy metadata and a closed
Released workspace: exactly the own Lakehouse, original source notebook and
its linked ready SQL endpoint, with exactly one managed Delta table at the
original OneLake path. No mixed workspace, foreign table/shortcut, group,
source Contributor or runtime member of Denied is accepted.

The only new data role permits `Read` on `Tables/[dbo/]c1_sources` for the own
runtime service principal and explicit tenant. The single-role preview POST
uses `dataAccessRoleConflictPolicy=Abort`; no collection replacement, default
reader edit, wildcard, SQL-mode switch or item sharing is performed. A separate
original-ID-bound Viewer assignment supplies the workspace prerequisite.
Unknown POST outcomes reconcile only the original intent and are never
replayed. Unjournaled roles/assignments and changed role versions block rather
than being adopted. Complete role readbacks before and after the Viewer step
must preserve the exact definition and per-role `eTag`. Explicit 401/403
denials are retained separately from uncertain outcomes.

The result is **role metadata only**, not the final `source-grants` receipt.
OneLake roles, SQL `ReadData` and workspace Viewer are not interchangeable.
SQL endpoint mode, effective positive/negative access, SQL inventory and
revocation/propagation remain separately unproven; there is no implicit
workspace-wide SQL-mode change. The documented name-based role DELETE has no
conditional `If-Match` contract, so this component does not pretend to provide
atomic role cleanup. Hosted synthetic cases are not native entitlement, B5,
G03/G04 or full-installer acceptance. No native grant has been made.

References: [single-role create/update and Abort](https://learn.microsoft.com/rest/api/fabric/core/onelake-data-access-security/create-or-update-single-data-access-role),
[role listing and versions](https://learn.microsoft.com/rest/api/fabric/core/onelake-data-access-security/list-data-access-roles),
[role deletion](https://learn.microsoft.com/rest/api/fabric/core/onelake-data-access-security/delete-data-access-role),
[private-link API and item-sharing boundaries](https://learn.microsoft.com/fabric/security/security-workspace-level-private-links-support),
[table inventory](https://learn.microsoft.com/rest/api/fabric/lakehouse/tables/list-tables),
[OneLake and SQL identity modes](https://learn.microsoft.com/fabric/onelake/security/sql-analytics-endpoint-onelake-security).

### Original synthetic workspace lifecycle cleanup

`FabricC1SourceCleanup.cs` adds a bounded component for the original materialized
Released or Denied workspace, not a name-based OneLake role revoke. It requires
the original workspace creation request/ID/marker/capacity, notebook definition,
completed original job and exact materialization receipt. The Contributor agent
workspace, unresolved jobs and supplied or adopted workspaces are excluded.
Fresh private metadata must still describe the closed own item/table inventory,
original administrator and only the recorded runtime Viewer/table-read role.
Changed or unjournaled memberships/roles block deletion.

The global cleanup fence prevents further provisioning. Every metadata and
DELETE request consumes the original total and reserved cleanup budget with
the same bounded deadline. Before the single workspace-ID DELETE, the original
target/materialization intent is durably journaled. A lost response or still
visible workspace permits only original-target GET reconciliation, never a
second DELETE. Explicit 401/403 denials are retained separately; restored
workspaces are not deleted again. An uncertain journal save fences all work
until the original journal is exclusively reopened.

Only a fresh private workspace GET returning 404 records active inventory
absence; DELETE 200 alone does not. This does **not** prove effective access
revocation, all workers stopped, soft-delete/retention purge, removal of ARM
private-link/endpoints/capacity, hub offboarding or complete C1 cleanup.
Original creation and evidence records are retained. The component does not
remove hub resources or identities, expose full `apply`, or supply the
independent deadline executor. No native deletion has been performed.

Reference: [delete the specified workspace and its items](https://learn.microsoft.com/rest/api/fabric/core/workspaces/delete-workspace).

## Shared-hub consumer

The sole schema authority is infrastructure's `cohors-shared-hub-consumer-1`,
pinned to SHA256
`43eee0651b0525f42115185540324e447a78ff0ff82eba73dd1087187c10ca19`.
Earlier v1 draft hashes are not accepted. The Fabric input binds the expected
hub generation, opaque hub hash, owner resource group, infrastructure commit,
compiled-template digest, original four-user/evidence hashes and reviewed
spoke/local-route/egress requirements. None are populated with live identities
in public source. The owner consumes Fabric's existing canonical manifest hash
and `deploymentId`/`manifestSha256` tags; Fabric does not change its historical
payload hashing to the owner's Python wire format.
The owner's final Git byte policy is `-text`, preserving the sole pinned
schema rather than accepting a second normalized hash. The mirrored schema
and synthetic fixtures retain their original bytes under the same policy.

`bind-hub --manifest PRIVATE_FILE --journal EXISTING_PRIVATE_FILE
--hub-receipt PRIVATE_FILE` imports the owner-issued receipt using the original
bound actor. It performs only exact, budgeted ARM GETs in the hub and own spoke
groups, checks actual body hashes/ETags and source provenance, then journals
the receipt and its history. It neither performs a directory lookup nor changes
Azure resources. It is prohibited on the credential-free build runner, requires
the existing private journal, and cannot restart budgets or timeboxes.
Same-revision refresh may advance only the observation timestamp/digest;
resource rebinding needs a new revision. Unknown journal writes fence all work.
Metadata keeps traffic/capacity/cost acceptance false and is not B5 acceptance.

`PrepareSharedHubStage` and the existing journaled `DeployArm` use exact
source-owned templates/parameters. They implement the cycle-free handshake:
reservation, own four-subnet `hub-spoke`, own `hub-peer` with remote gateways
false, owner `peered` receipt, own `hub-transit` with remote gateways true,
then a fresh owner `connected` receipt before workload/private-endpoint stages.
Only Fabric's `to-shared-hub` half is written; infrastructure owns `to-fabric`.
The original own completed requests and VNet generation are required, not just
same-name live resources. Lost ARM responses never authorize a replacement PUT.
Readback binds both peer directions, gateway flags, workload-only default UDR,
DNS proxy and exact restrictive NSGs, including an explicit platform-DNS
bypass deny. Fresh rule-group, DNS-rule/link and own-peer collection reads
reject entries omitted from the receipt. Firewall ordering and source ranges
cannot grant Fabric access through another consumer's rule; the DNS filtering
policy must retain its enabled allow-list and unmatched-query block.
The shared network is explicitly one
failure domain, not permission for spoke-to-spoke or VPN database access.

`c1.hub-spoke.json` has no gateway/firewall/public-IP resources. The adapter
reuses the existing foundation workload definitions but excludes their legacy
network and DNS resources; the private-endpoint adapter excludes zone groups.
Shared zones, records and links remain hub-owner-only. Operator HTTPS access
can be prepared only after `data-bound`, to exact observed own
workspace/vault/blob PE addresses on 443, never entire endpoint subnets or
VM/ACR/Hosted/database targets. No VPN audience, shared assignment or hub IAM
is created or deleted by the consumer. Its write fence preserves only the
three already-defined own storage custom-role IDs outside the own resource
group, not any hub or unrelated subscription role.

These are source components, not a new paid partial-apply command. Complete
driver composition, owner-issued live receipts, endpoint/DNS registration,
independent stop and ordered own-peer offboarding remain activation gates.
Own work and grants must stop before removing the own peer; infrastructure
then releases only this consumer's references. No hub or shared audience
deletion is inferred from consumer cleanup. The human Web/API HTTPS,
probe/proxy/limits contract is still unqualified, so `web-bound` is rejected;
Hosted 8088 and Fabric MCP/SSE are not substituted as WAF backends.
`tests\fixtures\fabric\shared-hub` contains the owner's explicitly synthetic
collector/emitter outputs for reserved, peered, connected and data-bound,
with manifest SHA256
`bbc2fa0bf89e44ad85e364b6cfb75ecf705d652ced75d1f2e35dd5d7a9910baa`.
Tests compare original byte/canonical digests with Python. The reservation
import test explicitly rebinds a copy in memory to a synthetic Fabric input;
own lifecycle tests separately construct source-owned requests and journals.
The original fixture is not adopted as an own provisioned consumer and no
original schema, receipt or ARM file is rewritten. Focused source execution
uses only the existing hosted lane, with results bound to its published SHA.
Historical R4-R7, failed/NotRun cases, deadlines and budgets remain unchanged.
No native B5/C1/model/full-installer pass follows.

## Fork and isolation

The initial clean worktree HEAD and merge base were both
`445efc75c0b79dcfa9f23489593df0ddb8be02dc`. Its parent is
`b5698387f84ce2f260a1da64f32e8601fd69b0af`. The independent implementation branch
is `micnhs-cohors-fabric-umsetzung`; no automatic merge, rebase or runtime
dependency on the primary branch is permitted.

[Delivery identity](../../fabric/delivery.json) binds the fork, canonical
terminology workbook and approved reference hashes. The whole fork commit
binds application source, configuration, schemas, rules, contracts and package
locks. Inherited source is not an acceptance certificate. Uncommitted files
from the primary worktree are neither inputs nor dependencies.

The generated concept, baseline workbook and config are retained byte-for-byte.
The distributed baseline manifest replaces only private workstation source paths
with document labels; its original hash and unchanged source hashes are retained.
Their historical "awaiting approval" labels are preserved:
the subsequent implementation approval does not rewrite the source documents
or imply approval of cloud resources, real communication or medical operation.

Source isolation is established. A single standard GitHub-hosted build/contract
job on this exact branch is allocated, without SQL, Docker, browser, Azure login,
secrets or shared caches. Normal pushes remain upload-free. The coordinator's
2026-11-18 narrow amendment permits one deliberately requested C1 code artifact
after the same job has completely passed existing validation.
Public-source/history checks must precede push.
Host, port, quota and cloud-resource isolation are NOT established merely by
this branch. Existing primary helper
scripts, containers, SQL instances, volumes, local settings, credentials and
deployment defaults must not be used. No primary process has been started,
stopped or modified by this implementation.

## Mandatory scope and evidence

All 118 original requirements (104 Muss, 14 Soll), X-01 through X-45,
23 T scenarios, 30 EU scenarios and nine scope clarifications are binding.
Original priorities, statuses and P/S/D/Z boundaries are unchanged.
In particular, X-12 transmission/jurisdiction preview and X-15 PSUR monthly
view remain obligations, not presumed completed features.

Evidence must distinguish specification, local execution, actual service
execution and functional/operational acceptance. Inherited passing test reports
must not be reused as Fabric evidence. Missing implementation stays visible.

The [implementation register](requirement-register.json) now gives every one of
the 163 IDs concrete UI/API candidate entry points, data, roles, recovery/gaps
and its own positive and negative specification (326 cases). Candidate source
and test files are not proof that the entire requirement is implemented.
[53 acceptance scenarios](acceptance-scenarios.json) and
[nine scope clarifications](scope-clarifications.json) retain the approved
wording. All feature, service and acceptance statuses remain open.

The register is reproducible entirely within this checkout:

```powershell
python .\scripts\fabric\baseline.py
python -m unittest discover -s .\scripts\fabric -p 'test_*.py' -v
```

Four lightweight tests cover missing/duplicate IDs, altered source priorities,
missing paths/test specifications and false promotion of inherited evidence.
These checks do not start .NET, SQL, Docker, a browser or any cloud service.
Edit the ID-specific map/profiles, then use `baseline.py --write` to regenerate
derived registers; never rewrite the approved workbook or concept.

Three additional workflow regression tests prohibit premature runner-context
use, inherited primary concurrency and expansion of the hosted execution scope.
The [commit contract](commit-contract.md) adds an executable, in-process
protection reducer without registering or switching any storage adapter.

## Blocking gates

G01 requires the complete journal/digest/geo-barrier/two-archive/certificate/
activation contract, including all interruption boundaries and independent
verification. A local transaction or a hash is not final business success.

G02 blocks production: after loss of A, B alone cannot finally acknowledge
normal commands. A second complete protection path, including historical
evidence, within 60 minutes is not demonstrated. Region C is only an option.
Awareness/deadlines continue during failure; local recording is not an RPO0 ACK.

G03 needs a real app-only Fabric IQ endpoint, permissions and source-version
proof. G04 needs the whole private path with public access disabled, including
Hosted Agents, IQ, cross-region protection and recovery. No fallback may
silently weaken these gates.

The [gate/evidence register](capability-gates.json) keeps these levels separate.
`python .\scripts\fabric\capabilities.py` checks evidence-level consistency;
`--require-runtime` currently fails closed with the exact outstanding gates and
allocations. A successful schema check is never permission to deploy.
[Native capability probes](capability-probes.md) record the exact missing inputs
and current endpoint/auth/network distinctions. Direct data-agent MCP documents
SP access; direct ontology MCP still excludes it. The private Foundry/Fabric
route has a documented path discrepancy and no actual end-to-end execution.

The first hosted attempt failed before any job started. The corrected
[build run](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44117767152)
binds `e6ba11f2bbd0ceadf4f2a02f20cd67466d46afed` and 24 selected .NET tests.
The first [model run](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44122134291)
binds `4e8d6388abedc6abd8353167af096b5723050302`: Web/Solution builds,
27 commit-model plus 24 existing in-process tests, and seven Python checks.
These are exact historical source-bound results, not blanket proof for later
edits, Fabric services, regional recovery or production.
The [extended model run](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44123472283)
on `4a5d38dec7543b30fa69663b11a0fcd01178cf6c` passes 31 commit-model cases
(55 .NET tests total), including native SQL digest formatting and ambiguous
JSON rejection. Both builds report zero warnings/errors.

## X-12 and X-15: frozen business read views

These features add business read projections and UI, not a Fabric adapter or
service acceptance. The original requirement register retains its specification
and acceptance boundaries; the following entry points implement the two wishes.

| Requirement | Implementation | Focused in-process regression class |
| --- | --- | --- |
| X-12 | `DispatchPreview.cs`, `DispatchPreviewService.cs`, `DispatchPreviewDialog.tsx`, Workbench and EUDAMED dossiers | `DispatchPreviewTests` |
| X-15 | `PsurMonthly.cs`, `AnalyticsService.Monthly`, `PsurMonthlyPanel.tsx`, PSUR workspace | `PsurMonthlyTests` |

X-12 uses `GET /api/cases/{id}/dispatch-preview?version=...` and
`GET /api/eudamed/dossiers/{id}/dispatch-preview?version=...&ledgerVersion=...`.
It shows the exact approved frozen payload, scope/payload hashes, package and
record versions, original/source references separately from actual attachments,
business and technical recipients, jurisdiction, countries, channel and explicit
simulation environment. Case metadata comes from the prepared source revision,
not current unapproved case fields. An old report lacking that source fails
explicitly rather than using live metadata. Reading/cancelling does not mutate,
reserve or send anything. Dynamic attempt IDs and AS4 security headers are not
presented as already frozen business content; the current paths remain simulators.

Fresh HTTP `submit` commands require the returned `confirmation` binding.
The existing application-level commands keep their legacy shape when this
optional property is absent, so stored receipt replay and original request
hashes are not rewritten. Existing receipts are resolved before the new HTTP
confirmation gate; supplied bindings are always checked. Existing four-eyes,
version, stale-profile, dependency, idempotency and Unknown/reconciliation
checks remain authoritative. After an unconfirmed POST the dialog does not
offer a blind second attempt. Preview is neither a new approval nor a claim
that referenced original files were natively re-read or that transport is ready.

X-15 uses `GET /api/psurs/{id}/months?version=...&productId=...`, authorizes the
entire frozen group and selects the exact stored report revision. It reads only
the snapshot: unique case IDs with bound revision/hash, UTC `OccurredAt` months,
clipped first/last months, exclusive N knowledge cutoff and separate late-known
cases through freeze. Unknown dates, earlier/later events, carry-over narrative
membership and known/unknown historical status are explicit. Open cases remain
in the event counts; freeze status never substitutes for cutoff status. A
non-calendar twelve-month report can occupy thirteen partial calendar months.
The selected family's table and chart never pool group members or overlapping
regions and never claim incidence. Missing/ambiguous historic manifests produce
`Unavailable` with `months: null`, not zero values or a live-data fallback.
No fields were added to old case/PSUR/package aggregates, signed payloads or
snapshot serializers.

The historical selections in `PreviewSubmission` and `Monthly` use the shared
`IStateStore.GetVersion<T>(kind, id, version, products)` operation. SQL binds
`Kind`, `Id` and `Version` against the existing `PK_AggregateVersions` and
preserves the previous product filter through the current aggregate's
`ProductId`. At most the selected historical payload is returned/deserialized;
there is no full-history enumeration or hidden current-state fallback. No schema
change or general history refactor is involved. Current permissions, all frozen
PSUR group members, preparation/hash/freeze checks and explicit missing/mismatch
errors remain authoritative.

The two regression classes exercise these application reads with a read-only
store double that rejects full-history/list/live-capture operations and leaves
unrelated raw payloads unread. They cover exact revision selection, missing or
out-of-scope records, changed hashes and both current/historical group scopes.
This is not execution evidence for SQL or a measured SLO/load result; input-signal
volumes are not counts of `SafetyCase` aggregates.

The existing single standard-hosted job includes both regression classes and
the Web/Solution build. Its exact source-bound log is the execution evidence;
it does not execute SQL, HTTP/API integration, a browser or cloud services.
Real UI/accessibility, API/authentication, transactional concurrency, stored
original availability and end-to-end simulator acceptance remain unproven here.
G01-G04 and all deployment/adapter gates remain open.

## Execution and deployment

The bounded [C1 client](c1-preparation.md) supplies the synthetic source,
null-bound preparation/execution templates and an explicit `--fabric-c1`
entry in the API executable, before its normal startup/DI. Existing .NET/Azure
Identity libraries implement fixed app-only credentials, bounded JSON/HTTP/SSE,
private-address binding, persisted original request/task/cursor state and the
separate Hosted Invocations entry/client. No storage/IQ/runtime adapter is
activated. No new package, local SDK/SQL/browser run or cloud allocation is used.

General cost approval and the bounded C1 execution request are recorded;
concrete private targets, identities/grants, model/budget/retention and cleanup
bindings remain necessary. The current credential-free checks use synthetic
HTTP handlers, not real DNS/TLS/Entra/Fabric/Foundry calls. They include protocol,
session/schema/claim drift, interruption without query replay, bounded response/
request/time handling and cancellation terminal readback. The executable's
unbound-input check exits before normal API startup or authentication.
No G03/G04 service or full seven-case acceptance follows from those checks.
Historical service failures/NotRun cases retain their original evidence.
App-only is authenticated service access: Entra user login and Medical/RA
approvals through the Cohors API remain unchanged. Its in-process checks are
not G03/G04 evidence or full pilot acceptance.

The [deployment plan](../../.azure/deployment-plan.md) records approval
boundaries. The Fabric-only push workflow runs one allocated standard hosted
build/contract job against the exact commit, with private temporary package/cache
directories. Inherited primary workflows are disabled in this branch only.
Local native execution and service validation remain unallocated. Before cloud execution, approve distinct
subscriptions/resources, regions, Fabric capacities/workspaces, identities,
network paths, costs and retention. Use synthetic data only.

The intended replacement order is persistence/evidence, knowledge/retrieval,
then hosted agents. Existing business logic, branding, approvals, deadlines and
Domibus/simulator semantics are retained. Adapter activation and productive
cutover remain blocked while their hard gates are open.

Rollback changes only this delivery's code/configuration. A decided command
rolls forward under the commit protocol; restoring an older image is never
permission to erase a decision, reuse an old writer epoch or resend an unknown
submission.
