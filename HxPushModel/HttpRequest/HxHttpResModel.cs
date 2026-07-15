using System;
using System.Collections.Generic;
using System.Text;

namespace HxPushModel.HttpRequest
{
    public class HxHttpResModel
    {
        /// <summary>
        /// 如果是0表示成功
        /// </summary>
        public int code { get; set; }
        /// <summary>
        /// 普通响应为提示文字，列表接口可返回业务数据数组。
        /// </summary>
        public object msg { get; set; } = string.Empty;
        public string otherData { get; set; } = string.Empty;
    }
}
