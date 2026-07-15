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
        private static readonly Lazy<SqliteHelper> LazyInstance = new(() => new SqliteHelper());

        private readonly SQLiteAsyncConnection database;
        private readonly SemaphoreSlim initializeLock = new(1, 1);
        private bool isInitialized;

        private SqliteHelper()
        {
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFileName);
            database = new SQLiteAsyncConnection(
                databasePath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
        }

        /// <summary>
        /// 全局单例，方便页面或其它服务复用同一个数据库连接。
        /// </summary>
        public static SqliteHelper Instance => LazyInstance.Value;

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
                        Msg TEXT NOT NULL
                    );
                    """);

                await database.ExecuteAsync(
                    """
                    CREATE INDEX IF NOT EXISTS IX_HxPushMsgModel_MsgDate_ID
                    ON HxPushMsgModel (MsgDate DESC, ID DESC);
                    """);

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
        public async Task SaveMessageAsync(HxPushMsgModel message)
        {
            await InitializeAsync();
            await database.InsertOrReplaceAsync(message);
        }

        /// <summary>
        /// 获取最近的消息，默认最多返回 50 条。
        /// </summary>
        public async Task<IReadOnlyList<HxPushMsgModel>> GetRecentMessagesAsync(int limit = 50)
        {
            await InitializeAsync();

            return await database.QueryAsync<HxPushMsgModel>(
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg
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
            int msgDate,
            string id,
            int limit = 50)
        {
            await InitializeAsync();

            return await database.QueryAsync<HxPushMsgModel>(
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg
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
        /// 根据消息 ID 删除本地消息。
        /// </summary>
        public async Task<int> DeleteMessageAsync(string id)
        {
            await InitializeAsync();

            return await database.ExecuteAsync(
                "DELETE FROM HxPushMsgModel WHERE ID = ?",
                id);
        }
    }
}
