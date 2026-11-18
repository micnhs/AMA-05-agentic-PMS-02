"""Identity/permission adapter only; all application DDL belongs to the original migrations."""

import uuid
import re
import hashlib

from primary_control import GateError


API_GRANTS = {
    "Aggregates": ("SELECT", "INSERT", "UPDATE"), "AggregateVersions": ("SELECT", "INSERT"),
    "Commands": ("SELECT", "INSERT"), "AuditEvents": ("SELECT", "INSERT"),
    "Outbox": ("SELECT", "INSERT", "UPDATE"), "Inbox": ("SELECT", "INSERT"),
    "Deadlines": ("SELECT", "INSERT", "UPDATE"), "SearchProjection": ("SELECT", "INSERT", "UPDATE"),
    "SchemaVersions": ("SELECT",),
}
OPERATIONS_ROLE = "cohors_operations_runtime"


def sql_grants(role, client_id, module_hashes=None):
    if role not in ("api", "operations"):
        raise GateError("No SQL runtime permission adapter exists for this role.")
    try:
        client = uuid.UUID(client_id)
    except (ValueError, AttributeError) as error:
        raise GateError("A real bound runtime client ID is required, never a SQL identifier/expression.") from error
    sid, name = client.bytes_le.hex(), "cohors_" + role
    tables = ",".join("(N'" + table + "')" for table in API_GRANTS)
    allowed_role = f"DATABASE_PRINCIPAL_ID(N'{OPERATIONS_ROLE}')" if role == "operations" else "-1"
    parts = [f"""
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF (SELECT COALESCE(MAX(Version),0) FROM dbo.SchemaVersions)<>3
    THROW 51000,'The original schema3 migrations must complete first',1;
DECLARE @objects table(Id int NOT NULL,Name sysname NOT NULL);
INSERT @objects SELECT t.object_id,t.name FROM sys.tables t
    JOIN (VALUES {tables}) wanted(Name) ON t.name=wanted.Name WHERE t.schema_id=SCHEMA_ID(N'dbo');
IF (SELECT COUNT(*) FROM @objects)<>9 THROW 51000,'Original application objects are incomplete',1;
IF EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{name}' AND
    (sid<>0x{sid} OR type<>'E' OR authentication_type<>4))
    OR EXISTS(SELECT 1 FROM sys.database_principals WHERE sid=0x{sid} AND name<>N'{name}')
    THROW 51000,'Runtime principal identity/type/alias mismatch',1;
IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{name}')
    CREATE USER [{name}] WITH SID=0x{sid},TYPE=E;
DECLARE @user int=DATABASE_PRINCIPAL_ID(N'{name}');
IF EXISTS(SELECT 1 FROM sys.database_role_members WHERE member_principal_id=@user
    AND role_principal_id<>{allowed_role})
    OR EXISTS(SELECT 1 FROM sys.schemas WHERE principal_id=@user)
    OR EXISTS(SELECT 1 FROM sys.objects WHERE principal_id=@user)
    OR EXISTS(SELECT 1 FROM sys.database_principals WHERE owning_principal_id=@user)
    THROW 51000,'Existing roles or ownership cannot be adopted or widened',1;
DECLARE @direct table(Class int,Major int,Minor int,State char(1),Permission sysname);
INSERT @direct VALUES(0,0,0,'G','CONNECT');
"""]
    if role == "api":
        rows = [f"(1,OBJECT_ID(N'dbo.{table}'),0,'G',N'{permission}')"
                for table, permissions in API_GRANTS.items() for permission in permissions]
        rows += [f"(1,OBJECT_ID(N'dbo.SchemaVersions'),0,'D',N'{permission}')"
                 for permission in ("INSERT", "UPDATE", "DELETE")]
        rows += [f"(3,SCHEMA_ID(N'dbo'),0,'D',N'{permission}')" for permission in ("DELETE", "ALTER")]
        parts.append("INSERT @direct VALUES " + ",".join(rows) + ";")
    parts.append("""
IF EXISTS(SELECT class,major_id,minor_id,state,permission_name FROM sys.database_permissions
    WHERE grantee_principal_id=@user
    EXCEPT SELECT Class,Major,Minor,State,Permission FROM @direct)
    THROW 51000,'Unexpected direct runtime rights must be reviewed, not silently inherited',1;
""")
    if role == "operations":
        if (not isinstance(module_hashes, dict)
                or set(module_hashes) != {"dbo.CohorsOperationsQuote", "dbo.CohorsOperationsWrite"}
                or any(not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value)
                       for value in module_hashes.values())):
            raise GateError("Operations grants require the actual compiled original SQL module-definition hashes.")
        parts.append(f"""
DECLARE @role int=DATABASE_PRINCIPAL_ID(N'{OPERATIONS_ROLE}');
IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE principal_id=@role AND type='R'
    AND owning_principal_id=DATABASE_PRINCIPAL_ID(N'dbo'))
    OR EXISTS(SELECT 1 FROM sys.database_role_members WHERE member_principal_id=@role)
    THROW 51000,'The original dbo-owned Operations role is missing or broadened',1;
IF OBJECT_ID(N'dbo.CohorsOperationsWrite',N'P') IS NULL OR OBJECT_ID(N'dbo.CohorsOperationsQuote',N'FN') IS NULL
    OR EXISTS(SELECT 1 FROM sys.objects o JOIN sys.schemas s ON o.schema_id=s.schema_id
        WHERE (o.object_id IN(SELECT Id FROM @objects) OR o.object_id IN(
            OBJECT_ID(N'dbo.CohorsOperationsWrite'),OBJECT_ID(N'dbo.CohorsOperationsQuote')))
        AND COALESCE(o.principal_id,s.principal_id)<>DATABASE_PRINCIPAL_ID(N'dbo'))
    THROW 51000,'The original restricted ownership chain is incomplete',1;
IF NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'dbo.CohorsOperationsQuote')
    AND HASHBYTES('SHA2_256',definition)=0x{module_hashes["dbo.CohorsOperationsQuote"]})
    OR NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'dbo.CohorsOperationsWrite')
    AND HASHBYTES('SHA2_256',definition)=0x{module_hashes["dbo.CohorsOperationsWrite"]})
    THROW 51000,'Deployed module bytes differ from the original compiled application migrations',1;
DECLARE @expected table(Class int,Major int,Minor int,State char(1),Permission sysname);
INSERT @expected SELECT 1,Id,0,'G','SELECT' FROM @objects
    WHERE Name IN(N'Aggregates',N'AggregateVersions',N'SchemaVersions');
INSERT @expected VALUES(1,OBJECT_ID(N'dbo.CohorsOperationsWrite'),0,'G','EXECUTE');
INSERT @expected SELECT 1,o.Id,0,'D',p.Name FROM @objects o
    CROSS JOIN (VALUES('INSERT'),('UPDATE'),('DELETE')) p(Name);
INSERT @expected SELECT 1,o.Id,c.column_id,'D','UPDATE'
    FROM @objects o JOIN sys.columns c ON c.object_id=o.Id;
INSERT @expected SELECT 0,0,0,'D',p.Name FROM (VALUES
    ('CREATE TABLE'),('CREATE PROCEDURE'),('CREATE FUNCTION'),('CREATE VIEW'),
    ('ALTER ANY ROLE'),('ALTER ANY USER'),('ALTER ANY SCHEMA')) p(Name);
INSERT @expected VALUES(3,SCHEMA_ID(N'dbo'),0,'D','ALTER'),(3,SCHEMA_ID(N'dbo'),0,'D','TAKE OWNERSHIP');
IF EXISTS(SELECT class,major_id,minor_id,state,permission_name FROM sys.database_permissions
    WHERE grantee_principal_id=@role EXCEPT SELECT Class,Major,Minor,State,Permission FROM @expected)
    OR EXISTS(SELECT Class,Major,Minor,State,Permission FROM @expected
    EXCEPT SELECT class,major_id,minor_id,state,permission_name FROM sys.database_permissions
    WHERE grantee_principal_id=@role)
    THROW 51000,'Original Operations role/object/column permissions changed',1;
IF NOT EXISTS(SELECT 1 FROM sys.database_role_members WHERE member_principal_id=@user AND role_principal_id=@role)
    ALTER ROLE [{OPERATIONS_ROLE}] ADD MEMBER [{name}];
""")
    else:
        parts.extend(f"GRANT {','.join(permissions)} ON OBJECT::dbo.[{table}] TO [{name}];"
                     for table, permissions in API_GRANTS.items())
        parts.extend((f"DENY DELETE,ALTER ON SCHEMA::dbo TO [{name}];",
                      f"DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.SchemaVersions TO [{name}];"))
    parts.append(f"""
GRANT CONNECT TO [{name}];
IF EXISTS(SELECT Class,Major,Minor,State,Permission FROM @direct
    EXCEPT SELECT class,major_id,minor_id,state,permission_name FROM sys.database_permissions
    WHERE grantee_principal_id=@user)
    THROW 51000,'Runtime permission readback differs after assignment',1;
DECLARE @permissions nvarchar(max)=(SELECT class,major_id,minor_id,state,permission_name
    FROM sys.database_permissions WHERE grantee_principal_id IN(@user,{allowed_role})
    ORDER BY grantee_principal_id,class,major_id,minor_id,permission_name FOR JSON PATH);
DECLARE @receipt nvarchar(max)=(SELECT
    'cohors-sql-permission-readback-1' AS contract,3 AS schemaVersion,
    '{role}' AS runtimeRole,LOWER(CONVERT(varchar(36),CONVERT(uniqueidentifier,sid))) AS principalClientId,
    name AS principalName,type_desc AS principalType,
    LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@permissions),2)) AS metadataSha256,
    CONVERT(bit,1) AS metadataVerified,CONVERT(bit,0) AS runtimeEntraAuthenticationProven
    FROM sys.database_principals WHERE principal_id=@user FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
COMMIT;
SELECT @receipt AS cohorsPermissionReceipt;
""")
    return "\n".join(parts)


def verify_receipt(value, role, client_id, statement):
    if (not isinstance(value, dict) or value.get("contract") != "cohors-sql-permission-readback-1"
            or value.get("runtimeRole") != role or value.get("schemaVersion") != 3
            or value.get("principalClientId") != str(uuid.UUID(client_id))
            or value.get("principalName") != "cohors_" + role or value.get("principalType") != "EXTERNAL_USER"
            or value.get("metadataVerified") is not True
            or value.get("runtimeEntraAuthenticationProven") is not False):
        raise GateError("Actual SQL permission receipt does not bind the selected runtime principal.")
    metadata_hash = value.get("metadataSha256", "")
    if not isinstance(metadata_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", metadata_hash):
        raise GateError("Actual SQL metadata readback hash is missing.")
    return {**value, "permissionStatementSha256": hashlib.sha256(statement.encode("utf-8")).hexdigest()}
