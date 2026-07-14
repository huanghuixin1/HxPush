namespace HxPushServerWeb
{
    // AppKey 白名单管理：暂时用 txt 手动维护，一行一个 AppKey。
    internal sealed class HxPushAppKeyManager
    {
        private readonly string appKeyFilePath;

        public HxPushAppKeyManager(string appKeyFilePath)
        {
            this.appKeyFilePath = appKeyFilePath;
            EnsureFileExists();
        }

        public bool Exists(string appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey))
            {
                return false;
            }

            EnsureFileExists();

            // 每次读取文件，方便手动修改 appkeys.txt 后立即生效。
            return File.ReadLines(appKeyFilePath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Any(line => string.Equals(line, appKey.Trim(), StringComparison.Ordinal));
        }

        private void EnsureFileExists()
        {
            var directory = Path.GetDirectoryName(appKeyFilePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(appKeyFilePath))
            {
                File.WriteAllText(
                    appKeyFilePath,
                    "# 一行一个 AppKey，空行和 # 开头的行会被忽略。" + Environment.NewLine +
                    "app-demo" + Environment.NewLine);
            }
        }
    }
}
