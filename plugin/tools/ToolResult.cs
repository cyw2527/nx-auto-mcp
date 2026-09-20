using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools
{
    /// <summary>
    /// 工具执行结果 — 统一的成功/失败响应格式
    /// </summary>
    public class ToolResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public JObject Data { get; set; }
        public List<string> Warnings { get; set; }

        public ToolResult()
        {
            Warnings = new List<string>();
        }

        public static ToolResult Ok(string message, JObject data = null)
        {
            return new ToolResult { Success = true, Message = message, Data = data ?? new JObject() };
        }

        public static ToolResult Fail(string error)
        {
            return new ToolResult { Success = false, Message = error };
        }

        public JObject ToJson()
        {
            var obj = new JObject();
            obj["success"] = Success;
            obj["message"] = Message ?? "";
            obj["data"] = Data ?? new JObject();
            obj["warnings"] = new JArray(Warnings);
            return obj;
        }
    }
}
