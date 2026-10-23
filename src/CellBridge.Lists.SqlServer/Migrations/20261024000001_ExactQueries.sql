-- Frozen SQL for migration 20261024000001_ExactQueries.
CREATE FUNCTION cellbridge_lists.json_scalar(@json nvarchar(max), @field nvarchar(36))
RETURNS nvarchar(max) WITH SCHEMABINDING AS
BEGIN
    DECLARE @value nvarchar(max);
    -- OPENJSON retains all scalar characters; JSON_VALUE would truncate at 4000.
    SELECT @value = [value] FROM OPENJSON(@json)
        WHERE [key] COLLATE Latin1_General_100_BIN2 = @field AND [type] IN (1,2,3);
    RETURN @value;
END;
GO
CREATE FUNCTION cellbridge_lists.scalar_key(@json nvarchar(max), @field nvarchar(36), @type int)
RETURNS nvarchar(max) WITH SCHEMABINDING AS
BEGIN
    DECLARE @value nvarchar(max) = cellbridge_lists.json_scalar(@json,@field);
    IF @value IS NULL RETURN NULL;
    IF @type IN (2,3)
    BEGIN
        -- Exact decimal key: sign and 28 integer/12 fractional digits. The
        -- accepted domain spans both extremes; no SQL fixed-scale cast fits it.
        -- Normalize JSON exponent spelling using digit operations only.
        DECLARE @negative bit = CASE WHEN LEFT(@value,1)='-' THEN 1 ELSE 0 END;
        IF @negative=1 SET @value=SUBSTRING(@value,2,DATALENGTH(@value)/2);
        DECLARE @epos int=CHARINDEX('e',LOWER(@value)), @exp bigint=0;
        IF @epos>0
        BEGIN
            SET @exp=TRY_CONVERT(bigint,SUBSTRING(@value,@epos+1,DATALENGTH(@value)/2));
            SET @value=LEFT(@value,@epos-1);
        END;
        DECLARE @dot int=CHARINDEX('.',@value), @before bigint;
        SET @before=CASE WHEN @dot=0 THEN DATALENGTH(@value)/2 ELSE @dot-1 END;
        SET @value=REPLACE(@value,'.','');
        DECLARE @start int=PATINDEX('%[^0]%',@value COLLATE Latin1_General_100_BIN2);
        IF @start=0 RETURN N'1'; -- All accepted spellings of zero, including huge exponents.
        SET @before=@before+@exp-(@start-1);
        SET @value=SUBSTRING(@value,@start,DATALENGTH(@value)/2);
        IF @before>28 OR @before < -12 OR @before IS NULL RETURN NULL;
        DECLARE @integer nvarchar(28)=N'', @fraction nvarchar(12), @magnitude nvarchar(40);
        IF @before>0
            SET @integer=LEFT(@value,CONVERT(int,@before))+REPLICATE(N'0',
                CASE WHEN @before>DATALENGTH(@value)/2 THEN CONVERT(int,@before)-DATALENGTH(@value)/2 ELSE 0 END);
        SET @fraction=CASE WHEN @before<0 THEN LEFT(REPLICATE(N'0',CONVERT(int,-@before))+@value,12)
            ELSE SUBSTRING(@value,CONVERT(int,@before)+1,12) END;
        SET @magnitude=RIGHT(REPLICATE(N'0',28)+@integer,28)+LEFT(@fraction+REPLICATE(N'0',12),12);
        RETURN CASE WHEN @negative=1 THEN N'0'+TRANSLATE(@magnitude,N'0123456789',N'9876543210') ELSE N'2'+@magnitude END;
    END;
    IF @type=1 RETURN CASE WHEN @value=N'true' THEN N'1' ELSE N'0' END;
    IF @type=5
        RETURN LEFT(@value,19)+LEFT(CASE WHEN DATALENGTH(@value)/2>20
            THEN SUBSTRING(@value,21,DATALENGTH(@value)/2-21) ELSE N'' END+N'0000000',7)+N'!';
    -- Hex UTF-16 code units preserve .NET ordinal order, including surrogate
    -- pairs. The terminator sorts before every hex digit and avoids SQL's
    -- space/zero padding equality. No scalar length is truncated.
    DECLARE @result nvarchar(max)=N'', @i int=1, @size int=DATALENGTH(@value)/2;
    DECLARE @bytes varbinary(max)=CONVERT(varbinary(max),@value);
    WHILE @i<=@size
    BEGIN
        SET @result=@result+CONVERT(nvarchar(4),CONVERT(varchar(4),
            SUBSTRING(@bytes,@i*2,1)+SUBSTRING(@bytes,@i*2-1,1),2));
        SET @i=@i+1;
    END;
    RETURN @result+N'!';
END;
GO
CREATE FUNCTION cellbridge_lists.uuid_key(@value uniqueidentifier)
RETURNS nvarchar(36) WITH SCHEMABINDING AS
BEGIN
    RETURN LOWER(CONVERT(nvarchar(36),@value));
END;
GO
CREATE FUNCTION cellbridge_lists.key_compare(@left nvarchar(max), @right nvarchar(max))
RETURNS int WITH SCHEMABINDING AS
BEGIN
    IF @left IS NULL OR @right IS NULL RETURN NULL;
    RETURN CASE WHEN @left COLLATE Latin1_General_100_BIN2 < @right COLLATE Latin1_General_100_BIN2 THEN -1
        WHEN @left COLLATE Latin1_General_100_BIN2 > @right COLLATE Latin1_General_100_BIN2 THEN 1 ELSE 0 END;
END;
