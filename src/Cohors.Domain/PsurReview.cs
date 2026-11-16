namespace Cohors.Domain;

public sealed record PsurReviewReport(VersionReference Source, string SeriesReference, int ReportNumber,
    DateOnly Start, DateOnly End, string SnapshotHash, string ScopeHash, string[] Products);
public sealed record PsurReviewQuestion(int Round, string Text, DateTimeOffset DueAt,
    bool ActionRequired, string ScheduleBasis, DateTimeOffset RecordedAt);
public sealed record PsurReviewAction(string Kind, VersionReference Source, string ProductId, string Title, string Status);
public sealed record PsurReviewDraft(PsurReviewReport Report, string Response,
    PsurDataSource[] Sources, PsurReviewAction[] Actions, string NoActionRationale);
public sealed record PsurReviewSubmission(int Round, PsurReviewQuestion Question, PsurReviewDraft Reply,
    string ScopeHash, Approval[] Approvals, string SimulatedReceipt, DateTimeOffset SentAt);
public sealed record PsurReviewAssessment(int Round, string Outcome, string Rationale,
    PsurReviewAction[] ActionEvidence, string RecordedBy, DateTimeOffset RecordedAt);
public sealed record PsurReview(string Id, int Version, string ProductId, string[] Products,
    string Reference, PsurReviewReport OriginalReport, string Jurisdiction, string Language, string Recipient,
    PsurReviewQuestion[] Questions, string Status, PsurReviewDraft? Draft, string ScopeHash,
    Approval[] Approvals, PsurReviewSubmission[] Submissions, PsurReviewAssessment[] Assessments,
    string[] Alerts, bool Simulated)
{
    public string Contract { get; init; } = "cohors-psur-review-1";
}

public static class PsurReviewRules
{
    public const string Recipient = "cohors:notified-body-simulator";

    public static PsurReviewReport Bind(Psur report)
    {
        var series = report.Snapshot.Series
            ?? throw new DomainException("psur_review_source", "Kontrollierte PSUR-Serie fehlt.");
        return new(new(report.Id, report.Version, Canonical.Hash(report)), series.Reference, series.ReportVersion,
            report.Snapshot.Start, report.Snapshot.End, report.SnapshotHash, report.ScopeHash,
            PsurGroupRules.Products(report.Snapshot).Order(StringComparer.Ordinal).ToArray());
    }

    public static string[] Products(PsurReview review)
    {
        var products = review.OriginalReport.Products
            .Concat(review.Draft?.Report.Products ?? [])
            .Concat(review.Submissions.SelectMany(s => s.Reply.Report.Products))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Guard.Against(review.Contract != "cohors-psur-review-1" || !review.Simulated ||
            review.Recipient != Recipient || review.Jurisdiction != "EU" || review.Language is not ("de" or "en") ||
            products.Any(p => !review.Products.Contains(p)) || !products.Contains(review.ProductId) ||
            review.Products.Distinct(StringComparer.Ordinal).Count() != review.Products.Length ||
            !review.Questions.Select(q => q.Round).SequenceEqual(Enumerable.Range(1, review.Questions.Length)) ||
            review.Questions.Length == 0,
            "psur_review_integrity", "BS-Vorgang hat keinen konsistenten synthetischen Produkt-/Empfaengerscope.", 409);
        return review.Products;
    }

    public static void RequireScope(PsurReview review, Actor actor)
    {
        foreach (var product in Products(review)) actor.RequireProduct(product);
    }

    public static string Scope(PsurReview review)
    {
        Guard.Against(review.Draft is null || review.Questions.Length == 0,
            "psur_review_draft", "Begruendeter Antwortentwurf und konkrete Rueckfrage erforderlich.");
        return Canonical.Hash(new {
            review.Contract, review.Id, review.ProductId, review.Reference, review.OriginalReport, review.Products,
            review.Jurisdiction, review.Language, review.Recipient,
            question = review.Questions[^1], review.Draft, boundary = "synthetic-correspondence-not-risk-acceptance"
        });
    }

    public static void RequireApproved(PsurReview review)
    {
        Guard.Against(review.Status != "ReplyApproved" || review.ScopeHash != Scope(review),
            "psur_review_gates", "Aktuelle, unveraenderte Antwortfreigabe fehlt.", 409);
        var medical = review.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == review.ScopeHash);
        var regulatory = review.Approvals.SingleOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == review.ScopeHash);
        Guard.Against(medical is null || regulatory is null || medical.ActorId == regulatory.ActorId,
            "psur_review_gates", "Zwei getrennte berechtigte menschliche Antwortfreigaben erforderlich.", 409);
    }
}
