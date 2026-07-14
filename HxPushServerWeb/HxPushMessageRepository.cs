using HxPushApp.models.Message;
using Microsoft.Data.Sqlite;

namespace HxPushServerWeb
{
    // SQLite 访问层：只负责建表和写入消息，避免接口代码直接拼 SQL。
    internal sealed class HxPushMessageRepository
    {
        private readonly string databasePath;

        public HxPushMessageRepository(string databasePath)
        {
            this.databasePath = databasePath;
        }

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
                    Msg TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                """;

            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertAsync(HxPushMsgModel message, CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO HxPushMessages (ID, AppKey, Hwid, Msg, CreatedAtUtc)
                VALUES ($id, $appKey, $hwid, $msg, $createdAtUtc);
                """;

            // 使用参数化 SQL，避免消息内容影响 SQL 语句本身。
            command.Parameters.AddWithValue("$id", message.ID);
            command.Parameters.AddWithValue("$appKey", message.AppKey);
            command.Parameters.AddWithValue("$hwid", message.Hwid);
            command.Parameters.AddWithValue("$msg", message.Msg);
            command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

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
