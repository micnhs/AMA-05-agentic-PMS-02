# Cohors Fabric

Independent alternative based on ALT-FAB-01 v0.2 and FAB-BASELINE-01 v0.2.
This is an implementation in progress, not an accepted or deployed system.

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

## Execution and deployment

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
