/*
    IWC CAD Tools V9 - Drawing Series schema
    Purpose:
      Track drawing files associated to dbo.Proj_Dash rows and the paper-space
      sheets/layouts inside each drawing file.

    Install order:
      1. Dwg_File
      2. Dwg_DashFile_Assoc
      3. Dwg_Sheet
*/

IF OBJECT_ID('dbo.Dwg_File', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Dwg_File
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dwg_File PRIMARY KEY,
        ProjID int NOT NULL,

        SavedPath nvarchar(1024) NULL,
        FileName nvarchar(260) NOT NULL,
        FullPath nvarchar(1400) NOT NULL,

        -- AutoCAD SummaryInfo fields shown on the existing File Properties tab.
        SummaryTitle nvarchar(255) NULL,
        SummarySubject nvarchar(255) NULL,
        SummaryAuthor nvarchar(255) NULL,
        SummaryKeywords nvarchar(1000) NULL,
        SummaryComments nvarchar(max) NULL,
        SummaryHyperlinkBase nvarchar(1000) NULL,
        SummaryRevision nvarchar(100) NULL,

        -- AutoCAD custom DWG properties shown on the existing File Properties tab.
        CustomPropertiesJson nvarchar(max) NULL,

        FileCreatedUtc datetime2 NULL,
        FileModifiedUtc datetime2 NULL,
        FileSizeBytes bigint NULL,
        LastScannedUtc datetime2 NULL,

        DateAdded datetime2 NOT NULL CONSTRAINT DF_Dwg_File_DateAdded DEFAULT SYSUTCDATETIME(),
        DateRevised datetime2 NOT NULL CONSTRAINT DF_Dwg_File_DateRevised DEFAULT SYSUTCDATETIME()
    );

    CREATE UNIQUE INDEX UX_Dwg_File_FullPath ON dbo.Dwg_File(FullPath);
    CREATE INDEX IX_Dwg_File_ProjID ON dbo.Dwg_File(ProjID);
END;
GO

IF OBJECT_ID('dbo.Dwg_DashFile_Assoc', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Dwg_DashFile_Assoc
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dwg_DashFile_Assoc PRIMARY KEY,
        DashID int NOT NULL,
        FileID int NOT NULL,
        DateAdded datetime2 NOT NULL CONSTRAINT DF_Dwg_DashFile_Assoc_DateAdded DEFAULT SYSUTCDATETIME(),
        DateRevised datetime2 NULL,
        IsVoid bit NOT NULL CONSTRAINT DF_Dwg_DashFile_Assoc_IsVoid DEFAULT 0,

        CONSTRAINT FK_Dwg_DashFile_Assoc_Proj_Dash
            FOREIGN KEY (DashID) REFERENCES dbo.Proj_Dash(ID),
        CONSTRAINT FK_Dwg_DashFile_Assoc_Dwg_File
            FOREIGN KEY (FileID) REFERENCES dbo.Dwg_File(ID)
    );

    CREATE UNIQUE INDEX UX_Dwg_DashFile_Assoc_Dash_File
        ON dbo.Dwg_DashFile_Assoc(DashID, FileID)
        WHERE IsVoid = 0;

    CREATE INDEX IX_Dwg_DashFile_Assoc_FileID ON dbo.Dwg_DashFile_Assoc(FileID);
END;
GO

IF OBJECT_ID('dbo.Dwg_Sheet', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Dwg_Sheet
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dwg_Sheet PRIMARY KEY,
        FileID int NOT NULL,

        -- Paper-space layout tab name.
        LayoutName nvarchar(255) NOT NULL,

        -- Titleblock attributes.
        SheetNumber nvarchar(50) NOT NULL,  -- titleblock attribute SHEET
        SheetSubject nvarchar(255) NULL,    -- titleblock attribute SUBJECT
        TitleBlockName nvarchar(255) NULL,

        DateAdded datetime2 NOT NULL CONSTRAINT DF_Dwg_Sheet_DateAdded DEFAULT SYSUTCDATETIME(),
        DateRevised datetime2 NOT NULL CONSTRAINT DF_Dwg_Sheet_DateRevised DEFAULT SYSUTCDATETIME(),
        IsVoid bit NOT NULL CONSTRAINT DF_Dwg_Sheet_IsVoid DEFAULT 0,

        CONSTRAINT FK_Dwg_Sheet_Dwg_File
            FOREIGN KEY (FileID) REFERENCES dbo.Dwg_File(ID)
    );

    CREATE UNIQUE INDEX UX_Dwg_Sheet_File_Layout
        ON dbo.Dwg_Sheet(FileID, LayoutName)
        WHERE IsVoid = 0;

    CREATE INDEX IX_Dwg_Sheet_FileID ON dbo.Dwg_Sheet(FileID);
    CREATE INDEX IX_Dwg_Sheet_SheetNumber ON dbo.Dwg_Sheet(SheetNumber);
END;
GO

/* Future revision-history table stub.  Enable when revision workflow is finalized.
IF OBJECT_ID('dbo.Dwg_SheetRevision', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Dwg_SheetRevision
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dwg_SheetRevision PRIMARY KEY,
        SheetID int NOT NULL,
        RevisionNumber nvarchar(50) NOT NULL,
        RevisionDescription nvarchar(500) NULL,
        RevisionDate date NULL,
        RevisedBy int NULL,
        DateAdded datetime2 NOT NULL CONSTRAINT DF_Dwg_SheetRevision_DateAdded DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Dwg_SheetRevision_Dwg_Sheet
            FOREIGN KEY (SheetID) REFERENCES dbo.Dwg_Sheet(ID)
    );
END;
GO
*/

/*
    2026-06-09 update:
    Defensive duplicate prevention for Drawing Series sheet logging.
    The application also checks for existing logged layouts/sheet numbers before inserting,
    but this index prevents accidental duplicate active sheet numbers within the same DWG file.
    Run after removing any duplicate active Dwg_Sheet rows for the same FileID/SheetNumber.
*/
IF OBJECT_ID('dbo.Dwg_Sheet', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Dwg_Sheet_File_SheetNumber' AND object_id = OBJECT_ID('dbo.Dwg_Sheet'))
BEGIN
    CREATE UNIQUE INDEX UX_Dwg_Sheet_File_SheetNumber
        ON dbo.Dwg_Sheet(FileID, SheetNumber)
        WHERE IsVoid = 0 AND SheetNumber <> '';
END;
GO
