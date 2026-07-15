using HxPushApp.models.Message;
using Microsoft.Data.Sqlite;

namespace HxPushServerWeb
{
    // SQLite 访问层：负责建表、写入和分页读取消息，避免接口代码直接拼 SQL。
    internal sealed class HxPushMessageRepository
    {
        private const long UnixMillisecondsThreshold = 100_000_000_000;

        // 数据库文件使用绝对路径，避免工作目录变化影响读写位置。
        private readonly string databasePath;
        private long lastSavedTimestamp;

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
                    IsRead INTEGER NOT NULL DEFAULT 0
                );
                """;

            await command.ExecuteNonQueryAsync();

            // 读取现有列，兼容早期数据库的增量迁移。
            command.CommandText = "PRAGMA table_info(HxPushMessages);";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            if (!columns.Contains("MsgDate"))
            {
                command.CommandText = "ALTER TABLE HxPushMessages ADD COLUMN MsgDate INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();

                if (columns.Contains("CreatedAtUtc"))
                {
                    // 仅迁移旧表时使用重复列补齐 MsgDate，迁移完成后删除该列。
                    command.CommandText =
                        "UPDATE HxPushMessages SET MsgDate = CAST(strftime('%s', CreatedAtUtc) AS INTEGER) * 1000 WHERE MsgDate = 0;";
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 旧版 MsgDate 是秒级时间戳，低于阈值的历史值安全升级为毫秒。
            command.CommandText =
                $"UPDATE HxPushMessages SET MsgDate = MsgDate * 1000 WHERE MsgDate > 0 AND MsgDate < {UnixMillisecondsThreshold};";
            await command.ExecuteNonQueryAsync();

            if (columns.Contains("CreatedAtUtc"))
            {
                // MsgDate 已承载服务端保存时间，删除重复的旧列。
                command.CommandText = "ALTER TABLE HxPushMessages DROP COLUMN CreatedAtUtc;";
                await command.ExecuteNonQueryAsync();
            }

            if (!columns.Contains("IsRead"))
            {
                // 旧消息无法确认是否送达，迁移时按未读处理。
                command.CommandText = "ALTER TABLE HxPushMessages ADD COLUMN IsRead INTEGER NOT NULL DEFAULT 0;";
                await command.ExecuteNonQueryAsync();
            }

            command.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_HxPushMessages_AppKey_MsgDate_ID ON HxPushMessages (AppKey, MsgDate DESC, ID DESC);";
            await command.ExecuteNonQueryAsync();

            command.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_HxPushMessages_AppKey_IsRead_MsgDate_ID ON HxPushMessages (AppKey, IsRead, MsgDate DESC, ID DESC);";
            await command.ExecuteNonQueryAsync();

            command.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_HxPushMessages_AppKey_Hwid_MsgDate_ID ON HxPushMessages (AppKey, Hwid, MsgDate DESC, ID DESC);";
            await command.ExecuteNonQueryAsync();

            command.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_HxPushMessages_AppKey_Hwid_IsRead_MsgDate_ID ON HxPushMessages (AppKey, Hwid, IsRead, MsgDate DESC, ID DESC);";
            await command.ExecuteNonQueryAsync();

            // 从数据库最大值继续递增，服务重启后也不会生成重复保存时间。
            command.CommandText = "SELECT COALESCE(MAX(MsgDate), 0) FROM HxPushMessages;";
            var maxTimestamp = Convert.ToInt64(await command.ExecuteScalarAsync());
            Interlocked.Exchange(ref lastSavedTimestamp, maxTimestamp);
        }

        // 将一条完整消息写入 SQLite。
        public async Task InsertAsync(HxPushMsgModel message, CancellationToken cancellationToken)
        {
            // 保存时间由服务端生成，并保证并发和连续写入时严格递增。
            message.MsgDate = CreateMessageTimestamp();

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO HxPushMessages (ID, AppKey, Hwid, MsgDate, Msg, IsRead)
                VALUES ($id, $appKey, $hwid, $msgDate, $msg, $isRead);
                """;

            // 使用参数化 SQL，避免消息内容影响 SQL 语句本身。
            command.Parameters.AddWithValue("$id", message.ID);
            command.Parameters.AddWithValue("$appKey", message.AppKey);
            command.Parameters.AddWithValue("$hwid", message.Hwid);
            command.Parameters.AddWithValue("$msgDate", message.MsgDate);
            command.Parameters.AddWithValue("$msg", message.Msg);
            command.Parameters.AddWithValue("$isRead", message.IsRead ? 1 : 0);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 按 AppKey 分页读取消息，并把本页原有未读记录标记为已读。
        public Task<IReadOnlyList<HxPushMsgModel>> GetPageAndMarkReadAsync(
            string appKey,
            string? hwid,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return GetAndMarkReadAsync(
                appKey,
                hwid,
                unreadOnly: false,
                pageIndex,
                pageSize,
                cancellationToken);
        }

        // 读取 AppKey 的全部未读消息，并把本次结果标记为已读。
        public Task<IReadOnlyList<HxPushMsgModel>> GetUnreadAndMarkReadAsync(
            string appKey,
            string? hwid,
            CancellationToken cancellationToken)
        {
            return GetAndMarkReadAsync(
                appKey,
                hwid,
                unreadOnly: true,
                pageIndex: null,
                pageSize: null,
                cancellationToken);
        }

        // 单条推送成功后更新已读状态。
        public async Task MarkAsReadAsync(string id, CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE HxPushMessages SET IsRead = 1 WHERE ID = $id AND IsRead = 0;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 在同一事务内完成查询和已读更新，避免并发读取重复消费未读消息。
        private async Task<IReadOnlyList<HxPushMsgModel>> GetAndMarkReadAsync(
            string appKey,
            string? hwid,
            bool unreadOnly,
            int? pageIndex,
            int? pageSize,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = unreadOnly
                ?
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                FROM HxPushMessages
                WHERE AppKey = $appKey
                  AND ($hwid = '' OR Hwid = $hwid)
                  AND IsRead = 0
                ORDER BY MsgDate DESC, ID DESC;
                """
                :
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                FROM HxPushMessages
                WHERE AppKey = $appKey
                  AND ($hwid = '' OR Hwid = $hwid)
                ORDER BY MsgDate DESC, ID DESC
                LIMIT $pageSize OFFSET $offset;
                """;

            // 组合索引与此排序一致，可避免数据量增大后的全表排序。
            command.Parameters.AddWithValue("$appKey", appKey);
            command.Parameters.AddWithValue("$hwid", hwid?.Trim() ?? string.Empty);
            if (!unreadOnly)
            {
                command.Parameters.AddWithValue("$pageSize", pageSize!.Value);
                command.Parameters.AddWithValue("$offset", (long)(pageIndex!.Value - 1) * pageSize.Value);
            }

            var messages = new List<HxPushMsgModel>();

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                // 保留查询时的 IsRead，让调用方知道哪些消息原本未读。
                while (await reader.ReadAsync(cancellationToken))
                {
                    messages.Add(new HxPushMsgModel
                    {
                        ID = reader.GetString(0),
                        AppKey = reader.GetString(1),
                        Hwid = reader.GetString(2),
                        MsgDate = reader.GetInt64(3),
                        Msg = reader.GetString(4),
                        IsRead = reader.GetInt32(5) != 0
                    });
                }
            }

            var unreadIds = messages
                .Where(message => !message.IsRead)
                .Select(message => message.ID)
                .ToArray();
            await MarkAsReadAsync(connection, (SqliteTransaction)transaction, unreadIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return messages;
        }

        // SQLite 参数数量有限，分页批量更新未读 ID。
        private static async Task MarkAsReadAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken)
        {
            foreach (var batch in ids.Chunk(500))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;

                var parameterNames = batch
                    .Select((_, index) => $"$id{index}")
                    .ToArray();
                command.CommandText =
                    $"UPDATE HxPushMessages SET IsRead = 1 WHERE IsRead = 0 AND ID IN ({string.Join(", ", parameterNames)});";

                for (var index = 0; index < batch.Length; index++)
                {
                    command.Parameters.AddWithValue(parameterNames[index], batch[index]);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 当前毫秒与上次值取较大值，必要时递增 1 毫秒。
        public long CreateMessageTimestamp()
        {
            while (true)
            {
                var previous = Volatile.Read(ref lastSavedTimestamp);
                var current = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var next = Math.Max(current, previous + 1);

                if (Interlocked.CompareExchange(ref lastSavedTimestamp, next, previous) == previous)
                {
                    return next;
                }
            }
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
