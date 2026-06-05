using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.Data.Doors
{
    /// <summary>
    /// Data access for the IWC door schedule workflow.
    /// Uses existing dbo.Drs_* tables and IWCConn connection resolution.
    /// </summary>
    public sealed class DoorScheduleRepository
    {
        public int GetOrCreateSchedule(int projectId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();

            using (var find = new SqlCommand(@"
                SELECT TOP (1) ID
                FROM dbo.Drs_Schedule
                WHERE ProjID = @pid
                ORDER BY ISNULL(ScheduleReV, 0) DESC, ID DESC;", conn))
            {
                find.Parameters.AddWithValue("@pid", projectId);
                var existing = find.ExecuteScalar();
                if (existing != null && existing != DBNull.Value)
                    return Convert.ToInt32(existing);
            }

            using var create = new SqlCommand(@"
                INSERT INTO dbo.Drs_Schedule (ProjID, ScheduleDate, ScheduleReV)
                OUTPUT INSERTED.ID
                VALUES (@pid, CAST(GETDATE() AS date), 0);", conn);
            create.Parameters.AddWithValue("@pid", projectId);
            return Convert.ToInt32(create.ExecuteScalar());
        }

        public DataTable LoadOpenings(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT ID, Arch_OpeningNum, Arch_TypeNum, Arch_Desc, Flr, [To], [From], LeafQty,
                       FinishPull, FinishPush, Swing, FlooringThk, TypeID, JambID, HdwID,
                       DwgSeriesID, DO_Width, DO_Height, DO_GapHinge, DO_GapStrike,
                       DO_GapTop, DO_GapUndercut, GenericType, VoidOpening, JambType,
                       JambBy, Note, DateAdded, DateRevised
                FROM dbo.Drs_Openings
                WHERE ProjID = @pid AND ScheduleID = @sid
                ORDER BY Arch_OpeningNum, ID;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadLeaves(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT l.ID, l.OpeningID, o.Arch_OpeningNum, l.Leaf_Tag, l.Leaf_Width,
                       l.Leaf_Height, l.Leaf_Thk, l.Leaf_Hand, l.TypeID,
                       l.Note_Public, l.Note_Internal, l.Date_Add, l.Date_Edit
                FROM dbo.Drs_Leaf l
                INNER JOIN dbo.Drs_Openings o ON o.ID = l.OpeningID
                WHERE o.ProjID = @pid AND l.ScheduleID = @sid
                ORDER BY o.Arch_OpeningNum, l.Leaf_Tag, l.ID;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadJambs(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT ID, JambNum, JambType, [Desc], DashID, OpeningWidth, OpeningHeight,
                       LeafThk, GapHead, GapHinge, GapStrike, GapUndercut, JambFabBy
                FROM dbo.Drs_Jambs
                WHERE ProjID = @pid AND ScheduleID = @sid
                ORDER BY JambNum, ID;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadHardwareGroups(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT ID, HdwGroup_Arch, HdwGroup_IWC, [Description], SupplyBy,
                       Note, Hdw_Approved, Date_HdwApproved, Date_HdwEdit
                FROM dbo.Drs_Hdw
                WHERE ProjID = @pid AND ScheduleID = @sid
                ORDER BY HdwGroup_Arch, HdwGroup_IWC, ID;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadHardwareItems(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT hi.ID, hi.HdwGroupID, h.HdwGroup_Arch, h.HdwGroup_IWC,
                       hi.HdwItemID, ph.HdwNo, ph.HdwDesc, hi.Qty, hi.RelateNotes
                FROM dbo.Drs_Hdw_HdwItems hi
                INNER JOIN dbo.Drs_Hdw h ON h.ID = hi.HdwGroupID
                LEFT JOIN dbo.Proj_Hdw ph ON ph.ID = hi.HdwItemID
                WHERE h.ProjID = @pid AND h.ScheduleID = @sid
                ORDER BY h.HdwGroup_Arch, h.HdwGroup_IWC, ph.HdwNo, hi.ID;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadJambLookup(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT ID, JambNum, JambType
                FROM dbo.Drs_Jambs
                WHERE ProjID = @pid AND ScheduleID = @sid
                ORDER BY JambNum, JambType;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadHardwareGroupLookup(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT ID, HdwGroup_Arch, HdwGroup_IWC
                FROM dbo.Drs_Hdw
                WHERE ProjID = @pid AND ScheduleID = @sid
                ORDER BY HdwGroup_Arch, HdwGroup_IWC;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public DataTable LoadSwingLookup()
        {
            return Fill(@"SELECT Swing FROM dbo.Drs_Mng_Swing ORDER BY IDNum;");
        }

        public DataTable LoadJambTypeLookup()
        {
            return Fill(@"SELECT [Type] FROM dbo.Drs_Mng_JambType ORDER BY [Type];");
        }

        public DataTable LoadProjectHardwareLookup(int projectId)
        {
            return Fill(@"
                SELECT ID, HdwNo, HdwDesc
                FROM dbo.Proj_Hdw
                WHERE Proj_ID = @pid AND (HdwVoid = 0 OR HdwVoid IS NULL)
                ORDER BY HdwNo, HdwDesc;", ("@pid", projectId));
        }

        public DataTable LoadDoorScheduleTable(int projectId, int scheduleId)
        {
            return Fill(@"
                SELECT o.Arch_OpeningNum AS OpeningNo,
                       l.Leaf_Tag AS LeafNo,
                       o.LeafQty,
                       CONCAT(COALESCE(CONVERT(varchar(30), l.Leaf_Width), ''), ' x ', COALESCE(CONVERT(varchar(30), l.Leaf_Height), '')) AS LeafSize,
                       CONVERT(varchar(30), l.Leaf_Thk) AS LeafThk,
                       l.Leaf_Hand AS Hand,
                       COALESCE(j.JambNum, o.JambType, '') AS Jamb,
                       COALESCE(h.HdwGroup_Arch, '') AS HdwArch,
                       COALESCE(h.HdwGroup_IWC, '') AS HdwIWC,
                       o.[From], o.[To],
                       COALESCE(l.Note_Public, o.Note, '') AS Note
                FROM dbo.Drs_Openings o
                LEFT JOIN dbo.Drs_Leaf l ON l.OpeningID = o.ID AND l.ScheduleID = o.ScheduleID
                LEFT JOIN dbo.Drs_Jambs j ON j.ID = o.JambID
                LEFT JOIN dbo.Drs_Hdw h ON h.ID = o.HdwID
                WHERE o.ProjID = @pid AND o.ScheduleID = @sid
                  AND (o.VoidOpening = 0 OR o.VoidOpening IS NULL)
                ORDER BY o.Arch_OpeningNum, l.Leaf_Tag, l.ID;",
                ("@pid", projectId), ("@sid", scheduleId));
        }

        public void SaveOpenings(DataTable table, int projectId, int scheduleId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (row.RowState == DataRowState.Unchanged) continue;

                int id = SafeInt(row, "ID");
                int oldLeafQty = id > 0 ? GetExistingLeafQty(conn, id) : 0;
                if (id <= 0)
                {
                    id = InsertOpening(conn, row, projectId, scheduleId);
                    row["ID"] = id;
                }
                else
                {
                    UpdateOpening(conn, row, projectId, scheduleId);
                }

                int leafQty = Math.Max(0, SafeInt(row, "LeafQty"));
                if (leafQty > 0 && leafQty != oldLeafQty)
                    EnsureLeafRows(conn, id, Convert.ToString(row["Arch_OpeningNum"]) ?? string.Empty, leafQty, scheduleId);
            }
            table.AcceptChanges();
        }

        public void SaveLeaves(DataTable table, int scheduleId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (row.RowState == DataRowState.Unchanged) continue;
                if (SafeInt(row, "ID") <= 0)
                    InsertLeaf(conn, row, scheduleId);
                else
                    UpdateLeaf(conn, row, scheduleId);
            }
            table.AcceptChanges();
        }

        public void SaveJambs(DataTable table, int projectId, int scheduleId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (row.RowState == DataRowState.Unchanged) continue;
                if (SafeInt(row, "ID") <= 0)
                {
                    int id = InsertJamb(conn, row, projectId, scheduleId);
                    row["ID"] = id;
                }
                else
                    UpdateJamb(conn, row, projectId, scheduleId);
            }
            table.AcceptChanges();
        }

        public void SaveHardwareGroups(DataTable table, int projectId, int scheduleId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (row.RowState == DataRowState.Unchanged) continue;
                if (SafeInt(row, "ID") <= 0)
                {
                    int id = InsertHardwareGroup(conn, row, projectId, scheduleId);
                    row["ID"] = id;
                }
                else
                    UpdateHardwareGroup(conn, row, projectId, scheduleId);
            }
            table.AcceptChanges();
        }

        public void SaveHardwareItems(DataTable table)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (row.RowState == DataRowState.Unchanged) continue;
                if (SafeInt(row, "ID") <= 0)
                {
                    int id = InsertHardwareItem(conn, row);
                    row["ID"] = id;
                }
                else
                    UpdateHardwareItem(conn, row);
            }
            table.AcceptChanges();
        }

        private static DataTable Fill(string sql, params (string Name, object? Value)[] parameters)
        {
            using var conn = IWCConn.GetSqlConnection();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var p in parameters)
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            using var da = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

        private static int InsertOpening(SqlConnection conn, DataRow r, int projectId, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Drs_Openings
                (ProjID, DashID, Arch_OpeningNum, Arch_TypeNum, Arch_Desc, Flr, [To], [From], LeafQty,
                 FinishPull, FinishPush, Swing, FlooringThk, TypeID, JambID, HdwID, DwgSeriesID, Note,
                 DateAdded, DateRevised, DO_Width, DO_Height, DO_GapHinge, DO_GapStrike, DO_GapTop,
                 DO_GapUndercut, GenericType, VoidOpening, ScheduleID, JambType, JambBy)
                OUTPUT INSERTED.ID
                VALUES
                (@ProjID, @DashID, @Arch_OpeningNum, @Arch_TypeNum, @Arch_Desc, @Flr, @To, @From, @LeafQty,
                 @FinishPull, @FinishPush, @Swing, @FlooringThk, @TypeID, @JambID, @HdwID, @DwgSeriesID, @Note,
                 SYSUTCDATETIME(), SYSUTCDATETIME(), @DO_Width, @DO_Height, @DO_GapHinge, @DO_GapStrike, @DO_GapTop,
                 @DO_GapUndercut, @GenericType, @VoidOpening, @ScheduleID, @JambType, @JambBy);", conn);
            AddOpeningParams(cmd, r, projectId, scheduleId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void UpdateOpening(SqlConnection conn, DataRow r, int projectId, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Drs_Openings SET
                    DashID=@DashID, Arch_OpeningNum=@Arch_OpeningNum, Arch_TypeNum=@Arch_TypeNum,
                    Arch_Desc=@Arch_Desc, Flr=@Flr, [To]=@To, [From]=@From, LeafQty=@LeafQty,
                    FinishPull=@FinishPull, FinishPush=@FinishPush, Swing=@Swing, FlooringThk=@FlooringThk,
                    TypeID=@TypeID, JambID=@JambID, HdwID=@HdwID, DwgSeriesID=@DwgSeriesID, Note=@Note,
                    DateRevised=SYSUTCDATETIME(), DO_Width=@DO_Width, DO_Height=@DO_Height,
                    DO_GapHinge=@DO_GapHinge, DO_GapStrike=@DO_GapStrike, DO_GapTop=@DO_GapTop,
                    DO_GapUndercut=@DO_GapUndercut, GenericType=@GenericType, VoidOpening=@VoidOpening,
                    JambType=@JambType, JambBy=@JambBy
                WHERE ID=@ID AND ProjID=@ProjID AND ScheduleID=@ScheduleID;", conn);
            AddOpeningParams(cmd, r, projectId, scheduleId);
            cmd.Parameters.AddWithValue("@ID", SafeInt(r, "ID"));
            cmd.ExecuteNonQuery();
        }

        private static void AddOpeningParams(SqlCommand cmd, DataRow r, int projectId, int scheduleId)
        {
            cmd.Parameters.AddWithValue("@ProjID", projectId);
            cmd.Parameters.AddWithValue("@ScheduleID", scheduleId);
            AddParam(cmd, "@DashID", r, "DashID");
            AddParam(cmd, "@Arch_OpeningNum", r, "Arch_OpeningNum");
            AddParam(cmd, "@Arch_TypeNum", r, "Arch_TypeNum");
            AddParam(cmd, "@Arch_Desc", r, "Arch_Desc");
            AddParam(cmd, "@Flr", r, "Flr");
            AddParam(cmd, "@To", r, "To");
            AddParam(cmd, "@From", r, "From");
            AddParam(cmd, "@LeafQty", r, "LeafQty");
            AddParam(cmd, "@FinishPull", r, "FinishPull");
            AddParam(cmd, "@FinishPush", r, "FinishPush");
            AddParam(cmd, "@Swing", r, "Swing");
            AddParam(cmd, "@FlooringThk", r, "FlooringThk");
            AddParam(cmd, "@TypeID", r, "TypeID");
            AddParam(cmd, "@JambID", r, "JambID");
            AddParam(cmd, "@HdwID", r, "HdwID");
            AddParam(cmd, "@DwgSeriesID", r, "DwgSeriesID");
            AddParam(cmd, "@Note", r, "Note");
            AddParam(cmd, "@DO_Width", r, "DO_Width");
            AddParam(cmd, "@DO_Height", r, "DO_Height");
            AddParam(cmd, "@DO_GapHinge", r, "DO_GapHinge");
            AddParam(cmd, "@DO_GapStrike", r, "DO_GapStrike");
            AddParam(cmd, "@DO_GapTop", r, "DO_GapTop");
            AddParam(cmd, "@DO_GapUndercut", r, "DO_GapUndercut");
            AddParam(cmd, "@GenericType", r, "GenericType");
            AddParam(cmd, "@VoidOpening", r, "VoidOpening");
            AddParam(cmd, "@JambType", r, "JambType");
            AddParam(cmd, "@JambBy", r, "JambBy");
        }

        private static int GetExistingLeafQty(SqlConnection conn, int openingId)
        {
            using var cmd = new SqlCommand("SELECT ISNULL(LeafQty, 0) FROM dbo.Drs_Openings WHERE ID=@id;", conn);
            cmd.Parameters.AddWithValue("@id", openingId);
            var val = cmd.ExecuteScalar();
            return val == null || val == DBNull.Value ? 0 : Convert.ToInt32(val);
        }

        private static void EnsureLeafRows(SqlConnection conn, int openingId, string openingNo, int leafQty, int scheduleId)
        {
            using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Drs_Leaf WHERE OpeningID=@id AND ScheduleID=@sid;", conn);
            count.Parameters.AddWithValue("@id", openingId);
            count.Parameters.AddWithValue("@sid", scheduleId);
            int existing = Convert.ToInt32(count.ExecuteScalar());

            for (int i = existing + 1; i <= leafQty; i++)
            {
                using var ins = new SqlCommand(@"
                    INSERT INTO dbo.Drs_Leaf (OpeningID, Leaf_Tag, ScheduleID, Date_Add, Date_Edit)
                    VALUES (@OpeningID, @Leaf_Tag, @ScheduleID, CAST(GETDATE() AS date), CAST(GETDATE() AS date));", conn);
                ins.Parameters.AddWithValue("@OpeningID", openingId);
                ins.Parameters.AddWithValue("@Leaf_Tag", $"{openingNo}.{i}");
                ins.Parameters.AddWithValue("@ScheduleID", scheduleId);
                ins.ExecuteNonQuery();
            }
        }

        private static void InsertLeaf(SqlConnection conn, DataRow r, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Drs_Leaf
                (OpeningID, TypeID, Leaf_Tag, Leaf_Width, Leaf_Height, Leaf_Thk, Note_Public,
                 Note_Internal, Date_Add, Date_Edit, Leaf_Hand, ScheduleID)
                VALUES
                (@OpeningID, @TypeID, @Leaf_Tag, @Leaf_Width, @Leaf_Height, @Leaf_Thk, @Note_Public,
                 @Note_Internal, CAST(GETDATE() AS date), CAST(GETDATE() AS date), @Leaf_Hand, @ScheduleID);", conn);
            AddLeafParams(cmd, r, scheduleId);
            cmd.ExecuteNonQuery();
        }

        private static void UpdateLeaf(SqlConnection conn, DataRow r, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Drs_Leaf SET
                    TypeID=@TypeID, Leaf_Tag=@Leaf_Tag, Leaf_Width=@Leaf_Width,
                    Leaf_Height=@Leaf_Height, Leaf_Thk=@Leaf_Thk, Note_Public=@Note_Public,
                    Note_Internal=@Note_Internal, Date_Edit=CAST(GETDATE() AS date), Leaf_Hand=@Leaf_Hand
                WHERE ID=@ID AND ScheduleID=@ScheduleID;", conn);
            AddLeafParams(cmd, r, scheduleId);
            cmd.Parameters.AddWithValue("@ID", SafeInt(r, "ID"));
            cmd.ExecuteNonQuery();
        }

        private static void AddLeafParams(SqlCommand cmd, DataRow r, int scheduleId)
        {
            cmd.Parameters.AddWithValue("@ScheduleID", scheduleId);
            AddParam(cmd, "@OpeningID", r, "OpeningID");
            AddParam(cmd, "@TypeID", r, "TypeID");
            AddParam(cmd, "@Leaf_Tag", r, "Leaf_Tag");
            AddParam(cmd, "@Leaf_Width", r, "Leaf_Width");
            AddParam(cmd, "@Leaf_Height", r, "Leaf_Height");
            AddParam(cmd, "@Leaf_Thk", r, "Leaf_Thk");
            AddParam(cmd, "@Note_Public", r, "Note_Public");
            AddParam(cmd, "@Note_Internal", r, "Note_Internal");
            AddParam(cmd, "@Leaf_Hand", r, "Leaf_Hand");
        }

        private static int InsertJamb(SqlConnection conn, DataRow r, int projectId, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Drs_Jambs
                (ProjID, JambNum, JambType, [Desc], DashID, OpeningWidth, OpeningHeight,
                 LeafThk, GapHead, GapHinge, GapStrike, GapUndercut, ScheduleID, JambFabBy)
                OUTPUT INSERTED.ID
                VALUES
                (@ProjID, @JambNum, @JambType, @Desc, @DashID, @OpeningWidth, @OpeningHeight,
                 @LeafThk, @GapHead, @GapHinge, @GapStrike, @GapUndercut, @ScheduleID, @JambFabBy);", conn);
            AddJambParams(cmd, r, projectId, scheduleId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void UpdateJamb(SqlConnection conn, DataRow r, int projectId, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Drs_Jambs SET
                    JambNum=@JambNum, JambType=@JambType, [Desc]=@Desc, DashID=@DashID,
                    OpeningWidth=@OpeningWidth, OpeningHeight=@OpeningHeight, LeafThk=@LeafThk,
                    GapHead=@GapHead, GapHinge=@GapHinge, GapStrike=@GapStrike,
                    GapUndercut=@GapUndercut, JambFabBy=@JambFabBy
                WHERE ID=@ID AND ProjID=@ProjID AND ScheduleID=@ScheduleID;", conn);
            AddJambParams(cmd, r, projectId, scheduleId);
            cmd.Parameters.AddWithValue("@ID", SafeInt(r, "ID"));
            cmd.ExecuteNonQuery();
        }

        private static void AddJambParams(SqlCommand cmd, DataRow r, int projectId, int scheduleId)
        {
            cmd.Parameters.AddWithValue("@ProjID", projectId);
            cmd.Parameters.AddWithValue("@ScheduleID", scheduleId);
            AddParam(cmd, "@JambNum", r, "JambNum");
            AddParam(cmd, "@JambType", r, "JambType");
            AddParam(cmd, "@Desc", r, "Desc");
            AddParam(cmd, "@DashID", r, "DashID");
            AddParam(cmd, "@OpeningWidth", r, "OpeningWidth");
            AddParam(cmd, "@OpeningHeight", r, "OpeningHeight");
            AddParam(cmd, "@LeafThk", r, "LeafThk");
            AddParam(cmd, "@GapHead", r, "GapHead");
            AddParam(cmd, "@GapHinge", r, "GapHinge");
            AddParam(cmd, "@GapStrike", r, "GapStrike");
            AddParam(cmd, "@GapUndercut", r, "GapUndercut");
            AddParam(cmd, "@JambFabBy", r, "JambFabBy");
        }

        private static int InsertHardwareGroup(SqlConnection conn, DataRow r, int projectId, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Drs_Hdw
                (ProjID, HdwGroup_IWC, [Description], SupplyBy, Note, Hdw_Approved,
                 Date_HdwApproved, HdwGroup_Arch, ScheduleID, Date_HdwEdit)
                OUTPUT INSERTED.ID
                VALUES
                (@ProjID, @HdwGroup_IWC, @Description, @SupplyBy, @Note, @Hdw_Approved,
                 @Date_HdwApproved, @HdwGroup_Arch, @ScheduleID, CAST(GETDATE() AS date));", conn);
            AddHardwareGroupParams(cmd, r, projectId, scheduleId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void UpdateHardwareGroup(SqlConnection conn, DataRow r, int projectId, int scheduleId)
        {
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Drs_Hdw SET
                    HdwGroup_IWC=@HdwGroup_IWC, [Description]=@Description, SupplyBy=@SupplyBy,
                    Note=@Note, Hdw_Approved=@Hdw_Approved, Date_HdwApproved=@Date_HdwApproved,
                    HdwGroup_Arch=@HdwGroup_Arch, Date_HdwEdit=CAST(GETDATE() AS date)
                WHERE ID=@ID AND ProjID=@ProjID AND ScheduleID=@ScheduleID;", conn);
            AddHardwareGroupParams(cmd, r, projectId, scheduleId);
            cmd.Parameters.AddWithValue("@ID", SafeInt(r, "ID"));
            cmd.ExecuteNonQuery();
        }

        private static void AddHardwareGroupParams(SqlCommand cmd, DataRow r, int projectId, int scheduleId)
        {
            cmd.Parameters.AddWithValue("@ProjID", projectId);
            cmd.Parameters.AddWithValue("@ScheduleID", scheduleId);
            AddParam(cmd, "@HdwGroup_IWC", r, "HdwGroup_IWC");
            AddParam(cmd, "@Description", r, "Description");
            AddParam(cmd, "@SupplyBy", r, "SupplyBy");
            AddParam(cmd, "@Note", r, "Note");
            AddParam(cmd, "@Hdw_Approved", r, "Hdw_Approved");
            AddParam(cmd, "@Date_HdwApproved", r, "Date_HdwApproved");
            AddParam(cmd, "@HdwGroup_Arch", r, "HdwGroup_Arch");
        }

        private static int InsertHardwareItem(SqlConnection conn, DataRow r)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Drs_Hdw_HdwItems (HdwGroupID, HdwItemID, Qty, RelateNotes)
                OUTPUT INSERTED.ID
                VALUES (@HdwGroupID, @HdwItemID, @Qty, @RelateNotes);", conn);
            AddHardwareItemParams(cmd, r);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void UpdateHardwareItem(SqlConnection conn, DataRow r)
        {
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Drs_Hdw_HdwItems SET
                    HdwGroupID=@HdwGroupID, HdwItemID=@HdwItemID, Qty=@Qty, RelateNotes=@RelateNotes
                WHERE ID=@ID;", conn);
            AddHardwareItemParams(cmd, r);
            cmd.Parameters.AddWithValue("@ID", SafeInt(r, "ID"));
            cmd.ExecuteNonQuery();
        }

        private static void AddHardwareItemParams(SqlCommand cmd, DataRow r)
        {
            AddParam(cmd, "@HdwGroupID", r, "HdwGroupID");
            AddParam(cmd, "@HdwItemID", r, "HdwItemID");
            AddParam(cmd, "@Qty", r, "Qty");
            AddParam(cmd, "@RelateNotes", r, "RelateNotes");
        }

        private static void AddParam(SqlCommand cmd, string paramName, DataRow row, string colName)
        {
            if (!row.Table.Columns.Contains(colName))
            {
                cmd.Parameters.AddWithValue(paramName, DBNull.Value);
                return;
            }

            var value = row[colName];
            if (value == DBNull.Value)
            {
                cmd.Parameters.AddWithValue(paramName, DBNull.Value);
                return;
            }

            if (value is string s && string.IsNullOrWhiteSpace(s))
            {
                cmd.Parameters.AddWithValue(paramName, DBNull.Value);
                return;
            }

            cmd.Parameters.AddWithValue(paramName, value);
        }

        private static int SafeInt(DataRow row, string colName)
        {
            if (!row.Table.Columns.Contains(colName)) return 0;
            var value = row[colName];
            if (value == DBNull.Value) return 0;
            return int.TryParse(Convert.ToString(value), out int result) ? result : 0;
        }
    }
}
