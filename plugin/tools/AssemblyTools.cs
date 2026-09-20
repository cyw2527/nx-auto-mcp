using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools.Assembly
{
    // ========================================================================
    // Parameter helpers
    // ========================================================================
    internal static class AssemblyParams
    {
        public static string GetParamString(JObject p, string key, string defaultValue = "")
        {
            var token = p == null ? null : p[key];
            return token != null && token.Type != JTokenType.Null ? token.Value<string>() : defaultValue;
        }

        public static double GetParamDouble(JObject p, string key, double defaultValue = 0.0)
        {
            var token = p == null ? null : p[key];
            return token != null && token.Type != JTokenType.Null ? token.Value<double>() : defaultValue;
        }

        public static int GetParamInt(JObject p, string key, int defaultValue = 0)
        {
            var token = p == null ? null : p[key];
            return token != null && token.Type != JTokenType.Null ? token.Value<int>() : defaultValue;
        }

        public static bool GetParamBool(JObject p, string key, bool defaultValue = false)
        {
            var token = p == null ? null : p[key];
            return token != null && token.Type != JTokenType.Null ? token.Value<bool>() : defaultValue;
        }

        public static JArray GetParamArray(JObject p, string key)
        {
            var token = p == null ? null : p[key];
            JArray arr = token as JArray;
            if (arr != null) return arr;
            return null;
        }
    }

    // ========================================================================
    // B2 约束清扫助手 (2026-09-08) — 僵尸约束真删 (reg_siphon_sub_0908 实证)
    //   NX Positioner: ComponentAssembly.RemoveComponent 不级联删除组件约束 (NX GUI 删除组件会删),
    //   旧约束在 remove+readd 后重链新 occurrence → CannotSolve 僵尸 → 污染后续全部 solve (每约束必 OC)
    //   → 工具层两处防: remove 前清目标件全部约束 + mate 前清被约束件非 Solved 残留
    // ========================================================================
    internal static class AsmConstraintPurge
    {
        // F5 同款真删 (网络外): UFObj.DeleteObject; 对象已删时二次删除抛异常 → false
        public static bool Delete(NXOpen.Positioning.ComponentConstraint con)
        {
            if (con == null) return false;
            try { NXOpen.UF.UFSession.GetUFSession().Obj.DeleteObject(con.Tag); return true; }
            catch { return false; }
        }

        // 删除组件挂载的全部约束 (Solved + 非 Solved) — 组件移除前调用 (对齐 NX GUI 删除组件级联语义)
        public static int PurgeAllOn(NXOpen.Assemblies.Component comp)
        {
            int n = 0;
            if (comp == null) return 0;
            try
            {
                foreach (NXOpen.Positioning.ComponentConstraint con in comp.GetConstraints())
                    if (Delete(con)) n++;
            }
            catch { }
            return n;
        }

        // 删除组件上的非 Solved 残留 (僵尸/半清理, 如 remove+readd 旧约束) — mate 前清扫;
        // Solved = 合法约束不动; NewlyCreated = 状态语义不明不动 (保守)
        public static int PurgeNonSolvedOn(NXOpen.Assemblies.Component comp)
        {
            int n = 0;
            if (comp == null) return 0;
            try
            {
                foreach (NXOpen.Positioning.ComponentConstraint con in comp.GetConstraints())
                {
                    if (con == null) continue;
                    string st = "?";
                    try { st = con.GetConstraintStatus().ToString(); } catch { }
                    if (st != "Solved" && st != "NewlyCreated")
                        if (Delete(con)) n++;
                }
            }
            catch { }
            return n;
        }
    }

    // ========================================================================
    // 1. nx_add_component
    // ========================================================================

    /// <summary>
    /// Add a component from a .prt file to the current assembly.
    /// Maps to ComponentAssembly.AddComponent(partPath, name).
    /// </summary>
    /// Parameters:
    ///   part_path (string, optional) - part_path parameter.
    ///   name (string, optional) - name parameter.
    ///   position (array, optional) - position parameter.
    ///
    public class AddComponentTool : IToolHandler
    {
        public string Name { get { return "nx_add_component"; } }
        public string Description { get { return "Add a component from a .prt file to the current assembly."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string partPath = AssemblyParams.GetParamString(parameters, "part_path");
                if (string.IsNullOrEmpty(partPath))
                    return ToolResult.Fail("Parameter 'part_path' is required.").ToJson();

                string name = AssemblyParams.GetParamString(parameters, "name", "");

                // NX2412: 7-param AddComponent matching nx_bridge.cs verified pattern
                JArray posArr = AssemblyParams.GetParamArray(parameters, "position");
                double px = 0, py = 0, pz = 0;
                if (posArr != null && posArr.Count >= 3) {
                    px = posArr[0].Value<double>(); py = posArr[1].Value<double>(); pz = posArr[2].Value<double>();
                }
                var ca = (NXOpen.Assemblies.ComponentAssembly)workPart.ComponentAssembly;
                var ident = new NXOpen.Matrix3x3();
                ident.Xx = 1.0; ident.Yy = 1.0; ident.Zz = 1.0;
                var pt = new NXOpen.Point3d(px, py, pz);
                NXOpen.PartLoadStatus loadStatus;
                string[] refSets = new string[] { "MODEL", "Entire Part" };
                NXOpen.Assemblies.Component component = null;
                string lastErr = "";
                for (int r = 0; r < refSets.Length; r++) {
                    try {
                        component = ca.AddComponent(partPath, refSets[r], name, pt, ident, -1, out loadStatus);
                        try { loadStatus.Dispose(); } catch { }
                        break;
                    } catch (Exception ex2) { lastErr = ex2.Message; }
                }
                if (component == null)
                    return ToolResult.Fail(string.Format("nx_add_component failed: {0}", lastErr)).ToJson();

                var data = new JObject();
                data["component"] = component.Name;
                data["part_path"] = partPath;
                data["position"] = new JArray(px, py, pz);
                return new ToolResult { Success = true, Message = string.Format("Added component '{0}' from {1} at ({2},{3},{4}).", component.Name, partPath, px, py, pz), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_add_component failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 2. nx_mate_component  (2026-09-05 实证重写: Positioner 直连配方)
    // ========================================================================

    /// <summary>
    /// Apply an assembly constraint between components (Positioner direct mode).
    /// Recipe validated on NX2412 runtime (docs/zhuangpei/mate_probe6/7):
    ///   ClearNetwork → BeginAssemblyConstraints → EstablishNetwork
    ///   → CreateConstraint(true) → ConstraintType/Alignment (CLR props)
    ///   → CreateConstraintReference(comp, occFace, false, false, false) x2
    ///   → fixedRef.SetFixHint(true) → Solve → ClearNetwork → EndAssemblyConstraints
    ///   KEY: Solve 后必须先 ClearNetwork 再 EndAssemblyConstraints, 否则约束停留 NewlyCreated
    ///   Geometry must be occurrence-scope: proto face resolved via Component.FindOccurrence()
    /// </summary>
    /// Parameters:
    ///   component (string, required) - 被约束(移动)组件名, e.g. "STAND"
    ///   mate_type (string, required) - Touch | Concentric | Fix | Distance | Parallel | Perpendicular
    ///   alignment (string, optional) - InferAlign(默认) | CoAlign | ContraAlign
    ///   to_component (string, required except Fix) - 基准组件名, e.g. "BASE"
    ///   to_geom (string, optional) - 基准件原型几何 journal id (面格式 "FACE 120 {(0,0,0) EXTRUDE(1)}", 特征作用域解析 2026-09-05 实证)
    ///   from_geom (string, optional) - 移动件原型几何 journal id (缺省=组件级引用)
    ///   offset (number, optional) - Distance/Angle 类约束值(mm 或 度)
    ///
    public class MateComponentTool : IToolHandler
    {
        public string Name { get { return "nx_mate_component"; } }
        public string Description { get { return "Apply an assembly constraint (11 types: Touch/Concentric/Fix/Distance/Parallel/Perpendicular/AlignLock/Angle/Center12/Bond/Fit) between components."; } }

        // 白名单: NXOpen.Positioning.Constraint+Type 全集筛选 (2026-09-08 扩展, docs/zhuangpei/journal.cs 录制实证:
        //   AlignLock@1897 / Angle@2244 / Center12@2423 / Bond@885; Fit 枚举在但 UI 无入口, 待探针)
        //   · 几何 2-引用 usesAxis=false: Touch/Concentric/Distance/Parallel/Perpendicular
        //   · 几何 2-引用 usesAxis=true (走几何轴, 录制孔圆柱 FACE+true): AlignLock/Angle
        //   · Bond = 组件级引用 (movableObject=geometry=组件自身, 无几何 journal id)
        //   · Center12 = 3 引用 (移动件面居中于两基准面; 新参 to2_component/to2_geom)
        //   · Fit = 枚举存在 UI 无入口 (书: 等尺寸配对; NX2412 集成约束疑被 Touch 合并), 待实证
        private static readonly string[] MateTypeWhitelist =
            new string[] { "Touch", "Concentric", "Fix", "Distance", "Parallel", "Perpendicular",
                           "AlignLock", "Angle", "Center12", "Bond", "Fit" };
        // 引用几何走轴 (CreateConstraintReference 第 3 参 usesAxis)
        private static bool UsesAxis(string t) { return t == "AlignLock" || t == "Angle"; }
        // 不赋值 ConstraintAlignment 的类型 (录制无 alignment 行; 对齐方向由类型/几何定义)
        private static bool NoAlignmentSet(string t) { return t == "Bond" || t == "Center12" || t == "AlignLock"; }

        // 直接子组件按名查找 (OrdinalIgnoreCase; 支持 exact 或 Contains 匹配)
        private static NXOpen.Assemblies.Component FindChild(NXOpen.Assemblies.ComponentAssembly asm, string name)
        {
            NXOpen.Assemblies.Component[] kids = asm.RootComponent.GetChildren();
            foreach (NXOpen.Assemblies.Component c in kids)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            foreach (NXOpen.Assemblies.Component c in kids)  // 宽松: 含名
            {
                if (c.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return c;
            }
            return null;
        }

        // 从 journal id 采样坐标段解析 Point3d (HelpPoint + 歧义消歧 v2 用)
        //   id 格式: "FACE 130 {(0,0,10) EXTRUDE(1)}" / "FACE 2 {(0,0,58)}" / "EDGE * 4 * 5 {(x,y,z)...} ..."
        //   v10 实证 (2026-09-05): 采样点带圆括号 "(x,y,z)", 且可能后随特征名/更多采样点 —
        //   旧实现剥 {} 后整段 Split(',') 必失败 → hint 恒 (0,0,0) → 歧义消歧选中错误候选 (FLASK Touch 壶底落 z0 底环底 world z10)
        //   新实现: 取第一个 "(x,y,z)" 括号段解析
        private static NXOpen.Point3d GeomIdSamplingPoint(string journalId)
        {
            int open = journalId.IndexOf('(');
            int close = open >= 0 ? journalId.IndexOf(')', open) : -1;
            if (open < 0 || close <= open) return new NXOpen.Point3d(0, 0, 0);
            string inner = journalId.Substring(open + 1, close - open - 1).Trim();
            string[] parts = inner.Split(',');
            if (parts.Length != 3) return new NXOpen.Point3d(0, 0, 0);
            double x, y, z;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x)) x = 0;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y)) y = 0;
            if (!double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out z)) z = 0;
            return new NXOpen.Point3d(x, y, z);
        }

        // B19 (2026-09-05): 参考面 NX 侧法向 Z 符号 (AskFaceData 反射, probe 同款 argv[8])
        // 返回 +1/-1/0; 切除/抽壳 mapped 面 NX 法向=入材料向, 原始特征面=物理外向 — 方向语义以 NX 为准
        private static int NxDirZ(NXOpen.Face f)
        {
            if (f == null) return 0;
            try
            {
                var ufs = NXOpen.UF.UFSession.GetUFSession();
                System.Reflection.MethodInfo m = null;
                foreach (var mi in ufs.Modl.GetType().GetMethods())
                    if (mi.Name == "AskFaceData") { m = mi; break; }
                if (m == null) return 0;
                object[] argv = new object[8];
                argv[0] = f.Tag; argv[1] = 0;
                argv[2] = new double[3]; argv[3] = new double[3]; argv[4] = new double[6];
                argv[5] = 0.0; argv[6] = 0.0; argv[7] = 0;
                m.Invoke(ufs.Modl, argv);
                double[] d = (double[])argv[3];
                if (Math.Abs(d[2]) < 0.5) return 0;
                return d[2] > 0 ? 1 : -1;
            }
            catch { return 0; }
        }

        // 采样点 → 面平面距离 (歧义消歧 v2): AskFaceData 面内点+法向, |dot(样本-面内点, 法向)|
        // planar/cyl 通用: 对平面=平面距离, 对圆柱=近似 (轴向分量); 歧义候选通常为平面 (底/顶面)
        private static double FacePointDist(NXOpen.Face f, NXOpen.Point3d p)
        {
            if (f == null) return double.MaxValue;
            try
            {
                var ufs = NXOpen.UF.UFSession.GetUFSession();
                System.Reflection.MethodInfo m = null;
                foreach (var mi in ufs.Modl.GetType().GetMethods())
                    if (mi.Name == "AskFaceData") { m = mi; break; }
                if (m == null) return double.MaxValue;
                object[] argv = new object[8];
                argv[0] = f.Tag; argv[1] = 0;
                argv[2] = new double[3]; argv[3] = new double[3]; argv[4] = new double[6];
                argv[5] = 0.0; argv[6] = 0.0; argv[7] = 0;
                m.Invoke(ufs.Modl, argv);
                double[] pt = (double[])argv[2];
                double[] dir = (double[])argv[3];
                double dx = p.X - pt[0], dy = p.Y - pt[1], dz = p.Z - pt[2];
                return Math.Abs(dx * dir[0] + dy * dir[1] + dz * dir[2]);
            }
            catch { return double.MaxValue; }
        }

        // 解析 journal id 内全部 (x,y,z) 采样组 (B1 修复 2026-09-08):
        //   id 形如 "EDGE * 1 * 2 {(32.5,56.29,0)(-65,0,0)(32.5,-56.29,0) REVOLVED(1)}"
        //   → 逐个 '(' ')' 组尝试解析 3 个 double; 特征名组 "(REVOLVED(1))" / 括号限定 "[EXTRUDE(4) ...]" 解析失败自动跳过
        private static System.Collections.Generic.List<NXOpen.Point3d> ParseSamplePoints(string journalId)
        {
            var pts = new System.Collections.Generic.List<NXOpen.Point3d>();
            if (journalId == null) return pts;
            int i = 0;
            while (i >= 0 && i < journalId.Length)
            {
                int open = journalId.IndexOf('(', i);
                if (open < 0) break;
                int close = journalId.IndexOf(')', open);
                if (close < 0) break;
                string inner = journalId.Substring(open + 1, close - open - 1).Trim();
                string[] parts = inner.Split(',');
                double x, y, z;
                if (parts.Length == 3
                    && double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                    && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)
                    && double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
                    pts.Add(new NXOpen.Point3d(x, y, z));
                i = close + 1;
            }
            return pts;
        }

        // 采样平面检测 (B1 修复): 边候选是否为 3 采样点所在平面上的圆 — 共享 id 边消歧
        //   (STAND 'EDGE * 1 * 2' 在 z0 r65 底 rim / z50 / z58 r22 顶 rim 多边共存; NX FindObject 恒返回
        //   首个命中忽略采样)。3 点定平面法向 → 边两端点到该平面距离 max < 5mm 视为匹配。
        private static double EdgePlaneDist(NXOpen.Edge e, System.Collections.Generic.List<NXOpen.Point3d> samples)
        {
            if (e == null || samples == null || samples.Count < 3) return double.MaxValue;
            NXOpen.Point3d s1 = samples[0];
            double d1x = samples[1].X - s1.X, d1y = samples[1].Y - s1.Y, d1z = samples[1].Z - s1.Z;
            double d2x = samples[2].X - s1.X, d2y = samples[2].Y - s1.Y, d2z = samples[2].Z - s1.Z;
            double nx = d1y * d2z - d1z * d2y;
            double ny = d1z * d2x - d1x * d2z;
            double nz = d1x * d2y - d1y * d2x;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) return double.MaxValue;   // 采样共线 (直线类) → 无法平面判定
            nx /= len; ny /= len; nz /= len;
            try
            {
                NXOpen.Point3d p1, p2;
                e.GetVertices(out p1, out p2);
                double h1 = Math.Abs((p1.X - s1.X) * nx + (p1.Y - s1.Y) * ny + (p1.Z - s1.Z) * nz);
                double h2 = Math.Abs((p2.X - s1.X) * nx + (p2.Y - s1.Y) * ny + (p2.Z - s1.Z) * nz);
                return Math.Max(h1, h2);
            }
            catch { return double.MaxValue; }
        }

        // 原型几何 journal id → occurrence 对象
        // 2026-09-05 实证 (mate_face_resolve): part.FindObject 直达 / "Features|<t>|<id>" 组合串均 ERR;
        // 唯一正确路径 = 特征作用域 feature.FindObject(id), id 形如 "FACE 130 {(0,0,0.01) EXTRUDE(1)}"
        // B1 修复 (2026-09-08, reg_siphon_sub_0908 探针实证): NX FindObject/feature.FindObject 对共享
        //   journal id 恒返回首个命中且忽略 {采样点} — STAND 体内 'EDGE * 1 * 2' 在 z0(r65 底 rim)/z50/
        //   z58(r22 顶 rim) 多边共存, 配 rim 圆时解析到错边 (z58) → Concentric 与贴合面 Touch 冲突
        //   OverConstrained (v1.1 O-239+O-253 配方不可复现根因)。修复 = 带采样 id 优先走路径3
        //   (body 全枚举 + 采样平面消歧, 面/边同权; 边=JournalIdentifier 前缀 + 3 采样点定平面判顶点距),
        //   路径1/2 降为兜底且对边结果加同款采样门控。
        private static NXOpen.NXObject ResolveOccurrence(NXOpen.Assemblies.Component comp, string journalId)
        {
            NXOpen.Part proto = comp.Prototype as NXOpen.Part;
            if (proto == null) return null;

            bool hasHint = journalId.IndexOf('{') >= 0;
            string mainId = journalId;
            int openB = journalId.IndexOf('{');
            if (openB > 0) mainId = journalId.Substring(0, openB).Trim();
            System.Collections.Generic.List<NXOpen.Point3d> samples = ParseSamplePoints(journalId);

            NXOpen.NXObject protoObj = null;

            // 路径3 (优先): body faces/edges 全枚举 + 采样消歧 (F4v2 语义, B1 补边候选支持)
            if (hasHint && protoObj == null)
            {
                var cands = new System.Collections.Generic.List<NXOpen.NXObject>();
                try
                {
                    foreach (NXOpen.Body b in proto.Bodies)
                    {
                        if (!b.IsSolidBody) continue;
                        foreach (NXOpen.Face f in b.GetFaces())
                            if (string.Equals(f.JournalIdentifier, mainId, StringComparison.OrdinalIgnoreCase)) cands.Add(f);
                        foreach (NXOpen.Edge e in b.GetEdges())
                            if (e.JournalIdentifier != null
                                && e.JournalIdentifier.StartsWith(mainId + " {", StringComparison.OrdinalIgnoreCase)) cands.Add(e);
                    }
                }
                catch { }
                if (cands.Count > 0)
                {
                    NXOpen.Point3d hint = samples.Count > 0 ? samples[0] : new NXOpen.Point3d(0, 0, 0);
                    double best = double.MaxValue;
                    NXOpen.NXObject bestO = null;
                    foreach (NXOpen.NXObject c in cands)
                    {
                        double d = double.MaxValue;
                        NXOpen.Face cf = c as NXOpen.Face;
                        if (cf != null) d = FacePointDist(cf, hint);
                        else { NXOpen.Edge ce = c as NXOpen.Edge; if (ce != null) d = EdgePlaneDist(ce, samples); }
                        if (d < best) { best = d; bestO = c; }
                    }
                    if (bestO != null && best < 5.0) protoObj = bestO;
                    else if (cands.Count == 1)
                        throw new Exception("journal id '" + mainId + "' 候选几何与采样点不匹配 (dist " + (bestO != null ? best.ToString("F2") : "N/A") + "mm):\n   采样 {x,y,z} 须落在目标面平面/目标边平面内");
                    else
                        throw new Exception("journal id '" + mainId + "' 采样消歧失败 (最近候选 dist " + (bestO != null ? best.ToString("F2") : "N/A") + "mm):\n   请给 {x,y,z} 采样点落在目标几何平面内, 或改带特征名的唯一 id");
                }
            }
            // 兜底链 (sample-less / 括号限定 id 等路径3 不可枚举形态): 路径1 → 路径2 → 特征穷举
            if (protoObj == null)
            {
                // 路径1: part 级直达 (body/feature/datum 等 part 作用域 id) — B1: 边结果过采样门控
                try
                {
                    NXOpen.NXObject o1 = (NXOpen.NXObject)proto.FindObject(journalId);
                    NXOpen.Edge e1 = o1 as NXOpen.Edge;
                    if (e1 == null || samples.Count < 3 || EdgePlaneDist(e1, samples) < 5.0) protoObj = o1;
                }
                catch { }
            }
            if (protoObj == null)
            {
                // 路径2: 特征作用域 (面/边: id 尾段 " NAME(k)}" 含特征名) — 边结果过采样门控
                int close = journalId.LastIndexOf(')');
                if (close > 0)
                {
                    string head = journalId.Substring(0, close);
                    int sp = head.LastIndexOf(' ');
                    if (sp >= 0)
                    {
                        string featName = head.Substring(sp + 1) + ")";
                        try
                        {
                            NXOpen.Features.Feature feat = (NXOpen.Features.Feature)proto.Features.FindObject(featName);
                            if (feat != null)
                            {
                                NXOpen.NXObject o2 = (NXOpen.NXObject)feat.FindObject(journalId);
                                NXOpen.Edge e2 = o2 as NXOpen.Edge;
                                if (e2 == null || samples.Count < 3 || EdgePlaneDist(e2, samples) < 5.0) protoObj = o2;
                            }
                        }
                        catch { }
                    }
                }
            }
            if (protoObj == null)
            {
                // 特征内穷举兜底 (原 F4, 部分上下文可达)
                try
                {
                    foreach (NXOpen.Features.Feature feat in proto.Features)
                    {
                        if (feat == null) continue;
                        try
                        {
                            NXOpen.NXObject o = (NXOpen.NXObject)feat.FindObject(mainId);
                            if (o != null) { protoObj = o; break; }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            if (protoObj == null) return null;
            if (protoObj is NXOpen.Face || protoObj is NXOpen.Edge || protoObj is NXOpen.DatumAxis || protoObj is NXOpen.DatumPlane)
            {
                return comp.FindOccurrence(protoObj);   // typed occurrence (NX3)
            }
            return null;
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string component = AssemblyParams.GetParamString(parameters, "component");
                if (string.IsNullOrEmpty(component))
                    return ToolResult.Fail("Parameter 'component' is required.").ToJson();
                string mateType = AssemblyParams.GetParamString(parameters, "mate_type");
                if (string.IsNullOrEmpty(mateType))
                    return ToolResult.Fail("Parameter 'mate_type' is required.").ToJson();
                string alignment = AssemblyParams.GetParamString(parameters, "alignment", "InferAlign");
                string toComponentName = AssemblyParams.GetParamString(parameters, "to_component");
                string toGeom = AssemblyParams.GetParamString(parameters, "to_geom");
                string fromGeom = AssemblyParams.GetParamString(parameters, "from_geom");
                // Center12 第 2 基准 (2026-09-08, journal.cs:2423 居中约束第 3 引用)
                string to2ComponentName = AssemblyParams.GetParamString(parameters, "to2_component");
                string to2Geom = AssemblyParams.GetParamString(parameters, "to2_geom");
                double offset = AssemblyParams.GetParamDouble(parameters, "offset", 0.0);

                string typeKey = null;
                foreach (string w in MateTypeWhitelist)
                    if (string.Equals(w, mateType.Trim(), StringComparison.OrdinalIgnoreCase)) { typeKey = w; break; }
                if (typeKey == null)
                    return ToolResult.Fail(string.Format("Unsupported mate type '{0}'. Use one of: {1} (实证白名单).", mateType, string.Join(", ", MateTypeWhitelist))).ToJson();

                var asm = workPart.ComponentAssembly as NXOpen.Assemblies.ComponentAssembly;
                var positioner = asm.Positioner as NXOpen.Positioning.ComponentPositioner;

                bool isFix = typeKey == "Fix";
                bool isBond = typeKey == "Bond";
                bool isCenter12 = typeKey == "Center12";
                bool geomRequired = !isFix && !isBond;   // Bond 引用组件级, 几何可空
                NXOpen.Assemblies.Component compTo = null;    // 基准 (固定)
                NXOpen.Assemblies.Component compFrom = null;  // 移动
                NXOpen.Assemblies.Component compTo2 = null;   // Center12 第 2 基准
                NXOpen.NXObject occTo = null, occFrom = null, occTo2 = null;

                if (isFix)
                {
                    compTo = FindChild(asm, component);
                    if (compTo == null) return ToolResult.Fail(string.Format("Component '{0}' not found among direct children.", component)).ToJson();
                }
                else
                {
                    if (string.IsNullOrEmpty(toComponentName))
                        return ToolResult.Fail("Parameter 'to_component' (基准组件) is required for type '" + typeKey + "'.").ToJson();
                    compFrom = FindChild(asm, component);
                    compTo = FindChild(asm, toComponentName);
                    if (compFrom == null) return ToolResult.Fail(string.Format("Moving component '{0}' not found.", component)).ToJson();
                    if (compTo == null) return ToolResult.Fail(string.Format("Base component '{0}' not found.", toComponentName)).ToJson();
                    if (geomRequired && (string.IsNullOrEmpty(toGeom) || string.IsNullOrEmpty(fromGeom)))
                        return ToolResult.Fail("Parameters 'to_geom' and 'from_geom' (原型几何 journal id, e.g. \"FACE 120 {(0,0,0) EXTRUDE(1)}\", 特征作用域解析) are required for type '" + typeKey + "'.").ToJson();
                    if (geomRequired)
                    {
                        occTo = ResolveOccurrence(compTo, toGeom);
                        occFrom = ResolveOccurrence(compFrom, fromGeom);
                        if (occTo == null || occFrom == null)
                            return ToolResult.Fail(string.Format("Geometry resolve failed: to_geom='{0}' -> {1}, from_geom='{2}' -> {3} (需组件原型内的 journal id).",
                                toGeom, occTo != null ? "ok" : "FAIL", fromGeom, occFrom != null ? "ok" : "FAIL")).ToJson();
                    }
                    if (isCenter12)
                    {
                        // Center12: 第 2 基准 (居中参照对) — journal.cs:2423 第 3 引用 (component3, face13)
                        if (string.IsNullOrEmpty(to2ComponentName) || string.IsNullOrEmpty(to2Geom))
                            return ToolResult.Fail("Parameters 'to2_component' and 'to2_geom' (Center12 第 2 基准组件/几何) are required for Center12.").ToJson();
                        compTo2 = FindChild(asm, to2ComponentName);
                        if (compTo2 == null) return ToolResult.Fail(string.Format("Base2 component '{0}' not found.", to2ComponentName)).ToJson();
                        occTo2 = ResolveOccurrence(compTo2, to2Geom);
                        if (occTo2 == null)
                            return ToolResult.Fail(string.Format("Geometry resolve failed: to2_geom='{0}' (需组件原型内的 journal id).", to2Geom)).ToJson();
                    }
                }

                // ── Concentric 几何裁决 (B18 实证 2026-09-05, reg_siphon_0905e): 必须圆形 EDGE 引用 ──
                // 圆柱面 FACE 引用 (usesAxis=false/true + HelpPoint) 恒 CannotSolve; UI 录制真迹同用圆 EDGE
                if (!isFix && typeKey == "Concentric")
                {
                    bool eFrom = occFrom is NXOpen.Edge;
                    bool eTo = occTo is NXOpen.Edge;
                    if (!eFrom || !eTo)
                        return ToolResult.Fail("Concentric 必须引用圆形 EDGE (rim 圆), 形如 'EDGE * 130 * 140 {(-2,3.46,95)(4,0,95)(-2,-3.46,95) REVOLVED(1)}'. " +
                            "圆柱面 FACE 引用不可程序化解 (B18: usesAxis 双变体+HelpPoint 均 CannotSolve); 请改传该圆柱端部圆形 rim 的 EDGE id (可用面/边 dump 探针输出).").ToJson();
                }

                // ── B2 清扫 (2026-09-08): 被约束件上的非 Solved 残留 (僵尸/半清理, 如 remove+readd
                //    旧约束 CannotSolve) 会污染本步 solve → 先清后配; Solved 合法约束不动 (实证 reg_siphon_sub_0908) ──
                int zombiePurged = 0;
                if (compTo != null) zombiePurged += AsmConstraintPurge.PurgeNonSolvedOn(compTo);
                if (!isFix && compFrom != null) zombiePurged += AsmConstraintPurge.PurgeNonSolvedOn(compFrom);

                // ── Positioner 会话 (UI 录制序列 1:1, 2026-09-05 journal.cs 实证) ──
                // 录制真迹关键点: Establish 先于 Begin · ArrangementsMode=Existing
                //   · MoveObjectsState=true · refs 后设 HelpPoint · 基准 ref SetFixHint(true)
                //   · Touch 显式 ContraAlign · Solve ×2 · SetFixHint(false) · Primary 设/还原
                //   · ClearNetwork → EndAssemblyConstraints (旧配方缺上述元素 → 2nd+ 约束恒 NewlyCreated)
                NXOpen.Positioning.Constraint constraint = null;
                try
                {
                    NXOpen.Assemblies.Arrangement primaryBackup = positioner.PrimaryArrangement;
                    NXOpen.Assemblies.Arrangement arrangement1 = null;
                    foreach (NXOpen.Assemblies.Arrangement a in workPart.ComponentAssembly.Arrangements)
                        if (a.Name == "Arrangement 1") { arrangement1 = a; break; }
                    if (arrangement1 != null) positioner.PrimaryArrangement = arrangement1;

                    // Establish 先 → Begin 后 (录制顺序)
                    NXOpen.Positioning.ComponentNetwork network =
                        positioner.EstablishNetwork() as NXOpen.Positioning.ComponentNetwork;
                    positioner.BeginAssemblyConstraints();
                    if (network != null)
                    {
                        network.NetworkArrangementsMode = NXOpen.Positioning.ComponentNetwork.ArrangementsMode.Existing;
                        network.DisplayComponent = null;
                        network.MoveObjectsState = true;
                    }
                    constraint = positioner.CreateConstraint(true);

                    // 枚举: ConstraintType/ConstraintAlignment 是 CLR 属性
                    var typeEnum = (NXOpen.Positioning.Constraint.Type)Enum.Parse(typeof(NXOpen.Positioning.Constraint.Type), typeKey);
                    // B19 铁律 (2026-09-05): Touch/Distance 平面配对, 两参考面 NX-dir 同向 → CoAlign, 反向 → ContraAlign
                    //   (切除/抽壳 mapped 面 NX 法向=入材料向; 同向配 ContraAlign = OverConstrained 或 180° 镜像)
                    // 显式 alignment 参数尊重; InferAlign/缺省 → NX-dir 规则首选 + fallbackAlign 换向重试
                    string alignKey = alignment;
                    string fallbackAlign = null;
                    bool skipAlign = NoAlignmentSet(typeKey);
                    if (string.Equals(alignment, "InferAlign", StringComparison.OrdinalIgnoreCase)
                        && (typeKey == "Touch" || typeKey == "Distance"))
                    {
                        int dF = NxDirZ(occFrom as NXOpen.Face);
                        int dT = NxDirZ(occTo as NXOpen.Face);
                        if (dF != 0 && dF == dT) { alignKey = "CoAlign"; fallbackAlign = "ContraAlign"; }
                        else { alignKey = "ContraAlign"; fallbackAlign = "CoAlign"; }
                    }
                    var cc = constraint as NXOpen.Positioning.ComponentConstraint;
                    cc.ConstraintType = typeEnum;
                    if (!skipAlign)
                    {
                        var alignEnum = (NXOpen.Positioning.Constraint.Alignment)Enum.Parse(typeof(NXOpen.Positioning.Constraint.Alignment), alignKey);
                        cc.ConstraintAlignment = alignEnum;
                    }

                    NXOpen.Positioning.ConstraintReference refFixed = null;
                    if (isFix)
                    {
                        refFixed = constraint.CreateConstraintReference(compTo, null, false, false, false);
                    }
                    else if (isBond)
                    {
                        // Bond (journal.cs:885 胶合): 组件级引用 — movableObject=geometry=组件自身, HelpPoint 省略
                        NXOpen.Positioning.ConstraintReference rMove =
                            constraint.CreateConstraintReference(compFrom, compFrom, false, false, false);
                        refFixed = constraint.CreateConstraintReference(compTo, compTo, false, false, false);
                    }
                    else
                    {
                        // 引用顺序: 移动件在前, 基准在后 + FixHint (录制同款)
                        bool usesAxis = UsesAxis(typeKey);   // AlignLock/Angle 走几何轴 (录制 true)
                        NXOpen.Positioning.ConstraintReference rMove =
                            constraint.CreateConstraintReference(compFrom, occFrom, usesAxis, false, false);
                        rMove.HelpPoint = GeomIdSamplingPoint(fromGeom);   // HelpPoint (录制: 世界点; 局部近似可解)
                        refFixed = constraint.CreateConstraintReference(compTo, occTo, usesAxis, false, false);
                        refFixed.HelpPoint = GeomIdSamplingPoint(toGeom);
                        if (isCenter12)
                        {
                            // Center12 (journal.cs:2423 居中): 第 3 引用 = 第 2 基准, FixHint 落此 (录制同款)
                            NXOpen.Positioning.ConstraintReference rThird =
                                constraint.CreateConstraintReference(compTo2, occTo2, false, false, false);
                            rThird.HelpPoint = GeomIdSamplingPoint(to2Geom);
                            refFixed = rThird;
                        }
                    }
                    if (refFixed == null) return ToolResult.Fail("CreateConstraintReference failed.").ToJson();
                    refFixed.SetFixHint(true);

                    // Distance/Angle 值 → 表达式 (offset 参数; Angle=角度°, journal.cs:2244 SetExpression 两次)
                    if (offset != 0.0 && (typeKey == "Distance" || typeKey == "Angle"))
                    {
                        constraint.SetExpression(offset.ToString("0.######"));
                    }

                    network.Solve();
                    network.Solve();                  // 录制: 对话框内+外双 Solve
                    refFixed.SetFixHint(false);       // 录制: 解完撤销 FixHint

                    // B19 换向重试: 首选 alignment 失败(OverConstrained/CannotSolve/镜像风险态) → 换另一向再解
                    string statusStr = "?";
                    try { statusStr = constraint.GetConstraintStatus().ToString(); } catch { }
                    if (statusStr != "Solved" && fallbackAlign != null)
                    {
                        try
                        {
                            cc.ConstraintAlignment = (NXOpen.Positioning.Constraint.Alignment)Enum.Parse(
                                typeof(NXOpen.Positioning.Constraint.Alignment), fallbackAlign);
                            network.Solve();
                            network.Solve();
                            string st2 = "?";
                            try { st2 = constraint.GetConstraintStatus().ToString(); } catch { }
                            if (st2 == "Solved") { alignKey = fallbackAlign; statusStr = st2; }
                        }
                        catch { }
                    }
                    // F5 (2026-09-05): 失败约束彻底删除 — RemoveConstraint 仅网络摘除, 对象仍挂组件
                    // (O-170/O-187 残留锁 DOF → 后续 OverConstrained 误报)
                    // 真删 = UF.UFObj.DeleteObject(tag) — eye_probe2 实证 (FLASK 8→7 行, O-170 彻底消失)
                    // 时序: 网络内先摘除 → ClearNetwork/End 后 DeleteObject (对象离开网络才可删)
                    NXOpen.Positioning.ComponentConstraint failedConstraint = null;
                    if (statusStr != "Solved")
                    {
                        try { network.RemoveConstraint(constraint); } catch { }
                        failedConstraint = constraint as NXOpen.Positioning.ComponentConstraint;
                        constraint = null;
                    }

                    positioner.PrimaryArrangement = primaryBackup;   // 还原 (录制置 null)
                    positioner.ClearNetwork();
                    positioner.EndAssemblyConstraints();

                    // F5 真删 (网络外): 失败约束对象从装配模型彻底移除
                    string cleanupNote = null;
                    if (failedConstraint != null)
                    {
                        try
                        {
                            NXOpen.UF.UFSession ufs = NXOpen.UF.UFSession.GetUFSession();
                            ufs.Obj.DeleteObject(failedConstraint.Tag);
                            cleanupNote = "deleted";
                        }
                        catch (Exception exDel) { cleanupNote = "delete FAIL: " + exDel.Message; }
                    }

                    // 结果读回: 约束类型 + Solver 状态 (F5 失败已移除 → constraint 可为 null)
                    var data = new JObject();
                    data["constraint"] = constraint != null ? constraint.JournalIdentifier : null;
                    data["type"] = typeKey;
                    data["status"] = statusStr != "?" ? statusStr : (constraint != null ? constraint.GetConstraintStatus().ToString() : "?");
                    data["alignment"] = alignKey;
                    data["cleanup"] = cleanupNote;   // F5 真删结果 (null=无需清理/deleted=已删/FAIL 文本)
                    data["zombie_purged"] = zombiePurged;   // B2 清扫 (2026-09-08): 解算前清掉的非 Solved 残留约束数
                    data["to_component"] = compTo.Name;
                    data["from_component"] = isFix ? compTo.Name : compFrom.Name;
                    if (occTo != null) data["to_geom"] = toGeom;
                    if (occFrom != null) data["from_geom"] = fromGeom;
                    if (compTo2 != null) data["to2_component"] = compTo2.Name;
                    if (occTo2 != null) data["to2_geom"] = to2Geom;
                    string msg = string.Format("Constraint {0} {1} (base={2}): status={3}{4}.",
                        typeKey, isFix ? "on " + compTo.Name : compFrom.Name + "↔" + compTo.Name, compTo.Name,
                        statusStr != "?" ? statusStr : "?",
                        statusStr == "Solved" ? " (alignment=" + alignKey + ")" : "");
                    return new ToolResult { Success = true, Message = msg, Data = data }.ToJson();
                }
                finally
                {
                    // 会话兜底收尾 (异常路径也保证退出约束模式)
                    try { positioner.EndAssemblyConstraints(); } catch { }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_mate_component failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 3. nx_list_components
    // ========================================================================

    /// <summary>
    /// List all components in the current assembly with position, orientation,
    /// reference set and suppression state.
    /// V2 (2026-09-05, Eye-TODO A2): typed GetPosition(out) — eye_probe1 实证
    /// (NX2412 运行时 8 件 origin/矩阵全读出; Component.FullPath 不存在 → proto.FullPath)
    /// V4 (2026-09-07, GAP-08): recursive=true 走 ComponentTree 全树扁平 (带 level/path);
    /// 默认 false 保持单级兼容。
    /// </summary>
    public class ListComponentsTool : IToolHandler
    {
        public string Name { get { return "nx_list_components"; } }
        public string Description { get { return "List all components with name, origin, rotation, reference set and suppression state. recursive=true 输出全树(带 level/path)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                bool recursive = AssemblyParams.GetParamBool(parameters, "recursive", false);

                var componentsList = new JArray();
                List<ComponentTree.Node> nodes = recursive
                    ? ComponentTree.Flatten(workPart)
                    : ComponentTree.DirectChildren(workPart);
                foreach (ComponentTree.Node node in nodes)
                    componentsList.Add(ComponentTree.NodeToJson(node, false));

                int count = componentsList.Count;
                var data = new JObject();
                data["components"] = componentsList;
                data["count"] = count;
                data["recursive"] = recursive;
                return new ToolResult { Success = true, Message = string.Format("Assembly contains {0} component(s).", count), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_list_components failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 4. nx_reposition_component
    // ========================================================================

    /// <summary>
    /// Move a component by translation and rotation offsets.
    /// Computes a rotation matrix from Euler angles (XYZ order, degrees)
    /// and applies it via ComponentAssembly.MoveComponent().
    /// </summary>
    /// Parameters:
    ///   component (string, optional) - component parameter.
    ///   dx (number, optional) - dx parameter.
    ///   dy (number, optional) - dy parameter.
    ///   dz (number, optional) - dz parameter.
    ///   rx (number, optional) - rx parameter.
    ///   ry (number, optional) - ry parameter.
    ///   rz (number, optional) - rz parameter.
    ///
    public class RepositionComponentTool : IToolHandler
    {
        public string Name { get { return "nx_reposition_component"; } }
        public string Description { get { return "Move a component by translation and rotation offsets."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string component = AssemblyParams.GetParamString(parameters, "component");
                if (string.IsNullOrEmpty(component))
                    return ToolResult.Fail("Parameter 'component' is required.").ToJson();

                double dx = AssemblyParams.GetParamDouble(parameters, "dx", 0.0);
                double dy = AssemblyParams.GetParamDouble(parameters, "dy", 0.0);
                double dz = AssemblyParams.GetParamDouble(parameters, "dz", 0.0);
                double rx = AssemblyParams.GetParamDouble(parameters, "rx", 0.0);
                double ry = AssemblyParams.GetParamDouble(parameters, "ry", 0.0);
                double rz = AssemblyParams.GetParamDouble(parameters, "rz", 0.0);

                dynamic compAssembly = workPart.ComponentAssembly;
                // GAP-08: 递归全树查找 (子装配内件也可按名操作)
                ComponentTree.Node foundNode = ComponentTree.FindByName(workPart as NXOpen.Part, component);
                dynamic target = foundNode != null ? foundNode.Component : null;

                if (target == null)
                    return ToolResult.Fail(string.Format("Component '{0}' not found in assembly. Use nx_list_components to see available components.", component)).ToJson();

                // Build translation
                // NX2412: NXOpen.Transform and MoveComponentBuilder don't exist.
                // Store translation values for reference.
                double[] translation = new double[] { dx, dy, dz };

                // Build rotation matrix from Euler angles (XYZ order)
                double radX = rx * Math.PI / 180.0;
                double radY = ry * Math.PI / 180.0;
                double radZ = rz * Math.PI / 180.0;

                double cx = Math.Cos(radX), sx = Math.Sin(radX);
                double cy = Math.Cos(radY), sy = Math.Sin(radY);
                double cz = Math.Cos(radZ), sz = Math.Sin(radZ);

                NXOpen.Matrix3x3 rot = new NXOpen.Matrix3x3();
                rot.Xx = cy * cz;
                rot.Xy = sx * sy * cz - cx * sz;
                rot.Xz = cx * sy * cz + sx * sz;
                rot.Yx = cy * sz;
                rot.Yy = sx * sy * sz + cx * cz;
                rot.Yz = cx * sy * sz - sx * cz;
                rot.Zx = -sy;
                rot.Zy = sx * cy;
                rot.Zz = cx * cy;

                // NX2412 实证 (2026-09-07 mate_probe22): Component.Move 不存在 — 旧实现 dynamic 晚绑定 + 空 catch = 静默 no-op
                // 正解: ComponentAssembly.MoveComponent(comp, Vector3d 平移增量, Matrix3x3 旋转增量, 列序存储)
                NXOpen.Assemblies.Component compObj = target as NXOpen.Assemblies.Component;
                NXOpen.Part wp = workPart as NXOpen.Part;
                if (compObj == null || wp == null)
                    return ToolResult.Fail("nx_reposition_component failed: component/workPart cast error.").ToJson();
                NXOpen.Vector3d delta = new NXOpen.Vector3d();
                delta.X = dx; delta.Y = dy; delta.Z = dz;
                wp.ComponentAssembly.MoveComponent(compObj, delta, rot);

                var data = new JObject();
                data["component"] = component;
                data["translation"] = new JArray(dx, dy, dz);
                data["rotation"] = new JArray(rx, ry, rz);
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Repositioned component '{0}' (dx={1}, dy={2}, dz={3}, rx={4}, ry={5}, rz={6}).", component, dx, dy, dz, rx, ry, rz),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_reposition_component failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 5. nx_remove_component
    // ========================================================================

    /// <summary>
    /// Remove a component from the current assembly.
    /// Maps to ComponentAssembly.RemoveComponent(target).
    /// </summary>
    /// Parameters:
    ///   component (string, optional) - component parameter.
    ///
    public class RemoveComponentTool : IToolHandler
    {
        public string Name { get { return "nx_remove_component"; } }
        public string Description { get { return "Remove a component from the current assembly."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string component = AssemblyParams.GetParamString(parameters, "component");
                if (string.IsNullOrEmpty(component))
                    return ToolResult.Fail("Parameter 'component' is required.").ToJson();

                dynamic compAssembly = workPart.ComponentAssembly;
                // GAP-08: 递归全树查找 (子装配内件也可按名操作)
                ComponentTree.Node foundNode = ComponentTree.FindByName(workPart as NXOpen.Part, component);
                dynamic target = foundNode != null ? foundNode.Component : null;

                if (target == null)
                    return ToolResult.Fail(string.Format("Component '{0}' not found in assembly. Use nx_list_components to see available components.", component)).ToJson();

                // B2 (2026-09-08): NX RemoveComponent 不级联删约束 — 旧约束在 remove+readd 后重链新 occ
                // 变 CannotSolve 僵尸污染后续 solve (reg_siphon_sub_0908 实证)。移除前先清该件全部约束,
                // 对齐 NX GUI 删除组件语义 (约束随组件删除)。约束在两端组件 GetConstraints 均列出 → 清一次即净。
                int purged = AsmConstraintPurge.PurgeAllOn(target as NXOpen.Assemblies.Component);

                compAssembly.RemoveComponent(target);

                var data = new JObject();
                data["removed"] = component;
                data["constraints_purged"] = purged;
                return new ToolResult { Success = true, Message = string.Format("Removed component '{0}' from assembly (purged {1} constraint(s)).", component, purged), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_remove_component failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 6. nx_suppress_component
    // ========================================================================

    /// <summary>
    /// Suppress or unsuppress a component in the assembly.
    /// Maps to ComponentAssembly.SetComponentSuppression(target, suppress).
    /// </summary>
    /// Parameters:
    ///   component (string, optional) - component parameter.
    ///   suppress (boolean, optional) - suppress parameter.
    ///
    public class SuppressComponentTool : IToolHandler
    {
        public string Name { get { return "nx_suppress_component"; } }
        public string Description { get { return "Suppress or unsuppress a component in the assembly."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string component = AssemblyParams.GetParamString(parameters, "component");
                if (string.IsNullOrEmpty(component))
                    return ToolResult.Fail("Parameter 'component' is required.").ToJson();

                bool suppress = AssemblyParams.GetParamBool(parameters, "suppress", true);

                dynamic compAssembly = workPart.ComponentAssembly;
                // GAP-08: 递归全树查找 (子装配内件也可按名操作)
                ComponentTree.Node foundNode = ComponentTree.FindByName(workPart as NXOpen.Part, component);
                dynamic target = foundNode != null ? foundNode.Component : null;

                if (target == null)
                    return ToolResult.Fail(string.Format("Component '{0}' not found in assembly. Use nx_list_components to see available components.", component)).ToJson();

                // NX2412: SetComponentSuppression removed. Use Component.Suppress()/Unsuppress().
                if (suppress)
                    target.Suppress();
                else
                    target.Unsuppress();

                string action = suppress ? "Suppressed" : "Unsuppressed";
                var data = new JObject();
                data["component"] = component;
                data["suppressed"] = suppress;
                return new ToolResult { Success = true, Message = string.Format("{0} component '{1}' in assembly.", action, component), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_suppress_component failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 7. nx_component_pattern
    // ========================================================================

    /// <summary>
    /// Create a component pattern (linear or circular) in the assembly.
    /// Linear: requires spacing, direction (X/Y/Z), and count.
    /// Circular: requires axis (X/Y/Z), direction, count, and optional angle_span.
    /// Uses ComponentAssembly.CreateComponentPatternBuilder().
    /// </summary>
    /// Parameters:
    ///   component (string, optional) - component parameter.
    ///   pattern_type (string, optional) - pattern_type parameter.
    ///   count (integer, optional) - count parameter.
    ///   direction (string, optional) - direction parameter.
    ///   axis (string, optional) - axis parameter.
    ///   angle_span (number, optional) - angle_span parameter.
    ///   spacing (string, optional) - spacing parameter.
    ///
    public class ComponentPatternTool : IToolHandler
    {
        public string Name { get { return "nx_component_pattern"; } }
        public string Description { get { return "Create a component pattern (linear or circular) in the assembly."; } }

        private static readonly Dictionary<string, double[]> LinearDirections = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            {"X", new[] { 1.0, 0.0, 0.0 }},
            {"Y", new[] { 0.0, 1.0, 0.0 }},
            {"Z", new[] { 0.0, 0.0, 1.0 }}
        };

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string component = AssemblyParams.GetParamString(parameters, "component");
                if (string.IsNullOrEmpty(component))
                    return ToolResult.Fail("Parameter 'component' is required.").ToJson();

                string patternType = AssemblyParams.GetParamString(parameters, "pattern_type");
                if (string.IsNullOrEmpty(patternType))
                    return ToolResult.Fail("Parameter 'pattern_type' is required.").ToJson();

                int count = AssemblyParams.GetParamInt(parameters, "count", 2);
                string direction = AssemblyParams.GetParamString(parameters, "direction");
                if (string.IsNullOrEmpty(direction))
                    return ToolResult.Fail("Parameter 'direction' is required.").ToJson();

                string patternKey = patternType.Trim().ToLowerInvariant();
                if (patternKey != "linear" && patternKey != "circular")
                    return ToolResult.Fail(string.Format("Unsupported pattern type: '{0}'. Use 'linear' or 'circular'.", patternType)).ToJson();

                string dirKey = direction.Trim().ToUpperInvariant();
                if (!LinearDirections.ContainsKey(dirKey))
                {
                    string valid = string.Join(", ", LinearDirections.Keys);
                    return ToolResult.Fail(string.Format("Invalid direction: '{0}'. Use one of: {1}.", direction, valid)).ToJson();
                }

                // Find the component
                dynamic compAssembly = workPart.ComponentAssembly;
                // GAP-08: 递归全树查找 (子装配内件也可按名操作)
                ComponentTree.Node foundNode = ComponentTree.FindByName(workPart as NXOpen.Part, component);
                dynamic target = foundNode != null ? foundNode.Component : null;

                if (target == null)
                    return ToolResult.Fail(string.Format("Component '{0}' not found in assembly. Use nx_list_components to see available components.", component)).ToJson();

                // Create and configure pattern builder
                // NX2412: pass target directly — CreateComponentPatternBuilder(Component) signature
                dynamic builder = compAssembly.CreateComponentPatternBuilder(target);
                // FIXME: NX2412 — PatternType, Count, Direction may be on builder.PatternDefinition sub-object
                builder.PatternType = char.ToUpper(patternKey[0]) + patternKey.Substring(1);
                builder.Count = count;
                builder.Direction = dirKey;

                var resultData = new JObject();
                resultData["component"] = component;
                resultData["pattern_type"] = patternKey;
                resultData["count"] = count;
                resultData["direction"] = dirKey;

                if (patternKey == "linear")
                {
                    double? spacing = parameters["spacing"] != null && parameters["spacing"].Type != JTokenType.Null
                        ? (double?)parameters.Value<double>("spacing")
                        : null;
                    if (spacing == null)
                        return ToolResult.Fail("Linear patterns require a 'spacing' parameter.").ToJson();

                    // FIXME: NX2412 — Spacing may be on builder.PatternDefinition sub-object
                    builder.Spacing = spacing.Value;
                    resultData["spacing"] = spacing.Value;
                }
                else // circular
                {
                    string axis = AssemblyParams.GetParamString(parameters, "axis", "Z");
                    double angleSpan = AssemblyParams.GetParamDouble(parameters, "angle_span", 360.0);

                    string axisKey = axis.Trim().ToUpperInvariant();
                    if (!LinearDirections.ContainsKey(axisKey))
                    {
                        string valid = string.Join(", ", LinearDirections.Keys);
                        return ToolResult.Fail(string.Format("Invalid axis: '{0}'. Use one of: {1}.", axis, valid)).ToJson();
                    }

                    // FIXME: NX2412 — AxisVector and AngleSpan may be on builder.PatternDefinition sub-object
                    builder.AxisVector = axisKey;
                    builder.AngleSpan = angleSpan;

                    resultData["axis"] = axisKey;
                    resultData["angle_span"] = angleSpan;
                }

                builder.Commit();
                builder.Destroy();

                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Created {0} pattern of '{1}' ({2} instances).", patternKey, component, count),
                    Data = resultData
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_component_pattern failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 8. nx_interference_check
    // ========================================================================

    /// <summary>
    /// Check for interference/overlap between assembly components.
    /// Maps to ComponentAssembly.CheckInterference(compObjects).
    /// If the components array is empty, checks all components.
    /// Returns a list of interference pairs with overlap volume.
    /// </summary>
    /// Parameters:
    ///   components (array, optional) - components parameter.
    ///
    public class InterferenceCheckTool : IToolHandler
    {
        public string Name { get { return "nx_interference_check"; } }
        public string Description { get { return "Check for interference/overlap between assembly components."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic compAssembly = workPart.ComponentAssembly;
                JArray compNames = AssemblyParams.GetParamArray(parameters, "components");

                // Resolve component objects by name (GAP-08: 全树遍历, 子装配内件也按名可查)
                var compObjects = new List<dynamic>();
                List<ComponentTree.Node> allNodes = ComponentTree.Flatten(workPart as NXOpen.Part);
                if (compNames != null && compNames.Count > 0)
                {
                    var nameSet = new HashSet<string>();
                    foreach (var token in compNames)
                        nameSet.Add(token.Value<string>());

                    foreach (ComponentTree.Node node in allNodes)
                    {
                        if (nameSet.Contains(node.Name))
                            compObjects.Add(node.Component);
                    }
                }

                // Source: 实测验证 (2026-08-04) — NX2412 real API:
                //   AssemblyManager.CreateClearanceAnalysisBuilder(ClearanceSet) → ClearanceAnalysisBuilder
                //   ClearanceComputation enum: Local | Distributed (verified in KB)
                //   Flow: create builder → add objects to CollectionOneObjects → commit →
                //         result.PerformAnalysis → GetNumberOfInterferences → GetResults
                dynamic assemblyMgr = workPart.AssemblyManager;
                dynamic builder = assemblyMgr.CreateClearanceAnalysisBuilder(null);
                // Unique name required — NX2412 rejects duplicate ClearanceSet names
                try { builder.ClearanceSetName = "Interference_" + DateTime.Now.Ticks.ToString(); } catch { }
                try
                {
                    // Add components to collection one (checked against everything else)
                    if (compObjects.Count > 0)
                    {
                        foreach (dynamic comp in compObjects)
                        {
                            try { builder.CollectionOneObjects.Add(comp); } catch { }
                        }
                    }
                    else
                    {
                        // No components specified: check all components in full tree (GAP-08)
                        foreach (ComponentTree.Node node in allNodes)
                        {
                            try { builder.CollectionOneObjects.Add(node.Component); } catch { }
                        }
                    }

                    // Commit to get ClearanceSet result object
                    dynamic result = builder.Commit();
                    builder.Destroy();

                    var interferences = new JArray();
                    try
                    {
                        // Perform the analysis on the clearance set (0 = CustomerDefault)
                        result.PerformAnalysis(0);
                        int numInterferences = result.GetNumberOfInterferences();
                        if (numInterferences > 0)
                        {
                            var entry = new JObject();
                            entry["interference_count"] = numInterferences;
                            try { entry["checked_pairs"] = (int)result.GetResults().NumCheckedPairs; } catch { }
                            interferences.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        return ToolResult.Fail("nx_interference_check analysis: " + ex.Message).ToJson();
                    }

                    int count = interferences.Count;
                    var data = new JObject();
                    data["interferences"] = interferences;
                    data["count"] = count;
                    if (count == 0)
                        return new ToolResult { Success = true, Message = "No interference found.", Data = data }.ToJson();
                    return new ToolResult
                    {
                        Success = true,
                        Message = string.Format("Found {0} interference(s).", count),
                        Data = data
                    }.ToJson();
                }
                catch (Exception ex)
                {
                    try { builder.Destroy(); } catch { }
                    return ToolResult.Fail("nx_interference_check: " + ex.Message).ToJson();
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_interference_check failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 9. nx_explode_assembly  (GAP-13 升级, 2026-09-08)
    //    V2: layout=none|linear|nx_auto + scale 死参数修复
    //    探针实证 (docs/zhuangpei/_gap_test/GAP12-13-PROBES.md P-13):
    //      .NET Explosion 无逐件 SetTranslation → 唯一逐件通道 = UF
    //      UFAssem.ExplodeComponent(ex.Tag, comp.Tag, double[4,4]) — col-major 平移在最后一列 [0,3]/[1,3]/[2,3]
    //      (p13b 实测: row-major 失败→col-major 成功; PC/SUB2/SUB1 dz=40/80/120 全 OK)
    //      AutoExplodeAllComponents(bool) 现代版; 子装配不自动爆炸; 忽略小片体 (用户反馈 2026-09-08)
    //      显示层分离实证: 爆炸前后 GetPosition 全同 (ComponentTree 读模型位姿不受爆炸影响)
    // ========================================================================

    /// <summary>
    /// Create or modify an explode view of the assembly.
    /// layout: "none" (空爆炸) | "linear" (逐件线性外推, UF ExplodeComponent) | "nx_auto" (NX 自带算法)。
    /// ⚠️ 爆炸是显示层变换, 模型位姿不变 (nx_list_components 读模型位姿不受影响)。
    /// </summary>
    /// Parameters:
    ///   explode (boolean, optional) - true=创建/更新爆炸, false=还原并删除全部爆炸 (默认 true)
    ///   layout (string, optional) - none | linear | nx_auto (默认 none)
    ///   axis (string, optional) - linear 布局轴向 X/Y/Z (默认 Z)
    ///   spacing (number, optional) - linear 布局间距 mm (缺省 = 装配 bbox 最大尺寸×0.5/件数, 下限 20)
    ///   scale (number, optional) - 间距乘子 (修复: 旧版死参数读入未用)
    ///   components (array, optional) - 只对指定组件名布局 (缺省=全树)
    ///
    public class ExplodeAssemblyTool : IToolHandler
    {
        public string Name { get { return "nx_explode_assembly"; } }
        public string Description { get { return "Create or modify an explode view of the assembly. layout=none|linear|nx_auto (逐件线性外推 UF 实证); 显示层变换不影响模型位姿."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                bool explode = AssemblyParams.GetParamBool(parameters, "explode", true);
                string layout = AssemblyParams.GetParamString(parameters, "layout", "none");
                string axis = AssemblyParams.GetParamString(parameters, "axis", "Z");
                double spacing = AssemblyParams.GetParamDouble(parameters, "spacing", 0.0);
                double scale = AssemblyParams.GetParamDouble(parameters, "scale", 1.0);
                JArray compNames = AssemblyParams.GetParamArray(parameters, "components");

                if (!explode)
                {
                    // 还原 + 删除全部爆炸
                    // ★实证 (2026-09-08 dbg_explode_del): 视图使用中的爆炸 ex.Delete() 报
                    //   "给出的爆炸仍然在视图中使用" — 必须用 Explosion.DeleteExplosions(数组) 批量删除
                    int removed = 0;
                    NXOpen.Assemblies.ExplosionCollection explosions = workPart.ComponentAssembly.Explosions;
                    NXOpen.Assemblies.Explosion[] all = explosions.ToArray();
                    if (all != null && all.Length > 0)
                    {
                        foreach (NXOpen.Assemblies.Explosion ex in all)
                        {
                            try { ex.UnexplodeAllComponents(); } catch { }
                        }
                        try { removed = all[0].DeleteExplosions(all); }
                        catch (Exception de)
                        {
                            // 兜底: 逐件
                            foreach (NXOpen.Assemblies.Explosion ex in all)
                            {
                                try { ex.Delete(); removed++; } catch { }
                            }
                        }
                    }
                    var dataCol = new JObject();
                    dataCol["exploded"] = false;
                    dataCol["removed"] = removed;
                    dataCol["note"] = "爆炸为显示层, 模型位姿不变 (ComponentTree 读模型真实位姿)";
                    return new ToolResult { Success = true, Message = string.Format("Assembly collapsed ({0} explosion(s) removed).", removed), Data = dataCol }.ToJson();
                }

                string layoutKey = layout.Trim().ToLowerInvariant();
                if (layoutKey != "none" && layoutKey != "linear" && layoutKey != "nx_auto")
                    return ToolResult.Fail(string.Format("Invalid layout '{0}'. Use: none | linear | nx_auto.", layout)).ToJson();

                // 名称唯一化 (NX 拒绝重名爆炸)
                NXOpen.Assemblies.ExplosionCollection explosionsC = workPart.ComponentAssembly.Explosions;
                NXOpen.Assemblies.Explosion ex2 = null;
                string exName = "Explosion1";
                int suffix = 1;
                while (ex2 == null)
                {
                    try { ex2 = explosionsC.FindObject(exName); exName = "Explosion" + (++suffix); }
                    catch { ex2 = null; break; }
                }
                try
                {
                    ex2 = explosionsC.Create(exName);
                }
                catch (Exception e)
                {
                    return ToolResult.Fail("Create explosion failed: " + e.Message).ToJson();
                }

                var data = new JObject();
                data["exploded"] = true;
                data["layout"] = layoutKey;
                data["explosion_name"] = exName;
                data["scale"] = scale;
                data["note"] = "爆炸为显示层, 模型位姿不变 (nx_list_components 不受影响)";

                if (layoutKey == "nx_auto")
                {
                    // NX 自带算法 (AutoExplodeAllComponents 现代版; 子装配不自动爆炸; 忽略小片体)
                    int n = 0;
                    try { n = ex2.AutoExplodeAllComponents(true); }
                    catch (Exception e) { return ToolResult.Fail("AutoExplodeAllComponents: " + e.Message).ToJson(); }
                    data["auto_exploded_count"] = n;
                    data["warning"] = "NX 自动爆炸: 子装配不自动爆炸; 含小片体组件被忽略 (用户实证 2026-09-08); 要完整逐件布局请用 layout=linear";
                }
                else if (layoutKey == "linear")
                {
                    // 逐件线性外推: ComponentTree 全树 → 每件 bbox 中心 → 沿轴排序 → spacing 外推 → UF ExplodeComponent
                    var result = ApplyLinearLayout(workPart, ex2, axis, spacing, scale, compNames, exName);
                    if (result.Item1 != null) return result.Item1;
                    data["components_exploded"] = result.Item2.Count;
                    data["offsets"] = result.Item3;
                }

                try { ex2.Show(workPart.ModelingViews.WorkView); } catch { }
                return new ToolResult { Success = true, Message = string.Format("Assembly exploded (layout={0}, {1}).", layoutKey, exName), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_explode_assembly failed: {0}", ex.Message)).ToJson();
            }
        }

        // 逐件线性布局 (P-13 配方)
        // 返回: (errJObject, explodedList, offsetsJArray)
        private Tuple<JObject, List<string>, JArray> ApplyLinearLayout(
            NXOpen.Part workPart, NXOpen.Assemblies.Explosion ex, string axis, double spacing,
            double scale, JArray compNames, string exName)
        {
            var exploded = new List<string>();
            var offsets = new JArray();
            try
            {
                double[] axisVec = AxisVector(axis);
                if (axisVec == null)
                    return new Tuple<JObject, List<string>, JArray>(ToolResult.Fail("Invalid axis. Use X/Y/Z.").ToJson(), exploded, offsets);

                // 目标组件 (全树; 或按名过滤)
                List<ComponentTree.Node> nodes = ComponentTree.Flatten(workPart);
                var targets = new List<ComponentTree.Node>();
                if (compNames != null && compNames.Count > 0)
                {
                    var nameSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    foreach (var tok in compNames) nameSet.Add(tok.Value<string>());
                    foreach (ComponentTree.Node n in nodes)
                        if (nameSet.Contains(n.Name)) targets.Add(n);
                }
                else targets = nodes;

                if (targets.Count == 0)
                    return new Tuple<JObject, List<string>, JArray>(ToolResult.Fail("No target components for explode.").ToJson(), exploded, offsets);

                // bbox 中心 (UF_MODL_ask_bounding_box, 与 nx_inspect_topology 同源; 失败回退 GetPosition)
                var centers = new System.Collections.Generic.List<double[]>();
                double[] minB = { double.MaxValue, double.MaxValue, double.MaxValue };
                double[] maxB = { double.MinValue, double.MinValue, double.MinValue };
                var ufs = NXOpen.UF.UFSession.GetUFSession();
                foreach (ComponentTree.Node n in targets)
                {
                    double[] center = { 0, 0, 0 };
                    try
                    {
                        double[] bb = new double[6];
                        ufs.Modl.AskBoundingBox(n.Component.Tag, bb);
                        center[0] = (bb[0] + bb[3]) / 2; center[1] = (bb[1] + bb[4]) / 2; center[2] = (bb[2] + bb[5]) / 2;
                        for (int i = 0; i < 3; i++)
                        {
                            if (bb[i] < minB[i]) minB[i] = bb[i];
                            if (bb[i + 3] > maxB[i]) maxB[i] = bb[i + 3];
                        }
                    }
                    catch
                    {
                        center[0] = n.Ox; center[1] = n.Oy; center[2] = n.Oz;   // 回退: 组件原点
                    }
                    centers.Add(center);
                }

                // 间距: 显式 spacing → 乘 scale; 缺省 = 装配 bbox 最大尺寸×0.5/件数, 下限 20
                double effSpacing = spacing;
                if (effSpacing <= 0)
                {
                    double span = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        double s = maxB[i] - minB[i];
                        if (s > span) span = s;
                    }
                    effSpacing = Math.Max(20.0, span * 0.5 / Math.Max(1, targets.Count));
                }
                effSpacing *= scale;   // ★scale 死参数修复: 现在作为间距乘子真实生效

                // 沿轴坐标排序 (层级优先: level 越深偏移越大; 同层按轴坐标)
                var ranks = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<ComponentTree.Node, double>>();
                for (int i = 0; i < targets.Count; i++)
                {
                    double coord = centers[i][0] * axisVec[0] + centers[i][1] * axisVec[1] + centers[i][2] * axisVec[2];
                    ranks.Add(new System.Collections.Generic.KeyValuePair<ComponentTree.Node, double>(targets[i], coord));
                }
                ranks.Sort((a, b) =>
                {
                    int lc = a.Key.Level.CompareTo(b.Key.Level);
                    if (lc != 0) return lc;
                    return a.Value.CompareTo(b.Value);
                });

                // 逐件设置爆炸变换 (col-major: 平移在最后一列 [0,3]/[1,3]/[2,3])
                double rankIdx = 0;
                foreach (var kv in ranks)
                {
                    rankIdx++;
                    double d = effSpacing * rankIdx;
                    double[,] tr = new double[4, 4];
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) tr[r, c] = (r == c) ? 1.0 : 0.0;
                    tr[0, 3] = axisVec[0] * d;
                    tr[1, 3] = axisVec[1] * d;
                    tr[2, 3] = axisVec[2] * d;
                    try
                    {
                        ufs.Assem.ExplodeComponent(ex.Tag, kv.Key.Component.Tag, tr);
                        exploded.Add(kv.Key.Name);
                        var o = new JObject();
                        o["component"] = kv.Key.Name;
                        o["level"] = kv.Key.Level;
                        o["offset"] = new JArray(tr[0, 3], tr[1, 3], tr[2, 3]);
                        offsets.Add(o);
                    }
                    catch (Exception e)
                    {
                        var err = new JObject();
                        err["component"] = kv.Key.Name;
                        err["error"] = e.Message;
                        offsets.Add(err);
                    }
                }
                return new Tuple<JObject, List<string>, JArray>(null, exploded, offsets);
            }
            catch (Exception e)
            {
                return new Tuple<JObject, List<string>, JArray>(ToolResult.Fail("linear layout: " + e.Message).ToJson(), exploded, offsets);
            }
        }

        private static double[] AxisVector(string axis)
        {
            string a = axis.Trim().ToUpperInvariant();
            if (a == "X") return new double[] { 1, 0, 0 };
            if (a == "Y") return new double[] { 0, 1, 0 };
            if (a == "Z") return new double[] { 0, 0, 1 };
            return null;
        }
    }

    // ========================================================================
    // 10. nx_asm_constraints  (Eye-TODO A2 V3, 2026-09-05)
    //     V3 读回: 逐组件 GetConstraints() → 类型/对齐/SolverStatus/两端引用
    //     API 实证: 实测查询 (ComponentConstraint→Constraint; GetConstraintStatus→
    //     Constraint.SolverStatus 19 成员; GetReferences→GetGeometry) + matprobe_v10_drop_cons
    //     设计: 同约束在两端组件重复列出 = F5 排错信号 (约束对象挂载件可见), 不去重
    // ========================================================================
    public class AsmConstraintsTool : IToolHandler
    {
        public string Name { get { return "nx_asm_constraints"; } }
        public string Description { get { return "List assembly constraints per component: type, alignment, solver status and geometry references."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                NXOpen.Assemblies.ComponentAssembly compAssembly = workPart.ComponentAssembly;
                NXOpen.Assemblies.Component rootComponent = compAssembly.RootComponent;

                var constraintsList = new JArray();
                if (rootComponent != null)
                {
                    foreach (NXOpen.Assemblies.Component comp in rootComponent.GetChildren())
                    {
                        NXOpen.Positioning.ComponentConstraint[] cons = comp.GetConstraints();
                        if (cons == null || cons.Length == 0) continue;
                        foreach (NXOpen.Positioning.ComponentConstraint con in cons)
                        {
                            var entry = new JObject();
                            entry["owner_component"] = comp.Name;
                            try { entry["tag"] = (long)con.Tag; } catch (Exception ex) { entry["tag"] = "ERR:" + ex.Message; }
                            try { entry["journal_id"] = con.JournalIdentifier; }
                            catch (Exception ex) { entry["journal_id"] = "ERR:" + ex.Message; }
                            try { entry["type"] = con.ConstraintType.ToString(); }
                            catch (Exception ex) { entry["type"] = "ERR:" + ex.Message; }
                            try { entry["alignment"] = con.ConstraintAlignment.ToString(); }
                            catch (Exception ex) { entry["alignment"] = "ERR:" + ex.Message; }
                            try { entry["status"] = con.GetConstraintStatus().ToString(); }
                            catch (Exception ex) { entry["status"] = "ERR:" + ex.Message; }
                            try { entry["suppressed"] = con.Suppressed; }
                            catch (Exception ex) { entry["suppressed"] = "ERR:" + ex.Message; }

                            // 两端几何引用
                            try
                            {
                                var refs = new JArray();
                                NXOpen.Positioning.ConstraintReference[] crs = con.GetReferences();
                                if (crs != null)
                                {
                                    foreach (NXOpen.Positioning.ConstraintReference cr in crs)
                                    {
                                        var r = new JObject();
                                        // Order 返回 ConstraintReference.ConstraintOrder 枚举 (编译实证, 非 int)
                                        try { r["order"] = cr.Order.ToString(); } catch { }
                                        try { r["solver_geometry"] = cr.SolverGeometryType.ToString(); } catch { }
                                        try
                                        {
                                            NXOpen.NXObject geo = cr.GetGeometry();
                                            r["geom_type"] = geo != null ? geo.GetType().Name : null;
                                            if (geo != null) r["geom_tag"] = (long)geo.Tag;
                                        }
                                        catch (Exception gex) { r["geom_error"] = gex.Message; }
                                        refs.Add(r);
                                    }
                                }
                                entry["references"] = refs;
                            }
                            catch (Exception rex) { entry["references_error"] = rex.Message; }

                            constraintsList.Add(entry);
                        }
                    }
                }

                int count = constraintsList.Count;
                var data = new JObject();
                data["constraints"] = constraintsList;
                data["count"] = count;
                return new ToolResult { Success = true, Message = string.Format("Assembly has {0} constraint record(s) across components.", count), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_asm_constraints failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 10. nx_bom_extract (GAP-07, 2026-09-07)
    // ========================================================================

    /// <summary>
    /// Extract BOM data (part name x quantity x level) from the full assembly tree.
    /// 数据侧 BOM (非图纸表格): 数量 = occ 实例总数 (NX BOM 标准, 含子装配内重复引用)。
    /// 底座: ComponentTree.BuildBom (全树扁平按原型归并)。
    /// </summary>
    /// Parameters:
    ///   tree (boolean, optional) - 同时输出嵌套层级树 (默认 false).
    ///
    public class BomExtractTool : IToolHandler
    {
        public string Name { get { return "nx_bom_extract"; } }
        public string Description { get { return "Extract BOM data (part name x quantity x level) from the full assembly tree."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                bool withTree = AssemblyParams.GetParamBool(parameters, "tree", false);

                Dictionary<string, ComponentTree.BomRow> rows = ComponentTree.BuildBom(workPart);
                var bom = new JArray();
                int totalInstances = 0;
                foreach (KeyValuePair<string, ComponentTree.BomRow> kv in rows)
                {
                    ComponentTree.BomRow r = kv.Value;
                    totalInstances += r.Quantity;
                    var o = new JObject();
                    o["part_name"] = r.PartName;
                    o["full_path"] = r.FullPath;
                    o["quantity"] = r.Quantity;
                    o["suppressed_count"] = r.SuppressedCount;
                    o["level"] = r.Level;
                    o["reference_set"] = r.ReferenceSet;
                    var paths = new JArray();
                    foreach (string p in r.InstancePaths) paths.Add(p);
                    o["instance_paths"] = paths;
                    bom.Add(o);
                }

                var data = new JObject();
                data["bom"] = bom;
                data["count"] = bom.Count;
                data["total_instances"] = totalInstances;
                if (withTree)
                {
                    var tree = new JArray();
                    foreach (ComponentTree.Node root in ComponentTree.Hierarchy(workPart))
                        tree.Add(ComponentTree.NodeToJson(root, true));
                    data["tree"] = tree;
                }
                return new ToolResult { Success = true, Message = string.Format("BOM extracted: {0} unique part(s), {1} instance(s).", bom.Count, totalInstances), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_bom_extract failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}