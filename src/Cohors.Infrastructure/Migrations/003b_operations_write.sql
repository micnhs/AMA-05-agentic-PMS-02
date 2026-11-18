CREATE OR ALTER PROCEDURE dbo.CohorsOperationsWrite
    @Kind nvarchar(40), @Id nvarchar(80), @ExpectedVersion int,
    @Result nvarchar(max), @At datetimeoffset(7), @Dependencies nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Kind IS NULL OR @Kind COLLATE Latin1_General_100_BIN2 NOT IN (N'case',N'psurReview',N'eudamedDelivery',N'operations')
        OR @Id IS NULL OR LEN(@Id)=0 OR @ExpectedVersion IS NULL OR @ExpectedVersion<0
        OR @At IS NULL OR DATEPART(TZOFFSET,@At)<>0
        OR ISNULL(ISJSON(@Result,OBJECT),0)<>1 OR ISNULL(ISJSON(@Dependencies,ARRAY),0)<>1
        THROW 51100,'Invalid restricted Operations command.',1;

    DECLARE @payload char(64)=LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
        CONVERT(varchar(max),@Result COLLATE Latin1_General_100_BIN2_UTF8)),2));
    DECLARE @request char(64)=LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
        CONVERT(varchar(max),CONCAT(@Kind,':',@Id,':',@ExpectedVersion,':',@payload,':',@Dependencies)
            COLLATE Latin1_General_100_BIN2_UTF8)),2));
    DECLARE @key char(64)=LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
        CONVERT(varchar(max),CONCAT('cohors-operations-write-v1:',@request)
            COLLATE Latin1_General_100_BIN2_UTF8)),2));
    DECLARE @current nvarchar(max), @product nvarchar(40), @version int=0, @hash char(64),
        @expected nvarchar(max), @action nvarchar(80), @reason nvarchar(4000),
        @lock int, @resource nvarchar(255), @mode nvarchar(32), @receipt nvarchar(max), @receiptHash char(64);
    DECLARE @dependenciesTable TABLE(Kind nvarchar(40),Id nvarchar(80),Version int,Hash char(64));
    INSERT @dependenciesTable SELECT Kind,Id,Version,Hash FROM OPENJSON(@Dependencies)
        WITH(Kind nvarchar(40) '$.kind',Id nvarchar(80) '$.source.id',
            Version int '$.source.version',Hash char(64) '$.source.hash');
    IF EXISTS(SELECT 1 FROM @dependenciesTable WHERE Kind NOT IN (N'eudamedDossier',N'eudamedInbox')
        OR Kind IS NULL OR Id IS NULL OR Version IS NULL OR Version<1 OR Hash IS NULL OR LEN(Hash)<>64)
        OR (@Kind<>N'eudamedDelivery' AND EXISTS(SELECT 1 FROM @dependenciesTable))
        THROW 51100,'Only exact correspondence source dependencies are accepted.',1;

    BEGIN TRY
        BEGIN TRANSACTION;
        SET @mode=CASE WHEN @Kind=N'eudamedDelivery' THEN N'Exclusive' ELSE N'Shared' END;
        EXEC @lock=sys.sp_getapplock @Resource=N'cohors-object-reference-gate',@LockMode=@mode,
            @LockOwner=N'Transaction',@LockTimeout=15000;
        IF @lock<0 THROW 51103,'Object reference gate is busy.',1;
        EXEC @lock=sys.sp_getapplock @Resource=@key,@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
        IF @lock<0 THROW 51103,'Operations command is busy.',1;
        SET @resource=CONCAT(N'aggregate:',@Kind,N':',@Id);
        EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
        IF @lock<0 THROW 51103,'Operations aggregate is busy.',1;

        SELECT @receipt=Result,@receiptHash=RequestHash FROM dbo.Commands WHERE IdempotencyKey=@key;
        IF @receipt IS NOT NULL
        BEGIN
            IF @receiptHash<>@request OR @receipt COLLATE Latin1_General_100_BIN2<>@Result COLLATE Latin1_General_100_BIN2
                THROW 51106,'Operations receipt differs from the original command.',1;
            COMMIT;
            SELECT @receipt;
            RETURN;
        END;

        SELECT @current=a.Json,@product=a.ProductId,@version=a.Version,@hash=h.Hash
        FROM dbo.Aggregates a WITH(UPDLOCK)
        LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
        WHERE a.Kind=@Kind AND a.Id=@Id;
        IF @version<>@ExpectedVersion THROW 51101,'Operations source version changed.',1;
        IF @current IS NULL AND (@Kind<>N'operations' OR @Id<>N'heartbeat')
            THROW 51100,'Operations cannot create a clinical aggregate.',1;
        IF @current IS NOT NULL AND (JSON_VALUE(@current,'$.id') IS NULL
            OR JSON_VALUE(@current,'$.id') COLLATE Latin1_General_100_BIN2<>@Id COLLATE Latin1_General_100_BIN2)
            THROW 51100,'Operations aggregate identity must match exactly.',1;
        IF @current IS NOT NULL AND (@hash IS NULL OR @hash<>LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
            CONVERT(varchar(max),@current COLLATE Latin1_General_100_BIN2_UTF8)),2)))
            THROW 51104,'Operations current source has no intact history binding.',1;
        IF @product IS NOT NULL AND @product NOT IN (N'ortho',N'cardio')
            THROW 51100,'Operations source is outside the configured pilot products.',1;
        IF EXISTS(SELECT 1 FROM @dependenciesTable d
            LEFT JOIN dbo.Aggregates a ON a.Kind=d.Kind AND a.Id=d.Id
            LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.Id IS NULL OR a.Version<>d.Version OR h.Hash IS NULL OR h.Hash<>d.Hash
                OR a.ProductId NOT IN (N'ortho',N'cardio')
                OR d.Hash<>LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
                    CONVERT(varchar(max),a.Json COLLATE Latin1_General_100_BIN2_UTF8)),2)))
            THROW 51102,'Operations correspondence dependency changed.',1;

        DECLARE @date nvarchar(40)=CONVERT(nvarchar(27),CONVERT(datetime2(7),@At),126);
        IF CHARINDEX(N'.',@date)>0
        BEGIN
            WHILE RIGHT(@date,1)=N'0' SET @date=LEFT(@date,LEN(@date)-1);
            IF RIGHT(@date,1)=N'.' SET @date=LEFT(@date,LEN(@date)-1);
        END;
        SET @date+=N'+00:00';
        DECLARE @dateJson nvarchar(100)=dbo.CohorsOperationsQuote(@date);

        IF @Kind=N'operations'
        BEGIN
            IF @Id COLLATE Latin1_General_100_BIN2<>N'heartbeat' THROW 51100,'Only the watchdog heartbeat can be written.',1;
            DECLARE @open int=TRY_CONVERT(int,JSON_VALUE(@Result,'$.openDeadlines')),
                @follow int=TRY_CONVERT(int,JSON_VALUE(@Result,'$.openFollowUps')),
                @escalations int=TRY_CONVERT(int,JSON_VALUE(@Result,'$.escalations'));
            IF @open IS NULL OR @open<0 OR @follow IS NULL OR @follow<0 OR @escalations IS NULL OR @escalations<0
                THROW 51100,'Invalid watchdog counters.',1;
            SET @product=N'ortho';
            SET @expected=CONCAT(N'{"businessTime":',@dateJson,N',"escalations":',@escalations,
                N',"id":"heartbeat","lastTick":',@dateJson,N',"openDeadlines":',@open,
                N',"openFollowUps":',@follow,N',"status":"Running","version":',@version+1,N'}');
            SET @action=N'WatchdogHeartbeat';
            SET @reason=N'Unabhaengige Fristenpruefung abgeschlossen; eingeschraenkter SQL-Operationsvertrag.';
        END
        ELSE
        BEGIN
            DECLARE @path nvarchar(100)=CASE WHEN @Kind=N'eudamedDelivery' THEN N'$.correspondenceAlerts' ELSE N'$.alerts' END;
            DECLARE @oldAlerts nvarchar(max)=JSON_QUERY(@current,@path), @newAlerts nvarchar(max)=JSON_QUERY(@Result,@path);
            IF @oldAlerts IS NULL
            BEGIN
                IF @Kind<>N'eudamedDelivery' OR EXISTS(SELECT 1 FROM OPENJSON(@current) WHERE [key]=N'correspondenceAlerts')
                    THROW 51104,'Stored watchdog alert array is invalid.',1;
                SET @oldAlerts=N'[]';
            END;
            IF ISNULL(ISJSON(@oldAlerts,ARRAY),0)<>1 OR ISNULL(ISJSON(@newAlerts,ARRAY),0)<>1
                THROW 51100,'Only append-only watchdog alert arrays are accepted.',1;
            DECLARE @oldCount int=(SELECT COUNT(*) FROM OPENJSON(@oldAlerts));
            IF (SELECT COUNT(*) FROM OPENJSON(@newAlerts))<=@oldCount
                OR EXISTS(SELECT 1 FROM OPENJSON(@oldAlerts) o LEFT JOIN OPENJSON(@newAlerts) n ON n.[key]=o.[key]
                    WHERE n.[key] IS NULL OR n.type<>o.type
                        OR n.value COLLATE Latin1_General_100_BIN2<>o.value COLLATE Latin1_General_100_BIN2)
                THROW 51100,'Existing Operations alerts cannot be removed, reordered or changed.',1;
            DECLARE @added TABLE(Ordinal int PRIMARY KEY,Json nvarchar(max),Type int);
            INSERT @added SELECT CONVERT(int,[key]),value,type FROM OPENJSON(@newAlerts) WHERE CONVERT(int,[key])>=@oldCount;
            DECLARE @append nvarchar(max);

            IF @Kind IN (N'case',N'psurReview')
            BEGIN
                DECLARE @allowed TABLE(Value nvarchar(4000));
                IF @Kind=N'case'
                BEGIN
                    INSERT @allowed
                    SELECT CONCAT(N'IMMEDIATE:',Id,N':Sofort an Medical Safety, RA und PRRC eskalieren.')
                    FROM OPENJSON(@current,'$.deadlines') WITH(Id nvarchar(80) '$.id',Status nvarchar(40) '$.status',Immediate bit '$.immediateAction')
                    WHERE Status=N'Open' AND Immediate=1
                    UNION ALL
                    SELECT CONCAT(N'LEGAL:',Id,N':Gesetzliche Hoechstfrist ueberschritten; keine Rueckdatierung.')
                    FROM OPENJSON(@current,'$.deadlines') WITH(Id nvarchar(80) '$.id',Status nvarchar(40) '$.status',Due datetimeoffset '$.legalDue')
                    WHERE Status=N'Open' AND Due<@At
                    UNION ALL
                    SELECT CONCAT(N'SAFETY:',Id,N':Interne SafetyDue ueberschritten; Stellvertretung alarmieren.')
                    FROM OPENJSON(@current,'$.deadlines') WITH(Id nvarchar(80) '$.id',Status nvarchar(40) '$.status',Due datetimeoffset '$.safetyDue')
                    WHERE Status=N'Open' AND Due<@At
                    UNION ALL
                    SELECT CONCAT(N'RFI:',Id,N':Interne Rueckmeldefrist ueberschritten; Nachverfolgung ohne Pause der LegalDue.')
                    FROM OPENJSON(@current,'$.informationRequests') WITH(Id nvarchar(80) '$.id',Status nvarchar(40) '$.status',Due datetimeoffset '$.dueAt')
                    WHERE Status<>N'SimulatedAnswered' AND Due<@At
                    UNION ALL
                    SELECT CASE WHEN Due<@At
                        THEN CONCAT(N'FOLLOWUP:',Id,N':Im freigegebenen MIR zugesagter Folgetermin ueberschritten; Medical Safety und RA eskalieren. Keine neue Initialfrist.')
                        ELSE CONCAT(N'FOLLOWUP-SAFETY:',Id,N':Interne Vorwarnung zum fest zugesagten Folgeberichtstermin.') END
                    FROM OPENJSON(@current,'$.followUps') WITH(Id nvarchar(80) '$.id',Status nvarchar(40) '$.status',
                        Due datetimeoffset '$.schedule.dueAt',SafetyDue datetimeoffset '$.schedule.safetyDue')
                    WHERE Status=N'Open' AND SafetyDue<@At;
                    SET @action=N'DeadlineEscalated';
                END
                ELSE
                BEGIN
                    INSERT @allowed SELECT CONCAT(N'PSUR-BS:',Round,
                        N':Vereinbarte Antwortfrist ueberschritten; RA/Medical nachverfolgen. Keine gesetzliche Pauschalfrist und kein Neustart alter Fristen.')
                    FROM OPENJSON(@current,'$.questions') WITH(Round int '$.round',Due datetimeoffset '$.dueAt')
                    WHERE Round=(SELECT MAX(Round) FROM OPENJSON(@current,'$.questions') WITH(Round int '$.round'))
                        AND Due<@At AND JSON_VALUE(@current,'$.status') IN (N'QuestionOpen',N'ReplyDraft',N'MedicalApproved',N'ReplyApproved');
                    SET @action=N'PsurQuestionEscalated';
                END;
                IF EXISTS(SELECT 1 FROM @added a WHERE a.Type<>1 OR NOT EXISTS(
                    SELECT 1 FROM @allowed v WHERE v.Value COLLATE Latin1_General_100_BIN2=a.Json COLLATE Latin1_General_100_BIN2))
                    OR EXISTS(SELECT 1 FROM @added a JOIN OPENJSON(@oldAlerts) o
                        ON o.value COLLATE Latin1_General_100_BIN2=a.Json COLLATE Latin1_General_100_BIN2)
                    OR EXISTS(SELECT Json COLLATE Latin1_General_100_BIN2 FROM @added GROUP BY Json COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
                    THROW 51100,'Alert is not a new permitted source-bound watchdog finding.',1;
                SELECT @append=STRING_AGG(CONVERT(nvarchar(max),dbo.CohorsOperationsQuote(Json)),N',')
                    WITHIN GROUP(ORDER BY Ordinal) FROM @added;
            END
            ELSE
            BEGIN
                DECLARE @entry nvarchar(max), @correspondence nvarchar(max), @inbox nvarchar(max),
                    @approved nvarchar(max), @dossier nvarchar(max), @alert nvarchar(max), @ordinal int,
                    @issues nvarchar(max), @message nvarchar(4000), @code nvarchar(40), @identity nvarchar(max),
                    @alertId char(64), @bindingHash char(64), @products nvarchar(max), @replied bit,
                    @originalDue datetimeoffset, @savedDue datetimeoffset, @expectedAlert nvarchar(max);
                DECLARE alerts CURSOR LOCAL FAST_FORWARD FOR SELECT Ordinal,Json FROM @added ORDER BY Ordinal;
                OPEN alerts;
                FETCH NEXT FROM alerts INTO @ordinal,@alert;
                WHILE @@FETCH_STATUS=0
                BEGIN
                    SET @entry=NULL; SET @correspondence=NULL; SET @inbox=NULL; SET @approved=NULL; SET @dossier=NULL;
                    IF ISNULL(ISJSON(@alert,OBJECT),0)<>1 THROW 51100,'Structured correspondence alert required.',1;
                    SELECT @entry=e.value,@correspondence=c.value FROM OPENJSON(@current,'$.entries') e
                    CROSS APPLY OPENJSON(e.value,'$.correspondence') c
                    WHERE JSON_VALUE(e.value,'$.attempt.id')=JSON_VALUE(@alert,'$.attemptId')
                        AND JSON_VALUE(c.value,'$.id')=JSON_VALUE(@alert,'$.correspondenceId');
                    IF @entry IS NULL OR @correspondence IS NULL
                        THROW 51100,'Correspondence alert has no stored source.',1;
                    SELECT @inbox=a.Json FROM dbo.Aggregates a JOIN @dependenciesTable d ON d.Kind=a.Kind AND d.Id=a.Id
                    WHERE a.Kind=N'eudamedInbox' AND a.Id=JSON_VALUE(@correspondence,'$.inboxId');
                    SELECT @dossier=a.Json FROM dbo.Aggregates a JOIN @dependenciesTable d ON d.Kind=a.Kind AND d.Id=a.Id
                    WHERE a.Kind=N'eudamedDossier' AND a.Id=JSON_VALUE(@entry,'$.dossierId');
                    SELECT @approved=Json FROM dbo.AggregateVersions
                    WHERE Kind=N'eudamedDossier' AND Id=JSON_VALUE(@entry,'$.dossierId')
                        AND Version=TRY_CONVERT(int,JSON_VALUE(@entry,'$.approvedDossier.version'))
                        AND Hash=JSON_VALUE(@entry,'$.approvedDossier.hash');
                    IF @inbox IS NULL OR @dossier IS NULL OR @approved IS NULL
                        THROW 51102,'Exact current and approved correspondence dependencies required.',1;
                    IF JSON_QUERY(@inbox,'$.original') IS NULL
                        OR JSON_VALUE(@inbox,'$.original.kind') IS NULL
                        OR JSON_VALUE(@inbox,'$.originalHash')<>JSON_VALUE(@correspondence,'$.originalHash')
                        OR JSON_VALUE(@inbox,'$.originalHash')<>LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
                            CONVERT(varchar(max),JSON_QUERY(@inbox,'$.original') COLLATE Latin1_General_100_BIN2_UTF8)),2))
                        OR JSON_VALUE(@inbox,'$.original.attemptId')<>JSON_VALUE(@entry,'$.attempt.id')
                        OR JSON_VALUE(@inbox,'$.original.correlationId')<>JSON_VALUE(@entry,'$.attempt.correlationId')
                        OR JSON_VALUE(@inbox,'$.original.packageHash')<>JSON_VALUE(@entry,'$.attempt.packageHash')
                        OR JSON_VALUE(@inbox,'$.original.dossierId')<>JSON_VALUE(@entry,'$.dossierId')
                        OR JSON_VALUE(@inbox,'$.original.dossierRevision')<>JSON_VALUE(@entry,'$.attempt.dossierRevision')
                        OR JSON_VALUE(@approved,'$.frozenPackageHash')<>JSON_VALUE(@entry,'$.attempt.packageHash')
                        OR JSON_VALUE(@approved,'$.scopeHash')<>JSON_VALUE(@entry,'$.attempt.packageHash')
                        OR JSON_VALUE(@inbox,'$.original.kind') NOT IN(N'Comment',N'InformationRequest',N'Evaluation')
                        THROW 51104,'Correspondence original binding is inconsistent.',1;
                    SET @originalDue=TRY_CONVERT(datetimeoffset,JSON_VALUE(@inbox,'$.original.dueAt'));
                    SET @savedDue=TRY_CONVERT(datetimeoffset,JSON_VALUE(@correspondence,'$.dueAt'));
                    IF JSON_VALUE(@inbox,'$.original.kind')<>N'InformationRequest' AND @originalDue IS NULL AND @savedDue IS NULL
                        THROW 51100,'An undated comment does not create a response deadline.',1;
                    SET @replied=0;
                    IF JSON_VALUE(@correspondence,'$.status')=N'Replied'
                        AND EXISTS(SELECT 1 FROM OPENJSON(@current,'$.entries') e
                            CROSS APPLY OPENJSON(e.value,'$.responses') r
                            WHERE JSON_VALUE(e.value,'$.dossierId')=JSON_VALUE(@correspondence,'$.replyDossierId')
                                AND JSON_VALUE(e.value,'$.intendedAction')=N'Reply'
                                AND JSON_VALUE(r.value,'$.attemptId')=JSON_VALUE(e.value,'$.attempt.id')
                                AND JSON_VALUE(r.value,'$.applied')=N'true' AND JSON_VALUE(r.value,'$.status')=N'BusinessAccepted')
                        SET @replied=1;
                    IF @replied=1 THROW 51100,'An accepted reply cannot be escalated as open.',1;
                    DECLARE @issueTable TABLE(Ordinal int,Text nvarchar(1000));
                    DELETE FROM @issueTable;
                    IF @originalDue IS NULL INSERT @issueTable VALUES(1,N'Keine ausdrueckliche Antwortfrist im Originaleingang; RA muss den Termin klaeren. Keine Pauschalfrist.');
                    IF @originalDue<>@savedDue OR (@originalDue IS NULL AND @savedDue IS NOT NULL) OR (@originalDue IS NOT NULL AND @savedDue IS NULL)
                        INSERT @issueTable VALUES(2,N'Gespeicherte Antwortfrist widerspricht dem Originaleingang; RA muss die Abweichung klaeren. Kein Ersatztermin.');
                    IF ISNULL(JSON_VALUE(@correspondence,'$.ownerRole'),N'')<>N'RegulatoryAffairs'
                        INSERT @issueTable VALUES(3,N'Die gespeicherte Verantwortung ist nicht Regulatory Affairs; RA muss die Zuordnung klaeren.');
                    IF ISNULL(JSON_VALUE(@correspondence,'$.status'),N'')<>N'Open'
                        OR JSON_VALUE(@correspondence,'$.replyDossierId') IS NOT NULL
                        INSERT @issueTable VALUES(4,N'Der Abschluss ist nicht durch eine angewandte fachliche Reply-Annahme belegt; RA muss den Status klaeren.');
                    SELECT @issues=CONCAT(N'[',STRING_AGG(CONVERT(nvarchar(max),dbo.CohorsOperationsQuote(Text)),N',')
                        WITHIN GROUP(ORDER BY Ordinal),N']'),
                        @message=STRING_AGG(Text,N' ') WITHIN GROUP(ORDER BY Ordinal) FROM @issueTable;
                    SET @code=CASE WHEN EXISTS(SELECT 1 FROM @issueTable) THEN N'ScheduleClarification' ELSE N'Overdue' END;
                    IF @code=N'Overdue'
                    BEGIN
                        IF @originalDue IS NULL OR @originalDue>=@At THROW 51100,'Response deadline is not overdue.',1;
                        SET @message=N'Originalgebundener EUDAMED-Antworttermin ueberschritten; Regulatory Affairs und Stellvertretung eskalieren. Keine neue MDR-Initialfrist und keine Verschiebung bestehender LegalDue.';
                    END;
                    SET @identity=CONCAT(N'{"attemptId":',dbo.CohorsOperationsQuote(JSON_VALUE(@entry,'$.attempt.id')),
                        N',"code":',dbo.CohorsOperationsQuote(@code),N',"correspondenceId":',dbo.CohorsOperationsQuote(JSON_VALUE(@correspondence,'$.id')),
                        N',"issues":',@issues,N',"ledgerId":',dbo.CohorsOperationsQuote(@Id),
                        N',"originalHash":',dbo.CohorsOperationsQuote(JSON_VALUE(@correspondence,'$.originalHash')),N'}');
                    SET @alertId=LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
                        CONVERT(varchar(max),@identity COLLATE Latin1_General_100_BIN2_UTF8)),2));
                    IF EXISTS(SELECT 1 FROM OPENJSON(@oldAlerts) WHERE JSON_VALUE(value,'$.id')=@alertId)
                        OR EXISTS(SELECT 1 FROM @added WHERE Ordinal<@ordinal AND JSON_VALUE(Json,'$.id')=@alertId)
                        THROW 51100,'Correspondence finding is already recorded.',1;
                    SET @bindingHash=LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
                        CONVERT(varchar(max),JSON_QUERY(@entry,'$.attempt.binding') COLLATE Latin1_General_100_BIN2_UTF8)),2));
                    SELECT @products=CONCAT(N'[',STRING_AGG(CONVERT(nvarchar(max),dbo.CohorsOperationsQuote(Product)),N',')
                        WITHIN GROUP(ORDER BY Product COLLATE Latin1_General_100_BIN2),N']')
                    FROM (SELECT value AS Product FROM OPENJSON(@dossier,'$.accessProducts')
                        UNION SELECT value FROM OPENJSON(@approved,'$.accessProducts')
                        UNION SELECT JSON_VALUE(@dossier,'$.productId')
                        UNION SELECT JSON_VALUE(@approved,'$.productId')) p;
                    SET @expectedAlert=CONCAT(N'{"at":',@dateJson,N',"attemptId":',dbo.CohorsOperationsQuote(JSON_VALUE(@entry,'$.attempt.id')),
                        N',"bindingHash":',dbo.CohorsOperationsQuote(@bindingHash),N',"code":',dbo.CohorsOperationsQuote(@code),
                        N',"correspondenceId":',dbo.CohorsOperationsQuote(JSON_VALUE(@correspondence,'$.id')),
                        N',"dossierId":',dbo.CohorsOperationsQuote(JSON_VALUE(@entry,'$.dossierId')),
                        N',"dossierRevision":',JSON_VALUE(@correspondence,'$.dossierRevision'),N',"id":',dbo.CohorsOperationsQuote(@alertId),
                        N',"inboxId":',dbo.CohorsOperationsQuote(JSON_VALUE(@correspondence,'$.inboxId')),N',"message":',dbo.CohorsOperationsQuote(@message),
                        N',"originalHash":',dbo.CohorsOperationsQuote(JSON_VALUE(@correspondence,'$.originalHash')),
                        N',"ownerRole":"RegulatoryAffairs","packageHash":',dbo.CohorsOperationsQuote(JSON_VALUE(@entry,'$.attempt.packageHash')),
                        N',"products":',@products,N'}');
                    IF @alert COLLATE Latin1_General_100_BIN2<>@expectedAlert COLLATE Latin1_General_100_BIN2
                        THROW 51100,'Correspondence alert differs from the permitted original-bound finding.',1;
                    FETCH NEXT FROM alerts INTO @ordinal,@alert;
                END;
                CLOSE alerts; DEALLOCATE alerts;
                SELECT @append=STRING_AGG(Json,N',') WITHIN GROUP(ORDER BY Ordinal) FROM @added;
                SET @action=N'EudamedCorrespondenceEscalated';
            END;

            DECLARE @merged nvarchar(max)=CONCAT(LEFT(@oldAlerts,LEN(@oldAlerts)-1),
                CASE WHEN @oldCount>0 THEN N',' ELSE N'' END,@append,N']');
            SET @expected=JSON_MODIFY(@current,'$.version',@version+1);
            IF @Kind=N'eudamedDelivery' AND NOT EXISTS(SELECT 1 FROM OPENJSON(@current) WHERE [key]=N'correspondenceAlerts')
                SET @expected=CONCAT(N'{"correspondenceAlerts":',@merged,N',',SUBSTRING(@expected,2,LEN(@expected)));
            ELSE SET @expected=JSON_MODIFY(@expected,@path,JSON_QUERY(@merged));
            SET @reason=N'Originalgebundene Fristeneskalation; ausschliesslich neue Watchdoghinweise, keine Freigabe oder Aenderung geschuetzter Nachweise.';
        END;

        -- Reconstructing the sole permitted delta preserves every other canonical byte, including future fields.
        IF @expected IS NULL OR DATALENGTH(@expected)<>DATALENGTH(@Result)
            OR @expected COLLATE Latin1_General_100_BIN2<>@Result COLLATE Latin1_General_100_BIN2
            THROW 51100,'Operations cannot change clinical state, identities, approvals or protected evidence.',1;
        IF EXISTS(SELECT 1 FROM dbo.Aggregates a LEFT JOIN dbo.AggregateVersions h
            ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.Kind=N'retention' AND JSON_VALUE(a.Json,'$.status') IN(N'Authorized',N'Deleting',N'DeletionFailed',N'Deleted')
                AND (JSON_VALUE(a.Json,'$.object.hash') IS NULL OR h.Hash IS NULL OR h.Hash<>LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
                    CONVERT(varchar(max),a.Json COLLATE Latin1_General_100_BIN2_UTF8)),2))
                    OR CHARINDEX(JSON_VALUE(a.Json,'$.object.hash'),@Result)>0))
            THROW 51105,'Retired reference or inconsistent retention gate.',1;

        DECLARE @previous char(64)=REPLICATE('0',64), @headVersion int=0, @headPayload char(64);
        SELECT TOP(1) @previous=Hash,@headVersion=Version,@headPayload=PayloadHash
            FROM dbo.AuditEvents WHERE AggregateId=@Id ORDER BY Sequence DESC;
        IF @headVersion<>@version OR (@version>0 AND @headPayload<>@hash)
            THROW 51104,'Audit head does not bind the current Operations source.',1;
        DECLARE @correlation nvarchar(120)=CONCAT(N'operations-',@key);
        DECLARE @audit nvarchar(max)=CONCAT(N'{"action":',dbo.CohorsOperationsQuote(@action),
            N',"actorId":"cohors-operations","actorName":"Cohors operations","aggregateId":',dbo.CohorsOperationsQuote(@Id),
            N',"at":',@dateJson,N',"correlationId":',dbo.CohorsOperationsQuote(@correlation),
            N',"hash":"","payloadHash":',dbo.CohorsOperationsQuote(@payload),
            N',"previousHash":',dbo.CohorsOperationsQuote(@previous),N',"reason":',dbo.CohorsOperationsQuote(@reason),
            N',"roles":["Workflow"],"sequence":0,"version":',@version+1,N'}');
        DECLARE @auditHash char(64)=LOWER(CONVERT(char(64),HASHBYTES('SHA2_256',
            CONVERT(varchar(max),@audit COLLATE Latin1_General_100_BIN2_UTF8)),2));
        IF @version=0
            INSERT dbo.Aggregates(Id,Kind,ProductId,Version,Json,UpdatedAt) VALUES(@Id,@Kind,@product,1,@Result,@At);
        ELSE UPDATE dbo.Aggregates SET Version=@version+1,Json=@Result,UpdatedAt=@At WHERE Kind=@Kind AND Id=@Id;
        INSERT dbo.AggregateVersions(Kind,Id,Version,Json,Hash,At) VALUES(@Kind,@Id,@version+1,@Result,@payload,@At);
        INSERT dbo.AuditEvents(AggregateId,Version,ActorId,ActorName,Roles,Action,Reason,At,CorrelationId,PayloadHash,PreviousHash,Hash)
            VALUES(@Id,@version+1,N'cohors-operations',N'Cohors operations',N'["Workflow"]',@action,@reason,@At,@correlation,@payload,@previous,@auditHash);
        INSERT dbo.Outbox(Id,AggregateId,AggregateKind,ProductId,Version,Type,Payload,CreatedAt)
            VALUES(REPLACE(CONVERT(nvarchar(36),NEWID()),N'-',N''),@Id,@Kind,@product,@version+1,@action,@Result,@At);
        INSERT dbo.Commands(IdempotencyKey,RequestHash,ActorId,Result,At) VALUES(@key,@request,N'cohors-operations',@Result,@At);
        IF @Kind=N'case'
        BEGIN
            IF EXISTS(SELECT 1 FROM OPENJSON(@current,'$.deadlines') WITH(Id nvarchar(80) '$.id',
                LegalDue datetimeoffset '$.legalDue',SafetyDue datetimeoffset '$.safetyDue',Status nvarchar(40) '$.status') original
                LEFT JOIN dbo.Deadlines d ON d.Id=original.Id
                WHERE d.Id IS NULL OR d.CaseId<>@Id OR d.ProductId<>@product
                    OR d.LegalDue<>original.LegalDue OR d.SafetyDue<>original.SafetyDue OR d.Status<>original.Status)
                THROW 51104,'Protected deadline projection differs from its source.',1;
            UPDATE d SET Version=@version+1 FROM dbo.Deadlines d
                JOIN OPENJSON(@current,'$.deadlines') WITH(Id nvarchar(80) '$.id') original ON original.Id=d.Id
                WHERE d.CaseId=@Id AND d.ProductId=@product;
            IF @@ROWCOUNT<>(SELECT COUNT(*) FROM OPENJSON(@current,'$.deadlines'))
                THROW 51104,'Existing deadline projection is incomplete.',1;
        END;
        COMMIT;
        SELECT @Result;
    END TRY
    BEGIN CATCH
        IF CURSOR_STATUS('local','alerts')>=0 CLOSE alerts;
        IF CURSOR_STATUS('local','alerts')>-3 DEALLOCATE alerts;
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
