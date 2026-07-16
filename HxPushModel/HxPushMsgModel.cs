
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
        /// 消息保存或发送时间，Unix 毫秒时间戳。
        /// </summary>
        public long MsgDate { get; set; }

        /// <summary>
        /// 消息主体
        /// </summary>
        public string Msg { get; set; } = string.Empty;

        /// <summary>
        /// 消息是否已被客户端确认持久化，或已被 HTTP 查询接口读取。
        /// </summary>
        public bool IsRead { get; set; }
    }
}
