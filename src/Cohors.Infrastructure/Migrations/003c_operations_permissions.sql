SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.CohorsOperationsWrite',N'P') IS NULL OR OBJECT_ID(N'dbo.CohorsOperationsQuote',N'FN') IS NULL
    THROW 51100,'Complete restricted Operations modules are required before schema readiness.',1;
IF DATABASE_PRINCIPAL_ID(N'cohors_operations_runtime') IS NULL
    CREATE ROLE cohors_operations_runtime AUTHORIZATION dbo;
IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'cohors_operations_runtime' AND type=N'R'
    AND owning_principal_id=DATABASE_PRINCIPAL_ID(N'dbo'))
    THROW 51100,'Operations role must remain owned by dbo.',1;

GRANT SELECT ON OBJECT::dbo.Aggregates TO cohors_operations_runtime;
GRANT SELECT ON OBJECT::dbo.AggregateVersions TO cohors_operations_runtime;
GRANT SELECT ON OBJECT::dbo.SchemaVersions TO cohors_operations_runtime;
GRANT EXECUTE ON OBJECT::dbo.CohorsOperationsWrite TO cohors_operations_runtime;

-- The same-owner procedure is the only write path; a broader accidental table grant must not bypass it.
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.Aggregates TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.AggregateVersions TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.Commands TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.AuditEvents TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.Outbox TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.Inbox TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.Deadlines TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.SearchProjection TO cohors_operations_runtime;
DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.SchemaVersions TO cohors_operations_runtime;
-- SQL permits a column GRANT to override a table DENY; bind the UPDATE denial at both levels.
DECLARE @columnDenials nvarchar(max);
SELECT @columnDenials=STRING_AGG(CONVERT(nvarchar(max),N'DENY UPDATE ON OBJECT::dbo.'+QUOTENAME(t.name)
    +N'('+columnList.Names+N') TO cohors_operations_runtime;'),CHAR(10))
FROM sys.tables t
CROSS APPLY(SELECT STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(c.name)),N',')
    WITHIN GROUP(ORDER BY c.column_id) AS Names FROM sys.columns c WHERE c.object_id=t.object_id) columnList
WHERE t.schema_id=SCHEMA_ID(N'dbo') AND t.name IN(N'Aggregates',N'AggregateVersions',N'Commands',N'AuditEvents',
    N'Outbox',N'Inbox',N'Deadlines',N'SearchProjection',N'SchemaVersions');
EXEC sys.sp_executesql @columnDenials;
DENY CREATE TABLE,CREATE PROCEDURE,CREATE FUNCTION,CREATE VIEW,ALTER ANY ROLE,ALTER ANY USER,ALTER ANY SCHEMA
    TO cohors_operations_runtime;
DENY ALTER,TAKE OWNERSHIP ON SCHEMA::dbo TO cohors_operations_runtime;

IF NOT EXISTS(SELECT 1 FROM dbo.SchemaVersions WHERE Version=3)
    INSERT dbo.SchemaVersions VALUES(3,SYSUTCDATETIME());
COMMIT;
