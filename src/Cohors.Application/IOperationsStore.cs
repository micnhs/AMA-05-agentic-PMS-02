using Cohors.Domain;

namespace Cohors.Application;

public interface IOperationsStore
{
    Task<SafetyCase> Escalate(SafetyCase current, string[] alerts, DateTimeOffset at, CancellationToken ct = default);
    Task<PsurReview> Escalate(PsurReview current, string alert, DateTimeOffset at, CancellationToken ct = default);
    Task<EudamedDeliveryLedger> Escalate(EudamedDeliveryLedger current, EudamedCorrespondenceAlert[] alerts,
        StoreDependency[] dependencies, DateTimeOffset at, CancellationToken ct = default);
    Task<OperationsHeartbeat> RecordHeartbeat(OperationsHeartbeat heartbeat, CancellationToken ct = default);
}
