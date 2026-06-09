using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IWCCadToolsV9.Data.Models;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.Data
{
    public sealed class DrawingSeriesRepository
    {
        public async Task<IReadOnlyList<DrawingSeriesDashNode>> GetDashSheetTreeAsync(int projectId, int currentDashId)
        {
            var current = await GetDashInfoAsync(currentDashId).ConfigureAwait(false);
            if (current == null)
                return Array.Empty<DrawingSeriesDashNode>();

            int seriesDashId = current.DashId;

            // Per current project requirements: 0 = Series, 1 = Component.
            // Some older IWC views have used 1/2.  Parent linkage is the safer signal,
            // so component rows are resolved to their parent whenever Dash_Parent is set.
            if (current.DashParent.HasValue && current.DashParent.Value > 0)
                seriesDashId = current.DashParent.Value;

            var dashes = new List<DrawingSeriesDashNode>();
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);

            using (var cmd = new SqlCommand(@"
                SELECT ID, Dash_Num, Dash_Desc, Dash_Parent, Dash_Type
                FROM dbo.Proj_Dash
                WHERE Proj_ID = @projectId
                  AND (ID = @seriesDashId OR Dash_Parent = @seriesDashId)
                  AND (Act_Void = 0 OR Act_Void IS NULL)
                ORDER BY CASE WHEN ID = @seriesDashId THEN 0 ELSE 1 END,
                         TRY_CAST(Dash_Num AS int), Dash_Num;", conn))
            {
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@seriesDashId", seriesDashId);
                using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await rdr.ReadAsync().ConfigureAwait(false))
                {
                    dashes.Add(new DrawingSeriesDashNode
                    {
                        DashId = SafeInt(rdr, "ID"),
                        DashNum = SafeString(rdr, "Dash_Num"),
                        DashDesc = SafeString(rdr, "Dash_Desc"),
                        DashParent = SafeNullableInt(rdr, "Dash_Parent"),
                        DashType = SafeNullableInt(rdr, "Dash_Type"),
                        IsCurrentDash = SafeInt(rdr, "ID") == currentDashId
                    });
                }
            }

            foreach (var dash in dashes)
            {
                var files = await GetFilesForDashAsync(conn, dash.DashId).ConfigureAwait(false);
                dash.Files.AddRange(files);
            }

            return dashes;
        }

        public Task<int> UpsertFileForCurrentDocumentAsync(
            int projectId,
            int dashId,
            string fullPath,
            string? summaryTitle,
            string? summarySubject,
            string? summaryAuthor,
            string? summaryKeywords,
            string? summaryComments,
            string? summaryHyperlinkBase,
            string? summaryRevision,
            IReadOnlyDictionary<string, string> customProperties)
            => UpsertFileForCurrentDocumentAsync(
                projectId, dashId, fullPath, summaryTitle, summarySubject, summaryAuthor,
                summaryKeywords, summaryComments, summaryHyperlinkBase, summaryRevision,
                customProperties, existingFileId: null);

        public async Task<int> UpsertFileForCurrentDocumentAsync(
            int projectId,
            int dashId,
            string fullPath,
            string? summaryTitle,
            string? summarySubject,
            string? summaryAuthor,
            string? summaryKeywords,
            string? summaryComments,
            string? summaryHyperlinkBase,
            string? summaryRevision,
            IReadOnlyDictionary<string, string> customProperties,
            int? existingFileId)
        {
            string fileName = Path.GetFileName(fullPath);
            string savedPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
            DateTime? createdUtc = File.Exists(fullPath) ? File.GetCreationTimeUtc(fullPath) : null;
            DateTime? modifiedUtc = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : null;
            long? fileSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : null;
            string customJson = JsonSerializer.Serialize(customProperties ?? new Dictionary<string, string>());

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = new SqlCommand(@"
                DECLARE @existingID int;

                IF @ExistingFileID IS NOT NULL
                BEGIN
                    SELECT TOP (1) @existingID = ID
                    FROM dbo.Dwg_File
                    WHERE ID = @ExistingFileID;
                END

                IF @existingID IS NULL
                BEGIN
                    SELECT TOP (1) @existingID = ID
                    FROM dbo.Dwg_File
                    WHERE FullPath = @FullPath;
                END

                IF @existingID IS NULL
                BEGIN
                    INSERT INTO dbo.Dwg_File
                    (
                        ProjID, SavedPath, FileName, FullPath,
                        SummaryTitle, SummarySubject, SummaryAuthor, SummaryKeywords,
                        SummaryComments, SummaryHyperlinkBase, SummaryRevision,
                        CustomPropertiesJson, FileCreatedUtc, FileModifiedUtc, FileSizeBytes,
                        DateAdded, DateRevised
                    )
                    VALUES
                    (
                        @ProjID, @SavedPath, @FileName, @FullPath,
                        @SummaryTitle, @SummarySubject, @SummaryAuthor, @SummaryKeywords,
                        @SummaryComments, @SummaryHyperlinkBase, @SummaryRevision,
                        @CustomPropertiesJson, @FileCreatedUtc, @FileModifiedUtc, @FileSizeBytes,
                        SYSUTCDATETIME(), SYSUTCDATETIME()
                    );
                    SET @existingID = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.Dwg_File
                    SET ProjID = @ProjID,
                        SavedPath = @SavedPath,
                        FileName = @FileName,
                        FullPath = @FullPath,
                        SummaryTitle = @SummaryTitle,
                        SummarySubject = @SummarySubject,
                        SummaryAuthor = @SummaryAuthor,
                        SummaryKeywords = @SummaryKeywords,
                        SummaryComments = @SummaryComments,
                        SummaryHyperlinkBase = @SummaryHyperlinkBase,
                        SummaryRevision = @SummaryRevision,
                        CustomPropertiesJson = @CustomPropertiesJson,
                        FileCreatedUtc = @FileCreatedUtc,
                        FileModifiedUtc = @FileModifiedUtc,
                        FileSizeBytes = @FileSizeBytes,
                        DateRevised = SYSUTCDATETIME()
                    WHERE ID = @existingID;
                END

                IF NOT EXISTS
                (
                    SELECT 1 FROM dbo.Dwg_DashFile_Assoc
                    WHERE DashID = @DashID AND FileID = @existingID
                )
                BEGIN
                    INSERT INTO dbo.Dwg_DashFile_Assoc(DashID, FileID, DateAdded)
                    VALUES(@DashID, @existingID, SYSUTCDATETIME());
                END

                SELECT @existingID;", conn);

            cmd.Parameters.AddWithValue("@ProjID", projectId);
            cmd.Parameters.AddWithValue("@DashID", dashId);
            cmd.Parameters.AddWithValue("@ExistingFileID", (object?)existingFileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SavedPath", savedPath);
            cmd.Parameters.AddWithValue("@FileName", fileName);
            cmd.Parameters.AddWithValue("@FullPath", fullPath);
            cmd.Parameters.AddWithValue("@SummaryTitle", (object?)summaryTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummarySubject", (object?)summarySubject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummaryAuthor", (object?)summaryAuthor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummaryKeywords", (object?)summaryKeywords ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummaryComments", (object?)summaryComments ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummaryHyperlinkBase", (object?)summaryHyperlinkBase ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummaryRevision", (object?)summaryRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CustomPropertiesJson", customJson);
            cmd.Parameters.AddWithValue("@FileCreatedUtc", (object?)createdUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileModifiedUtc", (object?)modifiedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileSizeBytes", (object?)fileSize ?? DBNull.Value);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }

        public async Task<int> UpsertSheetAsync(int fileId, SheetTitleBlockInfo sheet)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(@"
                DECLARE @existingID int;

                SELECT TOP (1) @existingID = ID
                FROM dbo.Dwg_Sheet
                WHERE FileID = @FileID
                  AND (IsVoid = 0 OR IsVoid IS NULL)
                  AND (
                        LayoutName = @LayoutName
                        OR (NULLIF(@SheetNumber, '') IS NOT NULL AND SheetNumber = @SheetNumber)
                      )
                ORDER BY CASE WHEN LayoutName = @LayoutName THEN 0 ELSE 1 END, ID;

                IF @existingID IS NULL
                BEGIN
                    INSERT INTO dbo.Dwg_Sheet
                    (
                        FileID, LayoutName, SheetNumber, SheetSubject,
                        TitleBlockName, DateAdded, DateRevised
                    )
                    VALUES
                    (
                        @FileID, @LayoutName, @SheetNumber, @SheetSubject,
                        @TitleBlockName, SYSUTCDATETIME(), SYSUTCDATETIME()
                    );
                    SET @existingID = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.Dwg_Sheet
                    SET SheetNumber = @SheetNumber,
                        SheetSubject = @SheetSubject,
                        TitleBlockName = @TitleBlockName,
                        DateRevised = SYSUTCDATETIME()
                    WHERE ID = @existingID;
                END

                SELECT @existingID;", conn);
            cmd.Parameters.AddWithValue("@FileID", fileId);
            cmd.Parameters.AddWithValue("@LayoutName", sheet.LayoutName);
            cmd.Parameters.AddWithValue("@SheetNumber", sheet.SheetNumber);
            cmd.Parameters.AddWithValue("@SheetSubject", sheet.SheetSubject);
            cmd.Parameters.AddWithValue("@TitleBlockName", sheet.TitleBlockName);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }


        public async Task<(int SheetId, bool Added)> AddSheetIfMissingAsync(int fileId, SheetTitleBlockInfo sheet)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);

            int? existingId = await FindExistingSheetIdAsync(conn, fileId, sheet).ConfigureAwait(false);
            if (existingId.HasValue && existingId.Value > 0)
                return (existingId.Value, false);

            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Dwg_Sheet
                (
                    FileID, LayoutName, SheetNumber, SheetSubject,
                    TitleBlockName, DateAdded, DateRevised
                )
                VALUES
                (
                    @FileID, @LayoutName, @SheetNumber, @SheetSubject,
                    @TitleBlockName, SYSUTCDATETIME(), SYSUTCDATETIME()
                );
                SELECT CONVERT(int, SCOPE_IDENTITY());", conn);

            cmd.Parameters.AddWithValue("@FileID", fileId);
            cmd.Parameters.AddWithValue("@LayoutName", sheet.LayoutName ?? string.Empty);
            cmd.Parameters.AddWithValue("@SheetNumber", sheet.SheetNumber ?? string.Empty);
            cmd.Parameters.AddWithValue("@SheetSubject", (object?)(sheet.SheetSubject ?? string.Empty) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TitleBlockName", (object?)(sheet.TitleBlockName ?? string.Empty) ?? DBNull.Value);
            int sheetId = Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
            return (sheetId, true);
        }

        public async Task<bool> IsSheetAlreadyLoggedAsync(int fileId, SheetTitleBlockInfo sheet)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            int? existingId = await FindExistingSheetIdAsync(conn, fileId, sheet).ConfigureAwait(false);
            return existingId.HasValue && existingId.Value > 0;
        }

        public async Task DeleteSheetAsync(int sheetId)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(@"
                DELETE FROM dbo.Dwg_Sheet
                WHERE ID = @SheetID;", conn);
            cmd.Parameters.AddWithValue("@SheetID", sheetId);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task DeleteFileEntryAsync(int dashId, int fileId)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                using (var cmd = new SqlCommand(@"
                    DELETE FROM dbo.Dwg_DashFile_Assoc
                    WHERE DashID = @DashID AND FileID = @FileID;", conn, (SqlTransaction)tx))
                {
                    cmd.Parameters.AddWithValue("@DashID", dashId);
                    cmd.Parameters.AddWithValue("@FileID", fileId);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                int remainingAssoc;
                using (var cmd = new SqlCommand(@"
                    SELECT COUNT(1)
                    FROM dbo.Dwg_DashFile_Assoc
                    WHERE FileID = @FileID;", conn, (SqlTransaction)tx))
                {
                    cmd.Parameters.AddWithValue("@FileID", fileId);
                    remainingAssoc = Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }

                if (remainingAssoc == 0)
                {
                    using (var cmd = new SqlCommand(@"
                        DELETE FROM dbo.Dwg_Sheet
                        WHERE FileID = @FileID;", conn, (SqlTransaction)tx))
                    {
                        cmd.Parameters.AddWithValue("@FileID", fileId);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    using (var cmd = new SqlCommand(@"
                        DELETE FROM dbo.Dwg_File
                        WHERE ID = @FileID;", conn, (SqlTransaction)tx))
                    {
                        cmd.Parameters.AddWithValue("@FileID", fileId);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }


        public async Task<int?> GetFileIdByFullPathAsync(string fullPath)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(@"
                SELECT TOP (1) ID
                FROM dbo.Dwg_File
                WHERE FullPath = @FullPath;", conn);
            cmd.Parameters.AddWithValue("@FullPath", fullPath);
            object? value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
        }

        public async Task<IReadOnlyList<DrawingSeriesSheetRecord>> GetSheetsForFileIdAsync(int fileId)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            return await GetSheetsForFileAsync(conn, fileId).ConfigureAwait(false);
        }

        public async Task UpdateSheetAsync(int sheetId, string layoutName, string sheetNumber, string sheetSubject)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Dwg_Sheet
                SET LayoutName = @LayoutName,
                    SheetNumber = @SheetNumber,
                    SheetSubject = @SheetSubject,
                    DateRevised = SYSUTCDATETIME()
                WHERE ID = @SheetID;", conn);
            cmd.Parameters.AddWithValue("@SheetID", sheetId);
            cmd.Parameters.AddWithValue("@LayoutName", layoutName ?? string.Empty);
            cmd.Parameters.AddWithValue("@SheetNumber", sheetNumber ?? string.Empty);
            cmd.Parameters.AddWithValue("@SheetSubject", sheetSubject ?? string.Empty);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }


        public async Task<int> RefreshLoggedSheetAsync(int fileId, SheetTitleBlockInfo sheet)
        {
            if (sheet.ExistingSheetId.HasValue && sheet.ExistingSheetId.Value > 0)
            {
                using var conn = IWCConn.GetSqlConnection();
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = new SqlCommand(@"
                    UPDATE dbo.Dwg_Sheet
                    SET LayoutName = @LayoutName,
                        SheetNumber = @SheetNumber,
                        SheetSubject = @SheetSubject,
                        DateRevised = SYSUTCDATETIME()
                    WHERE ID = @SheetID AND FileID = @FileID;", conn);
                cmd.Parameters.AddWithValue("@SheetID", sheet.ExistingSheetId.Value);
                cmd.Parameters.AddWithValue("@FileID", fileId);
                cmd.Parameters.AddWithValue("@LayoutName", sheet.LayoutName ?? string.Empty);
                cmd.Parameters.AddWithValue("@SheetNumber", sheet.SheetNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("@SheetSubject", sheet.SheetSubject ?? string.Empty);
                int affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                    return sheet.ExistingSheetId.Value;
            }

            return await UpsertSheetAsync(fileId, sheet).ConfigureAwait(false);
        }

        public async Task<bool> IsFileAssociatedWithDashAsync(int dashId, string fullPath)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(@"
                SELECT COUNT(1)
                FROM dbo.Dwg_File f
                INNER JOIN dbo.Dwg_DashFile_Assoc a ON a.FileID = f.ID
                WHERE a.DashID = @DashID AND f.FullPath = @FullPath;", conn);
            cmd.Parameters.AddWithValue("@DashID", dashId);
            cmd.Parameters.AddWithValue("@FullPath", fullPath);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) > 0;
        }

        public async Task<IReadOnlyList<DrawingSeriesSheetRecord>> GetSheetsForFilePathOrIdAsync(string fullPath, int? fileId)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);

            int? resolvedFileId = fileId;
            if (!resolvedFileId.HasValue || resolvedFileId.Value <= 0)
            {
                using var idCmd = new SqlCommand(@"
                    SELECT TOP (1) ID
                    FROM dbo.Dwg_File
                    WHERE FullPath = @FullPath;", conn);
                idCmd.Parameters.AddWithValue("@FullPath", fullPath ?? string.Empty);
                object? value = await idCmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (value != null && value != DBNull.Value)
                    resolvedFileId = Convert.ToInt32(value);
            }

            if (!resolvedFileId.HasValue || resolvedFileId.Value <= 0)
                return Array.Empty<DrawingSeriesSheetRecord>();

            return await GetSheetsForFileAsync(conn, resolvedFileId.Value).ConfigureAwait(false);
        }

        public async Task DeleteSheetsAsync(IEnumerable<int> sheetIds)
        {
            var ids = sheetIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return;

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);

            for (int i = 0; i < ids.Count; i++)
            {
                using var cmd = new SqlCommand("DELETE FROM dbo.Dwg_Sheet WHERE ID = @SheetID;", conn);
                cmd.Parameters.AddWithValue("@SheetID", ids[i]);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private async Task<DrawingSeriesDashNode?> GetDashInfoAsync(int dashId)
        {
            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(@"
                SELECT ID, Dash_Num, Dash_Desc, Dash_Parent, Dash_Type
                FROM dbo.Proj_Dash
                WHERE ID = @dashId;", conn);
            cmd.Parameters.AddWithValue("@dashId", dashId);
            using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await rdr.ReadAsync().ConfigureAwait(false)) return null;
            return new DrawingSeriesDashNode
            {
                DashId = SafeInt(rdr, "ID"),
                DashNum = SafeString(rdr, "Dash_Num"),
                DashDesc = SafeString(rdr, "Dash_Desc"),
                DashParent = SafeNullableInt(rdr, "Dash_Parent"),
                DashType = SafeNullableInt(rdr, "Dash_Type"),
                IsCurrentDash = true
            };
        }

        private static async Task<List<DrawingSeriesFileRecord>> GetFilesForDashAsync(SqlConnection conn, int dashId)
        {
            var files = new List<DrawingSeriesFileRecord>();
            using (var cmd = new SqlCommand(@"
                SELECT f.ID, a.DashID, d.Dash_Num, d.Dash_Desc,
                       f.FileName, f.SavedPath, f.FullPath, f.FileModifiedUtc
                FROM dbo.Dwg_DashFile_Assoc a
                INNER JOIN dbo.Dwg_File f ON f.ID = a.FileID
                INNER JOIN dbo.Proj_Dash d ON d.ID = a.DashID
                WHERE a.DashID = @dashId
                  AND (a.IsVoid = 0 OR a.IsVoid IS NULL)
                ORDER BY f.FileName;", conn))
            {
                cmd.Parameters.AddWithValue("@dashId", dashId);
                using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await rdr.ReadAsync().ConfigureAwait(false))
                {
                    files.Add(new DrawingSeriesFileRecord
                    {
                        FileId = SafeInt(rdr, "ID"),
                        DashId = SafeInt(rdr, "DashID"),
                        DashNum = SafeString(rdr, "Dash_Num"),
                        DashDesc = SafeString(rdr, "Dash_Desc"),
                        FileName = SafeString(rdr, "FileName"),
                        SavedPath = SafeString(rdr, "SavedPath"),
                        FullPath = SafeString(rdr, "FullPath"),
                        LastWriteTimeUtc = SafeNullableDateTime(rdr, "FileModifiedUtc")
                    });
                }
            }

            foreach (var file in files)
                file.Sheets.AddRange(await GetSheetsForFileAsync(conn, file.FileId).ConfigureAwait(false));

            return files;
        }

        private static async Task<List<DrawingSeriesSheetRecord>> GetSheetsForFileAsync(SqlConnection conn, int fileId)
        {
            var sheets = new List<DrawingSeriesSheetRecord>();
            using var cmd = new SqlCommand(@"
                SELECT ID, FileID, LayoutName, SheetNumber, SheetSubject
                FROM dbo.Dwg_Sheet
                WHERE FileID = @fileId
                  AND (IsVoid = 0 OR IsVoid IS NULL)
                ORDER BY TRY_CAST(SheetNumber AS int), SheetNumber, LayoutName;", conn);
            cmd.Parameters.AddWithValue("@fileId", fileId);
            using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await rdr.ReadAsync().ConfigureAwait(false))
            {
                sheets.Add(new DrawingSeriesSheetRecord
                {
                    SheetId = SafeInt(rdr, "ID"),
                    FileId = SafeInt(rdr, "FileID"),
                    LayoutName = SafeString(rdr, "LayoutName"),
                    SheetNumber = SafeString(rdr, "SheetNumber"),
                    SheetSubject = SafeString(rdr, "SheetSubject")
                });
            }
            return sheets;
        }


        private static async Task<int?> FindExistingSheetIdAsync(SqlConnection conn, int fileId, SheetTitleBlockInfo sheet)
        {
            using var cmd = new SqlCommand(@"
                SELECT TOP (1) ID
                FROM dbo.Dwg_Sheet
                WHERE FileID = @FileID
                  AND (IsVoid = 0 OR IsVoid IS NULL)
                  AND (
                        (@ExistingSheetID IS NOT NULL AND ID = @ExistingSheetID)
                        OR LayoutName = @LayoutName
                        OR (NULLIF(@SheetNumber, '') IS NOT NULL AND SheetNumber = @SheetNumber)
                      )
                ORDER BY CASE WHEN @ExistingSheetID IS NOT NULL AND ID = @ExistingSheetID THEN 0 ELSE 1 END,
                         ID;", conn);
            cmd.Parameters.AddWithValue("@FileID", fileId);
            cmd.Parameters.AddWithValue("@ExistingSheetID", (object?)sheet.ExistingSheetId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LayoutName", sheet.LayoutName ?? string.Empty);
            cmd.Parameters.AddWithValue("@SheetNumber", sheet.SheetNumber ?? string.Empty);
            object? value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
        }

        private static string SafeString(SqlDataReader rdr, string name)
            => rdr[name] == DBNull.Value ? string.Empty : rdr[name].ToString() ?? string.Empty;

        private static int SafeInt(SqlDataReader rdr, string name)
            => rdr[name] == DBNull.Value ? 0 : Convert.ToInt32(rdr[name]);

        private static int? SafeNullableInt(SqlDataReader rdr, string name)
            => rdr[name] == DBNull.Value ? null : Convert.ToInt32(rdr[name]);

        private static DateTime? SafeNullableDateTime(SqlDataReader rdr, string name)
            => rdr[name] == DBNull.Value ? null : Convert.ToDateTime(rdr[name]);
    }
}
