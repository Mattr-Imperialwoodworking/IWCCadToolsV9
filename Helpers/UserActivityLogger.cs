using System;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using IWCCadToolsV9.Data;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Logs user activity events to dbo.Mnge_UserActivityLog.
    /// Failures are swallowed silently so logging never disrupts workflow.
    /// </summary>
    public static class UserActivityLogger
    {
        /// <summary>
        /// Records an activity event.
        /// </summary>
        /// <param name="operation">Short description of the event (max ~50 chars).</param>
        /// <param name="projId">Optional project ID for context.</param>
        /// <param name="dashId">Optional dash/series ID for context.</param>
        public static void Log(string operation, int? projId = null, int? dashId = null)
        {
            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                int? userId = GetCurrentUserId(conn.OpenConn);

                using var cmd = new SqlCommand(@"
                    INSERT INTO dbo.Mnge_UserActivityLog (UserID, Operation, LogDate)
                    VALUES (@UserID, @Operation, @LogDate)", conn.OpenConn);

                cmd.Parameters.AddWithValue("@UserID",    (object?)userId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Operation", operation ?? string.Empty);
                cmd.Parameters.AddWithValue("@LogDate",   DateTime.Now);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IWC] UserActivityLogger.Log failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static int? GetCurrentUserId(SqlConnection conn)
        {
            using var cmd = new SqlCommand(
                "SELECT ID FROM dbo.Mng_Users WHERE UserLogin = @login", conn);
            cmd.Parameters.AddWithValue("@login", Environment.UserName);

            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value
                ? Convert.ToInt32(result)
                : null;
        }
    }
}
