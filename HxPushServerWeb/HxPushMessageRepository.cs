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
            long? beforeMsgDate,
            string? beforeId,
            CancellationToken cancellationToken)
        {
            return GetAndMarkReadAsync(
                appKey,
                hwid,
                unreadOnly: false,
                pageIndex,
                pageSize,
                beforeMsgDate,
                beforeId,
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
                beforeMsgDate: null,
                beforeId: null,
                cancellationToken);
        }

        // 只读取 AppKey 的全部未读消息；用于 WebSocket 确认发送成功后再更新状态。
        public async Task<IReadOnlyList<HxPushMsgModel>> GetUnreadAsync(
            string appKey,
            string? hwid,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                FROM HxPushMessages
                WHERE AppKey = $appKey
                  AND ($hwid = '' OR Hwid = $hwid)
                  AND IsRead = 0
                ORDER BY MsgDate DESC, ID DESC;
                """;
            command.Parameters.AddWithValue("$appKey", appKey);
            command.Parameters.AddWithValue("$hwid", hwid?.Trim() ?? string.Empty);

            return await ReadMessagesAsync(command, cancellationToken);
        }

        // 管理端分页查询：不修改 IsRead；支持筛选；按 MsgDate 排序（默认倒序）。
        public async Task<(IReadOnlyList<HxPushMsgModel> Messages, long Total)> QueryAdminPageAsync(
            string? appKey,
            string? hwid,
            bool? isRead,
            string? keyword,
            bool sortDescending,
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var whereSql = BuildAdminWhereClause(appKey, hwid, isRead, keyword, out var parameters);
            // 仅允许 ASC/DESC，避免拼接用户输入。
            var orderDirection = sortDescending ? "DESC" : "ASC";

            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(1) FROM HxPushMessages WHERE {whereSql};";
                AddParameters(countCommand, parameters);
                var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

                await using var listCommand = connection.CreateCommand();
                listCommand.CommandText =
                    $"""
                    SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                    FROM HxPushMessages
                    WHERE {whereSql}
                    ORDER BY MsgDate {orderDirection}, ID {orderDirection}
                    LIMIT $pageSize OFFSET $offset;
                    """;
                AddParameters(listCommand, parameters);
                listCommand.Parameters.AddWithValue("$pageSize", pageSize);
                listCommand.Parameters.AddWithValue("$offset", (long)(pageIndex - 1) * pageSize);

                var messages = await ReadMessagesAsync(listCommand, cancellationToken);
                return (messages, total);
            }
        }

        // 管理端按 ID 批量删除；空集合直接返回 0。
        public async Task<int> DeleteByIdsAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken)
        {
            var validIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (validIds.Length == 0)
            {
                return 0;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var deleted = 0;
            foreach (var batch in validIds.Chunk(500))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;

                var parameterNames = batch
                    .Select((_, index) => $"$id{index}")
                    .ToArray();
                command.CommandText =
                    $"DELETE FROM HxPushMessages WHERE ID IN ({string.Join(", ", parameterNames)});";

                for (var index = 0; index < batch.Length; index++)
                {
                    command.Parameters.AddWithValue(parameterNames[index], batch[index]);
                }

                deleted += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }

        // 管理端按当前筛选条件删除，避免只能逐条删。
        public async Task<int> DeleteByAdminFilterAsync(
            string? appKey,
            string? hwid,
            bool? isRead,
            string? keyword,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var whereSql = BuildAdminWhereClause(appKey, hwid, isRead, keyword, out var parameters);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM HxPushMessages WHERE {whereSql};";
            AddParameters(command, parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static string BuildAdminWhereClause(
            string? appKey,
            string? hwid,
            bool? isRead,
            string? keyword,
            out List<KeyValuePair<string, object>> parameters)
        {
            parameters = [];
            var clauses = new List<string> { "1 = 1" };

            var normalizedAppKey = appKey?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedAppKey))
            {
                clauses.Add("AppKey = $appKey");
                parameters.Add(new KeyValuePair<string, object>("$appKey", normalizedAppKey));
            }

            var normalizedHwid = hwid?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedHwid))
            {
                clauses.Add("Hwid = $hwid");
                parameters.Add(new KeyValuePair<string, object>("$hwid", normalizedHwid));
            }

            if (isRead.HasValue)
            {
                clauses.Add("IsRead = $isRead");
                parameters.Add(new KeyValuePair<string, object>("$isRead", isRead.Value ? 1 : 0));
            }

            var normalizedKeyword = keyword?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedKeyword))
            {
                clauses.Add("Msg LIKE $keyword");
                parameters.Add(new KeyValuePair<string, object>("$keyword", $"%{normalizedKeyword}%"));
            }

            return string.Join(" AND ", clauses);
        }

        private static void AddParameters(
            SqliteCommand command,
            IReadOnlyList<KeyValuePair<string, object>> parameters)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Key, parameter.Value);
            }
        }

        // 客户端 ACK 后按 AppKey 范围批量更新已读状态，不能因跨 AppKey 的 ID 猜测而修改其它消息。
        public async Task<int> MarkAsReadAsync(
            string appKey,
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken)
        {
            var normalizedAppKey = appKey?.Trim();
            var validIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (string.IsNullOrWhiteSpace(normalizedAppKey) || validIds.Length == 0)
            {
                return 0;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var updated = 0;

            foreach (var batch in validIds.Chunk(500))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;

                var parameterNames = batch
                    .Select((_, index) => $"$id{index}")
                    .ToArray();
                command.CommandText =
                    $"UPDATE HxPushMessages SET IsRead = 1 WHERE AppKey = $appKey AND IsRead = 0 AND ID IN ({string.Join(", ", parameterNames)});";
                command.Parameters.AddWithValue("$appKey", normalizedAppKey);

                for (var index = 0; index < batch.Length; index++)
                {
                    command.Parameters.AddWithValue(parameterNames[index], batch[index]);
                }

                updated += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return updated;
        }

        // 在同一事务内完成查询和已读更新，避免并发读取重复消费未读消息。
        private async Task<IReadOnlyList<HxPushMsgModel>> GetAndMarkReadAsync(
            string appKey,
            string? hwid,
            bool unreadOnly,
            int? pageIndex,
            int? pageSize,
            long? beforeMsgDate,
            string? beforeId,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            var useCursor = !unreadOnly &&
                            beforeMsgDate.HasValue &&
                            !string.IsNullOrWhiteSpace(beforeId);

            if (unreadOnly)
            {
                command.CommandText =
                    """
                    SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                    FROM HxPushMessages
                    WHERE AppKey = $appKey
                      AND ($hwid = '' OR Hwid = $hwid)
                      AND IsRead = 0
                    ORDER BY MsgDate DESC, ID DESC;
                    """;
            }
            else if (useCursor)
            {
                // 游标分页用于 App 滚动到底后继续拉取更旧消息，避免页码与本地缓存错位。
                command.CommandText =
                    """
                    SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                    FROM HxPushMessages
                    WHERE AppKey = $appKey
                      AND ($hwid = '' OR Hwid = $hwid)
                      AND (MsgDate < $beforeMsgDate OR (MsgDate = $beforeMsgDate AND ID < $beforeId))
                    ORDER BY MsgDate DESC, ID DESC
                    LIMIT $pageSize;
                    """;
            }
            else
            {
                command.CommandText =
                    """
                    SELECT ID, AppKey, Hwid, MsgDate, Msg, IsRead
                    FROM HxPushMessages
                    WHERE AppKey = $appKey
                      AND ($hwid = '' OR Hwid = $hwid)
                    ORDER BY MsgDate DESC, ID DESC
                    LIMIT $pageSize OFFSET $offset;
                    """;
            }

            // 组合索引与此排序一致，可避免数据量增大后的全表排序。
            command.Parameters.AddWithValue("$appKey", appKey);
            command.Parameters.AddWithValue("$hwid", hwid?.Trim() ?? string.Empty);
            if (!unreadOnly)
            {
                command.Parameters.AddWithValue("$pageSize", pageSize!.Value);

                if (useCursor)
                {
                    command.Parameters.AddWithValue("$beforeMsgDate", beforeMsgDate!.Value);
                    command.Parameters.AddWithValue("$beforeId", beforeId!.Trim());
                }
                else
                {
                    command.Parameters.AddWithValue("$offset", (long)(pageIndex!.Value - 1) * pageSize.Value);
                }
            }

            // 保留查询时的 IsRead，让调用方知道哪些消息原本未读。
            var messages = await ReadMessagesAsync(command, cancellationToken);

            var unreadIds = messages
                .Where(message => !message.IsRead)
                .Select(message => message.ID)
                .ToArray();
            await MarkAsReadAsync(connection, (SqliteTransaction)transaction, unreadIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return messages;
        }

        // 将统一的查询结果映射为共享消息模型。
        private static async Task<List<HxPushMsgModel>> ReadMessagesAsync(
            SqliteCommand command,
            CancellationToken cancellationToken)
        {
            var messages = new List<HxPushMsgModel>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
