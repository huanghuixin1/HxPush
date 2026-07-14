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
        public string msg { get; set; } = string.Empty;
        public string otherData { get; set; } = string.Empty;
    }
}
