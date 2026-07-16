using System;
using System.Collections.Generic;

namespace HxPushApp.models.Message
{
    /// <summary>
    /// App 将推送消息成功写入本地后发送的 WebSocket 回执。
    /// 服务端只有收到该回执才会把对应消息标记为已读。
    /// </summary>
    public sealed class HxPushDeliveryAckModel
    {
        public const string DeliveryAcknowledgementType = "deliveryAck";

        public string Type { get; set; } = DeliveryAcknowledgementType;

        public IReadOnlyList<string> MessageIds { get; set; } = Array.Empty<string>();
    }
}
