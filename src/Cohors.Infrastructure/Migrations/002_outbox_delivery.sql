IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version=2)
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    ALTER TABLE dbo.Outbox ADD AggregateKind nvarchar(40) NULL, ProductId nvarchar(40) NULL,
        LeaseOwner nvarchar(80) NULL, LeaseUntil datetimeoffset NULL, NextAttemptAt datetimeoffset NULL,
        RecoveryVersion int NOT NULL CONSTRAINT DF_Outbox_RecoveryVersion DEFAULT 0;
    EXEC sys.sp_executesql N'UPDATE e SET AggregateKind=a.Kind,ProductId=a.ProductId
        FROM dbo.Outbox e INNER JOIN dbo.Aggregates a ON a.Id=e.AggregateId
        WHERE (SELECT COUNT(*) FROM dbo.Aggregates x WHERE x.Id=e.AggregateId)=1;
    IF EXISTS (SELECT 1 FROM dbo.Outbox WHERE AggregateKind IS NULL OR ProductId IS NULL)
        THROW 51000,''Ambiguous legacy outbox scope requires explicit migration review.'',1;
    ALTER TABLE dbo.Outbox ALTER COLUMN AggregateKind nvarchar(40) NOT NULL;
    ALTER TABLE dbo.Outbox ALTER COLUMN ProductId nvarchar(40) NOT NULL;
    CREATE INDEX IX_Outbox_Delivery ON dbo.Outbox(CompletedAt,NextAttemptAt,CreatedAt)
        INCLUDE(Attempts,LeaseUntil,ProductId);
    INSERT dbo.SchemaVersions VALUES(2,SYSUTCDATETIME());';
    COMMIT;
END;
