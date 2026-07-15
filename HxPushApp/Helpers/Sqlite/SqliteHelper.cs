using HxPushApp.models.Message;
using Microsoft.Maui.Storage;
using SQLite;

namespace HxPushApp.Helpers.Sqlite
{
    /// <summary>
    /// SQLite 消息存储帮助类，只负责本地数据库初始化和消息读写。
    /// 不处理 WebSocket 连接，也不关心消息来源，保持通信和存储解耦。
    /// </summary>
    public sealed class SqliteHelper
    {
        private const string DatabaseFileName = "hxpush.db3";
        private const int MaxStoredMessages = 10_000;
        private static readonly Lazy<SqliteHelper> LazyInstance = new(() => new SqliteHelper());

        private readonly string databasePath;
        private SQLiteAsyncConnection database;
        private readonly SemaphoreSlim initializeLock = new(1, 1);
        private readonly SemaphoreSlim saveLock = new(1, 1);
        private bool isInitialized;

        private SqliteHelper()
        {
            databasePath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFileName);
            database = CreateDatabaseConnection();
        }

        /// <summary>
        /// 全局单例，方便页面或其它服务复用同一个数据库连接。
        /// </summary>
        public static SqliteHelper Instance => LazyInstance.Value;

        /// <summary>
        /// Raised after the local SQLite database has been deleted.
        /// </summary>
        public event EventHandler? DatabaseDeleted;

        /// <summary>
        /// Closes and removes the local SQLite database and its journal files.
        /// A new empty database will be created on the next read or write operation.
        /// </summary>
        public async Task DeleteDatabaseAsync()
        {
            await initializeLock.WaitAsync();
            await saveLock.WaitAsync();
            try
            {
                await database.CloseAsync();
                DeleteDatabaseFile(databasePath);
                DeleteDatabaseFile($"{databasePath}-wal");
                DeleteDatabaseFile($"{databasePath}-shm");

                database = CreateDatabaseConnection();
                isInitialized = false;
            }
            finally
            {
                saveLock.Release();
                initializeLock.Release();
            }

            DatabaseDeleted?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 初始化数据库表结构。重复调用是安全的。
        /// </summary>
        public async Task InitializeAsync()
        {
            if (isInitialized)
            {
                return;
            }

            await initializeLock.WaitAsync();
            try
            {
                if (isInitialized)
                {
                    return;
                }

                await database.ExecuteAsync(
                    """
                    CREATE TABLE IF NOT EXISTS HxPushMsgModel (
                        ID TEXT NOT NULL PRIMARY KEY,
                        AppKey TEXT NOT NULL,
                        Hwid TEXT NOT NULL,
                        MsgDate INTEGER NOT NULL,
                        Msg TEXT NOT NULL,
                        IsRead INTEGER NOT NULL DEFAULT 0
                    );
                    """);

                // 兼容已有安装：共享模型增加 IsRead 后补齐本地表字段。
                var columns = await database.GetTableInfoAsync("HxPushMsgModel");
                if (!columns.Any(column =>
                        string.Equals(column.Name, nameof(HxPushMsgModel.IsRead), StringComparison.OrdinalIgnoreCase)))
                {
                    await database.ExecuteAsync(
                        "ALTER TABLE HxPushMsgModel ADD COLUMN IsRead INTEGER NOT NULL DEFAULT 0;");
                }

                // 旧版 MsgDate 使用秒级时间戳，安全升级为毫秒后再参与排序。
                await database.ExecuteAsync(
                    "UPDATE HxPushMsgModel SET MsgDate = MsgDate * 1000 WHERE MsgDate > 0 AND MsgDate < 100000000000;");

                await database.ExecuteAsync(
                    """
                    CREATE INDEX IF NOT EXISTS IX_HxPushMsgModel_MsgDate_ID
                    ON HxPushMsgModel (MsgDate DESC, ID DESC);
                    """);

                await database.ExecuteAsync(
                    """
                    CREATE INDEX IF NOT EXISTS IX_HxPushMsgModel_Hwid_MsgDate_ID
                    ON HxPushMsgModel (Hwid, MsgDate DESC, ID DESC);
                    """);

                await DeleteOverflowMessagesAsync();

                isInitialized = true;
            }
            finally
            {
                initializeLock.Release();
            }
        }

        /// <summary>
        /// 保存一条推送消息。若 ID 已存在，则覆盖旧记录。
        /// </summary>
        public Task SaveMessageAsync(HxPushMsgModel message)
        {
            return SaveMessagesAsync(new[] { message });
        }

        /// <summary>
        /// 批量保存连接后补发的未读消息，并只在整批完成后清理超量记录。
        /// </summary>
        public async Task SaveMessagesAsync(IReadOnlyCollection<HxPushMsgModel> messages)
        {
            if (messages.Count == 0)
            {
                return;
            }

            await InitializeAsync();

            await saveLock.WaitAsync();
            try
            {
                // 相同 ID 使用覆盖写入，使服务端重试补发保持幂等。
                foreach (var message in messages)
                {
                    await database.InsertOrReplaceAsync(message);
                }

                await DeleteOverflowMessagesAsync();
            }
            finally
            {
                saveLock.Release();
            }
        }

        /// <summary>
        /// 仅保留按时间排序后的最新 10000 条消息。
        /// </summary>
        private Task<int> DeleteOverflowMessagesAsync()
        {
            return database.ExecuteAsync(
                """
                DELETE FROM HxPushMsgModel
                WHERE rowid IN (
                    SELECT rowid
                    FROM HxPushMsgModel
                    ORDER BY MsgDate DESC, ID DESC
                    LIMIT -1 OFFSET ?
                );
                """,
                MaxStoredMessages);
        }

        /// <summary>
        /// 获取最近的消息，默认最多返回 50 条。
        /// </summary>
        public async Task<IReadOnlyList<HxPushMsgModel>> GetRecentMessagesAsync(
            int limit = 50,
            string? hwid = null)
        {
            await InitializeAsync();

            if (!string.IsNullOrWhiteSpace(hwid))
            {
                return await database.QueryAsync<HxPushMsgModel>(
                    """
                    SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                    FROM HxPushMsgModel
                    WHERE Hwid = ?
                    ORDER BY MsgDate DESC, ID DESC
                    LIMIT ?
                    """,
                    hwid,
                    limit);
            }

            return await database.QueryAsync<HxPushMsgModel>(
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                FROM HxPushMsgModel
                ORDER BY MsgDate DESC, ID DESC
                LIMIT ?
                """,
                limit);
        }

        /// <summary>
        /// 获取指定消息之前的更早消息，用于列表向下滚动分页。
        /// 发送时间相同时使用 ID 作为游标，避免重复或遗漏消息。
        /// </summary>
        public async Task<IReadOnlyList<HxPushMsgModel>> GetMessagesBeforeAsync(
            long msgDate,
            string id,
            int limit = 50,
            string? hwid = null)
        {
            await InitializeAsync();

            if (!string.IsNullOrWhiteSpace(hwid))
            {
                return await database.QueryAsync<HxPushMsgModel>(
                    """
                    SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                    FROM HxPushMsgModel
                    WHERE Hwid = ?
                      AND (MsgDate < ? OR (MsgDate = ? AND ID < ?))
                    ORDER BY MsgDate DESC, ID DESC
                    LIMIT ?
                    """,
                    hwid,
                    msgDate,
                    msgDate,
                    id,
                    limit);
            }

            return await database.QueryAsync<HxPushMsgModel>(
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                FROM HxPushMsgModel
                WHERE MsgDate < ? OR (MsgDate = ? AND ID < ?)
                ORDER BY MsgDate DESC, ID DESC
                LIMIT ?
                """,
                msgDate,
                msgDate,
                id,
                limit);
        }

        /// <summary>
        /// 获取本地消息中全部非空设备 ID，供消息列表筛选使用。
        /// </summary>
        public async Task<IReadOnlyList<string>> GetDeviceIdsAsync()
        {
            await InitializeAsync();

            var rows = await database.QueryAsync<DeviceIdRow>(
                """
                SELECT DISTINCT Hwid
                FROM HxPushMsgModel
                WHERE Hwid <> ''
                ORDER BY Hwid COLLATE NOCASE;
                """);

            return rows.Select(row => row.Hwid).ToList();
        }

        /// <summary>
        /// 根据消息 ID 删除本地消息。
        /// </summary>
        public async Task<int> DeleteMessageAsync(string id)
        {
            await InitializeAsync();

            return await database.ExecuteAsync(
                "DELETE FROM HxPushMsgModel WHERE ID = ?",
                id);
        }

        private sealed class DeviceIdRow
        {
            public string Hwid { get; set; } = string.Empty;
        }

        private SQLiteAsyncConnection CreateDatabaseConnection()
        {
            return new SQLiteAsyncConnection(
                databasePath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
        }

        private static void DeleteDatabaseFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
