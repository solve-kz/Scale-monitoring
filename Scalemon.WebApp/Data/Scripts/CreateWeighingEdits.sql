-- Храним аудит правок взвешиваний. Внешний ключ на Weighings не задаём,
-- чтобы записи об удалениях оставались доступными.
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[WeighingEdits]') AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[WeighingEdits]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_WeighingEdits] PRIMARY KEY,
        [WeighingId] INT NOT NULL,
        [EditedAt] DATETIME2(3) NOT NULL CONSTRAINT [DF_WeighingEdits_EditedAt] DEFAULT (SYSUTCDATETIME()),
        [EditedBy] NVARCHAR(256) NULL,
        [Action] NVARCHAR(32) NOT NULL,
        [OldWeight] DECIMAL(18,2) NULL,
        [NewWeight] DECIMAL(18,2) NULL,
        [RecordedAtSnapshot] DATETIME2(3) NULL,
        [Comment] NVARCHAR(512) NULL
    );

    CREATE INDEX [IX_WeighingEdits_EditedAt] ON [dbo].[WeighingEdits]([EditedAt]);
    CREATE INDEX [IX_WeighingEdits_EditedBy] ON [dbo].[WeighingEdits]([EditedBy]);
    CREATE INDEX [IX_WeighingEdits_RecordedAt] ON [dbo].[WeighingEdits]([RecordedAtSnapshot]);
END;
