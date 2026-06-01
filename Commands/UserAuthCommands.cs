using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Microsoft.Data.SqlClient;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception   = System.Exception;

namespace IWCCadToolsV9.Commands
{
    /// <summary>
    /// AutoCAD commands for IWC user authentication.
    /// Handles login, new-user creation, and password changes.
    /// </summary>
    public class UserAuthCommands
    {
        [CommandMethod("IWCUserLogin")]
        public void IWCUserLogin()
        {
            string? username = InputHelper.ShowInputBox("Enter your username:", "IWC User Login");
            if (string.IsNullOrEmpty(username)) return;

            string? password = InputHelper.ShowPasswordBox("Enter your password:", "IWC User Login");
            if (string.IsNullOrEmpty(password)) return;

            using var conn = new IWCConn();
            conn.DBConnect();

            byte[]? dbHash = null, dbSalt = null;
            using (var cmd = new SqlCommand(
                "SELECT PasswordHash, PasswordSalt FROM dbo.Mng_Users WHERE UserLogin = @u", conn.OpenConn))
            {
                cmd.Parameters.AddWithValue("@u", username);
                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    dbHash = rdr["PasswordHash"] as byte[];
                    dbSalt = rdr["PasswordSalt"] as byte[];
                }
            }

            // New user
            if (dbHash == null || dbSalt == null)
            {
                MessageBox.Show("Username not found. Creating new user.", "IWC User Login");
                if (SetOrChangePassword(username, password, conn.OpenConn))
                {
                    MessageBox.Show("User created. Login successful.");
                    RunStartup();
                }
                else
                {
                    MessageBox.Show("Failed to create user.");
                }
                return;
            }

            // Existing user – verify
            if (!PasswordHelper.VerifyPassword(password, dbHash, dbSalt))
            {
                MessageBox.Show("Invalid password.", "IWC User Login");
                return;
            }

            // Optionally change password
            if (MessageBox.Show("Change password?", "IWC User Login", MessageBoxButtons.YesNo)
                == DialogResult.Yes)
            {
                if (SetOrChangePassword(username, null, conn.OpenConn))
                    MessageBox.Show("Password changed successfully.");
            }

            MessageBox.Show("Login successful.");
            RunStartup();
        }

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static bool SetOrChangePassword(string username, string? initialPassword,
            SqlConnection conn)
        {
            string? newPw = initialPassword
                ?? InputHelper.ShowPasswordBox("Enter new password:", "Set Password");
            if (string.IsNullOrEmpty(newPw)) return false;

            string? confirm = InputHelper.ShowPasswordBox("Re-enter new password:", "Confirm Password");
            if (string.IsNullOrEmpty(confirm)) return false;
            if (confirm != newPw)
            {
                MessageBox.Show("Passwords do not match.", "Password Error");
                return false;
            }

            var (hash, salt) = PasswordHelper.HashPassword(newPw);
            const string sql = @"
                IF EXISTS (SELECT 1 FROM dbo.Mng_Users WHERE UserLogin = @u)
                    UPDATE dbo.Mng_Users
                       SET PasswordHash = @h, PasswordSalt = @s
                     WHERE UserLogin = @u
                ELSE
                    INSERT INTO dbo.Mng_Users (UserLogin, PasswordHash, PasswordSalt)
                    VALUES (@u, @h, @s)";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@u", username);
            cmd.Parameters.AddWithValue("@h", hash);
            cmd.Parameters.AddWithValue("@s", salt);
            cmd.ExecuteNonQuery();
            return true;
        }

        private static void RunStartup()
        {
            new Core.IWCStartup().Initialize();
        }
    }

    // ---------------------------------------------------------------------------
    // Small, reusable WinForms input dialogs
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Lightweight modal dialog helpers for collecting user input inside AutoCAD.
    /// </summary>
    public static class InputHelper
    {
        public static string? ShowInputBox(string prompt, string title)
            => ShowBox(prompt, title, maskInput: false);

        public static string? ShowPasswordBox(string prompt, string title)
            => ShowBox(prompt, title, maskInput: true);

        private static string? ShowBox(string prompt, string title, bool maskInput)
        {
            using var form = new Form
            {
                Width         = 420,
                Height        = 160,
                Text          = title,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox   = false, MinimizeBox = false
            };
            var label = new Label  { Left = 12, Top = 18, Width = 380, Text = prompt };
            var box   = new TextBox
            {
                Left         = 12, Top = 44, Width = 380,
                PasswordChar = maskInput ? '*' : '\0'
            };
            var ok = new Button
            {
                Text         = "OK",
                Left         = 310, Width = 82, Top = 82,
                DialogResult = DialogResult.OK
            };
            form.Controls.AddRange(new Control[] { label, box, ok });
            form.AcceptButton = ok;

            return form.ShowDialog() == DialogResult.OK ? box.Text : null;
        }
    }
}
