
using System;

namespace HxPushApp.models.Message
{
    public class HxPushMsgModel
    {
        public string ID { get; set; } = string.Empty;
        /// <summary>
        /// 应用key 用来区分不同的用户
        /// </summary>
        public string AppKey { get; set; } = string.Empty;
        /// <summary>
        /// 设备id
        /// </summary>
        public string Hwid { get; set; } = string.Empty;

        /// <summary>
        /// 消息的发送时间 时间戳 精确到秒
        /// </summary>
        public int MsgDate { get; set; }

        /// <summary>
        /// 消息主体
        /// </summary>
        public string Msg { get; set; } = string.Empty;
    }
}
