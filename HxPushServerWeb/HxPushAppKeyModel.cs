namespace HxPushServerWeb
{
    // AppKey 管理接口和持久化文件共用的数据结构。
    internal sealed class HxPushAppKeyModel
    {
        // 应用访问服务时使用的唯一 Key。
        public string AppKey { get; set; } = string.Empty;

        // 管理员维护的用途说明，缺省时使用创建日期。
        public string Remark { get; set; } = string.Empty;
    }
}
