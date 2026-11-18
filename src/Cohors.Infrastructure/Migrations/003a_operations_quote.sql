CREATE OR ALTER FUNCTION dbo.CohorsOperationsQuote(@value nvarchar(max))
RETURNS nvarchar(max)
WITH SCHEMABINDING
AS
BEGIN
    IF @value IS NULL RETURN NULL;
    DECLARE @result nvarchar(max)=N'"', @i int=1, @length int=DATALENGTH(@value)/2, @code int, @character nchar(1);
    WHILE @i<=@length
    BEGIN
        SET @character=SUBSTRING(@value COLLATE Latin1_General_100_BIN2,@i,1);
        SET @code=UNICODE(@character);
        SET @result+=CASE @code
            WHEN 8 THEN N'\b' WHEN 9 THEN N'\t' WHEN 10 THEN N'\n'
            WHEN 12 THEN N'\f' WHEN 13 THEN N'\r' WHEN 92 THEN N'\\'
            ELSE CASE WHEN @code<32 OR @code>=127 OR @code IN (34,38,39,43,60,62,96)
                THEN N'\u'+CONVERT(nvarchar(4),CONVERT(binary(2),@code),2)
                ELSE @character END END;
        SET @i+=1;
    END;
    RETURN @result+N'"';
END;
