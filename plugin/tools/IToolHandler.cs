using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools
{
    /// <summary>
    /// 工具处理器接口 — 所有 NX 操作工具必须实现此接口
    /// </summary>
    public interface IToolHandler
    {
        /// <summary>工具名称 (与 NX MCP 保持一致)</summary>
        string Name { get; }

        /// <summary>工具描述</summary>
        string Description { get; }

        /// <summary>
        /// 执行工具
        /// </summary>
        /// <param name="session">NXOpen Session (dynamic)</param>
        /// <param name="params">JSON 参数</param>
        /// <returns>JSON 结果</returns>
        JObject Execute(dynamic session, JObject parameters);
    }
}
