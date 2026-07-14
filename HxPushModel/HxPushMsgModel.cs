
namespace HxPushApp.models.Message
{
    internal class HxPushMsgModel
    {
        public string ID { get; set; }
        /// <summary>
        /// 应用key 用来区分不同的用户
        /// </summary>
        public string AppKey { get; set; }
        /// <summary>
        /// 设备id
        /// </summary>
        public string Hwid { get; set; }
        /// <summary>
        /// 消息主体
        /// </summary>
        public string Msg { get; set; }
    }
}
