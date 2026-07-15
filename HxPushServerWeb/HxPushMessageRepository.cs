using HxPushApp.models.Message;
using Microsoft.Data.Sqlite;

namespace HxPushServerWeb
{
    // SQLite 访问层：负责建表、写入和分页读取消息，避免接口代码直接拼 SQL。
    internal sealed class HxPushMessageRepository
    {
        // 数据库文件使用绝对路径，避免工作目录变化影响读写位置。
        private readonly string databasePath;

        // 保存数据库文件位置，连接在每次操作时按需创建。
        public HxPushMessageRepository(string databasePath)
        {
            this.databasePath = databasePath;
        }

        // 初始化消息表、旧库字段和分页索引。
        public async Task InitializeAsync()
        {
            // App_Data 或数据库文件不存在时，OpenAsync 会配合 CREATE TABLE 自动完成初始化。
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

            await using var connection = CreateConnection();
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS HxPushMessages (
                    ID TEXT NOT NULL PRIMARY KEY,
                    AppKey TEXT NOT NULL,
                    Hwid TEXT NOT NULL,
                    MsgDate INTEGER NOT NULL,
                    Msg TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                """;

            await command.ExecuteNonQueryAsync();

            // 兼容旧数据库：早期表结构没有 MsgDate，需要补列并用入库时间回填。
            command.CommandText = "PRAGMA table_info(HxPushMessages);";
            var hasMsgDate = false;

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    if (string.Equals(reader.GetString(1), "MsgDate", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMsgDate = true;
                        break;
                    }
                }
            }

            if (!hasMsgDate)
            {
                command.CommandText = "ALTER TABLE HxPushMessages ADD COLUMN MsgDate INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();

                command.CommandText =
                    "UPDATE HxPushMessages SET MsgDate = CAST(strftime('%s', CreatedAtUtc) AS INTEGER) WHERE MsgDate = 0;";
                await command.ExecuteNonQueryAsync();
            }

            command.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_HxPushMessages_AppKey_MsgDate_ID ON HxPushMessages (AppKey, MsgDate DESC, ID DESC);";
            await command.ExecuteNonQueryAsync();
        }

        // 将一条完整消息写入 SQLite。
        public async Task InsertAsync(HxPushMsgModel message, CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO HxPushMessages (ID, AppKey, Hwid, MsgDate, Msg, CreatedAtUtc)
                VALUES ($id, $appKey, $hwid, $msgDate, $msg, $createdAtUtc);
                """;

            // 使用参数化 SQL，避免消息内容影响 SQL 语句本身。
            command.Parameters.AddWithValue("$id", message.ID);
            command.Parameters.AddWithValue("$appKey", message.AppKey);
            command.Parameters.AddWithValue("$hwid", message.Hwid);
            command.Parameters.AddWithValue("$msgDate", message.MsgDate);
            command.Parameters.AddWithValue("$msg", message.Msg);
            command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 按 AppKey 分页读取消息，页码从 1 开始。
        public async Task<IReadOnlyList<HxPushMsgModel>> GetPageAsync(
            string appKey,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg
                FROM HxPushMessages
                WHERE AppKey = $appKey
                ORDER BY MsgDate DESC, ID DESC
                LIMIT $pageSize OFFSET $offset;
                """;

            // 组合索引与此排序一致，可避免数据量增大后的全表排序。
            command.Parameters.AddWithValue("$appKey", appKey);
            command.Parameters.AddWithValue("$pageSize", pageSize);
            command.Parameters.AddWithValue("$offset", (long)(pageIndex - 1) * pageSize);

            var messages = new List<HxPushMsgModel>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // 将查询结果逐行映射回共享消息模型。
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new HxPushMsgModel
                {
                    ID = reader.GetString(0),
                    AppKey = reader.GetString(1),
                    Hwid = reader.GetString(2),
                    MsgDate = reader.GetInt32(3),
                    Msg = reader.GetString(4)
                });
            }

            return messages;
        }

        // 创建指向当前数据库文件的短连接。
        private SqliteConnection CreateConnection()
        {
            // 每次操作打开短连接；SQLite 场景下简单可靠。
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();

            return new SqliteConnection(connectionString);
        }
    }
}
