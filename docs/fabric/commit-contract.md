# Fabric commit contract: implementation and evidence boundary

The additive `Cohors.Domain.Fabric.FabricCommit` reducer implements the ordering
and binding checks from ALT-FAB-01 sections 3-4, 16 and 20-21. It is not registered
as an `IStateStore`, not a SQL transaction coordinator and not a production
certifier. Existing business models, canonical historical payloads, UI, approval
logic and SQL registrations are unchanged.

## Executable protection order

| Phase | Required bound observation | Forbidden before completion |
|---|---|---|
| Reserved | Environment, command fingerprint, complete certified read/write vector, predecessor, current writer/topology, historical protection inventory | A conflicting retry or a dependent successor |
| InputsProtected | Complete original bytes and replay envelope read back from both separately administered regional archives; exact hash/length/version, locked non-append retention | Preparation from a single archive or derivative bytes |
| Prepared | Complete pending revision, audit draft and blocked effect set bound to the envelope | Visible revision or released outbox |
| Decided | Journal incarnation, transaction, sequence, predecessor, epoch and read/write sets | Rollback after decision or an unknown response |
| BlockClosed | Complete native ledger digest identifying this decision/block/database | Geo protection based only on an earlier open block |
| GeoProtected | Barrier on the primary to the named secondary after digest closure, plus separate matching secondary decision/digest readback | Treating log durability as read readiness |
| ProofsProtected | Native digest and complete earlier proof bundle directly archived/read back in both regions | Signing from unpreserved proof material |
| Certified | RSA-PSS/SHA256 signature from the separately configured, versioned certifier key; exact authorization/rule/schema/proof scope | Final acknowledgement based on the signature alone |
| CertificateProtected | Separate, later A/B readbacks of the signed certificate | Claiming a certificate contains evidence of its own later storage |
| Activated | Exact certified revision vector, matching certificate and effect set, within effective retention | Final success for another revision or expired protection |

The request contains complete canonical afterstates, a canonical audit draft,
original references, effects, protection class and actual observation time.
Duplicate JSON property names are rejected. Fingerprints bind these values;
different bytes with the same CommandId conflict. An unresolved retry resolves
the existing command instead of opening a new reservation.

`CanAcknowledge` describes the completed contract at its activation boundary,
not the current health of remote storage or an ongoing RPO guarantee.
`ReadActivatedSnapshot` additionally requires matching current heads and fence;
a historical successful receipt must not overwrite a newer certified state.
`ExpectedActivationSnapshot` is only a candidate vector, never a business read.

Every transition checks the bound writer identity, epoch, journal incarnation
and topology. A change requires explicit recovery continuity; this model does
not invent a new epoch, create a replacement region or merge histories.
Both archive subscriptions/administrators and regions must differ as required
by the approved concept. Service identity comparisons treat GUID spellings,
DNS case/root notation and programmatic region case appropriately; they never
rewrite original bytes, object keys, business IDs or signed payloads. The archive
region set must equal the two journal protection regions, so A/C or C/D archives
cannot silently replace a complete A/B topology. A missing second archive or an incomplete historical
highwatermark blocks normal reservations. Region C is not selected.

Awareness uses the same final-acknowledgement barriers. The immutable request
preserves the original observation time. Local emergency observation, missing
originals and deadline/watchdog operation still require their real adapters;
this model does not turn local recording into a protected ingress ACK.

## What these tests do and do not prove

`tests/Cohors.Tests/FabricCommitContractTests.cs` uses explicitly synthetic
observations, bytes and an ephemeral in-memory signing key. It checks every
interruption boundary; incomplete/corrupt archives; retention and append flags;
dependent multiaggregate conflicts; stale fencing; digest/geo ordering and
secondary identity; incomplete proof preservation; forged/wrong-scope
certificates; certificate storage; exact activation; abort and historical retry.

The standard hosted workflow executes this class independently of SQL fixtures.
It verifies from the TRX results that every selected test class actually ran and
passed; an OR filter silently matching only older tests is not sufficient.
Only bounded counts and hashes are printed. TRX stays in this job's private
temporary directory and is not uploaded.

The reducer accepts typed **observations**, not service-authenticated receipts.
It cannot establish that an archive exists, that a policy is really locked,
that a journal transaction was serializable, that a verifier actually queried
SQL, or that a declared topology/inventory is trustworthy. These observations
must later be produced by independently authorized adapters and checked by the
real certifier. A signature authenticates its issuer and exact claims; it does
not make those claims true.

There is deliberately no runtime registration, live reservation store, native
ledger verifier, original-copy adapter, certificate issuer, recovery supervisor
or dispatch-claim implementation yet. No normal IQ read has been switched.
The existing domain medical/RA authorization remains authoritative and must be
independently rechecked by the actual certifier, not replaced by a test signer.

## Required native proof before adapter activation

See [capability probes](capability-probes.md). In particular:

1. Persist and race the same read/write vector under actual journal/Fabric
   transactions. Inject crashes and lost replies between every durable write.
2. Read bytes/policy/version from both actual WORM archives; attempt forbidden
   overwrite, append, retention reduction and privileged deletion.
3. Generate the native closed-block digest, then run the named SQL geo barrier.
   Demonstrate independent secondary read readiness and native ledger
   verification against externally retained, allowlisted digests.
4. Prove signer separation, rejection of unauthorized but valid ledger inserts,
   truncation/completeness detection, key continuity and terminal reservations.
5. Lose region A, fence every old writer/dispatcher and reconstruct all previously
   acknowledged data from B. Normal writes stay blocked until the second full
   protection path, including historical originals/proofs, is repaired.

G01 and G02 remain open. No RPO0, regional RTO, production acceptance or deployment
is established by the model or the hosted build.
