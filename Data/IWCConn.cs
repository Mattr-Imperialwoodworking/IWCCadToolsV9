using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.Data
{
    /// <summary>
    /// Central SQL connection helper for IWC CAD Tools V9.
    ///
    /// Connection string resolution order:
    ///   1. Explicit override passed to constructor / static helpers.
    ///   2. Environment variable  IWC_SQL_CONN
    ///   3. Built-in default (see <see cref="_defaultCs"/>).
    ///
    /// IMPORTANT: Do NOT commit real credentials to source control.
    /// Set the IWC_SQL_CONN environment variable on workstations instead.
    /// </summary>
    public sealed class IWCConn : IDisposable
    {
        // ---------------------------------------------------------------------------
        // Default connection string – replace credentials with env var in production.
        // ---------------------------------------------------------------------------
        private static readonly string _defaultCs =
            "Data Source=iwcprojectportal.database.windows.net;" +
            "Initial Catalog=iwcproj;" +
            "User ID=imperialww;" +
            "Password=IMP6920+FH;" +          // TODO: move to env var / Key Vault
            "Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";

        // Instance members
        private readonly string _connectionString;
        private bool _disposed;

        /// <summary>The open (or closed) instance connection. Always call <see cref="DBConnect"/> before use.</summary>
        public SqlConnection? Conn { get; private set; }

        /// <summary>
        /// Non-nullable accessor. Throws <see cref="InvalidOperationException"/> if
        /// <see cref="DBConnect"/> has not been called. Use instead of <c>Conn!</c>.
        /// </summary>
        public SqlConnection OpenConn => Conn ?? throw new InvalidOperationException(
            "Call DBConnect() before accessing OpenConn.");

        // ---------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------

        public IWCConn(string? connectionString = null)
        {
            _connectionString = Resolve(connectionString);
        }

        // ---------------------------------------------------------------------------
        // Instance API (open / close lifecycle)
        // ---------------------------------------------------------------------------

        /// <summary>Opens the instance connection if not already open.</summary>
        public void DBConnect()
        {
            ThrowIfDisposed();
            Conn ??= new SqlConnection(_connectionString);
            if (Conn.State != ConnectionState.Open)
                Conn.Open();
        }

        /// <summary>Closes the instance connection without disposing it.</summary>
        public void DBClose()
        {
            if (Conn?.State != ConnectionState.Closed)
                Conn?.Close();
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            DBClose();
            Conn?.Dispose();
            Conn = null!;
            _disposed = true;
        }

        // ---------------------------------------------------------------------------
        // Static helpers – convenience one-liners for controls / commands
        // ---------------------------------------------------------------------------

        /// <summary>Returns an already-OPEN <see cref="SqlConnection"/>. Caller must dispose.</summary>
        public static SqlConnection GetOpenConnection(string? connectionString = null)
        {
            var conn = new SqlConnection(Resolve(connectionString));
            conn.Open();
            return conn;
        }

        /// <summary>Returns a CLOSED <see cref="SqlConnection"/>. Caller may open later.</summary>
        public static SqlConnection GetSqlConnection(string? connectionString = null)
            => new(Resolve(connectionString));

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static string Resolve(string? overrideCs)
        {
            if (!string.IsNullOrWhiteSpace(overrideCs))
                return overrideCs;

            var fromEnv = Environment.GetEnvironmentVariable("IWC_SQL_CONN");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            return _defaultCs;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IWCConn));
        }
    }
}
