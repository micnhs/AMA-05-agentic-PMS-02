IF OBJECT_ID('dbo.Aggregates') IS NULL
BEGIN
    CREATE TABLE dbo.Aggregates(
        Id nvarchar(80) NOT NULL, Kind nvarchar(40) NOT NULL, ProductId nvarchar(40) NOT NULL,
        Version int NOT NULL, Json nvarchar(max) NOT NULL, UpdatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_Aggregates PRIMARY KEY (Kind,Id),
        CONSTRAINT CK_Aggregates_Json CHECK (ISJSON(Json)=1),
        CONSTRAINT CK_Aggregates_Version CHECK (Version>0));
    CREATE INDEX IX_Aggregates_Product ON dbo.Aggregates(Kind,ProductId);
    CREATE TABLE dbo.AggregateVersions(
        Kind nvarchar(40) NOT NULL, Id nvarchar(80) NOT NULL, Version int NOT NULL,
        Json nvarchar(max) NOT NULL, Hash char(64) NOT NULL, At datetimeoffset NOT NULL,
        CONSTRAINT PK_AggregateVersions PRIMARY KEY (Kind,Id,Version),
        CONSTRAINT CK_Versions_Json CHECK (ISJSON(Json)=1));
    CREATE TABLE dbo.Commands(
        IdempotencyKey char(64) NOT NULL PRIMARY KEY, RequestHash char(64) NOT NULL,
        ActorId nvarchar(100) NOT NULL, Result nvarchar(max) NOT NULL, At datetimeoffset NOT NULL);
    CREATE TABLE dbo.AuditEvents(
        Sequence bigint IDENTITY NOT NULL PRIMARY KEY, AggregateId nvarchar(80) NOT NULL,
        Version int NOT NULL, ActorId nvarchar(100) NOT NULL, ActorName nvarchar(150) NOT NULL,
        Roles nvarchar(1000) NOT NULL, Action nvarchar(80) NOT NULL, Reason nvarchar(4000) NOT NULL,
        At datetimeoffset NOT NULL, CorrelationId nvarchar(120) NOT NULL,
        PayloadHash char(64) NOT NULL, PreviousHash char(64) NOT NULL, Hash char(64) NOT NULL)
        WITH (LEDGER = ON (APPEND_ONLY = ON));
    CREATE INDEX IX_Audit_Aggregate ON dbo.AuditEvents(AggregateId,Sequence);
    CREATE TABLE dbo.Outbox(
        Id nvarchar(80) NOT NULL PRIMARY KEY, AggregateId nvarchar(80) NOT NULL, Version int NOT NULL,
        Type nvarchar(80) NOT NULL, Payload nvarchar(max) NOT NULL, CreatedAt datetimeoffset NOT NULL,
        Attempts int NOT NULL DEFAULT 0, Error nvarchar(200) NULL, CompletedAt datetimeoffset NULL);
    CREATE TABLE dbo.Inbox(
        EventId nvarchar(80) NOT NULL, Consumer nvarchar(80) NOT NULL, ProcessedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_Inbox PRIMARY KEY (EventId,Consumer));
    CREATE TABLE dbo.Deadlines(
        Id nvarchar(80) NOT NULL PRIMARY KEY, CaseId nvarchar(80) NOT NULL, ProductId nvarchar(40) NOT NULL,
        LegalDue datetimeoffset NOT NULL, SafetyDue datetimeoffset NOT NULL, Status nvarchar(40) NOT NULL,
        Version int NOT NULL);
    CREATE INDEX IX_Deadlines_Open ON dbo.Deadlines(Status,SafetyDue) INCLUDE(CaseId,LegalDue);
    CREATE TABLE dbo.SearchProjection(
        Id nvarchar(80) NOT NULL PRIMARY KEY, ProductId nvarchar(40) NOT NULL, Version int NOT NULL,
        Content nvarchar(max) NOT NULL, IndexedAt datetimeoffset NOT NULL, Hash char(64) NOT NULL);
    CREATE TABLE dbo.SchemaVersions(Version int NOT NULL PRIMARY KEY, AppliedAt datetimeoffset NOT NULL);
    INSERT dbo.SchemaVersions VALUES(1,SYSUTCDATETIME());
END;
