# Cohors Fabric

Independent alternative based on ALT-FAB-01 v0.2 and FAB-BASELINE-01 v0.2.
This is an implementation in progress, not an accepted or deployed system.

## C1 provisioning increment: partial source implementation

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

Implemented source components:

- `infra\fabric\c1.foundation.json` describes an isolated F2 (overage disabled),
  private VM/VNet/subnets, DNS, Key Vault, private storage/ACR, and a Foundry
  account with injection at creation plus a separate project identity.
  The selected technical work profile is F2/FranceCentral, not a live quota/cost
  result. Client-subnet NAT plus a dedicated static egress IPv4 provide explicit
  translation without a public VM NIC; they are not a FQDN filter or a proven
  artifact/Managed Run Command path. Both remain in the cost/cleanup inventory.
  Vault purge protection and seven-day soft-delete retention are enabled; the
  deleted-vault/name tail is retained rather than falsely reported as purged.
  `c1.workspace-network.json` binds workspace PLS/PE/DNS from creation outputs.
- `FabricC1Provisioner` implements journal-before-effect creation, exact
  manifest/ownership checks, bounded inventory, unknown-create reconciliation
  without blind POST replay, ARM deployment polling, distinct ARM/Fabric
  capacity identities, Graph app/SP bootstrap, workspace/Lakehouse/DataAgent
  APIs, publication readback, and the agent-workspace-only Contributor grant.
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
leave paid resources waiting for these gaps. Firewall/VPN remain absent/off.

These are explicit **implementation gaps**, not assertions that Microsoft
lacks the corresponding APIs. Source-grant/ontology-private-link and the
custom Hosted app-only path may additionally encounter actual provider
limits during the bounded native probe; no G03/G04 pass is a prerequisite
disguised as a deployment gate. DataAgent and Ontology creation/definition
APIs exist; Ontology creation is not Ontology MCP app-only/private support.

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
secrets, shared caches or uploads. Public-source/history checks must precede push.
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
