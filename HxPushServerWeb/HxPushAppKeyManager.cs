using System.Security.Cryptography;
using System.Text;

namespace HxPushServerWeb
{
    // AppKey 管理：内存缓存负责高频校验，文本文件负责持久化。
    internal sealed class HxPushAppKeyManager
    {
        private const string DefaultManagerPassword = "123";
        private const int MaxAppKeyLength = 200;

        // 文件路径位于程序运行目录的 App_Data 下。
        private readonly string appKeyFilePath;
        private readonly string passwordFilePath;
        private readonly object syncRoot = new();
        private HashSet<string> appKeys = new(StringComparer.Ordinal);

        // 初始化管理文件并一次性加载 AppKey 缓存。
        public HxPushAppKeyManager(string appKeyFilePath, string passwordFilePath)
        {
            this.appKeyFilePath = appKeyFilePath;
            this.passwordFilePath = passwordFilePath;

            EnsureFilesExist();
            appKeys = LoadAppKeys();
        }

        // 仅查询内存缓存，避免每次鉴权都读取文件。
        public bool Exists(string appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey))
            {
                return false;
            }

            lock (syncRoot)
            {
                return appKeys.Contains(appKey.Trim());
            }
        }

        // 返回排序后的缓存副本，避免调用方修改内部集合。
        public IReadOnlyList<string> GetAll()
        {
            lock (syncRoot)
            {
                return appKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
        }

        // 覆盖持久化 AppKey，并在写入成功后原子替换缓存引用。
        public void ReplaceAll(IEnumerable<string> values)
        {
            var normalizedValues = NormalizeAppKeys(values);
            var fileLines = new[] { "# 一行一个 AppKey，空行和 # 开头的行会被忽略。" }
                .Concat(normalizedValues)
                .ToArray();

            lock (syncRoot)
            {
                File.WriteAllLines(appKeyFilePath, fileLines, Encoding.UTF8);
                appKeys = new HashSet<string>(normalizedValues, StringComparer.Ordinal);
            }
        }

        // 管理密码每次从文件读取，修改文件后无需重启服务。
        public bool ValidateManagerPassword(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return false;
            }

            EnsurePasswordFileExists();
            var expectedPassword = File.ReadAllText(passwordFilePath, Encoding.UTF8).Trim();

            // 比较哈希可避免按字符短路造成明显的时序差异。
            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedPassword));
            var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }

        // 从文本文件读取并规范化缓存初始值。
        private HashSet<string> LoadAppKeys()
        {
            return File.ReadLines(appKeyFilePath, Encoding.UTF8)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToHashSet(StringComparer.Ordinal);
        }

        // 去除空行和重复项，同时拒绝会破坏文本文件格式的值。
        private static string[] NormalizeAppKeys(IEnumerable<string> values)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (var value in values)
            {
                var appKey = value?.Trim();
                if (string.IsNullOrWhiteSpace(appKey))
                {
                    continue;
                }

                if (appKey.Length > MaxAppKeyLength ||
                    appKey.StartsWith('#') ||
                    appKey.Contains('\r') ||
                    appKey.Contains('\n'))
                {
                    throw new ArgumentException($"AppKey 格式无效：{appKey}");
                }

                result.Add(appKey);
            }

            return result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        // 缺少目录或文件时创建默认 AppKey 和密码配置。
        private void EnsureFilesExist()
        {
            var directory = Path.GetDirectoryName(appKeyFilePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                // CreateDirectory 可重复调用，目录已存在时不会报错。
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(appKeyFilePath))
            {
                // 提供示例 AppKey，方便首次启动后直接测试。
                File.WriteAllText(
                    appKeyFilePath,
                    "# 一行一个 AppKey，空行和 # 开头的行会被忽略。" + Environment.NewLine +
                    "app-demo" + Environment.NewLine);
            }

            EnsurePasswordFileExists();
        }

        // 密码文件缺失时写入默认密码 123。
        private void EnsurePasswordFileExists()
        {
            var directory = Path.GetDirectoryName(passwordFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(passwordFilePath))
            {
                File.WriteAllText(passwordFilePath, DefaultManagerPassword, Encoding.UTF8);
            }
        }
    }
}
