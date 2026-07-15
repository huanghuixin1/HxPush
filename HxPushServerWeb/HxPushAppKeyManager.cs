using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HxPushServerWeb
{
    // AppKey 管理：内存缓存负责高频校验，文本文件负责持久化。
    internal sealed class HxPushAppKeyManager
    {
        private const string DefaultManagerPassword = "123";
        private const int MaxAppKeyLength = 200;
        private const int MaxRemarkLength = 500;

        // JSON Lines 保留备注，同时继续支持旧版纯 AppKey 文本行。
        private static readonly JsonSerializerOptions FileJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        // 文件路径位于程序运行目录的 App_Data 下。
        private readonly string appKeyFilePath;
        private readonly string passwordFilePath;
        private readonly object syncRoot = new();
        private Dictionary<string, HxPushAppKeyModel> appKeys = new(StringComparer.Ordinal);

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
                return appKeys.ContainsKey(appKey.Trim());
            }
        }

        // 返回排序后的缓存副本，避免调用方修改内部集合。
        public IReadOnlyList<HxPushAppKeyModel> GetAll()
        {
            lock (syncRoot)
            {
                return appKeys.Values
                    .OrderBy(value => value.AppKey, StringComparer.Ordinal)
                    .Select(Clone)
                    .ToArray();
            }
        }

        // 覆盖持久化 AppKey，并在写入成功后原子替换缓存引用。
        public void ReplaceAll(IEnumerable<HxPushAppKeyModel> values)
        {
            var normalizedValues = NormalizeAppKeys(values);
            var fileLines = new[] { "# 每行一个 JSON AppKey 记录；旧版纯文本行仍兼容读取。" }
                .Concat(normalizedValues.Select(value => JsonSerializer.Serialize(value, FileJsonOptions)))
                .ToArray();

            lock (syncRoot)
            {
                File.WriteAllLines(appKeyFilePath, fileLines, Encoding.UTF8);
                appKeys = normalizedValues.ToDictionary(
                    value => value.AppKey,
                    Clone,
                    StringComparer.Ordinal);
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
        private Dictionary<string, HxPushAppKeyModel> LoadAppKeys()
        {
            var legacyRemark = GetFileCreationDate();
            var values = new List<HxPushAppKeyModel>();

            foreach (var rawLine in File.ReadLines(appKeyFilePath, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                HxPushAppKeyModel? value = null;
                if (line.StartsWith('{'))
                {
                    try
                    {
                        value = JsonSerializer.Deserialize<HxPushAppKeyModel>(line, FileJsonOptions);
                    }
                    catch (JsonException)
                    {
                        // 单行损坏不阻止服务启动，其余合法 AppKey 仍可继续使用。
                    }
                }
                else
                {
                    // 旧版纯文本 Key 使用文件创建日期作为初始备注。
                    value = new HxPushAppKeyModel { AppKey = line, Remark = legacyRemark };
                }

                if (value is not null)
                {
                    values.Add(value);
                }
            }

            return NormalizeAppKeys(values).ToDictionary(
                value => value.AppKey,
                Clone,
                StringComparer.Ordinal);
        }

        // 去除空值和重复项，同时为缺少备注的新记录补创建日期。
        private static HxPushAppKeyModel[] NormalizeAppKeys(IEnumerable<HxPushAppKeyModel> values)
        {
            var result = new Dictionary<string, HxPushAppKeyModel>(StringComparer.Ordinal);

            foreach (var value in values)
            {
                var appKey = value?.AppKey?.Trim();
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

                var remark = value?.Remark?.Trim();
                if (string.IsNullOrWhiteSpace(remark))
                {
                    remark = GetCurrentDate();
                }

                if (remark.Length > MaxRemarkLength || remark.Contains('\r') || remark.Contains('\n'))
                {
                    throw new ArgumentException($"AppKey 备注格式无效：{appKey}");
                }

                // 重复 Key 以后出现的记录为准，便于编辑后覆盖备注。
                result[appKey] = new HxPushAppKeyModel { AppKey = appKey, Remark = remark };
            }

            return result.Values
                .OrderBy(value => value.AppKey, StringComparer.Ordinal)
                .ToArray();
        }

        // 复制模型，避免管理接口修改缓存中的实例。
        private static HxPushAppKeyModel Clone(HxPushAppKeyModel value)
        {
            return new HxPushAppKeyModel { AppKey = value.AppKey, Remark = value.Remark };
        }

        private static string GetCurrentDate()
        {
            return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // 旧文件没有真实备注时，优先使用文件创建日期。
        private string GetFileCreationDate()
        {
            var creationTime = File.GetCreationTime(appKeyFilePath);
            return creationTime.Year > 1970
                ? creationTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : GetCurrentDate();
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
                var defaultValue = new HxPushAppKeyModel
                {
                    AppKey = "app-demo",
                    Remark = GetCurrentDate()
                };
                File.WriteAllLines(
                    appKeyFilePath,
                    [
                        "# 每行一个 JSON AppKey 记录；旧版纯文本行仍兼容读取。",
                        JsonSerializer.Serialize(defaultValue, FileJsonOptions)
                    ],
                    Encoding.UTF8);
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
