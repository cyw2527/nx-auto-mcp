using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools
{
    /// <summary>
    /// 工具辅助方法 — 所有工具共用的参数提取和对象查找方法
    /// </summary>
    public static class ToolHelpers
    {
        // ================================================================
        // 参数提取
        // ================================================================

        /// <summary>从 JObject 获取字符串参数</summary>
        public static string GetString(JObject p, string key, string defaultValue = "")
        {
            if (p == null) return defaultValue;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            return token.Value<string>() ?? defaultValue;
        }

        /// <summary>从 JObject 获取 double 参数</summary>
        public static double GetDouble(JObject p, string key, double defaultValue = 0.0)
        {
            if (p == null) return defaultValue;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<double>();
            double parsed;
            if (double.TryParse(token.Value<string>(), out parsed))
                return parsed;
            return defaultValue;
        }

        /// <summary>从 JObject 获取 int 参数</summary>
        public static int GetInt(JObject p, string key, int defaultValue = 0)
        {
            if (p == null) return defaultValue;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Integer)
                return token.Value<int>();
            int parsed;
            if (int.TryParse(token.Value<string>(), out parsed))
                return parsed;
            return defaultValue;
        }

        /// <summary>从 JObject 获取 bool 参数</summary>
        public static bool GetBool(JObject p, string key, bool defaultValue = false)
        {
            if (p == null) return defaultValue;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            var s = token.Value<string>();
            if (s != null)
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return defaultValue;
        }

        /// <summary>从 JObject 获取字符串数组参数</summary>
        public static List<string> GetStringArray(JObject p, string key)
        {
            var result = new List<string>();
            if (p == null) return result;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return result;
            JArray arr = token as JArray;
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    if (item.Type != JTokenType.Null)
                        result.Add(item.Value<string>() ?? "");
                }
            }
            return result;
        }

        /// <summary>从 JObject 获取 double 数组参数</summary>
        public static double[] GetDoubleArray(JObject p, string key, double[] defaultValue = null)
        {
            if (p == null) return defaultValue ?? new double[0];
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue ?? new double[0];
            JArray arr = token as JArray;
            if (arr != null)
            {
                var result = new double[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i].Type == JTokenType.Float || arr[i].Type == JTokenType.Integer)
                        result[i] = arr[i].Value<double>();
                }
                return result;
            }
            return defaultValue ?? new double[0];
        }

        /// <summary>从 JObject 获取 int 数组参数</summary>
        public static List<int> GetIntArray(JObject p, string key)
        {
            var result = new List<int>();
            if (p == null) return result;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return result;
            JArray arr = token as JArray;
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    if (item.Type != JTokenType.Null)
                        result.Add(item.Value<int>());
                }
            }
            return result;
        }

        /// <summary>从 JObject 获取嵌套对象参数</summary>
        public static JObject GetObject(JObject p, string key)
        {
            if (p == null) return null;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token as JObject;
        }

        // ================================================================
        // 方向向量
        // ================================================================

        /// <summary>
        /// 将方向字符串转换为向量数组 [x, y, z]
        /// 支持: X, Y, Z, -X, -Y, -Z
        /// </summary>
        public static double[] DirectionVector(string direction)
        {
            if (string.IsNullOrEmpty(direction)) return new double[] { 0, 0, 1 };
            var key = direction.Trim().ToUpper();
            switch (key)
            {
                case "X": return new double[] { 1, 0, 0 };
                case "Y": return new double[] { 0, 1, 0 };
                case "Z": return new double[] { 0, 0, 1 };
                case "-X": return new double[] { -1, 0, 0 };
                case "-Y": return new double[] { 0, -1, 0 };
                case "-Z": return new double[] { 0, 0, -1 };
                default: return new double[] { 0, 0, 1 };
            }
        }

        // ================================================================
        // NX 对象查找 (基于 tag) — 已实测验证的写法
        // ================================================================

        public static dynamic FindBodyByTag(dynamic workPart, int tag)
        {
            if (tag <= 0 || workPart == null) return null;
            try
            {
                foreach (dynamic body in workPart.Bodies)
                {
                    try
                    {
                        if (body != null && (int)Convert.ToDouble(body.Tag) == tag)
                            return body;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public static dynamic FindFaceByTag(dynamic workPart, int tag)
        {
            if (tag <= 0 || workPart == null) return null;
            try
            {
                foreach (dynamic body in workPart.Bodies)
                {
                    if (body == null) continue;
                    dynamic faces = body.GetFaces();
                    if (faces == null) continue;
                    foreach (dynamic face in faces)
                    {
                        // NXOpen.Tag is a struct — Convert.ToDouble works, (int)cast fails on dynamic
                        try
                        {
                            if (face != null && (int)Convert.ToDouble(face.Tag) == tag)
                                return face;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Add a Face to a Builder collector.
        /// Runtime probe + 实测 (2026-07-31) revealed the REAL API:
        ///   ScCollector has NO Add(face) — it uses SelectionIntentRule:
        ///     wp.ScRuleFactory.CreateRuleFaceDumb(Face[]) → collector.AddRules(rule[])
        ///   FaceRecognitionBuilder props (FaceToResize/FaceToOffset/FaceToMove)
        ///     resolve to their inner FaceCollector (also rule-based).
        /// </summary>
        public static bool AddFaceToCollector(dynamic workPart, dynamic builder, string collectorName, dynamic face)
        {
            if (workPart == null || builder == null || face == null) return false;
            try
            {
                // Resolve collector (handle FaceRecognitionBuilder.FaceCollector nesting)
                object collector = GetCollectorInstance(builder, collectorName);
                if (collector == null) return false;

                // Build FaceDumbRule via ScRuleFactory
                var faceArr = Array.CreateInstance(face.GetType(), 1);
                faceArr.SetValue(face, 0);
                var ruleFactory = workPart.ScRuleFactory;
                var rule = ruleFactory.CreateRuleFaceDumb(faceArr);

                // AddRules(SelectionIntentRule[])
                var ruleArr = Array.CreateInstance(rule.GetType(), 1);
                ruleArr.SetValue(rule, 0);
                var addRules = collector.GetType().GetMethod("AddRules");
                if (addRules == null) return false;
                addRules.Invoke(collector, new object[] { ruleArr });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Add object to a SelectObjectList/SelectFaceList property (e.g. FacesToExtract, Body, FaceToProjectTo).
        /// These lists use plain Add(obj) — different from ScCollector (rule-based).
        /// </summary>
        public static bool AddToObjectList(dynamic builder, string propName, dynamic obj)
        {
            if (builder == null || obj == null) return false;
            try
            {
                var prop = builder.GetType().GetProperty(propName);
                if (prop == null) return false;
                var list = prop.GetValue(builder, null);
                if (list == null) return false;
                // L1: Direct Add(obj)
                var add = list.GetType().GetMethod("Add");
                if (add != null)
                {
                    try { add.Invoke(list, new object[] { obj }); return true; } catch { }
                }
                // L2: Rule-based AddRules fallback (ScCollector, SelectFaceList)
                try
                {
                    var addRules = list.GetType().GetMethod("AddRules");
                    if (addRules != null)
                    {
                        var arr = Array.CreateInstance(obj.GetType(), 1);
                        arr.SetValue(obj, 0);
                        try { addRules.Invoke(list, new object[] { arr }); return true; } catch { }
                    }
                }
                catch { }
                // L3: Try direct property assignment (single-object properties)
                try { prop.SetValue(builder, obj, null); return true; } catch { }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Resolve collector property, unwrapping FaceRecognitionBuilder.FaceCollector nesting.</summary>
        private static object GetCollectorInstance(dynamic builder, string collectorName)
        {
            var cur = (object)builder;
            foreach (var seg in collectorName.Split('.'))
            {
                if (cur == null) return null;
                var prop = cur.GetType().GetProperty(seg);
                if (prop == null) return null;
                cur = prop.GetValue(cur, null);
            }
            return cur;
        }

        /// <summary>
        /// Set an Expression property value (Distance/Diameter/Radius are Expression, not double).
        /// Runtime probe revealed: b.Diameter is NXOpen.Expression → b.Diameter.Value = x
        /// </summary>
        public static bool SetExpressionValue(dynamic builder, string propName, double value)
        {
            if (builder == null) return false;
            try
            {
                var prop = builder.GetType().GetProperty(propName);
                if (prop == null) return false;
                var expr = prop.GetValue(builder, null);
                if (expr == null) return false;
                var valueProp = expr.GetType().GetProperty("Value");
                if (valueProp == null || !valueProp.CanWrite) return false;
                valueProp.SetValue(expr, value, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 通过 tag 在 workPart 中查找 Edge 对象
        /// </summary>
        public static dynamic FindEdgeByTag(dynamic workPart, int tag)
        {
            if (tag <= 0 || workPart == null) return null;
            try
            {
                foreach (dynamic body in workPart.Bodies)
                {
                    if (body == null) continue;
                    dynamic edges = body.GetEdges();
                    if (edges == null) continue;
                    foreach (dynamic edge in edges)
                    {
                        try { if (edge != null && (int)Convert.ToDouble(edge.Tag) == tag) return edge; } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通过名称在 workPart 中查找 Feature 对象
        /// </summary>
        public static dynamic FindFeatureByName(dynamic workPart, string name)
        {
            if (string.IsNullOrEmpty(name) || workPart == null) return null;
            try
            {
                foreach (dynamic feat in workPart.Features)
                {
                    if (feat != null && feat.Name == name)
                        return feat;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通过 journal_id 在 workPart 中查找 Feature 对象
        /// journal_id 格式: "FeatureType(Timestamp)" e.g. "Pattern Feature(6)"
        /// Source: 实测 → Feature.Timestamp:int, Feature.FeatureType:property
        /// </summary>
        public static dynamic FindFeatureByJournalId(dynamic workPart, string journalId)
        {
            if (string.IsNullOrEmpty(journalId) || workPart == null) return null;
            try
            {
                foreach (dynamic feat in workPart.Features)
                {
                    if (feat == null) continue;
                    try
                    {
                        string jid = feat.FeatureType.ToString() + "(" + feat.Timestamp + ")";
                        if (jid == journalId)
                            return feat;
                    }
                    catch { }
                    // Fallback: try JournalIdentifier property
                    try
                    {
                        if (feat.JournalIdentifier == journalId)
                            return feat;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 综合查找 Feature: 先按 name，再按 journal_id，最后按 FeatureType 名称
        /// Source: H-006 修复 — Pattern Feature 等名字为空的特征无法通过 name 查找
        /// </summary>
        public static dynamic FindFeature(dynamic workPart, string nameOrJournalId)
        {
            if (string.IsNullOrEmpty(nameOrJournalId) || workPart == null) return null;
            // L1: exact name match
            var f = FindFeatureByName(workPart, nameOrJournalId);
            if (f != null) return f;
            // L2: journal_id match
            f = FindFeatureByJournalId(workPart, nameOrJournalId);
            if (f != null) return f;
            // L3: partial FeatureType match (for e.g. "Pattern Feature")
            try
            {
                foreach (dynamic feat in workPart.Features)
                {
                    if (feat == null) continue;
                    try
                    {
                        if (feat.FeatureType.ToString().IndexOf(nameOrJournalId, StringComparison.OrdinalIgnoreCase) >= 0)
                            return feat;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通过名称在 workPart 中查找 Body 对象
        /// </summary>
        public static dynamic FindBodyByName(dynamic workPart, string name)
        {
            if (string.IsNullOrEmpty(name) || workPart == null) return null;
            try
            {
                foreach (dynamic body in workPart.Bodies)
                {
                    if (body != null && body.Name == name)
                        return body;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 统一目标体解析（P2 2026-09-02 新增）— 替代全库盲选 `foreach(Bodies){ target=body; break; }`。
        /// 解析优先级:
        ///   L1: target 是纯数字 → 按 body tag 匹配
        ///   L2: target 非空 → 按 body name / feature journal_id（取其输出体）匹配
        ///   L3: target 空 → 仅当零件恰有 1 个 body 时返回它；多 body 报错要求显式 target
        /// </summary>
        public static dynamic GetTargetBody(dynamic workPart, string target, out string error)
        {
            error = null;
            if (workPart == null) { error = "No active work part."; return null; }
            long tag;
            if (!string.IsNullOrEmpty(target) && long.TryParse(target, out tag) && tag > 0)
            {
                dynamic b = FindBodyByTag(workPart, (int)tag);
                if (b != null) return b;
                error = string.Format("未找到 tag={0} 的 body（可用 nx_inspect_topology 查 body tag）。", tag);
                return null;
            }
            if (!string.IsNullOrEmpty(target))
            {
                dynamic b = FindBodyByName(workPart, target);
                if (b != null) return b;
                dynamic f = FindFeature(workPart, target);
                if (f != null)
                {
                    try
                    {
                        dynamic bodies = f.GetBodies();
                        if (bodies != null)
                        {
                            foreach (dynamic bd in bodies)
                                if (bd != null) return bd;
                        }
                    }
                    catch { }
                    error = string.Format("特征 '{0}' 无输出体。", target);
                    return null;
                }
                error = string.Format("未找到 body/feature '{0}'（支持 body 名 / journal_id / tag）。", target);
                return null;
            }
            int count = 0; dynamic single = null;
            try
            {
                foreach (dynamic body in workPart.Bodies)
                {
                    if (body == null) continue;
                    count++;
                    single = body;
                    if (count > 1) break;
                }
            }
            catch { }
            if (count == 1) return single;
            if (count == 0) { error = "零件中没有 body。"; return null; }
            error = string.Format("零件中有 {0} 个 body，目标不明确——请用显式 target 参数指定（body 名 / journal_id / tag）。", count);
            return null;
        }

        /// <summary>
        /// 逆向 Limit 防御（P2 2026-09-02）：EndExtend &lt; StartExtend 时 NX 静默反转区间导致几何错。
        /// start_distance/distance 均为【绝对限位】而非长度（见 nx_extrude friction 坑）。
        /// </summary>
        public static JObject CheckReversedLimits(double startDistance, double distance)
        {
            if (distance < startDistance)
                return Fail(string.Format(
                    "逆向 Limit：start_distance={0} 大于 distance={1}。两者均为绝对限位(StartExtend/EndExtend)非长度，" +
                    "NX 会静默反转区间产生错误几何。挖 [start,end] 的槽请填 start_distance=start, distance=end（如 2mm 槽 = start_distance=8, distance=10）。",
                    startDistance, distance));
            return null;
        }

        /// <summary>
        /// 通过名称在 workPart 中查找 Curve 对象
        /// </summary>
        public static dynamic FindCurveByName(dynamic workPart, string name)
        {
            if (string.IsNullOrEmpty(name) || workPart == null) return null;
            try
            {
                foreach (dynamic curve in workPart.Curves)
                {
                    if (curve != null && curve.Name == name)
                        return curve;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通过名称在 workPart 中查找 Sketch 对象
        /// </summary>
        public static dynamic FindSketchByName(dynamic workPart, string name)
        {
            if (string.IsNullOrEmpty(name) || workPart == null) return null;
            try
            {
                dynamic sketches = workPart.Sketches;
                if (sketches == null) return null;
                // ToArray() approach (works for NXOpen collections with dynamic)
                try
                {
                    var arr = sketches.ToArray();
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            dynamic sk = arr[i];
                            try
                            {
                                string sname = sk.Name.ToString();
                                if (sname == name || sname.EndsWith(name))
                                    return sk;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Get the first sketch (fallback when name lookup fails).
        /// NXOpen sketch.Name may differ from journal_id shown in feature tree.
        /// dynamic foreach on Sketches collection fails — use ToArray() or index access.
        /// </summary>
        public static dynamic GetFirstSketch(dynamic workPart)
        {
            if (workPart == null) return null;
            try
            {
                var sketches = workPart.Sketches;
                if (sketches == null) return null;
                // Try ToArray() first (works for NXOpen collections)
                try { var arr = sketches.ToArray(); if (arr != null && arr.Length > 0) return arr[0]; } catch { }
                // Try index access
                try { return sketches[0]; } catch { }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 获取特征 journal_id（P3 2026-09-02; W10 2026-09-03 修正）。
        /// NX2412 特征 Name 为空; 早期用 FeatureType+"("+Timestamp+")" 标识。
        /// 🔴 W10/A10 实证: 对 tube/revolve FeatureType.ToString() 与特征树/NX UI 不同源 —
        ///   nx_tube 返回 SWP104(1) (UF 底层类型码), 树/删除侧却是 CABLE(1) (JournalIdentifier)
        ///   → 返回 id 无法 delete ("not found")。
        /// 修正: L1 = Feature.JournalIdentifier (与 nx_delete_feature/特征树同源);
        ///       L2 回退 FeatureType+"("+Timestamp+")"。
        /// </summary>
        public static string GetJournalId(dynamic feature)
        {
            try
            {
                if (feature == null) return "";
                // L1: JournalIdentifier — 删除/列表/树显示的同一标识
                try
                {
                    string jid = feature.JournalIdentifier;
                    if (!string.IsNullOrEmpty(jid)) return jid;
                }
                catch { }
                // L2: FeatureType(Timestamp)
                return feature.FeatureType.ToString() + "(" + feature.Timestamp + ")";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 获取特征首个输出体的 JournalIdentifier（P3 2026-09-02）。
        /// 模式来源: SelectionMonitor.cs:240 (body.JournalIdentifier)。
        /// </summary>
        public static string GetFirstBodyJournalId(dynamic feature)
        {
            try
            {
                if (feature == null) return "";
                dynamic bodies = feature.GetBodies();
                if (bodies != null)
                {
                    foreach (dynamic b in bodies)
                    {
                        if (b == null) continue;
                        try { string j = b.JournalIdentifier; if (!string.IsNullOrEmpty(j)) return j; } catch { }
                        try { string n = b.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 创建工具结果统一补句柄（P3 2026-09-02）: journal_id + body + feature 兼容别名。
        /// 建特征工具 Commit 后调用, 让下一步能按 journal_id/body 引用上一步输出。
        /// </summary>
        public static void AddFeatureResult(JObject result, dynamic feature)
        {
            if (result == null) return;
            string jid = GetJournalId(feature);
            string body = GetFirstBodyJournalId(feature);
            result["feature"] = jid;       // 旧字段名保持兼容, 语义升级为 journal_id
            result["journal_id"] = jid;
            result["body"] = body;
        }

        /// <summary>
        /// 获取当前活动草图 (NX2412 没有 ActiveSketch 属性)
        /// </summary>
        public static dynamic GetActiveSketch(dynamic workPart)
        {
            if (workPart == null) return null;
            try
            {
                foreach (dynamic sketch in workPart.Sketches)
                {
                    if (sketch.IsActive)
                        return sketch;
                }
            }
            catch { }
            return null;
        }

        // ================================================================
        // 结果构建
        // ================================================================

        /// <summary>构建成功结果</summary>
        public static JObject Ok(string message, JObject data = null)
        {
            return new JObject
            {
                { "success", true },
                { "message", message ?? "" },
                { "data", data ?? new JObject() },
                { "warnings", new JArray() }
            };
        }

        /// <summary>构建失败结果</summary>
        public static JObject Fail(string error)
        {
            return new JObject
            {
                { "success", false },
                { "message", error ?? "" },
                { "data", new JObject() },
                { "warnings", new JArray() }
            };
        }

        /// <summary>构建带数据的结果</summary>
        public static JObject Result(string message, Dictionary<string, object> data)
        {
            var jObj = new JObject();
            if (data != null)
            {
                foreach (var kv in data)
                {
                    jObj[kv.Key] = JToken.FromObject(kv.Value);
                }
            }
            return new JObject
            {
                { "success", true },
                { "message", message ?? "" },
                { "data", jObj },
                { "warnings", new JArray() }
            };
        }
    }
}
