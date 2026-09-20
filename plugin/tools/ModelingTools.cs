using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;
using NxMcpPlugin.Features;

namespace NxMcpPlugin.Tools.Modeling
{
    // ========================================================================
    // Modeling-specific helpers (param extraction via ToolHelpers static import)
    // ========================================================================

    /// <summary>
    /// Modeling-specific helpers for direction vectors, boolean mapping, and object resolution.
    /// General param extraction uses ToolHelpers from parent namespace (imported via using static).
    /// </summary>
    internal static class ModelingHelpers
    {
        /// <summary>
        /// Resolve a cardinal direction string to an NXOpen Vector3d.
        /// Valid values: X, Y, Z, -X, -Y, -Z.
        /// </summary>
        public static dynamic GetDirectionVector(dynamic session, string direction)
        {
            string key = direction.Trim().ToUpper();
            double dx = 0, dy = 0, dz = 0;
            switch (key)
            {
                case "X":  dx = 1.0; break;
                case "Y":  dy = 1.0; break;
                case "Z":  dz = 1.0; break;
                case "-X": dx = -1.0; break;
                case "-Y": dy = -1.0; break;
                case "-Z": dz = -1.0; break;
                default:
                    throw new ArgumentException(
                        string.Format("Invalid direction '{0}'. Use one of: X, Y, Z, -X, -Y, -Z", direction));
            }
            return new NXOpen.Vector3d(dx, dy, dz);
        }

        /// <summary>
        /// 返回与给定方向垂直的方向 (用于 linear 2D 阵列的 Y 方向缺省值)。
        /// X→Y, Y→X, Z→X; 负方向取同轴正方向。
        /// </summary>
        public static string PerpendicularDirection(string direction)
        {
            string key = (direction ?? "Z").Trim().ToUpper().Replace("-", "");
            switch (key)
            {
                case "X": return "Y";
                case "Y": return "X";
                default: return "X"; // Z or anything else → X
            }
        }

        /// <summary>
        /// Map a boolean string to NXOpen BooleanType or null for "none".
        /// NX2412: BooleanType lives under GeometricUtilities.BooleanOperation.BooleanType.
        /// </summary>
        public static object GetBooleanType(string boolean)
        {
            string key = boolean.Trim().ToLower();
            if (key == "none") return null;

            switch (key)
            {
                case "unite":
                    return NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Unite;
                case "subtract":
                    return NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
                case "intersect":
                    return NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Intersect;
                default:
                    throw new ArgumentException(
                        string.Format("Invalid boolean type '{0}'. Use: none, unite, subtract, intersect", boolean));
            }
        }

        /// <summary>Resolve a curve/sketch/feature by name from sketches, curves or features in the work part.</summary>
        /// W6 (2026-09-03, A6): 增加 features 搜索 — nx_helix 输出是 HELIX feature (命名后
        /// "HELIXn"), 不在 Sketches/Curves 集合里, tube/sweep 要按名引用必须先能解析到 feature。
        public static dynamic ResolveObjectByName(dynamic workPart, string name)
        {
            if (string.IsNullOrEmpty(name) || workPart == null) return null;
            // Search sketches
            foreach (dynamic sketch in workPart.Sketches)
            {
                if (sketch != null && (sketch.Name == name || sketch.Name.EndsWith(name)))
                    return sketch;
            }
            // Search curves
            foreach (dynamic curve in workPart.Curves)
            {
                if (curve != null && (curve.Name == name || curve.Name.EndsWith(name)))
                    return curve;
            }
            // Search features (by Name, and by journal_id "TYPE(timestamp)" form)
            try
            {
                foreach (dynamic feat in workPart.Features)
                {
                    if (feat == null) continue;
                    try
                    {
                        if (feat.Name == name || feat.Name.EndsWith(name))
                            return feat;
                    }
                    catch { }
                    try
                    {
                        string jid = feat.FeatureType.ToString() + "(" + feat.Timestamp + ")";
                        if (jid == name) return feat;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Create a NXOpen Direction from origin + direction vector.
        /// NX2412: Direction creation needs a Point object, not work_part.Origin.
        /// </summary>
        public static dynamic CreateDirection(dynamic workPart, double[] origin, double[] direction)
        {
            // NX2412: 直接用 Point3d，不要用 Points.CreatePoint()
            dynamic pt3d = new NXOpen.Point3d(origin[0], origin[1], origin[2]);
            dynamic vec = new NXOpen.Vector3d(direction[0], direction[1], direction[2]);
            return workPart.Directions.CreateDirection(pt3d, vec, 0);
        }

        /// <summary>
        /// Fill a builder-owned Section (read-only getter) with curves from a curve/sketch object.
        /// NX2412 (probe 2026-08-17): SweptBuilder.SectionList/GuideList, LoftBuilder SectionsList
        /// entries are read-only Sections — AddToSection fills them. Pattern mirrors ExtrudeTool/SectionHelper.
        /// </summary>
        public static bool FillSection(dynamic section, dynamic workPart, dynamic obj)
        {
            try
            {
                NXOpen.IBaseCurve iBase = null;
                NXOpen.Curve curveObj = null;
                // L1/L2: 直接 IBaseCurve / Curve
                try { iBase = (NXOpen.IBaseCurve)obj; } catch { }
                if (iBase == null) try { curveObj = (NXOpen.Curve)obj; } catch { }

                // L3: sketch → 全部几何曲线 (一次 AddToSection 数组, 保留闭合链语义);
                // ⚠️ Feature 无 GetAllGeometry → 抛异常 → 不得 return false, 须落入 L4
                if (iBase == null && curveObj == null)
                {
                    bool l3Done = false;
                    try
                    {
                        dynamic sc = obj.GetAllGeometry();
                        var iBaseArr = new System.Collections.Generic.List<NXOpen.IBaseCurve>();
                        foreach (dynamic c in sc)
                        {
                            try { iBaseArr.Add((NXOpen.IBaseCurve)c); } catch { }
                        }
                        if (iBaseArr.Count > 0)
                        {
                            dynamic rule = workPart.ScRuleFactory.CreateRuleBaseCurveDumb(iBaseArr.ToArray());
                            NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule };
                            NXOpen.Point3d hp = new NXOpen.Point3d(0.0, 0.0, 0.0);
                            section.AddToSection(selRules, (NXOpen.NXObject)iBaseArr[0], null, null, hp, 0, false);
                            l3Done = true;
                        }
                    }
                    catch { }
                    if (l3Done) return true;
                    // 落入 L4 (Feature → GetEntities)
                }

                // L4: Feature (HELIXn 等曲线特征) → GetEntities() 实体 (2026-09-12 M0 case09 补螺纹)
                if (iBase == null && curveObj == null && obj is NXOpen.Features.Feature)
                {
                    try
                    {
                        var entities = ((NXOpen.Features.Feature)obj).GetEntities();
                        if (entities != null)
                        {
                            foreach (NXOpen.NXObject ent in entities)
                            {
                                if (ent == null) continue;
                                try { var c = ent as NXOpen.Curve; if (c != null) { curveObj = c; break; } } catch { }
                                try { var ib = ent as NXOpen.IBaseCurve; if (ib != null) { iBase = ib; break; } } catch { }
                            }
                        }
                    }
                    catch { }
                }
                if (iBase == null && curveObj == null) return false;

                NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                if (iBase != null)
                {
                    dynamic rule = workPart.ScRuleFactory.CreateRuleBaseCurveDumb(new NXOpen.IBaseCurve[] { iBase });
                    NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule };
                    section.AddToSection(selRules, (NXOpen.NXObject)iBase, null, null, helpPoint, 0, false);
                }
                else
                {
                    dynamic rule = workPart.ScRuleFactory.CreateRuleCurveDumb(new NXOpen.Curve[] { curveObj });
                    NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule };
                    section.AddToSection(selRules, curveObj, null, null, helpPoint, 0, false);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Find a Face by name across all bodies in the work part.</summary>
        public static dynamic FindFaceByName(dynamic workPart, string name)
        {
            if (string.IsNullOrEmpty(name) || workPart == null) return null;
            try
            {
                foreach (dynamic body in workPart.Bodies)
                {
                    if (body == null) continue;
                    dynamic faces = body.GetFaces();
                    if (faces == null) continue;
                    foreach (dynamic face in faces)
                    {
                        if (face != null && face.Name == name)
                            return face;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // ========================================================================
    // 1. nx_extrude
    // ========================================================================

    /// <summary>
    /// Extrude a section or sketch by a given distance.
    ///
    /// Parameters:
    ///   distance        (number, required) -- END limit value (Limits.EndExtend, absolute from section plane; NOT extrude length). Default 10.
    ///   direction       (string, optional) -- X, Y, Z, -X, -Y, -Z (default Z)
    ///   boolean         (string, optional) -- none, unite, subtract, intersect
    ///   sketch_name     (string, optional) -- Name of the sketch to extrude (falls back to most recent)
    ///   start_distance  (number, optional) -- START limit value (Limits.StartExtend, absolute from section plane). Default 0.
    ///   end_condition   (string, optional) -- value, until_next, through_all, until_selected (default value)
    ///   target          (string, optional) -- boolean 目标体: body 名 / journal_id / tag。多 body 时必须显式指定，否则报错（不再盲选第一个）
    ///
    /// Returns:
    ///   feature     (string) -- JournalIdentifier like "EXTRUDE(2)" (NX2412 Name is empty; use as reference)
    ///   body        (string) -- JournalIdentifier of the created body (empty if boolean=subtract removed it)
    ///   journal_id  (string) -- alias of feature
    /// </summary>
    /// <summary>
    /// M0-20260914 A2: 区域选择 —— 多区域并进同一个 section。
    ///
    /// ★ 配方**逐行照抄人类 journal**（`m0-census/c750_utf8.vb` L2735-3047，MODEL_4 同款）：
    ///   section3 = workPart.Sections.CreateSection(...)
    ///   extrudeBuilder1.Section = section3          ← 一个 builder
    ///   for each region:
    ///     regionBoundaryRuleN = CreateRuleRegionBoundary(sketch1, curvesN, seedPointN, 0.01)
    ///     section3.AllowSelfIntersection(True)      ← 每次 add 前都设
    ///     section3.AddToSection(rulesN, sketch1, null, null, helpPointN, Section.Mode.Create, False)
    ///   一次 commit → 一个特征 → 1 个体
    /// 实测该案例 4 个 region 并集（MODEL_4 是 5 个），不是各自拉伸。
    ///
    /// 数据依据：641 个 region / 128 案例中 64 案例（50%）在同一组曲线上用多个种子；
    /// 其中 14 案例「种子数 &gt; 特征数」= 真需并集（其余各自单独拉伸即可）。
    /// </summary>
    internal static class RegionSelect
    {
        /// <summary>区域诊断消息累加 (不覆盖)。实测直接赋值会让 not-found 被随后的"缺少截面线串"吞掉。</summary>
        public static string Err(string prev, string msg)
        {
            return string.IsNullOrEmpty(prev) ? msg : prev + " | " + msg;
        }

        /// <summary>解析 "x,y,z" 单个种子；失败返回 false。</summary>
        public static bool ParseSeed(string s, out NXOpen.Point3d p)
        {
            p = new NXOpen.Point3d(0, 0, 0);
            if (string.IsNullOrEmpty(s)) return false;
            string[] xyz = s.Split(',');
            if (xyz.Length < 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double x, y, z;
            if (!double.TryParse(xyz[0].Trim(), System.Globalization.NumberStyles.Float, ci, out x)) return false;
            if (!double.TryParse(xyz[1].Trim(), System.Globalization.NumberStyles.Float, ci, out y)) return false;
            if (!double.TryParse(xyz[2].Trim(), System.Globalization.NumberStyles.Float, ci, out z)) return false;
            p = new NXOpen.Point3d(x, y, z);
            return true;
        }

        /// <summary>
        /// 收集种子列表。`seed_points`（分号分隔多种子）优先；否则回退 `seed_point`（单种子）。
        /// 两者都空 → 返回空列表（调用方走非区域路径）。
        /// </summary>
        public static List<NXOpen.Point3d> ParseSeeds(string seedPointsStr, string seedPointStr, ref string error)
        {
            var seeds = new List<NXOpen.Point3d>();
            string src = !string.IsNullOrEmpty(seedPointsStr) ? seedPointsStr : seedPointStr;
            if (string.IsNullOrEmpty(src)) return seeds;
            string[] toks = src.Split(';');
            for (int i = 0; i < toks.Length; i++)
            {
                string t = toks[i].Trim();
                if (t.Length == 0) continue;
                NXOpen.Point3d p;
                if (ParseSeed(t, out p)) seeds.Add(p);
                else error = Err(error, "seed-parse-failed:" + t);
            }
            return seeds;
        }

        /// <summary>
        /// 把 region 规则加进 section —— 每个种子各一条规则，全部 Mode.Create，
        /// 每次 add 前 AllowSelfIntersection(True)（照抄 journal）。
        /// 返回 true 表示至少加了一条。
        /// </summary>
        public static bool Apply(dynamic workPart, dynamic sketch, dynamic section,
            List<dynamic> curveList, string seedPointsStr, string seedPointStr,
            string curveNamesStr, string curveIdxStr,
            out string note, out string error)
        {
            note = null; error = null;
            // 诊断回显用实际生效的种子串 (单种子时 seedPointsStr 为 null, 回显 "seed=()" 是 bug)
            string seedSrc = !string.IsNullOrEmpty(seedPointsStr) ? seedPointsStr : seedPointStr;
            var seeds = ParseSeeds(seedPointsStr, seedPointStr, ref error);
            if (seeds.Count == 0) return false;
            if (curveList == null || curveList.Count == 0) return false;

            var iCurves = new List<NXOpen.ICurve>();
            int castFail = 0;
            foreach (dynamic c in curveList)
            {
                // 不吞异常: DLR 绑定失败要计数上报，否则区域静默少曲线
                try { iCurves.Add((NXOpen.ICurve)c); } catch { castFail++; }
            }

            // A8: 按名选 (优先, 不受顺序影响)
            if (!string.IsNullOrEmpty(curveNamesStr))
            {
                var wanted = new List<string>();
                foreach (string tok in curveNamesStr.Split(','))
                {
                    string t = tok.Trim();
                    if (t.Length > 0) wanted.Add(t.ToLowerInvariant());
                }
                var picked = new List<NXOpen.ICurve>();
                var missing = new List<string>();
                foreach (string w in wanted)
                {
                    bool hit = false;
                    foreach (NXOpen.ICurve c in iCurves)
                    {
                        string nm = null;
                        try { nm = ((NXOpen.NXObject)c).Name; } catch { }
                        if (nm != null && nm.Trim().ToLowerInvariant() == w)
                        { picked.Add(c); hit = true; break; }
                    }
                    if (!hit) missing.Add(w);
                }
                if (picked.Count > 0) iCurves = picked;
                if (missing.Count > 0)
                    error = Err(error, "curve_names-not-found:" + string.Join(" ", missing.ToArray()));
            }
            // A8: 按 1-based 索引选 (按 curveList 顺序)
            else if (!string.IsNullOrEmpty(curveIdxStr))
            {
                int total = iCurves.Count;
                var picked = new List<NXOpen.ICurve>();
                int bad = 0;
                foreach (string tok in curveIdxStr.Split(','))
                {
                    string t = tok.Trim();
                    if (t.Length == 0) continue;
                    int idx;
                    if (!int.TryParse(t, out idx)) { bad++; continue; }
                    if (idx >= 1 && idx <= total) picked.Add(iCurves[idx - 1]); else bad++;
                }
                if (picked.Count > 0) iCurves = picked;
                if (bad > 0) error = Err(error, "curve_indices-out-of-range:" + bad + "/" + total);
            }

            if (iCurves.Count == 0) return false;

            int applied = 0;
            string aSIWarn = null;
            foreach (NXOpen.Point3d seed in seeds)
            {
                try
                {
                    dynamic rbOpts = workPart.ScRuleFactory.CreateRuleOptions();
                    rbOpts.SetSelectedFromInactive(false);
                    // 签名(DB 实测): CreateRuleRegionBoundary(DisplayableObject seedObj /*face 或 sketch*/,
                    //   ICurve[] curves, Point3d seedPoint, double distanceTolerance, SelectionIntentRuleOptions)
                    dynamic rbRule = workPart.ScRuleFactory.CreateRuleRegionBoundary(
                        (NXOpen.DisplayableObject)sketch, iCurves.ToArray(), seed, 0.01,
                        (NXOpen.SelectionIntentRuleOptions)rbOpts);

                    NXOpen.SelectionIntentRule[] rbRules = new NXOpen.SelectionIntentRule[]
                        { (NXOpen.SelectionIntentRule)rbRule };
                    NXOpen.NXObject nullObj = null;
                    // journal L2828/2886/2949/3042: 每次 AddToSection 前都 AllowSelfIntersection(True)
                    // (注意: 是 Section 上的 AllowSelfIntersection, 与 Builder.AllowSelfIntersectingSection 是不同 API)
                    // 不静默吞: 名字/绑定不对要留痕, 否则多区域并集失败时无从排查
                    try { section.AllowSelfIntersection(true); }
                    catch (Exception asx) { aSIWarn = "allowSelfIntersection-failed:" + asx.Message; }
                    // 区域规则: seed 传 null、helpPoint 传种子点 (journal L2833 同款, Mode 恒为 Create)
                    section.AddToSection(rbRules, nullObj, nullObj, nullObj, seed,
                        NXOpen.Section.Mode.Create, false);
                    applied++;
                }
                catch (Exception rex)
                {
                    error = Err(error, "seed#(" + seed.X + "," + seed.Y + "," + seed.Z + "):" + rex.Message);
                }
            }

            if (applied == 0) return false;
            note = (seeds.Count > 1 ? "seeds=" + applied + "/" + seeds.Count : "seed=("
                    + seedSrc + ")")
                + " curves=" + iCurves.Count
                + (castFail > 0 ? " castFail=" + castFail : "")
                + (aSIWarn != null ? " " + aSIWarn : "");
            return true;
        }
    }

    public class ExtrudeTool : IToolHandler
    {
        public string Name { get { return "nx_extrude"; } }
        public string Description { get { return "Extrude a section or sketch by a given distance. Optional seed_point='x,y,z' selects ONE region of a multi-loop sketch (region boundary rule) instead of extruding every closed loop into separate bodies. Optional seed_points='x,y,z;x,y,z' unions SEVERAL regions into ONE section/feature (the human pattern in 50% of multi-region journals — one builder, one section, N region rules, Mode.Create each). Optional curve_indices='1,3,5' restricts the region boundary to that 1-based subset of the sketch's curves. Optional curve_names='Arc9,Line3' does the same by curve NAME (preferred — names survive replay, positions do not)."; } }

        // 区域诊断消息累加 (不覆盖) —— M0-20260914 A8。
        // 实测: 直接赋值会让 "curve_names-not-found" 被随后的 "缺少截面线串" 吞掉, 用户看不到真因。
        static string RegionErr(string prev, string msg)
        {
            return string.IsNullOrEmpty(prev) ? msg : prev + " | " + msg;
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string sketchName = ToolHelpers.GetString(parameters, "sketch_name", null);
                double distance = ToolHelpers.GetDouble(parameters, "distance", 10.0);
                string direction = ToolHelpers.GetString(parameters, "direction", "Z");
                string boolean = ToolHelpers.GetString(parameters, "boolean", "none");
                // 区域诊断消息累加器 —— 名字对不上 / 索引越界 / 区域规则抛错 三类原因都要留痕,
                // 早期直接赋值会让后一个原因把前一个覆盖掉 (实测 not-found 被 "缺少截面线串" 吞掉)。
                // (helper 定义见类尾 RegionErr)
                double startDistance = ToolHelpers.GetDouble(parameters, "start_distance", 0.0);
                string endCondition = ToolHelpers.GetString(parameters, "end_condition", "value");
                string target = ToolHelpers.GetString(parameters, "target", null);
                // M0-20260914: 区域选择 — 96.6% 人造 journal 走"曲线汤 + 种子点"圈区域
                string seedPointStr = ToolHelpers.GetString(parameters, "seed_point", null);
                // M0-20260914 A8: 区域曲线子集 — 人类原 region 数组常只取草图的一部分曲线。
                // 传全部曲线会把区域切碎(多出的同心圆/构造线)。1-based 索引, 逗号分隔, 按草图曲线顺序。
                string curveIdxStr = ToolHelpers.GetString(parameters, "curve_indices", null);
                // M0-20260914 A8b: 按名选曲线 — .prt 保留 NX 自动名 (Arc9/Line3), 与人类 journal
                // region 数组里的变量名同号。名字是稳定的, 而"新草图 GetAllGeometry 的序号"不稳定
                // (实测同一子集换顺序就报"输入截面无效")。curve_names 优先于 curve_indices。
                string curveNamesStr = ToolHelpers.GetString(parameters, "curve_names", null);
                // M0-20260914 A2: 多区域**并集** — 人类会把同一草图上的多个 region 并进同一个 section
                // 再一次性拉伸（journal c750_utf8.vb L2735-3047: 一个 builder + 一个 section + 4 条 region rule）。
                // 分号分隔多个种子: "x,y,z;x,y,z"。给了 seed_points 就忽略 seed_point。
                // 数据依据: 641 region / 128 案例中 64 案例(50%)在同一组曲线上用多种子。
                string seedPointsStr = ToolHelpers.GetString(parameters, "seed_points", null);

                // Resolve sketch by name, or use most recent
                dynamic sketch = null;
                if (!string.IsNullOrEmpty(sketchName))
                {
                    foreach (dynamic sk in workPart.Sketches)
                    {
                        if (sk != null && (sk.Name == sketchName || sk.Name.EndsWith(sketchName)))
                        {
                            sketch = sk;
                            break;
                        }
                    }
                    if (sketch == null)
                        return ToolHelpers.Fail(string.Format("Sketch '{0}' not found.", sketchName));
                }
                else
                {
                    foreach (dynamic sk in workPart.Sketches) { sketch = sk; }
                }

                dynamic dirVec = ModelingHelpers.GetDirectionVector(session, direction);
                object boolType = ModelingHelpers.GetBooleanType(boolean);

                // W2 (2026-09-03): 语义名 — NX undo 列表/GUI 按此标识 (ASCII only)
                using (var mark = new UndoMarkScope(session,
                    "Extrude@" + (boolean != "none" ? boolean.ToLowerInvariant() + "-" : "") + "d" + distance))
                {
                    dynamic builder = workPart.Features.CreateExtrudeBuilder(null);
                    try
                    {
                        bool regionApplied = false;   // M0-20260914
                        string regionNote = null;
                        string regionError = null;

                        // NX2412: Use Section and Limits instead of SetDistance/SetDirection
                        if (sketch != null)
                        {
                            dynamic section = workPart.Sections.CreateSection(0.00095, 0.001, 0.01);
                            builder.Section = section;
                            builder.AllowSelfIntersectingSection(true);

                            // 获取草图中的所有曲线
                            var curveList = new System.Collections.Generic.List<dynamic>();
                            try
                            {
                                dynamic sketchCurves = sketch.GetAllGeometry();
                                foreach (dynamic curve in sketchCurves)
                                {
                                    if (curve != null) curveList.Add(curve);
                                }
                            }
                            catch { }

                            // ---- M0-20260914 A2 区域选择 (多区域并集) ----
                            // 人造 journal 96.6% (85/88) 用"先画曲线汤 → CreateRuleRegionBoundary 圈目标区域"。
                            // 旧实现走 GetAllGeometry 全曲线 → 多闭合环草图全部区域一起拉伸 → 多体。
                            // 配方统一在 RegionSelect.Apply（与 RevolveTool 共用一份实现，防两处漂移）。
                            if (curveList.Count > 0
                                && (!string.IsNullOrEmpty(seedPointsStr) || !string.IsNullOrEmpty(seedPointStr)))
                            {
                                string rNote, rErr;
                                regionApplied = RegionSelect.Apply(workPart, sketch, section, curveList,
                                    seedPointsStr, seedPointStr, curveNamesStr, curveIdxStr,
                                    out rNote, out rErr);
                                if (regionApplied) regionNote = rNote;
                                regionError = RegionErr(regionError, rErr);
                            }

                            if (!regionApplied && curveList.Count > 0)
                            {
                                // NX2412: CreateRuleBaseCurveDumb 需要 IBaseCurve[] 类型
                                // 将曲线列表转换为 IBaseCurve 数组
                                var iBaseCurves = new System.Collections.Generic.List<NXOpen.IBaseCurve>();
                                foreach (dynamic curve in curveList)
                                {
                                    try { iBaseCurves.Add((NXOpen.IBaseCurve)curve); }
                                    catch { }
                                }

                                if (iBaseCurves.Count > 0)
                                {
                                    // NX2412: 使用曲线数组一次性创建规则并添加到截面
                                    NXOpen.IBaseCurve[] curveArray = iBaseCurves.ToArray();
                                    try
                                    {
                                        // NX2412: CreateRuleBaseCurveDumb 返回 CurveDumbRule
                                        // AddToSection 需要 SelectionIntentRule[] — 需要显式转换
                                        dynamic rule = workPart.ScRuleFactory.CreateRuleBaseCurveDumb(curveArray);
                                        NXOpen.SelectionIntentRule selRule = (NXOpen.SelectionIntentRule)rule;
                                        NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { selRule };

                                        NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                                        NXOpen.NXObject refObj = (NXOpen.NXObject)curveArray[0];
                                        section.AddToSection(selRules, refObj, null, null,
                                            helpPoint, 0, false);
                                    }
                                    catch
                                    {
                                        // 回退: 逐条添加
                                        NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                                        foreach (NXOpen.IBaseCurve iCurve in iBaseCurves)
                                        {
                                            try
                                            {
                                                NXOpen.NXObject nObj = (NXOpen.NXObject)iCurve;
                                                dynamic singleRule = workPart.ScRuleFactory.CreateRuleBaseCurveDumb(
                                                    new NXOpen.IBaseCurve[] { iCurve });
                                                NXOpen.SelectionIntentRule singleSelRule = (NXOpen.SelectionIntentRule)singleRule;
                                                NXOpen.SelectionIntentRule[] singleSelRules = new NXOpen.SelectionIntentRule[] { singleSelRule };
                                                section.AddToSection(singleSelRules, nObj, null, null,
                                                    helpPoint, 0, false);
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }

                        // NX2412: Direction creation needs a Point object
                        // 必须设置方向，否则 NX 报 "未选择轴"
                        builder.Direction = ModelingHelpers.CreateDirection(workPart,
                            new double[] { 0, 0, 0 },
                            new double[] { dirVec.X, dirVec.Y, dirVec.Z });

                        // P2 (2026-09-02): 逆向 Limit 防御 — NX 会静默反转 [end,start] 区间产生错误几何
                        var reversedCheck = ToolHelpers.CheckReversedLimits(startDistance, distance);
                        if (reversedCheck != null) return reversedCheck;

                        // NX2412: Use Limits instead of SetDistance
                        builder.Limits.StartExtend.Value.RightHandSide = startDistance.ToString();
                        builder.Limits.EndExtend.Value.RightHandSide = distance.ToString();

                        double preBoolVolume = -1.0; // M0: 布尔前目标体体积（结果验证用）
                        if (boolType != null)
                        {
                            // P2 (2026-09-02): 显式 target 替代盲选 foreach(Bodies) 取第一个（多 body 时选错）
                            string targetErr;
                            dynamic targetBody = ToolHelpers.GetTargetBody(workPart, target, out targetErr);
                            if (targetBody == null && !string.IsNullOrEmpty(targetErr))
                                return ToolHelpers.Fail(targetErr);
                            if (targetBody != null)
                            {
                                builder.BooleanOperation.SetBooleanOperationAndBody(
                                    (NXOpen.GeometricUtilities.BooleanOperation.BooleanType)boolType,
                                    (NXOpen.Body)targetBody);
                                try
                                {
                                    // 体积用 AskMassProps3d (units=4 → kg/m), mp[1]=体积 m3 → *1e9 = mm3
                                    // (实测配方: MeasureTools.cs / FindSameBodiesTool.cs, 2026-08-13 球基准 r=10 V=4188.79)
                                    NXOpen.UF.UFSession ufTmp = NXOpen.UF.UFSession.GetUFSession();
                                    double[] acc = new double[11]; acc[0] = 0.001;
                                    double[] mp = new double[47]; double[] stats = new double[13];
                                    ufTmp.Modl.AskMassProps3d(new NXOpen.Tag[] { ((NXOpen.Body)targetBody).Tag },
                                        1, 1, 4, 0.0, 1, acc, mp, stats);
                                    preBoolVolume = mp[1] * 1e9;
                                }
                                catch { }
                            }
                            else
                                builder.BooleanOperation.Type = (NXOpen.GeometricUtilities.BooleanOperation.BooleanType)boolType;
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Extrude";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["distance"] = distance;
                        result["direction"] = direction;
                        result["start_distance"] = startDistance;
                        result["end_condition"] = endCondition;
                        // M0-20260912: 显式标注语义 — distance 是绝对位置 (Limits.EndExtend)，非拉伸长度
                        result["semantics"] = "absolute-from-section-plane (EndExtend/StartExtend), NOT length";
                        // M0-20260914: 区域选择回执 — region_applied=false 且传了 seed_point = 区域没生效，需排查
                        result["region_applied"] = regionApplied;
                        if (regionNote != null) result["region"] = regionNote;
                        if (regionError != null) result["region_error"] = regionError;
                        if (!string.IsNullOrEmpty(curveIdxStr)) result["curve_indices"] = curveIdxStr;
                        if (!string.IsNullOrEmpty(curveNamesStr)) result["curve_names"] = curveNamesStr;
                        if (!string.IsNullOrEmpty(seedPointsStr)) result["seed_points"] = seedPointsStr;

                        // M0-20260912: 结果验证 — 成功≠几何正确。
                        // ① 新体 (boolean=none)：bbox 沿拉伸方向必须覆盖 [start, end]，否则几何没到位；
                        // ② unite/subtract：目标体体积变化必须显著非零，否则拉伸被吞/未生效。
                        try
                        {
                            VerifyExtrudeResult(result, feature, direction, startDistance, distance, boolType, preBoolVolume);
                        }
                        catch (Exception vex) { result["geometry_warning"] = "verify-failed:" + vex.Message; }

                        return ToolHelpers.Ok(
                            string.Format("Extruded '{0}' by {1} mm along {2}.", featureName, distance, direction),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_extrude failed: {0}", ex.Message));
            }
        }

        /// <summary>
        /// M0-20260912: 拉伸结果验证 — 成功(Commit 无异常) ≠ 几何正确。
        /// 新体: bbox 沿拉伸方向须覆盖 [start,end]；布尔: 目标体体积须显著变化。
        /// 失败只写 geometry_warning 到 result，不阻断主流程。
        /// </summary>
        private static void VerifyExtrudeResult(JObject result, dynamic feature,
            string direction, double startDistance, double distance, object boolTypeObj, double preBoolVolume)
        {
            if (feature == null) return;
            NXOpen.UF.UFSession uf = NXOpen.UF.UFSession.GetUFSession();
            string dirUp = direction != null ? direction.ToUpperInvariant() : "Z";
            int axisIdx = (dirUp == "X" || dirUp == "-X") ? 0
                        : (dirUp == "Y" || dirUp == "-Y") ? 1 : 2;
            double lo = Math.Min(startDistance, distance);
            double hi = Math.Max(startDistance, distance);
            double tol = 0.02 + 0.05 * (hi - lo);

            dynamic bodies = feature.GetBodies();
            if (bodies == null || bodies.Length == 0) return;

            // ★ 2026-09-14 修复假阳性: GetBodies() 会把**零体积片体**一并返回。
            // 实证 (ASSEMBLY_10_PART_3, v_a4.prt): GetBodies()=2 → 告警"2 个独立体"，
            // 但 nx_inspect_topology 显示是 1 solid(18面48边) + 1 sheet(1面4边)，
            // nx_measure_volume 总量 85059.36227988401 = GT 85059.3623 **精确命中**。
            // ⇒ 几何本来是对的，是"把片体也数成体"造出了假告警（并误导过一次结论）。
            // 现在只把**实体**计入多体判定；片体单独计数上报，不触发告警。
            var solidBodies = new List<NXOpen.Body>();
            var bodyNames = new List<string>();
            int sheetCount = 0;
            for (int i = 0; i < bodies.Length; i++)
            {
                NXOpen.Body b = (NXOpen.Body)bodies[i];
                bool solid;
                // 判不出时按"实体"算 —— 宁可多报也不要静默放过真多体
                try { solid = (bool)b.IsSolidBody; } catch { solid = true; }
                if (!solid) { sheetCount++; continue; }
                solidBodies.Add(b);
                string bnm = null;
                try { bnm = ToolHelpers.GetJournalId((NXOpen.NXObject)b); } catch { }
                if (string.IsNullOrEmpty(bnm))
                {
                    try { bnm = "tag=" + ((NXOpen.NXObject)b).Tag; } catch { bnm = "body" + i; }
                }
                bodyNames.Add(bnm);
            }
            if (sheetCount > 0) result["sheet_bodies"] = sheetCount;

            // M0 case08 实证: 复杂草图轮廓未闭合 → NX 把每个独立闭合环各自拉伸 → 输出多实体。
            // 单闭合轮廓(含孔)应输出 1 个实体。这是"成功但几何错误"的明确信号 → 报 warning。
            if (solidBodies.Count > 1 && boolTypeObj == null)
            {
                // M0-20260914 A4: 列出体名 —— 用户可直接把名字喂给 nx_boolean / nx_unite_all。
                result["bodies"] = solidBodies.Count;
                result["body_names"] = new JArray(bodyNames.ToArray());
                result["geometry_warning"] = string.Format(
                    "拉伸输出 {0} 个独立实体（草图轮廓未闭合 / 多个未互联闭合环被各自拉伸）。单闭合轮廓(可含孔)应输出 1 个实体。检查弧段/线端点是否相连、是否缺少共点/切线约束。若几何本身正确、只是需要合并成 1 体，调用 nx_unite_all。体名: {1}",
                    solidBodies.Count, string.Join(" | ", bodyNames.ToArray()));
                return;
            }
            if (solidBodies.Count == 0) return;
            NXOpen.Body body = solidBodies[0];
            // bbox 实测签名: AskBoundingBox(tag, double[6]) = {xmin,ymin,zmin,xmax,ymax,zmax}
            // (AssemblyTools.cs:1430 / FindSameBodiesTool.cs:66 同款)
            double[] bb = new double[6];
            uf.Modl.AskBoundingBox(body.Tag, bb);

            if (boolTypeObj == null)
            {
                // 新体: 沿拉伸方向必须覆盖目标区间（EndExtend 语义）
                if (bb[axisIdx] > lo + tol || bb[axisIdx + 3] < hi - tol)
                {
                    result["geometry_warning"] = string.Format(
                        "新体 bbox 沿 {0} 为 [{1:F2}, {2:F2}]，目标区间 [{3:F2}, {4:F2}] — 拉伸未达设计区间。distance/start_distance 是绝对位置 (EndExtend/StartExtend) 非长度，检查方向/起止值。",
                        dirUp, bb[axisIdx], bb[axisIdx + 3], lo, hi);
                }
            }
            else if (preBoolVolume > 0)
            {
                double vAfter;
                {
                    NXOpen.UF.UFSession ufTmp = NXOpen.UF.UFSession.GetUFSession();
                    double[] acc = new double[11]; acc[0] = 0.001;
                    double[] mp = new double[47]; double[] stats = new double[13];
                    ufTmp.Modl.AskMassProps3d(new NXOpen.Tag[] { body.Tag },
                        1, 1, 4, 0.0, 1, acc, mp, stats);
                    vAfter = mp[1] * 1e9;
                }
                bool unite = boolTypeObj.ToString().ToLowerInvariant().Contains("unite");
                double delta = vAfter - preBoolVolume;
                double minDelta = Math.Max(preBoolVolume * 0.0005, 0.5); // ≥0.05% 或 0.5mm³
                if ((unite && delta < minDelta) || (!unite && delta > -minDelta))
                {
                    result["geometry_warning"] = string.Format(
                        "布尔拉伸体积变化过小 (Δ={0:F3} mm³, 目标体 {1:F1}→{2:F1}) — 工具体可能被整体包含(小体在目标体内)或位于目标体外，布尔未生效。distance/start_distance 是绝对位置非长度。",
                        delta, preBoolVolume, vAfter);
                }
            }
        }
    }

    // ========================================================================
    // 2. nx_revolve
    // ========================================================================

    /// <summary>
    /// Revolve a section or sketch around an axis.
    ///
    /// Parameters:
    ///   angle       (number, optional) -- Revolution angle in degrees (default 360)
    ///   axis        (string, optional) -- Axis of revolution: X, Y, Z (UPPERCASE only; default Z).
    ///     ⚠️ lowercase/vector values fail with "Invalid direction".
    ///   sketch_name (string, optional) -- Name of the sketch to revolve (falls back to most recent).
    ///     ⚠️ pure-line profiles are rejected ("缺少截面线串") — include at least one arc in the sketch.
    ///   boolean     (string, optional) -- none, unite, subtract, intersect
    ///   target      (string, optional) -- boolean 目标体: body 名 / journal_id / tag。多 body 时必须显式指定
    ///
    /// Returns:
    ///   journal_id  (string) -- 特征句柄 "REVOLVE(2)" (NX2412 Name 为空, 用此引用)
    ///   body        (string) -- 输出体 JournalIdentifier
    /// </summary>

    // ========================================================================
    // 2. nx_revolve
    // ========================================================================

    /// <summary>
    /// Revolve a section or sketch around an axis.
    ///
    /// Parameters:
    ///   angle       (number, optional) -- Revolution angle in degrees (default 360)
    ///   axis        (string, optional) -- Axis of revolution: X, Y, Z (UPPERCASE only; default Z).
    ///     ⚠️ lowercase/vector values fail with "Invalid direction".
    ///   sketch_name (string, optional) -- Name of the sketch to revolve (falls back to most recent).
    ///     ⚠️ pure-line profiles are rejected ("缺少截面线串") — include at least one arc in the sketch.
    ///   boolean     (string, optional) -- none, unite, subtract, intersect
    ///   target      (string, optional) -- boolean 目标体: body 名 / journal_id / tag。多 body 时必须显式指定
    ///
    /// Returns:
    ///   journal_id  (string) -- 特征句柄 "REVOLVE(2)" (NX2412 Name 为空, 用此引用)
    ///   body        (string) -- 输出体 JournalIdentifier
    /// </summary>
    public class RevolveTool : IToolHandler
    {
        public string Name { get { return "nx_revolve"; } }
        public string Description { get { return "Revolve a section or sketch around an axis. Optional seed_point='x,y,z' / seed_points='x,y,z;x,y,z' selects region(s) of a multi-loop sketch (region boundary rule) instead of revolving every closed loop into separate bodies — the human pattern in 34 of 35 revolve journals. Optional curve_indices / curve_names restrict the region boundary to a curve subset."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            // ⚠️ axis 必须在 try **外**声明: C# 里 try 块内声明的局部变量在 catch 块中不可见
            // (实测 CS0103 "当前上下文中不存在名称 axis")。下面 catch 的报错提示要用它。
            string axis = ToolHelpers.GetString(parameters, "axis", "Z");
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string sketchName = ToolHelpers.GetString(parameters, "sketch_name", null);
                double angle = ToolHelpers.GetDouble(parameters, "angle", 360.0);
                string boolean = ToolHelpers.GetString(parameters, "boolean", "none");
                string target = ToolHelpers.GetString(parameters, "target", null);
                // M0-20260914 A3: 区域选择 —— 旋转也走"曲线汤 + 种子点"。
                // 数据依据: 35 个 committed Revolve 案例中 34 个同时用了 region。
                string seedPointStr = ToolHelpers.GetString(parameters, "seed_point", null);
                string seedPointsStr = ToolHelpers.GetString(parameters, "seed_points", null);
                string curveNamesStr = ToolHelpers.GetString(parameters, "curve_names", null);
                string curveIdxStr = ToolHelpers.GetString(parameters, "curve_indices", null);

                // Resolve sketch
                dynamic sketch = null;
                if (!string.IsNullOrEmpty(sketchName))
                {
                    foreach (dynamic sk in workPart.Sketches)
                    {
                        if (sk != null && (sk.Name == sketchName || sk.Name.EndsWith(sketchName)))
                        {
                            sketch = sk;
                            break;
                        }
                    }
                    if (sketch == null)
                        return ToolHelpers.Fail(string.Format("Sketch '{0}' not found.", sketchName));
                }
                else
                {
                    foreach (dynamic sk in workPart.Sketches) { sketch = sk; }
                }

                dynamic axisVec = ModelingHelpers.GetDirectionVector(session, axis);
                object boolType = ModelingHelpers.GetBooleanType(boolean);

                using (var mark = new UndoMarkScope(session,
                    "Revolve@" + (boolean != "none" ? boolean.ToLowerInvariant() + "-" : "") + "a" + angle))
                {
                    dynamic builder = workPart.Features.CreateRevolveBuilder(null);
                    try
                    {
                        // NX2412: Set up section from sketch.
                        // 模式镜像 ExtrudeTool (L184-248) / SectionHelper.CreateSectionFromSketch (SurfaceTools.cs L33-57):
                        // GetAllGeometry → 显式 IBaseCurve 转型 → 批量 CreateRuleBaseCurveDumb(IBaseCurve[])
                        // → (SelectionIntentRule) → 7 参 AddToSection(mode int 0)。
                        // ⚠️ 禁止 new dynamic[]{...} (运行时 object[], 无数组协变 → RuntimeBinderException)。
                        // ⚠️ 不要复制 ExtrudeTool 的 AllowSelfIntersectingSection (RevolveBuilder 无此方法)。
                        // M0-20260914 A3: 区域选择簿记 (与 ExtrudeTool 同构)
                        bool regionApplied = false;
                        string regionNote = null;
                        string regionError = null;

                        if (sketch != null)
                        {
                            dynamic section = workPart.Sections.CreateSection(0.00095, 0.001, 0.01);
                            builder.Section = section;

                            var curveList = new System.Collections.Generic.List<dynamic>();
                            try
                            {
                                dynamic sketchCurves = sketch.GetAllGeometry();
                                foreach (dynamic curve in sketchCurves) { if (curve != null) curveList.Add(curve); }
                            }
                            catch { }   // 过滤语义: 草图取几何失败则走空列表, 由下方 Count 判断 (与 ExtrudeTool L196-202 一致)

                            // M0-20260914 A3: 区域选择优先 (35 个 committed Revolve 案例中 34 个用 region)
                            if (curveList.Count > 0
                                && (!string.IsNullOrEmpty(seedPointsStr) || !string.IsNullOrEmpty(seedPointStr)))
                            {
                                string rNote, rErr;
                                regionApplied = RegionSelect.Apply(workPart, sketch, section, curveList,
                                    seedPointsStr, seedPointStr, curveNamesStr, curveIdxStr,
                                    out rNote, out rErr);
                                if (regionApplied) regionNote = rNote;
                                regionError = RegionSelect.Err(regionError, rErr);
                            }

                            if (!regionApplied && curveList.Count > 0)
                            {
                                var iBaseCurves = new System.Collections.Generic.List<NXOpen.IBaseCurve>();
                                foreach (dynamic curve in curveList)
                                {
                                    try { iBaseCurves.Add((NXOpen.IBaseCurve)curve); }
                                    catch { }   // 过滤语义: 跳过非 IBaseCurve 几何 (参考线/标注等), 与 ExtrudeTool L204-209 一致
                                }

                                if (iBaseCurves.Count > 0)
                                {
                                    NXOpen.IBaseCurve[] curveArray = iBaseCurves.ToArray();
                                    dynamic rule = workPart.ScRuleFactory.CreateRuleBaseCurveDumb(curveArray);
                                    NXOpen.SelectionIntentRule selRule = (NXOpen.SelectionIntentRule)rule;
                                    NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { selRule };
                                    NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                                    NXOpen.NXObject refObj = (NXOpen.NXObject)curveArray[0];
                                    section.AddToSection(selRules, refObj, null, null, helpPoint, 0, false);
                                }
                            }
                        }

                        // NX2412: Build Axis from point + direction
                        dynamic originPt = new NXOpen.Point3d(0.0, 0.0, 0.0);
                        dynamic originPoint = workPart.Points.CreatePoint(originPt);
                        dynamic dirObj = workPart.Directions.CreateDirection(
                            originPt, axisVec, NXOpen.SmartObject.UpdateOption.WithinModeling);
                        dynamic axisObj = workPart.Axes.CreateAxis(
                            originPoint, dirObj, NXOpen.SmartObject.UpdateOption.WithinModeling);
                        builder.Axis = axisObj;

                        // NX2412: Use Limits instead of SetAngle
                        // ⚠️ 裸数值, 不带 "deg" 后缀 — "360 deg" 在 NX2412 表达式解析报 "字符串包含语法错误"
                        builder.Limits.StartExtend.Value.RightHandSide = "0";
                        builder.Limits.EndExtend.Value.RightHandSide = angle.ToString();

                        if (boolType != null)
                        {
                            // P2 (2026-09-02): 显式 target 替代盲选 foreach(Bodies) 取第一个
                            string targetErr;
                            dynamic targetBody = ToolHelpers.GetTargetBody(workPart, target, out targetErr);
                            if (targetBody == null && !string.IsNullOrEmpty(targetErr))
                                return ToolHelpers.Fail(targetErr);
                            if (targetBody != null)
                                builder.BooleanOperation.SetBooleanOperationAndBody(
                                    (NXOpen.GeometricUtilities.BooleanOperation.BooleanType)boolType,
                                    (NXOpen.Body)targetBody);
                            else
                                builder.BooleanOperation.Type = (NXOpen.GeometricUtilities.BooleanOperation.BooleanType)boolType;
                        }

                        // NX2412: open section defaults to SHEET — force SOLID via
                        // SmartVolumeProfile.OpenProfileSmartVolumeOption (verified by
                        // nx_probe_builder: RevolveBuilder has NO SetBodyPreference,
                        // only SmartVolumeProfile {OpenProfileSmartVolumeOption, CloseProfileRule})
                        try { builder.SmartVolumeProfile.OpenProfileSmartVolumeOption = true; } catch { }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Revolve";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["angle"] = angle;
                        result["axis"] = axis;
                        // M0-20260914 A3: 区域回执 (与 nx_extrude 同构)
                        result["region_applied"] = regionApplied;
                        if (regionNote != null) result["region"] = regionNote;
                        if (regionError != null) result["region_error"] = regionError;
                        if (!string.IsNullOrEmpty(curveNamesStr)) result["curve_names"] = curveNamesStr;
                        if (!string.IsNullOrEmpty(curveIdxStr)) result["curve_indices"] = curveIdxStr;
                        if (!string.IsNullOrEmpty(seedPointsStr)) result["seed_points"] = seedPointsStr;

                        // M0-20260914 A3: 结果验证 —— Commit 无异常 ≠ 几何对。
                        // 实证 (L1 §4): 孤立线会旋出 0 体积退化体; 多体说明剖面被拆成不相连环。
                        try { VerifyRevolveResult(result, feature, boolType); }
                        catch (Exception vex) { result["geometry_warning"] = "verify-failed:" + vex.Message; }
                        return ToolHelpers.Ok(
                            string.Format("Revolved '{0}' by {1} degrees around {2}.", featureName, angle, axis),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                // M0-20260914 A3: 报错时附轴提示。**不自动重试其他轴** ——
                // L1 §3 实证 (00049f4a): axis=X 失败 / Y 成功，但 Pappus 手算 2π·8.625·76.3=4134.9
                // 与工具输出一致 ⇒ 工具没错，是我的剖面/轴选错。自动重试会把这个真因掩盖掉。
                return ToolHelpers.Fail(string.Format(
                    "nx_revolve failed: {0} (axis={1}; 若报「无法根据指定的截面、轴和参数创建旋转体」，"
                    + "先确认剖面在轴的哪一侧、是否被轴穿过，再换轴，不要盲试)",
                    ex.Message, axis));
            }
        }

        /// <summary>
        /// M0-20260914 A3: 旋转结果验证 —— 与 VerifyExtrudeResult 对等。
        /// 实证问题 (M0-L1-RESULTS.md §4 第 4 条): 孤立线会旋出 0 体积退化体,
        /// 导致"成功但多体"。这里报出体数与体名, 不阻断主流程。
        /// </summary>
        private static void VerifyRevolveResult(JObject result, dynamic feature, object boolTypeObj)
        {
            if (feature == null) return;
            dynamic bodies = feature.GetBodies();
            if (bodies == null || bodies.Length == 0) return;

            var names = new List<string>();
            var vols = new List<double>();
            int degenerate = 0;
            int sheetCount = 0;
            for (int i = 0; i < bodies.Length; i++)
            {
                NXOpen.Body bi = (NXOpen.Body)bodies[i];
                // 与 VerifyExtrudeResult 同款修复: 片体不算"独立体"(否则零体积片体会假报多体)
                bool solid;
                try { solid = (bool)bi.IsSolidBody; } catch { solid = true; }
                if (!solid) { sheetCount++; continue; }

                string bnm = null;
                try { bnm = ToolHelpers.GetJournalId((NXOpen.NXObject)bi); } catch { }
                if (string.IsNullOrEmpty(bnm)) { try { bnm = "tag=" + ((NXOpen.NXObject)bi).Tag; } catch { bnm = "body" + i; } }
                names.Add(bnm);
                double v = -1.0;
                try
                {
                    NXOpen.UF.UFSession ufTmp = NXOpen.UF.UFSession.GetUFSession();
                    double[] acc = new double[11]; acc[0] = 0.001;
                    double[] mp = new double[47]; double[] stats = new double[13];
                    ufTmp.Modl.AskMassProps3d(new NXOpen.Tag[] { ((NXOpen.NXObject)bi).Tag },
                        1, 1, 4, 0.0, 1, acc, mp, stats);
                    v = mp[1] * 1e9;
                }
                catch { }
                vols.Add(v);
                if (v >= 0 && v < 0.5) degenerate++;
            }
            if (sheetCount > 0) result["sheet_bodies"] = sheetCount;
            if (names.Count == 0) return;

            if (names.Count > 1 && boolTypeObj == null)
            {
                result["bodies"] = names.Count;
                result["body_names"] = new JArray(names.ToArray());
                result["body_volumes_mm3"] = new JArray(vols.ToArray());
                result["geometry_warning"] = string.Format(
                    "旋转输出 {0} 个独立实体（剖面含不相连环 / 孤立曲线被各自旋转）。{1}闭合剖面应输出 1 个实体。若几何本身正确、只是需要合并，调用 nx_unite_all。体名: {2}",
                    names.Count,
                    degenerate > 0 ? "其中 " + degenerate + " 个体积≈0（退化体，通常由孤立线旋出）。" : "",
                    string.Join(" | ", names.ToArray()));
            }
            else if (degenerate > 0)
            {
                result["body_volumes_mm3"] = new JArray(vols.ToArray());
                result["geometry_warning"] = string.Format(
                    "旋转输出含 {0} 个 0 体积退化体（孤立线/非闭合段被旋转）——剖面里有多余曲线。体名: {1}",
                    degenerate, string.Join(" | ", names.ToArray()));
            }
        }
    }

    // ========================================================================
    // 3. nx_sweep
    // ========================================================================

    /// <summary>
    /// Sweep a single section along a guide curve ("Sweep Along Guide").
    ///
    /// NX2412 (probe 2026-08-18): the legacy SweptBuilder/SweptBuilder1 (standard "Swept"
    /// feature) requires sections at BOTH ends of the guide — a single section always
    /// fails with "无法逼近引导线串". The correct builder for one section + one guide is
    /// SweepAlongGuideBuilder (CreateSweepAlongGuideBuilder), verified working.
    ///
    /// ⚠️ CONSTRAINT: the section must be oriented PERPENDICULAR to the guide's start
    /// tangent. A coplanar section+guide fails with "共面的截面及引导线串".
    ///
    /// Parameters:
    ///   section  (string, required) -- Name of the section curve or sketch
    ///   guide    (string, required) -- Name of the guide curve or sketch
    ///   boolean  (string, optional) -- none, unite, subtract, intersect
    ///   target   (string, optional) -- boolean 目标体: body 名 / journal_id / tag。多 body 时必须显式指定
    ///
    /// Returns:
    ///   journal_id  (string) -- 特征句柄 "SWEEP(2)" (NX2412 Name 为空, 用此引用)
    ///   body        (string) -- 输出体 JournalIdentifier
    /// </summary>
    public class SweepTool : IToolHandler
    {
        public string Name { get { return "nx_sweep"; } }
        public string Description { get { return "Sweep a single section along a guide (Sweep Along Guide). Section must be perpendicular to guide start tangent."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string sectionName = ToolHelpers.GetString(parameters, "section", null);
                string guideName = ToolHelpers.GetString(parameters, "guide", null);
                string boolean = ToolHelpers.GetString(parameters, "boolean", "none");
                string target = ToolHelpers.GetString(parameters, "target", null);

                if (string.IsNullOrEmpty(sectionName))
                    return ToolHelpers.Fail("Parameter 'section' is required.");
                if (string.IsNullOrEmpty(guideName))
                    return ToolHelpers.Fail("Parameter 'guide' is required.");

                dynamic sectionObj = ModelingHelpers.ResolveObjectByName(workPart, sectionName);
                if (sectionObj == null)
                    return ToolHelpers.Fail(string.Format("Section '{0}' not found.", sectionName));

                dynamic guideObj = ModelingHelpers.ResolveObjectByName(workPart, guideName);
                if (guideObj == null)
                    return ToolHelpers.Fail(string.Format("Guide '{0}' not found.", guideName));

                object boolType = ModelingHelpers.GetBooleanType(boolean);

                using (var mark = new UndoMarkScope(session, "Sweep"))
                {
                    // NX2412 (probe 2026-08-18): SweepAlongGuideBuilder = single section swept
                    // along guide. Section/Guide are builder-owned Section getters — fill via
                    // AddToSection (NOT SectionList.Append — SectionList is a container, no AddToSection).
                    dynamic builder = workPart.Features.CreateSweepAlongGuideBuilder(null);
                    try
                    {
                        if (!ModelingHelpers.FillSection(builder.Section, workPart, sectionObj))
                            return ToolHelpers.Fail(string.Format("Section '{0}' could not be added to sweep.", sectionName));
                        if (!ModelingHelpers.FillSection(builder.Guide, workPart, guideObj))
                            return ToolHelpers.Fail(string.Format("Guide '{0}' could not be added to sweep.", guideName));

                        if (boolType != null)
                        {
                            // P2 (2026-09-02): 显式 target 替代盲选 foreach(Bodies) 取第一个
                            string targetErr;
                            dynamic targetBody = ToolHelpers.GetTargetBody(workPart, target, out targetErr);
                            if (targetBody == null && !string.IsNullOrEmpty(targetErr))
                                return ToolHelpers.Fail(targetErr);
                            if (targetBody != null)
                                builder.BooleanOperation.SetBooleanOperationAndBody(
                                    (NXOpen.GeometricUtilities.BooleanOperation.BooleanType)boolType,
                                    (NXOpen.Body)targetBody);
                            else
                                builder.BooleanOperation.Type = (NXOpen.GeometricUtilities.BooleanOperation.BooleanType)boolType;
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Sweep";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["section"] = sectionName;
                        result["guide"] = guideName;
                        return ToolHelpers.Ok(
                            string.Format("Swept '{0}' along guide '{1}'.", featureName, guideName),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_sweep failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 4. nx_blend
    // ========================================================================

    /// <summary>
    /// Create an edge blend (fillet) on specified edges.
    ///
    /// Parameters:
    ///   edges  (string[], required) -- List of edge names to blend
    ///   radius (number, required)   -- Blend radius
    ///
    /// Returns:
    ///   journal_id  (string) -- 特征句柄 "BLEND(2)" (NX2412 Name 为空, 用此引用)
    /// </summary>
    public class BlendTool : IToolHandler
    {
        public string Name { get { return "nx_blend"; } }
        public string Description { get { return "Create an edge blend (fillet) on specified edges."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> edges = ToolHelpers.GetStringArray(parameters, "edges");
                double radius = ToolHelpers.GetDouble(parameters, "radius", 2.0);

                if (edges.Count == 0)
                    return ToolHelpers.Fail("Parameter 'edges' is required.");

                using (var mark = new UndoMarkScope(session, "Blend"))
                {
                    dynamic builder = workPart.Features.CreateEdgeBlendBuilder(null);
                    try
                    {
                        // NX2412: SetRadius/AddEdge don't exist.
                        // Use AddChainset(scCollector, radius) + ScCollector pattern.
                        builder.Tolerance = 0.01;
                        builder.RemoveSelfIntersection = true;
                        builder.RollOverSmoothEdge = true;
                        builder.RollOntoEdge = true;
                        builder.MoveSharpEdge = true;

                        // Resolve edge objects by name or tag (NX2412: Part.Edges does not exist,
                        // edges are nameless — iterate Body.GetEdges() and match name OR tag string)
                        var edgeList = new System.Collections.Generic.List<dynamic>();
                        foreach (string edgeName in edges)
                        {
                            foreach (dynamic body in workPart.Bodies)
                            {
                                foreach (dynamic edge in body.GetEdges())
                                {
                                    if (edge.Name == edgeName || edge.Tag.ToString() == edgeName)
                                    { edgeList.Add(edge); break; }
                                }
                                if (edgeList.Count > 0 && edgeList[edgeList.Count - 1].Name == edgeName) break;
                            }
                        }

                        if (edgeList.Count == 0)
                            return ToolHelpers.Fail("No valid edges found. Edge names are empty in NX2412 — pass edge tags (see nx_inspect_topology).");

                        // Create ScCollector for edge selection
                        // ⚠️ 禁止 dynamic[]/List<dynamic>.ToArray() (object[] 无数组协变 → RuntimeBinderException)
                        var edgeArr = edgeList.ConvertAll(e => (NXOpen.Edge)e).ToArray();
                        dynamic rule = workPart.ScRuleFactory.CreateRuleEdgeDumb(edgeArr);
                        dynamic scCollector = workPart.ScCollectors.CreateCollector();
                        scCollector.ReplaceRules(new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule }, false);

                        // Add chainset: collector + radius as string
                        builder.AddChainset(scCollector, radius.ToString("F3"));

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Blend";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["edges"] = new JArray(edges);
                        result["radius"] = radius;
                        return ToolHelpers.Ok(
                            string.Format("Blended {0} edge(s) with radius {1} mm.", edges.Count, radius),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_blend failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 5. nx_chamfer
    // ========================================================================

    /// <summary>
    /// Create a chamfer on specified edges.
    /// Supports symmetric, asymmetric, and angle-offset modes.
    ///
    /// Parameters:
    ///   edges   (string[], required) -- List of edge names to chamfer
    ///   offset  (number, required)   -- Chamfer offset distance
    ///   offset2 (number, optional)   -- Second offset for asymmetric chamfer
    ///   angle   (number, optional)   -- Angle in degrees for angle-offset chamfer
    /// </summary>
    public class ChamferTool : IToolHandler
    {
        public string Name { get { return "nx_chamfer"; } }
        public string Description { get { return "Create a chamfer on specified edges. Supports symmetric, asymmetric, and angle-offset modes."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                // Validate: cannot provide both offset2 and angle
                JToken offset2Token = parameters != null ? parameters["offset2"] : null;
                JToken angleToken = parameters != null ? parameters["angle"] : null;
                if (offset2Token != null && offset2Token.Type != JTokenType.Null &&
                    angleToken != null && angleToken.Type != JTokenType.Null)
                {
                    return ToolHelpers.Fail(
                        "Cannot specify both 'offset2' and 'angle'. " +
                        "Use offset+offset2 for asymmetric, or offset+angle for angle-offset chamfer.");
                }

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> edges = ToolHelpers.GetStringArray(parameters, "edges");
                double offset = ToolHelpers.GetDouble(parameters, "offset", 1.0);

                if (edges.Count == 0)
                    return ToolHelpers.Fail("Parameter 'edges' is required.");

                using (var mark = new UndoMarkScope(session, "Chamfer"))
                {
                    dynamic builder = workPart.Features.CreateChamferBuilder(null);
                    try
                    {
                        // NX2412: SetOffset/SetOffset1/SetOffset2/AddEdge don't exist.
                        // Use Option + SetFirstOffset/SetSecondOffset/SetAngle (string) + SmartCollector.
                        // Option: 0=SymmetricOffsets, 1=TwoOffsets, 2=OffsetAndAngle
                        string chamferMode;

                        // Resolve edge objects by name or tag (NX2412: Part.Edges does not exist,
                        // edges are nameless — iterate Body.GetEdges() and match name OR tag string)
                        var edgeList = new System.Collections.Generic.List<dynamic>();
                        foreach (string edgeName in edges)
                        {
                            foreach (dynamic body in workPart.Bodies)
                            {
                                foreach (dynamic edge in body.GetEdges())
                                {
                                    if (edge.Name == edgeName || edge.Tag.ToString() == edgeName)
                                    { edgeList.Add(edge); break; }
                                }
                                if (edgeList.Count > 0 && edgeList[edgeList.Count - 1].Name == edgeName) break;
                            }
                        }

                        if (edgeList.Count == 0)
                            return ToolHelpers.Fail("No valid edges found. Edge names are empty in NX2412 — pass edge tags (see nx_inspect_topology).");

                        // Create ScCollector for edge selection
                        var edgeArr = edgeList.ConvertAll(e => (NXOpen.Edge)e).ToArray();
                        dynamic rule = workPart.ScRuleFactory.CreateRuleEdgeDumb(edgeArr);
                        dynamic scCollector = workPart.ScCollectors.CreateCollector();
                        scCollector.ReplaceRules(new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule }, false);
                        builder.SmartCollector = scCollector;

                        if (offset2Token != null && offset2Token.Type != JTokenType.Null)
                        {
                            // Asymmetric mode: TwoOffsets
                            double offset2 = offset2Token.Value<double>();
                            builder.Option = NXOpen.Features.ChamferBuilder.ChamferOption.TwoOffsets;
                            builder.FirstOffset = offset.ToString("F3");
                            builder.SecondOffset = offset2.ToString("F3");
                            chamferMode = "asymmetric";
                        }
                        else if (angleToken != null && angleToken.Type != JTokenType.Null)
                        {
                            // Angle-offset mode: OffsetAndAngle
                            double angle = angleToken.Value<double>();
                            builder.Option = NXOpen.Features.ChamferBuilder.ChamferOption.OffsetAndAngle;
                            builder.FirstOffset = offset.ToString("F3");
                            builder.Angle = angle.ToString("F1");
                            chamferMode = "angle_offset";
                        }
                        else
                        {
                            // Symmetric mode: SymmetricOffsets
                            builder.Option = NXOpen.Features.ChamferBuilder.ChamferOption.SymmetricOffsets;
                            builder.FirstOffset = offset.ToString("F3");
                            chamferMode = "symmetric";
                        }

                        builder.Tolerance = 0.01;

                        dynamic feature = builder.CommitFeature();
                        string featureName = feature != null ? feature.Name : "Chamfer";
                        builder.Destroy();

                        mark.Commit();

                        var data = new JObject();
                        data["feature"] = featureName;
                        data["edges"] = new JArray(edges);
                        data["offset"] = offset;
                        data["chamfer_mode"] = chamferMode;
                        if (offset2Token != null && offset2Token.Type != JTokenType.Null)
                            data["offset2"] = offset2Token.Value<double>();
                        if (angleToken != null && angleToken.Type != JTokenType.Null)
                            data["angle"] = angleToken.Value<double>();

                        string msg;
                        switch (chamferMode)
                        {
                            case "asymmetric":
                                msg = string.Format("Chamfered {0} edge(s) with asymmetric offsets.", edges.Count);
                                break;
                            case "angle_offset":
                                msg = string.Format("Chamfered {0} edge(s) with offset and angle.", edges.Count);
                                break;
                            default:
                                msg = string.Format("Chamfered {0} edge(s) with offset {1} mm.", edges.Count, offset);
                                break;
                        }

                        return ToolHelpers.Ok(msg, data);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_chamfer failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 6. nx_hole
    // ========================================================================

    /// <summary>
    /// Create a hole feature at the specified location.
    ///
    /// NX2412 (probe 2026-08-18, verified commit tag=67571): the hole position MUST be a
    /// Point FEATURE referenced via FeaturePointsRule (NOT AddSmartPoint — that gives
    /// "缺少目标体"). The HolePosition section must be SetAllowedEntityTypes(OnlyPoints)
    /// first (else "指定的公差需要三个数"), and the direction must be "沿矢量" pointing into
    /// the target body (else "垂直于面方向找不到最近的面").
    ///
    /// Parameters:
    ///   diameter        (number, required) -- Hole diameter
    ///   depth           (number, required) -- Hole depth
    ///   x               (number, required) -- X coordinate of hole center (must be ON target body surface)
    ///   y               (number, required) -- Y coordinate of hole center
    ///   z               (number, required) -- Z coordinate of hole center
    ///   hole_type       (string, optional) -- simple, counterbore, countersink (default simple)
    ///   direction       (string, optional) -- Drilling direction: X, Y, Z, -X, -Y, -Z (default -Z)
    ///   face_reference  (string, optional) -- Face name for target body resolution
    /// </summary>
    public class HoleTool : IToolHandler
    {
        public string Name { get { return "nx_hole"; } }
        public string Description { get { return "Create a hole feature at the specified location. Position must be on the target body surface; direction points into the body."; } }

        /// <summary>Find the target body: the body whose face is nearest to the point, or from face_reference.</summary>
        private static NXOpen.Body ResolveTargetBody(dynamic workPart, double x, double y, double z, string faceReference)
        {
            if (!string.IsNullOrEmpty(faceReference))
            {
                dynamic faceObj = ModelingHelpers.FindFaceByName(workPart, faceReference);
                if (faceObj != null)
                {
                    try { return faceObj.GetBody(); } catch { }
                }
            }
            // Auto: find body whose face is closest to the point
            NXOpen.Body bestBody = null;
            double bestDist = double.MaxValue;
            foreach (NXOpen.Body body in (System.Collections.IEnumerable)workPart.Bodies)
            {
                if (body == null || !body.IsSolidBody) continue;
                foreach (NXOpen.Face face in body.GetFaces())
                {
                    try
                    {
                        foreach (NXOpen.Edge e in face.GetEdges())
                        {
                            NXOpen.Point3d v1, v2;
                            e.GetVertices(out v1, out v2);
                            double d = Math.Min(Dist2(v1, x, y, z), Dist2(v2, x, y, z));
                            if (d < bestDist) { bestDist = d; bestBody = body; }
                        }
                    }
                    catch { }
                }
            }
            return bestBody;
        }

        private static double Dist2(NXOpen.Point3d p, double x, double y, double z)
        {
            double dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static NXOpen.Vector3d GetDrillVector(string direction)
        {
            switch ((direction ?? "Z").Trim().ToUpper())
            {
                case "X": return new NXOpen.Vector3d(1, 0, 0);
                case "-X": return new NXOpen.Vector3d(-1, 0, 0);
                case "Y": return new NXOpen.Vector3d(0, 1, 0);
                case "-Y": return new NXOpen.Vector3d(0, -1, 0);
                case "Z": return new NXOpen.Vector3d(0, 0, 1);
                case "-Z": default: return new NXOpen.Vector3d(0, 0, -1);
            }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                double diameter = ToolHelpers.GetDouble(parameters, "diameter", 10.0);
                double depth = ToolHelpers.GetDouble(parameters, "depth", 10.0);
                double x = ToolHelpers.GetDouble(parameters, "x", 0.0);
                double y = ToolHelpers.GetDouble(parameters, "y", 0.0);
                double z = ToolHelpers.GetDouble(parameters, "z", 0.0);
                string holeType = ToolHelpers.GetString(parameters, "hole_type", "simple");
                string faceReference = ToolHelpers.GetString(parameters, "face_reference", "");
                string direction = ToolHelpers.GetString(parameters, "direction", "-Z");

                // NX2412 (probe 2026-08-18): 孔位必须是点特征 → FeaturePointsRule (AddSmartPoint 报"缺少目标体")
                NXOpen.Features.Feature pointFeature = null;
                try
                {
                    NXOpen.Point rawPt = workPart.Points.CreatePoint(new NXOpen.Point3d(x, y, z));
                    NXOpen.Features.PointFeatureBuilder pfb = workPart.BaseFeatures.CreatePointFeatureBuilder(null);
                    pfb.Point = rawPt;
                    pointFeature = (NXOpen.Features.Feature)pfb.Commit();
                    pfb.Destroy();
                }
                catch (Exception ex)
                {
                    return ToolHelpers.Fail(string.Format("Failed to create hole position point: {0}", ex.Message));
                }

                // 目标体 (点必须在目标体表面)
                NXOpen.Body targetBody = ResolveTargetBody(workPart, x, y, z, faceReference);
                if (targetBody == null)
                    return ToolHelpers.Fail("No target body found. The hole point (x,y,z) must lie on a solid body surface.");

                using (var mark = new UndoMarkScope(session, "Hole"))
                {
                    NXOpen.Features.HolePackageBuilder holeBuilder = workPart.Features.CreateHolePackageBuilder(null);
                    try
                    {
                        // ===== 官方 journal 序列 (2026-08-18 验证 commit tag=67571) =====
                        holeBuilder.Tolerance = 0.01;
                        holeBuilder.HolePosition.DistanceTolerance = 0.01;
                        holeBuilder.HolePosition.ChainingTolerance = 0.0094999999999999998;
                        holeBuilder.HolePosition.SetAllowedEntityTypes(NXOpen.Section.AllowTypes.OnlyPoints);
                        holeBuilder.HolePosition.AllowSelfIntersection(true);
                        holeBuilder.HolePosition.AllowDegenerateCurves(false);

                        // 方向: 沿矢量朝目标体内 (默认 -Z)
                        holeBuilder.ProjectionDirection.ProjectDirectionMethod =
                            NXOpen.GeometricUtilities.ProjectionOptions.DirectionType.Vector;
                        NXOpen.Direction projDir = workPart.Directions.CreateDirection(
                            new NXOpen.Point3d(0, 0, 0), GetDrillVector(direction),
                            NXOpen.SmartObject.UpdateOption.WithinModeling);
                        holeBuilder.ProjectionDirection.ProjectVector = projDir;

                        // 孔型
                        string lowerType = holeType.ToLower();
                        if (lowerType == "counterbore")
                            holeBuilder.GeneralHoleForm = NXOpen.Features.HolePackageBuilder.HoleForms.Counterbored;
                        else if (lowerType == "countersink")
                            holeBuilder.GeneralHoleForm = NXOpen.Features.HolePackageBuilder.HoleForms.Countersink;
                        else
                            holeBuilder.GeneralHoleForm = NXOpen.Features.HolePackageBuilder.HoleForms.Simple;
                        holeBuilder.HoleSize = NXOpen.Features.HolePackageBuilder.Holesize.Custom;
                        holeBuilder.HoleType = NXOpen.Features.HolePackageBuilder.Holetype.Simple;

                        if (lowerType == "counterbore")
                        {
                            holeBuilder.GeneralCounterboreHoleDiameter.RightHandSide = diameter.ToString("F3");
                            if (depth > 0)
                                holeBuilder.GeneralCounterboreHoleDepth.RightHandSide = depth.ToString("F3");
                        }
                        else if (lowerType == "countersink")
                        {
                            holeBuilder.GeneralCountersinkHoleDiameter.RightHandSide = diameter.ToString("F3");
                            if (depth > 0)
                                holeBuilder.GeneralCountersinkHoleDepth.RightHandSide = depth.ToString("F3");
                        }
                        else
                        {
                            holeBuilder.GeneralSimpleHoleDiameter.RightHandSide = diameter.ToString("F3");
                            if (depth > 0)
                                holeBuilder.GeneralSimpleHoleDepth.RightHandSide = depth.ToString("F3");
                        }

                        // 深度限制: 给了深度用 Value, 否则 ThroughBody
                        if (depth > 0)
                        {
                            holeBuilder.HoleDepthLimitOption = NXOpen.Features.HolePackageBuilder.HoleDepthLimitOptions.Value;
                            holeBuilder.GeneralTipAngle.RightHandSide = "0";
                        }
                        else
                        {
                            holeBuilder.HoleDepthLimitOption = NXOpen.Features.HolePackageBuilder.HoleDepthLimitOptions.ThroughBody;
                            holeBuilder.GeneralTipAngle.RightHandSide = "118";
                        }

                        // 位置: FeaturePointsRule (点特征) + seed=null
                        // ⚠️ FeaturePointsRule 在 NXOpen 命名空间 (非 NXOpen.Features) — 录制 journal 证实
                        NXOpen.FeaturePointsRule rule = workPart.ScRuleFactory.CreateRuleFeaturePoints(
                            new NXOpen.Features.Feature[] { pointFeature }, null);
                        holeBuilder.HolePosition.AddToSection(
                            new NXOpen.SelectionIntentRule[] { rule }, null, null, null,
                            new NXOpen.Point3d(0, 0, 0), NXOpen.Section.Mode.Create, false);

                        // 目标体: GetTargetBodiesCollector + BodyDumbRule (SetTargetBodies 会触发"公差需三个数"? 用 collector 官方路径)
                        NXOpen.ScCollector sc = holeBuilder.BooleanOperation.GetTargetBodiesCollector();
                        sc.ReplaceRules(new NXOpen.SelectionIntentRule[0], false);
                        NXOpen.SelectionIntentRuleOptions opts = workPart.ScRuleFactory.CreateRuleOptions();
                        opts.SetSelectedFromInactive(false);
                        NXOpen.BodyDumbRule bdr = workPart.ScRuleFactory.CreateRuleBodyDumb(
                            new NXOpen.Body[] { targetBody }, true, opts);
                        opts.Dispose();
                        sc.ReplaceRules(new NXOpen.SelectionIntentRule[] { bdr }, false);

                        NXObject feature = holeBuilder.Commit();
                        string featureName = feature != null ? feature.Name : "Hole";
                        holeBuilder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["diameter"] = diameter;
                        result["depth"] = depth;
                        result["location"] = new JArray(x, y, z);
                        result["hole_type"] = holeType;
                        result["face_reference"] = faceReference;
                        return ToolHelpers.Ok(
                            string.Format("Created hole '{0}' (dia={1}, depth={2}, type={3}) at ({4}, {5}, {6}).",
                                featureName, diameter, depth, holeType, x, y, z),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { holeBuilder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_hole failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 7. nx_pattern
    // ========================================================================

    /// <summary>
    /// Create a feature pattern (阵列) of any supported type.
    ///
    /// Pattern types (NX2412 PatternEnum — journal特征.cs / 特征2.cs 官方录制逆向):
    ///   linear (RectangularDefinition, 1D/2D), circular (CircularDefinition),
    ///   polygon (PolygonDefinition), spiral (SpiralDefinition),
    ///   along_path (AlongPathDefinition), helix (HelixDefinition),
    ///   mirror (MirrorDefinition).
    ///
    /// 官方序列 (journal特征2.cs 2026-08-19):
    ///   ① PatternMethod=Simple
    ///   ② FeatureList.Add(Feature[]) — 目标特征 (journal: FindObject("SIMPLE HOLE(3)") 强转 HolePackage)
    ///   ③ ReferencePointService.Point=CreatePoint(参考点) — 对所有类型都要设 (journal 在类型切换前)
    ///   ④ PatternService.PatternType=PatternEnum.X
    ///   ⑤ 对应 Definition 参数 (X/Y spacing, AngularSpacing, PolygonSpacing, ...)
    ///   ⑥ ParentFeatureInternal=false
    ///   ⑦ Commit()
    ///
    /// Parameters:
    ///   features     (string[], required) -- 目标特征名 (按 Name/journal_id 匹配)
    ///   pattern_type (string, default "linear") -- linear|circular|polygon|spiral|along_path|helix|mirror
    ///   reference_x/y/z (number, default 0) -- 参考点 (第一份特征的真实位置), 所有类型都要!
    ///
    ///   linear:   count, spacing, direction (default "Z"), y_count (可选 2D), y_spacing,
    ///             y_direction (default 垂直), rotation_angle (可选)
    ///   circular: count, angle_span (default 360), axis (X/Y/Z/±), center_x/y/z (圆心),
    ///             radial_count, radial_spacing (可选, 径向阵列)
    ///   polygon:  count (每边份数), number_of_sides (default 6), span_angle (default 360),
    ///             axis, radial_count, radial_spacing
    ///   spiral:   count (沿螺旋份数), number_of_turns (default 1), total_angle (default 360),
    ///             radial_pitch (default 10), pitch (沿路径节距, default 50)
    ///   along_path: path (路径曲线/边名, required), count, pitch, span
    ///   helix:    count (实例数), number_of_turns (default 2), angle_pitch (default 30),
    ///             distance_pitch (default 10), helix_pitch (default 50), helix_span (default 100)
    ///   mirror:   plane (镜像基准面名, required)
    /// </summary>
    public class PatternTool : IToolHandler
    {
        public string Name { get { return "nx_pattern"; } }
        public string Description { get { return "Create a pattern (linear/circular/polygon/spiral/along_path/helix/mirror) of features."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> features = ToolHelpers.GetStringArray(parameters, "features");
                string patternType = ToolHelpers.GetString(parameters, "pattern_type", "linear");
                int count = ToolHelpers.GetInt(parameters, "count", 5);
                double spacing = ToolHelpers.GetDouble(parameters, "spacing", 10.0);
                string direction = ToolHelpers.GetString(parameters, "direction", "Z");
                string axis = ToolHelpers.GetString(parameters, "axis", null);
                double angleSpan = ToolHelpers.GetDouble(parameters, "angle_span", 360.0);

                if (features.Count == 0)
                    return ToolHelpers.Fail("Parameter 'features' is required.");

                string pt = patternType.Trim().ToLower();
                if (pt == "mirror" || pt == "along_path")
                {
                    // These two require a geometry reference by name (plane / path)
                }

                using (var mark = new UndoMarkScope(session, "Pattern"))
                {
                    dynamic builder = workPart.Features.CreatePatternFeatureBuilder(null);
                    try
                    {
                        // NX2412 (probe 2026-08-17): PatternMethodOptions = Variational(1)/Simple(2)/SingleOutput(4).
                        // Old PatternMethod=0/1 was INVALID → "被约束的属性超出极限值". Use Simple.
                        builder.PatternMethod = NXOpen.Features.PatternFeatureBuilder.PatternMethodOptions.Simple;

                        // NX2412 (probe 2026-08-17 3 轮): PatternDefinition 顶层无 Count/SpacingDistance/
                        // ArcAngle/AxisDirection/PatternDirection（这些旧代码属性全不存在）。
                        // 正确路径: PatternType + (Rect|Circ)ularDefinition.*Spacing.SpaceType +
                        // NCopies/PitchDistance/PitchAngle 表达式。
                        dynamic patternService = builder.PatternService;

                        // ① 目标特征: 用 FindObject (官方 journal 序列) 优先, 回退 FindFeatureByName。
                        // journal: objects1[0]=(HolePackage)workPart.Features.FindObject("SIMPLE HOLE(3)");
                        //          FeatureList.Add(objects1)
                        foreach (string featName in features)
                        {
                            NXOpen.Features.Feature feat = FindPatternFeature(workPart, featName);
                            if (feat != null)
                                builder.FeatureList.Add(new NXOpen.Features.Feature[] { feat });
                        }

                        // ② 参考点: 官方序列在 PatternType 切换之前设置, 所有类型都要。
                        // 参考点 = 阵列基准位置 (特征的实际位置, journal 用孔心 20,20,16.7857),
                        // 不设报"该操作的对象不正确"。注: 参考点是"第一份"锚点, 不是阵列圆心。
                        double rx = ToolHelpers.GetDouble(parameters, "reference_x", 0.0);
                        double ry = ToolHelpers.GetDouble(parameters, "reference_y", 0.0);
                        double rz = ToolHelpers.GetDouble(parameters, "reference_z", 0.0);
                        builder.ReferencePointService.Point = workPart.Points.CreatePoint(new NXOpen.Point3d(rx, ry, rz));

                        switch (pt)
                        {
                            case "circular":
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Circular;
                                dynamic circ = patternService.CircularDefinition;
                                dynamic angSpacing = circ.AngularSpacing;
                                // NX2412 (journal特征2.cs 官方录制 2026-08-19 + _fan_pattern.cs 实测 9×360°):
                                // 必须 SpaceType=Span, 设 NCopies + SpanAngle (angle_span 参数落点)。
                                // 不能用 SpacingType.Pitch + PitchAngle: Pitch 模式下 NCopies 是派生只读
                                // 表达式, 设 RightHandSide 报"被约束的属性超出极限值"/份数错。
                                angSpacing.SpaceType = NXOpen.GeometricUtilities.PatternSpacing.SpacingType.Span;
                                angSpacing.NCopies.RightHandSide = count.ToString("F3");
                                angSpacing.SpanAngle.RightHandSide = angleSpan.ToString("F3");
                                // 径向阵列 (可选): RadialSpacing.NCopies/PitchDistance
                                int radialCount = ToolHelpers.GetInt(parameters, "radial_count", 1);
                                double radialSpacing = ToolHelpers.GetDouble(parameters, "radial_spacing", 10.0);
                                if (radialCount > 1)
                                {
                                    dynamic radial = circ.RadialSpacing;
                                    radial.SpaceType = NXOpen.GeometricUtilities.PatternSpacing.SpacingType.Pitch;
                                    radial.NCopies.RightHandSide = radialCount.ToString("F3");
                                    radial.PitchDistance.RightHandSide = radialSpacing.ToString("F3");
                                }
                                // 旋转轴: 官方 journal 序列 = Axes.CreateAxis(Point3d, Vector3d, UpdateOption)
                                // (journal特征.cs line 300/660 官方录制; _fan_pattern.cs 实测同配方)。
                                // 圆心 = 参考点 (参考点是"第一份"锚点, 圆心是旋转轴经过点, 可不同)。
                                double cx = ToolHelpers.GetDouble(parameters, "center_x", rx);
                                double cy = ToolHelpers.GetDouble(parameters, "center_y", ry);
                                double cz = ToolHelpers.GetDouble(parameters, "center_z", rz);
                                circ.RotationCenter = workPart.Points.CreatePoint(new NXOpen.Point3d(cx, cy, cz));
                                NXOpen.Vector3d axisVec = ModelingHelpers.GetDirectionVector(session,
                                    string.IsNullOrEmpty(axis) ? "Z" : axis);
                                circ.RotationAxis = workPart.Axes.CreateAxis(
                                    new NXOpen.Point3d(cx, cy, cz), (NXOpen.Vector3d)axisVec,
                                    NXOpen.SmartObject.UpdateOption.WithinModeling);
                                break;

                            case "polygon":
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Polygon;
                                dynamic poly = patternService.PolygonDefinition;
                                // journal特征.cs: PolygonSpacing.SpaceType=PolygonCountPerSide + NCopies 每边份数
                                dynamic polySpacing = poly.PolygonSpacing;
                                polySpacing.SpaceType =
                                    NXOpen.GeometricUtilities.PatternSpacing.SpacingType.PolygonCountPerSide;
                                polySpacing.NCopies.RightHandSide = count.ToString("F3");
                                poly.NumberOfSides.RightHandSide =
                                    ToolHelpers.GetInt(parameters, "number_of_sides", 6).ToString("F3");
                                polySpacing.SpanAngle.RightHandSide = angleSpan.ToString("F3");
                                // 径向阵列 (可选)
                                int pRadial = ToolHelpers.GetInt(parameters, "radial_count", 1);
                                double pRadialSp = ToolHelpers.GetDouble(parameters, "radial_spacing", 10.0);
                                if (pRadial > 1)
                                {
                                    dynamic radial = poly.RadialSpacing;
                                    radial.SpaceType = NXOpen.GeometricUtilities.PatternSpacing.SpacingType.Pitch;
                                    radial.NCopies.RightHandSide = pRadial.ToString("F3");
                                    radial.PitchDistance.RightHandSide = pRadialSp.ToString("F3");
                                }
                                // 法向: HorizontalRef.HorizontalRefVector (journal 设 X 方向 0° 参考)
                                dynamic pDirVec = ModelingHelpers.GetDirectionVector(session,
                                    string.IsNullOrEmpty(axis) ? "Z" : axis);
                                poly.HorizontalRef.HorizontalRefVector = workPart.Directions.CreateDirection(
                                    new NXOpen.Point3d(0.0, 0.0, 0.0), (NXOpen.Vector3d)pDirVec,
                                    NXOpen.SmartObject.UpdateOption.WithinModeling);
                                break;

                            case "spiral":
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Spiral;
                                dynamic spir = patternService.SpiralDefinition;
                                spir.NumberOfTurns.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "number_of_turns", 1.0).ToString("F3");
                                spir.TotalAngle.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "total_angle", 360.0).ToString("F3");
                                spir.RadialPitch.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "radial_pitch", 10.0).ToString("F3");
                                // PitchAlongSpiral: 沿螺旋的份数 + 节距
                                dynamic along = spir.PitchAlongSpiral;
                                along.NCopies.RightHandSide = count.ToString("F3");
                                along.OnPathPitchDistance.Expression.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "pitch", 50.0).ToString("F3");
                                break;

                            case "helix":
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Helix;
                                dynamic helix = patternService.HelixDefinition;
                                helix.CountOfInstances.RightHandSide = count.ToString("F3");
                                helix.NumberOfTurns.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "number_of_turns", 2.0).ToString("F3");
                                helix.AnglePitch.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "angle_pitch", 30.0).ToString("F3");
                                helix.DistancePitch.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "distance_pitch", 10.0).ToString("F3");
                                helix.HelixPitch.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "helix_pitch", 50.0).ToString("F3");
                                helix.HelixSpan.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "helix_span", 100.0).ToString("F3");
                                break;

                            case "along_path":
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.AlongPath;
                                dynamic ap = patternService.AlongPathDefinition;
                                ap.XPathOption = NXOpen.GeometricUtilities.AlongPathPattern.PathOptions.Offset;
                                dynamic xOn = ap.XOnPathSpacing;
                                xOn.NCopies.RightHandSide = count.ToString("F3");
                                xOn.OnPathPitchDistance.Expression.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "pitch", 50.0).ToString("F3");
                                xOn.OnPathSpanDistance.Expression.RightHandSide =
                                    ToolHelpers.GetDouble(parameters, "span", 100.0).ToString("F3");
                                // 路径: 需要把曲线加入 Section (Path = Section)。journal 用 section3。
                                dynamic pathSec = workPart.Sections.CreateSection(0.0095, 0.01, 0.5);
                                string pathName = ToolHelpers.GetString(parameters, "path", null);
                                if (string.IsNullOrEmpty(pathName))
                                    throw new ArgumentException(
                                        "Along-path pattern requires a 'path' (curve/edge name).");
                                dynamic pathCurve = ToolHelpers.FindCurveByName(workPart, pathName);
                                if (pathCurve == null)
                                    throw new ArgumentException(
                                        string.Format("Path curve '{0}' not found.", pathName));
                                // 路径 Section 填充: 复用 ModelingHelpers.FillSection (SelectionIntentRule + 7参 AddToSection,
                                // 已验证的 NX2412 配方) — 1参 AddToSection(NXObject[]) 不存在。
                                if (!ModelingHelpers.FillSection(pathSec, workPart, pathCurve))
                                    throw new ArgumentException(
                                        string.Format("Failed to add path curve '{0}' to section.", pathName));
                                ap.Path = pathSec;
                                break;

                            case "mirror":
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Mirror;
                                string planeName = ToolHelpers.GetString(parameters, "plane", null);
                                if (string.IsNullOrEmpty(planeName))
                                    throw new ArgumentException(
                                        "Mirror pattern requires a 'plane' (datum plane name).");
                                // MirrorDefinition.NewPlane = DatumPlane (journal特征.cs line 45)
                                dynamic plane = FindDatumPlaneByName(workPart, planeName);
                                if (plane == null)
                                    throw new ArgumentException(
                                        string.Format("Datum plane '{0}' not found.", planeName));
                                patternService.MirrorDefinition.NewPlane = plane;
                                break;

                            default: // linear (1D/2D)
                                patternService.PatternType =
                                    NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Linear;
                                dynamic rect = patternService.RectangularDefinition;
                                dynamic xSpacing = rect.XSpacing;
                                xSpacing.SpaceType = NXOpen.GeometricUtilities.PatternSpacing.SpacingType.Pitch;
                                xSpacing.PitchDistance.RightHandSide = spacing.ToString("F3");
                                xSpacing.NCopies.RightHandSide = count.ToString("F3");
                                // 方向: XDirection 是 Direction — 用 Part.Directions.CreateDirection(Point3d, Vector3d, UpdateOption)
                                dynamic dirVec = ModelingHelpers.GetDirectionVector(session, direction);
                                dynamic dir = workPart.Directions.CreateDirection(
                                    new NXOpen.Point3d(0.0, 0.0, 0.0),
                                    (NXOpen.Vector3d)dirVec,
                                    NXOpen.SmartObject.UpdateOption.WithinModeling);
                                rect.XDirection = dir;
                                // 2D 线性 (可选): UseYDirectionToggle=true + YDirection + YSpacing
                                int yCount = ToolHelpers.GetInt(parameters, "y_count", 1);
                                if (yCount > 1)
                                {
                                    string yDir = ToolHelpers.GetString(parameters, "y_direction",
                                        ModelingHelpers.PerpendicularDirection(direction));
                                    double ySpacing = ToolHelpers.GetDouble(parameters, "y_spacing", spacing);
                                    rect.UseYDirectionToggle = true;
                                    dynamic yDirVec = ModelingHelpers.GetDirectionVector(session, yDir);
                                    dynamic yDirObj = workPart.Directions.CreateDirection(
                                        new NXOpen.Point3d(0.0, 0.0, 0.0),
                                        (NXOpen.Vector3d)yDirVec,
                                        NXOpen.SmartObject.UpdateOption.WithinModeling);
                                    rect.YDirection = yDirObj;
                                    dynamic ySpacingObj = rect.YSpacing;
                                    ySpacingObj.SpaceType = NXOpen.GeometricUtilities.PatternSpacing.SpacingType.Pitch;
                                    ySpacingObj.PitchDistance.RightHandSide = ySpacing.ToString("F3");
                                    ySpacingObj.NCopies.RightHandSide = yCount.ToString("F3");
                                }
                                else
                                {
                                    rect.UseYDirectionToggle = false;
                                }
                                // 旋转角 (可选, HorizontalRef.RotationAngle)
                                double rotAngle = ToolHelpers.GetDouble(parameters, "rotation_angle", 0.0);
                                if (Math.Abs(rotAngle) > 0.0001)
                                    rect.HorizontalRef.RotationAngle.RightHandSide = rotAngle.ToString("F3");
                                break;
                        }

                        // ③ 官方序列关键行 (journal特征2.cs line 348): ParentFeatureInternal=false
                        // 缺此行某些特征类型 Commit 可能报错/行为异常。
                        builder.ParentFeatureInternal = false;

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Pattern";
                        builder.Destroy();

                        mark.Commit();

                        var data = new JObject();
                        data["feature"] = featureName;
                        data["features"] = new JArray(features);
                        data["pattern_type"] = patternType;
                        data["count"] = count;
                        data["reference_x"] = rx;
                        data["reference_y"] = ry;
                        data["reference_z"] = rz;
                        if (pt == "circular")
                        {
                            data["axis"] = axis;
                            data["angle_span"] = angleSpan;
                        }
                        else if (pt == "linear")
                        {
                            data["spacing"] = spacing;
                            data["direction"] = direction;
                        }

                        return ToolHelpers.Ok(
                            string.Format("Patterned {0} feature(s) x{1} ({2}).",
                                features.Count, count, patternType),
                            data);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_pattern failed: {0}", ex.Message));
            }
        }

        /// <summary>
        /// 找目标特征: 先 FindObject (官方 journal 序列), 回退 FindFeatureByName/FindFeature。
        /// </summary>
        private static NXOpen.Features.Feature FindPatternFeature(dynamic workPart, string name)
        {
            try
            {
                return (NXOpen.Features.Feature)workPart.Features.FindObject(name);
            }
            catch { }
            dynamic f = ToolHelpers.FindFeature(workPart, name);
            return f as NXOpen.Features.Feature;
        }

        /// <summary>
        /// 按名字找基准平面 (DatumPlane)。journal: MirrorDefinition.NewPlane = DatumPlane。
        /// </summary>
        private static dynamic FindDatumPlaneByName(dynamic workPart, string name)
        {
            try
            {
                foreach (dynamic obj in workPart.Datums)
                {
                    if (obj != null && obj.Name == name)
                        return obj;
                }
            }
            catch { }
            try
            {
                foreach (dynamic obj in workPart.Features)
                {
                    if (obj != null && obj.Name == name &&
                        obj.FeatureType.ToString().Contains("DatumPlane"))
                        return obj;
                }
            }
            catch { }
            return null;
        }
    }

    // ========================================================================
    // 8. nx_boolean
    // ========================================================================

    /// <summary>
    /// Perform a boolean operation (unite, subtract, intersect) between bodies.
    ///
    /// Parameters:
    ///   boolean_type (string, required)   -- unite, subtract, intersect
    ///   targets      (string[], required) -- List of target body names (first = Target, rest become tools)
    ///   tools        (string[], optional) -- Extra tool body names (beyond targets[1:])
    /// </summary>
    public class BooleanTool : IToolHandler
    {
        public string Name { get { return "nx_boolean"; } }
        public string Description { get { return "Perform a boolean operation (unite, subtract, intersect)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string booleanType = ToolHelpers.GetString(parameters, "boolean_type", null);
                List<string> targets = ToolHelpers.GetStringArray(parameters, "targets");

                if (string.IsNullOrEmpty(booleanType))
                    return ToolHelpers.Fail("Parameter 'boolean_type' is required.");
                if (targets.Count == 0)
                    return ToolHelpers.Fail("Parameter 'targets' is required.");

                // NX2412: BooleanType lives under GeometricUtilities.BooleanOperation.BooleanType
                object boolType;
                string key = booleanType.Trim().ToLower();
                switch (key)
                {
                    case "unite":
                        boolType = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Unite;
                        break;
                    case "subtract":
                        boolType = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
                        break;
                    case "intersect":
                        boolType = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Intersect;
                        break;
                    default:
                        return ToolHelpers.Fail(
                            string.Format("Invalid boolean_type '{0}'. Use: unite, subtract, intersect.",
                                booleanType));
                }

                // W2 (2026-09-03): 语义名含布尔类型
                using (var mark = new UndoMarkScope(session, "Boolean@" + key))
                {
                    dynamic builder = workPart.Features.CreateBooleanBuilder(null);
                    try
                    {
                        builder.Operation = (NXOpen.Features.Feature.BooleanType)boolType;
                        // NX2412 (probe 2026-08-17): BooleanBuilder has Target (single) + Tools (list) —
                        // TargetBody/ToolBody do NOT exist (DLR "未包含 TargetBody 的定义"). First target
                        // → Target, remaining targets + tools param → Tools.Add().
                        // W3 (2026-09-03, A3): 目标/工具解析从 FindBodyByName 升级为 GetTargetBody
                        // 三段式 — tag 数字 / body 名 / feature journal_id 均可 (与 extrude/revolve 对齐)。
                        List<string> tools = ToolHelpers.GetStringArray(parameters, "tools");
                        bool firstTarget = true;
                        int resolvedTargets = 0, resolvedTools = 0;

                        // Commit 前 body 数 — W4 (2026-09-03, A4) 假成功校验基准
                        int bodyCountBefore = 0;
                        try { foreach (dynamic b in workPart.Bodies) { if (b != null) bodyCountBefore++; } } catch { }

                        foreach (string targetName in targets)
                        {
                            if (string.IsNullOrEmpty(targetName)) continue;
                            string err;
                            dynamic targetBody = ToolHelpers.GetTargetBody(workPart, targetName, out err);
                            if (targetBody == null)
                            {
                                // 工具体缺一个可继续; 目标体缺失则报错 (没有目标一切无从谈起)
                                if (firstTarget)
                                    return ToolHelpers.Fail(err ?? ("未找到目标体 '" + targetName + "' (支持 body 名 / tag / journal_id)。"));
                                continue;
                            }
                            resolvedTargets++;
                            if (firstTarget) { builder.Target = targetBody; firstTarget = false; }
                            else builder.Tools.Add(targetBody);
                        }
                        foreach (string toolName in tools)
                        {
                            if (string.IsNullOrEmpty(toolName)) continue;
                            string err;
                            dynamic toolBody = ToolHelpers.GetTargetBody(workPart, toolName, out err);
                            if (toolBody != null) { builder.Tools.Add(toolBody); resolvedTools++; }
                        }
                        if (firstTarget)
                            return ToolHelpers.Fail("未找到任何有效目标体。请用显式 target 参数: body 名 / tag / feature journal_id (空名 body 可用 tag 或 nx_inspect_topology 查 tag)。");

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Boolean";
                        builder.Destroy();

                        // W4 (2026-09-03, A4 静默假成功): 面贴无体积重叠时 NX kernel 不合并但也不报错。
                        // Commit 后复查 body 数 — 未减少即没有实际合并 (u/s/i 均要求体积重叠>0)。
                        int bodyCountAfter = 0;
                        try { foreach (dynamic b in workPart.Bodies) { if (b != null) bodyCountAfter++; } } catch { }
                        if (bodyCountAfter >= bodyCountBefore && bodyCountBefore > 0)
                        {
                            return ToolHelpers.Fail(string.Format(
                                "nx_boolean {0} 返回成功但 body 数未减少 (前 {1} → 后 {2}) — 典型的无体积重叠假成功。" +
                                "NX kernel 只能粘合体积重叠>0 的体; 完全共面的贴合面无法合并。" +
                                "建议: 让工具体伸入目标体 ≥1mm (如灯芯下探), 或改走 extrude boolean 加长截面。",
                                key, bodyCountBefore, bodyCountAfter));
                        }

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["boolean_type"] = booleanType;
                        result["targets"] = new JArray(targets);
                        result["bodies_before"] = bodyCountBefore;
                        result["bodies_after"] = bodyCountAfter;
                        result["resolved_targets"] = resolvedTargets;
                        result["resolved_tools"] = resolvedTools;
                        return ToolHelpers.Ok(
                            string.Format("Boolean {0} applied to {1} target(s) + {2} tool(s); body {3}→{4}.",
                                booleanType, resolvedTargets, resolvedTools, bodyCountBefore, bodyCountAfter),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_boolean failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 8b. nx_unite_all  (M0-20260914 A4)
    // ========================================================================

    /// <summary>
    /// 把 work part 里**所有实体**合并成 1 个。
    ///
    /// 为什么需要（A4 前提，已用数据验证）：
    ///   `f764bfb5` 原件 `gt_batch_L1.txt` 记 **BODIES=1** VOL=85059.3623，
    ///   而同一草图轮廓被拉伸时输出 **2 个独立体** —— 体积相同、拓扑不同。
    ///   人工作品是 1 体，所以"几何对但多体"是真实缺陷，不是可忽略的差异。
    ///
    /// `nx_boolean` 能做同一件事，但要求逐个传体名；本工具省掉枚举步骤，
    /// 且可挂在**任意特征之后**（extrude 的多体告警里也会提示这个工具）。
    ///
    /// 与 nx_boolean 同款的假成功校验：unite 要求体积重叠 &gt; 0，
    /// 完全共面贴合无法合并 —— Commit 后 body 数没减少即报失败。
    /// </summary>
    public class UniteAllTool : IToolHandler
    {
        public string Name { get { return "nx_unite_all"; } }
        public string Description { get { return "Unite ALL solid bodies of the work part into one. Use after a feature reports multiple bodies (e.g. an extrude whose sketch has several non-connected closed loops). No parameters required. Reports bodies_before/bodies_after so a silent no-op is visible."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                var bodies = new List<NXOpen.Body>();
                var names = new List<string>();
                int sheetsSkipped = 0;
                try
                {
                    foreach (dynamic b in workPart.Bodies)
                    {
                        if (b == null) continue;
                        // IsSolidBody 是**属性**非方法 (7 处既有调用点实证: FindSameBodiesTool.cs:155 /
                        // ValidateTools.cs:127 / AssemblyTools.cs:379 等)。片体不参与 unite。
                        // 判不出时按"实体"算 —— 宁可让 unite 报错也不要静默漏体。
                        bool solid;
                        try { solid = (bool)b.IsSolidBody; } catch { solid = true; }
                        if (!solid) { sheetsSkipped++; continue; }
                        bodies.Add((NXOpen.Body)b);
                        string nm = null;
                        try { nm = ToolHelpers.GetJournalId((NXOpen.NXObject)b); } catch { }
                        if (string.IsNullOrEmpty(nm))
                        {
                            try { nm = "tag=" + ((NXOpen.NXObject)b).Tag; } catch { nm = "body" + names.Count; }
                        }
                        names.Add(nm);
                    }
                }
                catch (Exception ex)
                {
                    return ToolHelpers.Fail("枚举 work part 实体失败: " + ex.Message);
                }

                if (bodies.Count == 0)
                    return ToolHelpers.Fail("work part 里没有实体（0 个 solid body）。");

                if (bodies.Count == 1)
                {
                    var single = new JObject();
                    single["bodies_before"] = 1;
                    single["bodies_after"] = 1;
                    single["united"] = false;
                    single["body_names"] = new JArray(names.ToArray());
                    if (sheetsSkipped > 0) single["sheets_skipped"] = sheetsSkipped;
                    return ToolHelpers.Ok("只有 1 个实体，无需合并。", single);
                }

                int bodiesBefore = bodies.Count;
                using (var mark = new UndoMarkScope(session, "UniteAll@" + bodiesBefore))
                {
                    dynamic builder = workPart.Features.CreateBooleanBuilder(null);
                    try
                    {
                        // 与 BooleanTool 同款 (L2098): builder.Operation 收 Features.Feature.BooleanType
                        builder.Operation = (NXOpen.Features.Feature.BooleanType)
                            ModelingHelpers.GetBooleanType("unite");
                        // BooleanBuilder: Target(单个) + Tools(列表) — TargetBody/ToolBody 不存在
                        builder.Target = bodies[0];
                        for (int i = 1; i < bodies.Count; i++) builder.Tools.Add(bodies[i]);

                        dynamic feature = builder.Commit();
                        builder.Destroy();
                        mark.Commit();

                        int after = 0;
                        try
                        {
                            foreach (dynamic b in workPart.Bodies)
                            {
                                if (b == null) continue;
                                bool solid = false;
                                try { solid = (bool)b.IsSolidBody; } catch { }
                                if (solid) after++;
                            }
                        }
                        catch { }

                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["bodies_before"] = bodiesBefore;
                        result["bodies_after"] = after;
                        result["united"] = after < bodiesBefore;
                        result["merged_from"] = new JArray(names.ToArray());
                        if (sheetsSkipped > 0) result["sheets_skipped"] = sheetsSkipped;

                        if (after >= bodiesBefore)
                        {
                            // 与 nx_boolean 同款假成功拦截: 无体积重叠时 NX kernel 不合并也不报错
                            result["geometry_warning"] = string.Format(
                                "unite 返回成功但实体数未减少（前 {0} → 后 {1}）—— 典型的无体积重叠假成功。"
                                + "NX kernel 只能粘合体积重叠>0 的体；完全共面的贴合面无法合并。"
                                + "先确认这些体是否真的相接，或让其中一个伸入另一个 ≥1mm。",
                                bodiesBefore, after);
                            return ToolHelpers.Ok(
                                string.Format("Unite {0} bodies reported success but body count did not drop.", bodiesBefore),
                                result);
                        }

                        return ToolHelpers.Ok(
                            string.Format("United {0} solid bodies into {1}.", bodiesBefore, after),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_unite_all failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 9. nx_delete_feature
    // ========================================================================

    /// <summary>
    /// Delete a feature by name or journal_id. Returns available features if not found.
    ///
    /// Parameters:
    ///   name       (string, optional) -- Name of the feature to delete
    ///   journal_id (string, optional) -- JournalIdentifier of the feature (works for unnamed features)
    /// </summary>
    public class DeleteFeatureTool : IToolHandler
    {
        public string Name { get { return "nx_delete_feature"; } }
        public string Description { get { return "Delete a feature by name or journal_id. Returns available features if not found."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string name = ToolHelpers.GetString(parameters, "name", null);
                string journalId = ToolHelpers.GetString(parameters, "journal_id", null);
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(journalId))
                    return ToolHelpers.Fail("Parameter 'name' or 'journal_id' is required.");

                dynamic found = null;
                var available = new List<string>();
                foreach (dynamic feat in workPart.Features)
                {
                    // JournalIdentifier lives on NXObject (实测确认); catch only guards
                    // non-NXObject entries from DLR binding (expected, not an error path)
                    string jid = null;
                    try { jid = feat.JournalIdentifier; } catch { }
                    available.Add(feat.Name + "[" + jid + "]");
                    if (!string.IsNullOrEmpty(name) && feat.Name == name)
                        found = feat;
                    else if (!string.IsNullOrEmpty(journalId) && jid == journalId)
                        found = feat;
                }

                if (found == null)
                {
                    string availableStr = string.Join(", ",
                        available.GetRange(0, Math.Min(30, available.Count)));
                    return ToolHelpers.Fail(
                        string.Format("Feature '{0}' not found. Available: {1}", name ?? journalId, availableStr));
                }

                using (var mark = new UndoMarkScope(session, "DeleteFeature"))
                {
                    // NX2412: FeatureCollection.Delete(Feature) does not exist (DLR binding
                    // fails with 'FeatureCollection does not contain definition for Delete').
                    // Use UF_MODL_delete_feature instead (plugin compiles with NXOpen.UF.dll).
                    try
                    {
                        UFSession ufs = UFSession.GetUFSession();
                        // 反编译验证的 signature: void DeleteFeature(Tag[] cmtags)
                        // NXObject.Tag is already NXOpen.Tag (per MeasureTools precedent)
                        NXOpen.Tag featTag = (NXOpen.Tag)found.Tag;
                        ufs.Modl.DeleteFeature(new NXOpen.Tag[] { featTag });
                    }
                    catch (Exception ufEx)
                    {
                        return ToolHelpers.Fail(
                            string.Format("UF delete failed: {0}", ufEx.Message));
                    }
                    mark.Commit();
                }

                return ToolHelpers.Ok(
                    string.Format("Feature '{0}' deleted.", name ?? journalId),
                    new JObject() { { "deleted", name ?? journalId } });
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_delete_feature failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 10. nx_edit_feature
    // ========================================================================

    /// <summary>
    /// Edit a feature's parameters by updating its expressions.
    ///
    /// Parameters:
    ///   name       (string, optional) -- Feature name to edit
    ///   journal_id (string, optional) -- Feature journal_id (e.g. "Pattern Feature(6)")
    ///   params (object, required) -- Key-value pairs of parameter names to new values
    /// </summary>
    public class EditFeatureTool : IToolHandler
    {
        public string Name { get { return "nx_edit_feature"; } }
        public string Description { get { return "Edit a feature's parameters. Use name or journal_id."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string name = ToolHelpers.GetString(parameters, "name", null);
                string journalId = ToolHelpers.GetString(parameters, "journal_id", null);
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(journalId))
                    return ToolHelpers.Fail("Parameter 'name' or 'journal_id' is required.");

                JObject paramObj = ToolHelpers.GetObject(parameters, "params");
                if (paramObj == null)
                    return ToolHelpers.Fail("Parameter 'params' (object) is required.");

                dynamic found = ToolHelpers.FindFeature(workPart, name ?? journalId);
                if (found == null)
                    return ToolHelpers.Fail(string.Format("Feature '{0}' not found.", name ?? journalId));

                using (var mark = new UndoMarkScope(session, "EditFeature"))
                {
                    var updated = new JObject();
                    // NX2412 (probe 2026-08-17): Feature.GetExpression does NOT exist —
                    // iterate GetExpressions() and match by name (DLR "未包含 GetExpression 的定义")
                    dynamic[] exprs = found.GetExpressions();
                    foreach (var prop in paramObj.Properties())
                    {
                        dynamic matched = null;
                        foreach (dynamic expr in exprs)
                        {
                            if (expr.Name == prop.Name) { matched = expr; break; }
                        }
                        if (matched == null)
                            return ToolHelpers.Fail(string.Format("Expression '{0}' not found on feature '{1}'. Available: {2}",
                                prop.Name, name, string.Join(", ", Array.ConvertAll(exprs, e => (string)e.Name))));
                        matched.SetFormula(prop.Value.ToString());
                        updated[prop.Name] = prop.Value;
                    }

                    mark.Commit();
                    var result = new JObject();
                    result["feature"] = name;
                    result["updated"] = updated;
                    return ToolHelpers.Ok(
                        string.Format("Feature '{0}' updated.", name),
                        result);
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_edit_feature failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 10a. nx_rebuild_model
    // ========================================================================

    /// <summary>
    /// Trigger model rebuild after expression changes.
    /// Source: 实测 → UpdateManager.DoUpdate(UndoMarkId), Session.SetUndoMark
    /// NX2412 runtime: no-arg DoUpdate() does NOT exist — must pass UndoMarkId
    /// </summary>
    public class RebuildModelTool : IToolHandler
    {
        public string Name { get { return "nx_rebuild_model"; } }
        public string Description { get { return "Rebuild/update the model after expression or parameter changes."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolHelpers.Fail("No work part.");
                dynamic markId = session.SetUndoMark(0, "RebuildModel");
                session.UpdateManager.DoUpdate(markId);
                return ToolHelpers.Ok("Model updated.");
            }
            catch (Exception ex) { return ToolHelpers.Fail("nx_rebuild_model: " + ex.Message); }
        }
    }

    // ========================================================================
    // 11. nx_mirror_body
    // ========================================================================

    /// <summary>
    /// Mirror a body across a datum plane.
    ///
    /// Parameters:
    ///   body  (string, required) -- Name of the body to mirror
    ///   plane (string, required) -- Name of the datum plane for mirroring
    /// </summary>
    public class MirrorBodyTool : IToolHandler
    {
        public string Name { get { return "nx_mirror_body"; } }
        public string Description { get { return "Mirror a body across a datum plane."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string body = ToolHelpers.GetString(parameters, "body", null);
                string plane = ToolHelpers.GetString(parameters, "plane", null);
                if (string.IsNullOrEmpty(body))
                    return ToolHelpers.Fail("Parameter 'body' is required.");
                if (string.IsNullOrEmpty(plane))
                    return ToolHelpers.Fail("Parameter 'plane' is required.");

                using (var mark = new UndoMarkScope(session, "MirrorBody"))
                {
                    dynamic builder = workPart.Features.CreateMirrorBodyBuilder(null);
                    try
                    {
                        // Resolve body by name
                        dynamic resolvedBody = ToolHelpers.FindBodyByName(workPart, body);
                        if (resolvedBody == null)
                            return ToolHelpers.Fail(string.Format("Body '{0}' not found.", body));
                        // NX2412 (probe 2026-08-17): MirrorBodyCollector is read-only ScCollector;
                        // the real selection API is MirrorBodyList (SelectBodyList) + Plane (SelectDatumPlane).
                        builder.MirrorBodyList.Add(resolvedBody);

                        // Resolve mirror plane by name
                        dynamic resolvedPlane = null;
                        foreach (dynamic datum in workPart.Datums)
                        {
                            if (datum.Name == plane) { resolvedPlane = datum; break; }
                        }
                        if (resolvedPlane != null)
                        {
                            // NX2412 (实测 2026-08-17): SelectDatumPlane.SetValue requires
                            // (DatumPlane, View, Point3d) — no 1-arg overload.
                            builder.Plane.SetValue(resolvedPlane, null, new NXOpen.Point3d(0.0, 0.0, 0.0));
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Mirror";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["body_input"] = body;
                        result["plane"] = plane;
                        return ToolHelpers.Ok(
                            string.Format("Mirrored body '{0}' across plane '{1}'.", body, plane),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_mirror_body failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 12. nx_shell
    // ========================================================================

    /// <summary>
    /// Hollow out a solid body by removing specified faces and applying uniform wall thickness.
    ///
    /// Parameters:
    ///   thickness        (number, required)   -- Wall thickness (positive, inward).
    ///   faces_to_remove  (string[], optional) -- Face names to remove (empty = inner hollow).
    ///   face_tags        (integer[], optional) -- Face tags to remove (NX2412 faces are nameless;
    ///     ⚠️ for a through tube with BOTH ends open you MUST pass both end-face tags here,
    ///     otherwise you get a closed cup).
    ///   body             (string, optional)   -- Target body: body 名 / journal_id / tag（多 body 时必须显式指定）。
    ///   thickness_flip   (boolean, optional)  -- Flip shell direction (default false = inward).
    ///
    /// Returns:
    ///   journal_id  (string) -- 特征句柄 "SHELL(2)" (NX2412 Name 为空, 用此引用)
    ///   body        (string) -- 输出体 JournalIdentifier
    /// </summary>
    public class ShellTool : IToolHandler
    {
        public string Name { get { return "nx_shell"; } }
        public string Description { get { return "Hollow out a solid body by removing specified faces and applying uniform wall thickness."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                double thickness = ToolHelpers.GetDouble(parameters, "thickness", 2.0);
                List<string> facesToRemove = ToolHelpers.GetStringArray(parameters, "faces_to_remove");
                List<int> faceTags = ToolHelpers.GetIntArray(parameters, "face_tags");
                string bodyTarget = ToolHelpers.GetString(parameters, "body", ToolHelpers.GetString(parameters, "target", null));

                using (var mark = new UndoMarkScope(session, "Shell@t" + thickness))
                {
                    dynamic builder = workPart.Features.CreateShellBuilder(null);
                    int addedFaces = 0;
                    try
                    {
                        // NX2412: SetDefaultThickness(string) — 实测确认
                        builder.SetDefaultThickness(thickness.ToString("F3"));
                        // P2 (2026-09-02): 显式 body/target 替代盲选 foreach(Bodies) 取第一个。
                        // 探针+实测验证: ShellBuilder.Body must be set explicitly,
                        // otherwise shell acts on all bodies → "抽壳数据无效" (2026-08-13)
                        string shellBodyErr;
                        dynamic shellTargetBody = ToolHelpers.GetTargetBody(workPart, bodyTarget, out shellBodyErr);
                        if (shellTargetBody == null && !string.IsNullOrEmpty(shellBodyErr))
                            return ToolHelpers.Fail(shellBodyErr);
                        if (shellTargetBody != null)
                        {
                            try { builder.Body = shellTargetBody; } catch { }
                        }
                        // W1 (2026-09-03, A1 实测反转): NX2412 DefaultThicknessFlip=false(默认) 实测
                        // 得到【外扩壳】(top_funnel 61,101mm³ 畸形: 内壁 r=外壁+3, 材料加外侧);
                        // flip=true 得内壁 r10/顶口Ø80 正确 (53,744mm³)。因此工具默认改为 true=向内。
                        // (2026-08-13 "flip=true broke conical-shell 抽壳数据无效" 在引入 Body 显式
                        // 选择 + Tolerance 0.01 后不再复现 — 09-02 锥壳 top_funnel flip=true 实测成功)
                        bool thicknessFlip = ToolHelpers.GetBool(parameters, "thickness_flip", true);
                        builder.DefaultThicknessFlip = thicknessFlip;

                        // NX2412: shell fails with "公差错误" when Tolerance stays at its
                        // default (0) on conical geometry — GUI uses ~0.01. Set explicitly.
                        try { builder.Tolerance = 0.01; } catch { }

                        // 使用 RemovedFacesCollector (ScCollector) — 赋值而非 Add
                        if (faceTags.Count > 0)
                        {
                            var faceList = new System.Collections.Generic.List<NXOpen.Face>();
                            foreach (int tag in faceTags)
                            {
                                dynamic face = ToolHelpers.FindFaceByTag(workPart, tag);
                                if (face != null && face is NXOpen.Face) { faceList.Add((NXOpen.Face)face); addedFaces++; }
                            }
                            if (faceList.Count > 0)
                            {
                                var faceArr = faceList.ToArray();
                                NXOpen.FaceDumbRule faceRule = workPart.ScRuleFactory.CreateRuleFaceDumb(faceArr);
                                NXOpen.SelectionIntentRule[] rules = new NXOpen.SelectionIntentRule[] { faceRule };
                                dynamic collector = workPart.ScCollectors.CreateCollector();
                                collector.ReplaceRules(rules, false);
                                builder.RemovedFacesCollector = (NXOpen.ScCollector)collector;
                            }
                        }
                        else if (facesToRemove.Count > 0)
                        {
                            var faceList = new System.Collections.Generic.List<NXOpen.Face>();
                            foreach (string faceName in facesToRemove)
                            {
                                dynamic face = ModelingHelpers.FindFaceByName(workPart, faceName);
                                if (face != null && face is NXOpen.Face) { faceList.Add((NXOpen.Face)face); addedFaces++; }
                            }
                            if (faceList.Count > 0)
                            {
                                var faceArr = faceList.ToArray();
                                NXOpen.FaceDumbRule faceRule = workPart.ScRuleFactory.CreateRuleFaceDumb(faceArr);
                                NXOpen.SelectionIntentRule[] rules = new NXOpen.SelectionIntentRule[] { faceRule };
                                dynamic collector = workPart.ScCollectors.CreateCollector();
                                collector.ReplaceRules(rules, false);
                                builder.RemovedFacesCollector = (NXOpen.ScCollector)collector;
                            }
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Shell";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["thickness"] = thickness;
                        result["removed_faces"] = addedFaces;
                        return ToolHelpers.Ok(
                            string.Format("Created shell with thickness {0} mm, removed {1} faces.", thickness, addedFaces),
                            result);
                    }
                    catch (Exception ex)
                    {
                        string dbg = "SHELL_ERR " + ex.GetType().Name + ": " + ex.Message;
                        try { dbg += " | Validate=" + builder.Validate(); } catch { }
                        try { dbg += " | facesAdded=" + addedFaces; } catch { }
                        try { builder.Destroy(); } catch { }
                        throw new Exception(dbg);
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_shell failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 13. nx_draft
    // ========================================================================

    /// <summary>
    /// Apply a draft angle to faces relative to a pull direction.
    ///
    /// Parameters:
    ///   faces          (string[], required) -- Face names to draft
    ///   angle          (number, required)   -- Draft angle in degrees
    ///   pull_direction (string, required)   -- Pull direction: X, Y, Z, -X, -Y, -Z
    ///   fixed_edge     (string, optional)   -- Edge or plane defining stationary reference
    /// </summary>
    public class DraftTool : IToolHandler
    {
        public string Name { get { return "nx_draft"; } }
        public string Description { get { return "Apply a draft angle to faces relative to a pull direction."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> faces = ToolHelpers.GetStringArray(parameters, "faces");
                double angle = ToolHelpers.GetDouble(parameters, "angle", 5.0);
                string pullDirection = ToolHelpers.GetString(parameters, "pull_direction", "Z");
                string fixedEdge = ToolHelpers.GetString(parameters, "fixed_edge", null);

                if (faces.Count == 0)
                    return ToolHelpers.Fail("Parameter 'faces' is required.");

                dynamic dirVec = ModelingHelpers.GetDirectionVector(session, pullDirection);

                using (var mark = new UndoMarkScope(session, "Draft"))
                {
                    dynamic builder = workPart.Features.CreateDraftBuilder(null);
                    try
                    {
                        // NX2412 (probe 2026-08-17): DraftBuilder has NO Angle property.
                        // Real API: TypeOfDraft.Face + FaceSetAngleExpressionList (ExpressionCollectorSetList)
                        // + Direction. Angle via set.Collector (ScCollector) + set.ItemValue (Expression).
                        builder.TypeOfDraft = NXOpen.Features.DraftBuilder.Type.Face;
                        builder.Direction = ModelingHelpers.CreateDirection(workPart,
                            new double[] { 0, 0, 0 },
                            new double[] { dirVec.X, dirVec.Y, dirVec.Z });

                        // NX2412: use FaceCollector (SCCollector) instead of AddFace()
                        var faceCollector = workPart.ScCollectors.CreateCollector();
                        var faceList = new System.Collections.Generic.List<dynamic>();
                        foreach (string faceName in faces)
                        {
                            dynamic face = ModelingHelpers.FindFaceByName(workPart, faceName);
                            if (face != null)
                                faceList.Add(face);
                        }
                        if (faceList.Count > 0)
                        {
                            var faceArr = faceList.ConvertAll(f => (NXOpen.Face)f).ToArray();
                            dynamic faceRule = workPart.ScRuleFactory.CreateRuleFaceDumb(faceArr);
                            faceCollector.ReplaceRules(new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)faceRule }, false);
                            dynamic set = builder.FaceSetAngleExpressionList.CreateSet();
                            set.Collector = faceCollector;
                            set.ItemValue.RightHandSide = angle.ToString("F1");
                        }
                        else
                            return ToolHelpers.Fail("No valid faces found. Faces matched by name — face names may be empty (pass face tags via face_tags param if supported).");

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Draft";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["angle"] = angle;
                        result["faces"] = new JArray(faces);
                        return ToolHelpers.Ok(
                            string.Format("Applied {0} degree draft to {1} face(s).", angle, faces.Count),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_draft failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 14. nx_trim_body
    // ========================================================================

    /// <summary>
    /// Trim a solid body using a plane or face as the cutting tool.
    ///
    /// Parameters:
    ///   body         (string, required) -- Name of body to trim
    ///   tool         (string, required) -- Name of trimming plane or face
    ///   side_to_keep (string, optional) -- positive or negative (default positive)
    /// </summary>
    public class TrimBodyTool : IToolHandler
    {
        public string Name { get { return "nx_trim_body"; } }
        public string Description { get { return "Trim a solid body using a plane or face as the cutting tool."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string bodyName = ToolHelpers.GetString(parameters, "body", null);
                // NX2412 2026-08-17: renamed 'tool' → 'cutting_tool' — the nx-exec MCP adapter
                // reserves 'tool' as its tool-selector field (params.tool overrode the tool name).
                string toolName = ToolHelpers.GetString(parameters, "cutting_tool", null);
                string sideToKeep = ToolHelpers.GetString(parameters, "side_to_keep", "positive");

                if (string.IsNullOrEmpty(bodyName))
                    return ToolHelpers.Fail("Parameter 'body' is required.");
                if (string.IsNullOrEmpty(toolName))
                    return ToolHelpers.Fail("Parameter 'tool' is required.");

                // Resolve target body
                dynamic targetBody = ToolHelpers.FindBodyByName(workPart, bodyName);
                if (targetBody == null)
                    return ToolHelpers.Fail(string.Format("Body '{0}' not found.", bodyName));

                using (var mark = new UndoMarkScope(session, "TrimBody"))
                {
                    dynamic builder = workPart.Features.CreateTrimBodyBuilder(null);
                    try
                    {
                        builder.AddTarget(targetBody);

                        // Resolve trimming tool (face or datum plane)
                        dynamic trimmingPlaneOrFace = ModelingHelpers.FindFaceByName(workPart, toolName);
                        if (trimmingPlaneOrFace == null)
                        {
                            foreach (dynamic datum in workPart.Datums)
                            {
                                if (datum.Name == toolName) { trimmingPlaneOrFace = datum; break; }
                            }
                        }
                        if (trimmingPlaneOrFace != null)
                        {
                            // NX2412 (journal 直通 2026-08-17): TrimBodyBuilder.Tool 必须接收
                            // NXOpen.Plane 对象 — face/datum plane/Feature 全部报 "工具由无效的实体组成"。
                            // face/datum → 用其几何创建 Plane(origin, normal, UpdateOption)。
                            if (trimmingPlaneOrFace is NXOpen.DatumPlane)
                            {
                                NXOpen.DatumPlane dp = (NXOpen.DatumPlane)trimmingPlaneOrFace;
                                builder.Tool = workPart.Planes.CreatePlane(
                                    dp.Origin, dp.Normal,
                                    NXOpen.SmartObject.UpdateOption.WithinModeling);
                            }
                            else if (trimmingPlaneOrFace is NXOpen.Face)
                            {
                                NXOpen.UF.UFSession uf = NXOpen.UF.UFSession.GetUFSession();
                                int ftype;
                                double[] pt = new double[3];
                                double[] dir = new double[3];
                                double[] box = new double[6];
                                double radius = 0, radData = 0;
                                int normDir = 0;
                                uf.Modl.AskFaceData(
                                    ((NXOpen.Face)trimmingPlaneOrFace).Tag,
                                    out ftype, pt, dir, box, out radius, out radData, out normDir);
                                builder.Tool = workPart.Planes.CreatePlane(
                                    new NXOpen.Point3d(pt[0], pt[1], pt[2]),
                                    new NXOpen.Vector3d(dir[0], dir[1], dir[2]),
                                    NXOpen.SmartObject.UpdateOption.WithinModeling);
                            }
                            else
                            {
                                builder.Tool = trimmingPlaneOrFace;
                            }
                        }

                        // Configure which side to keep
                        string side = sideToKeep.Trim().ToLower();
                        // NX2412 (probe 2026-08-17): TrimDirection = DirectionType
                        // (Invalid=0/PositiveNormal=1/NegativeNormal=-1) — 缺此设置报 "无效实体"
                        builder.TrimDirection = (side == "negative")
                            ? NXOpen.Features.TrimBodyBuilder.DirectionType.NegativeNormal
                            : NXOpen.Features.TrimBodyBuilder.DirectionType.PositiveNormal;

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "TrimBody";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["body_input"] = bodyName;
                        result["tool"] = toolName;
                        result["side_to_keep"] = sideToKeep;
                        return ToolHelpers.Ok(
                            string.Format("Trimmed body '{0}' with tool '{1}'.", bodyName, toolName),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_trim_body failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 15. nx_loft
    // ========================================================================

    /// <summary>
    /// Create a lofted solid/surface through two or more cross-section sketches.
    ///
    /// Parameters:
    ///   sections      (string[], required) -- Ordered list of section/sketch names
    ///   guide_curves  (string[], optional) -- Optional guide curves
    ///   boolean       (string, optional)   -- none, unite, subtract, intersect
    /// </summary>
    public class LoftTool : IToolHandler
    {
        public string Name { get { return "nx_loft"; } }
        public string Description { get { return "Create a lofted solid/surface through two or more cross-section sketches."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> sections = ToolHelpers.GetStringArray(parameters, "sections");
                List<string> guideCurves = ToolHelpers.GetStringArray(parameters, "guide_curves");
                string boolean = ToolHelpers.GetString(parameters, "boolean", "none");

                if (sections.Count == 0)
                    return ToolHelpers.Fail("Parameter 'sections' is required.");

                using (var mark = new UndoMarkScope(session, "Loft"))
                {
                    dynamic builder = workPart.Features.CreateThroughCurvesBuilder(null);
                    try
                    {
                        foreach (string sectionName in sections)
                        {
                            dynamic section = ModelingHelpers.ResolveObjectByName(workPart, sectionName);
                            if (section == null)
                                return ToolHelpers.Fail(string.Format("Section '{0}' not found.", sectionName));
                            // NX2412 (probe 2026-08-17): Append requires Section[] — build a Section
                            // from the sketch's curves first (direct sketch append → overload error)
                            dynamic sec = workPart.Sections.CreateSection(0.00095, 0.001, 0.01);
                            if (!ModelingHelpers.FillSection(sec, workPart, section))
                                return ToolHelpers.Fail(string.Format("Section '{0}' has no usable curves.", sectionName));
                            builder.SectionsList.Append(new NXOpen.Section[] { (NXOpen.Section)sec });
                        }

                        // NX2412: ThroughCurvesBuilder does not support guide curves; guide_curves parameter is ignored.

                        // NX2412: ThroughCurvesBuilder has no BooleanOperation; boolean parameter is ignored.
                        // If boolean operation is needed, use a separate BooleanFeature pass.

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Loft";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["sections"] = new JArray(sections);
                        return ToolHelpers.Ok(
                            string.Format("Created loft through {0} section(s).", sections.Count),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_loft failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 16. nx_tube
    // ========================================================================

    /// <summary>
    /// Create a tube/pipe feature along a guide curve.
    ///
    /// Parameters:
    ///   guide_curve    (string, required) -- Name of the guide curve
    ///   outer_diameter (number, required) -- Outer diameter (mm)
    ///   inner_diameter (number, optional) -- Inner diameter (mm), 0 = solid
    ///   boolean        (string, optional) -- none, unite, subtract, intersect
    /// </summary>
    public class TubeTool : IToolHandler
    {
        public string Name { get { return "nx_tube"; } }
        public string Description { get { return "Create a tube/pipe feature along a guide curve."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string guideCurve = ToolHelpers.GetString(parameters, "guide_curve", null);
                double outerDiameter = ToolHelpers.GetDouble(parameters, "outer_diameter", 10.0);
                double innerDiameter = ToolHelpers.GetDouble(parameters, "inner_diameter", 0.0);
                string boolean = ToolHelpers.GetString(parameters, "boolean", "none");

                if (string.IsNullOrEmpty(guideCurve))
                    return ToolHelpers.Fail("Parameter 'guide_curve' is required.");

                dynamic guideObj = ModelingHelpers.ResolveObjectByName(workPart, guideCurve);
                if (guideObj == null)
                    return ToolHelpers.Fail(string.Format("Guide curve '{0}' not found.", guideCurve));

                using (var mark = new UndoMarkScope(session, "Tube"))
                {
                    dynamic builder = workPart.Features.CreateTubeBuilder(null);
                    try
                    {
                        // NX2412: TubeBuilder.Tolerance defaults to 0.0, which causes
                        // "NXOpen.NXException: Tolerance error." on Commit.
                        if (builder.Tolerance <= 0.0)
                            builder.Tolerance = 0.01;

                        // Set path section (guide curve) — 解析阶梯 (W6 2026-09-03 扩展):
                        //   L1 直接 IBaseCurve (Line/Arc/Spline 等) → CreateRuleBaseCurveDumb
                        //   L2 通用 NXOpen.Curve (helix 输出等) → CreateRuleCurveDumb(Curve[])
                        //      (实测证实 ScRuleFactory.CreateRuleCurveDumb 收 Curve[] — 2026-09-03)
                        //   L3 sketch → 取草图几何第一条可用曲线
                        //   L4 HELIX 等 Feature → GetEntities() 实体转 Curve/IBaseCurve
                        // ⚠️ 禁止 new dynamic[]{...} (运行时 object[], 无数组协变 → RuntimeBinderException)。
                        NXOpen.IBaseCurve guideIBase = null;
                        NXOpen.Curve guideCurveObj = null;
                        try { guideIBase = (NXOpen.IBaseCurve)guideObj; } catch { }
                        if (guideIBase == null) try { guideCurveObj = (NXOpen.Curve)guideObj; } catch { }

                        if (guideIBase == null && guideCurveObj == null)
                        {
                            // L3: sketch 几何
                            try
                            {
                                dynamic sc = guideObj.GetAllGeometry();
                                foreach (dynamic c in sc)
                                {
                                    try { guideIBase = (NXOpen.IBaseCurve)c; if (guideIBase != null) break; } catch { }
                                    if (guideIBase == null) try { guideCurveObj = (NXOpen.Curve)c; if (guideCurveObj != null) break; } catch { }
                                }
                            }
                            catch { }   // 过滤语义: 无法取几何则走 L4
                        }
                        if (guideIBase == null && guideCurveObj == null && guideObj is NXOpen.Features.Feature)
                        {
                            // L4: Feature (HELIXn 等曲线特征) → GetEntities() 实体
                            try
                            {
                                var entities = ((NXOpen.Features.Feature)guideObj).GetEntities();
                                if (entities != null)
                                {
                                    foreach (NXOpen.NXObject ent in entities)
                                    {
                                        if (ent == null) continue;
                                        try { var c = ent as NXOpen.Curve; if (c != null) { guideCurveObj = c; break; } } catch { }
                                        try { var ib = ent as NXOpen.IBaseCurve; if (ib != null) { guideIBase = ib; break; } } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                        if (guideIBase == null && guideCurveObj == null)
                            return ToolHelpers.Fail(string.Format("Guide '{0}' has no usable curve geometry.", guideCurve));

                        NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                        NXOpen.NXObject seedObj;
                        if (guideIBase != null)
                        {
                            dynamic rule = workPart.ScRuleFactory.CreateRuleBaseCurveDumb(new NXOpen.IBaseCurve[] { guideIBase });
                            NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule };
                            seedObj = (NXOpen.NXObject)guideIBase;
                            builder.PathSection.AddToSection(selRules, seedObj, null, null, helpPoint, 0, false);
                        }
                        else
                        {
                            // L2: 通用 Curve 规则 — helix 输出等非 IBaseCurve 曲线的唯一路径
                            dynamic rule = workPart.ScRuleFactory.CreateRuleCurveDumb(new NXOpen.Curve[] { guideCurveObj });
                            NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rule };
                            builder.PathSection.AddToSection(selRules, guideCurveObj, null, null, helpPoint, 0, false);
                        }

                        // Set diameters
                        builder.OuterDiameter.RightHandSide = outerDiameter.ToString();
                        if (innerDiameter > 0)
                            builder.InnerDiameter.RightHandSide = innerDiameter.ToString();

                        // W5 (2026-09-03, A5 修复): 原代码对只读 BooleanOption 赋 true 必炸
                        // ("无法为属性或索引器 BooleanOption 赋值 - 它是只读的") — 该赋值已删除。
                        // boolean 参数已从契约/tool-schemas 剔除; 需要布尔组合请对输出体单独跑 nx_boolean。

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Tube";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["guide_curve"] = guideCurve;
                        result["outer_diameter"] = outerDiameter;
                        result["inner_diameter"] = innerDiameter;
                        if (boolean.Trim().ToLower() != "none")
                            result["warning"] = "nx_tube boolean 已废弃 (BooleanOption 只读, A5/W5); 布尔请改用 nx_boolean 作用于输出体。";
                        return ToolHelpers.Ok(
                            string.Format("Created tube '{0}' (OD={1}, ID={2}) along '{3}'.",
                                featureName, outerDiameter, innerDiameter, guideCurve),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_tube failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 17. nx_split_body
    // ========================================================================

    /// <summary>
    /// Split a solid body using a plane or face as the cutting tool.
    ///
    /// Parameters:
    ///   body (string, required) -- Name of the body to split
    ///   tool (string, required) -- Name of the splitting plane or face
    /// </summary>
    public class SplitBodyTool : IToolHandler
    {
        public string Name { get { return "nx_split_body"; } }
        public string Description { get { return "Split a solid body using a plane or face as the cutting tool."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string bodyName = ToolHelpers.GetString(parameters, "body", null);
                // NX2412 2026-08-17: renamed 'tool' → 'cutting_tool' (adapter reserves 'tool').
                string toolName = ToolHelpers.GetString(parameters, "cutting_tool", null);

                if (string.IsNullOrEmpty(bodyName))
                    return ToolHelpers.Fail("Parameter 'body' is required.");
                if (string.IsNullOrEmpty(toolName))
                    return ToolHelpers.Fail("Parameter 'cutting_tool' is required.");

                // Resolve target body
                dynamic targetBody = ToolHelpers.FindBodyByName(workPart, bodyName);
                if (targetBody == null)
                    return ToolHelpers.Fail(string.Format("Body '{0}' not found.", bodyName));

                // Resolve splitting tool (face or datum plane)
                dynamic toolFace = ModelingHelpers.FindFaceByName(workPart, toolName);

                // Try datum planes if face not found
                if (toolFace == null)
                {
                    foreach (dynamic datum in workPart.Datums)
                    {
                        if (datum.Name == toolName) { toolFace = datum; break; }
                    }
                }

                if (toolFace == null)
                    return ToolHelpers.Fail(string.Format("Splitting tool '{0}' not found.", toolName));

                using (var mark = new UndoMarkScope(session, "SplitBody"))
                {
                    dynamic builder = workPart.Features.CreateSplitBodyBuilder(null);
                    try
                    {
                        builder.TargetBody.Add(targetBody);

                        builder.BooleanTool.ToolOption =
                            NXOpen.GeometricUtilities.BooleanToolBuilder.BooleanToolType.FaceOrPlane;

                        // Check if it's a datum plane or a face
                        // NX2412 (2026-08-17): Origin/Normal try/catch is unreliable — datum
                        // planes lack those properties AND faces may throw. Use type check.
                        bool isDatum = toolFace is NXOpen.DatumPlane;

                        if (isDatum)
                        {
                            // NX2412 (probe 2026-08-17): DatumPlane 有 Origin(Point3d)+Normal(Vector3d)
                            // (CreatePlane(Feature) 不行——DatumPlane 不是 Features.Feature)。
                            // 用 CreatePlane(origin, normal, UpdateOption) 构建 Plane 对象。
                            NXOpen.DatumPlane dp = (NXOpen.DatumPlane)toolFace;
                            dynamic plane = workPart.Planes.CreatePlane(
                                dp.Origin, dp.Normal,
                                NXOpen.SmartObject.UpdateOption.WithinModeling);
                            builder.BooleanTool.FacePlaneTool.ToolPlane = plane;
                        }
                        else
                        {
                            dynamic scRuleFactory = workPart.ScRuleFactory;
                            dynamic rules = scRuleFactory.CreateRuleFaceDumb(new NXOpen.Face[] { (NXOpen.Face)toolFace });
                            // NX2412 (probe 2026-08-17): ToolFaces 是 FaceSetData（非 ScCollector 子类），
                            // .FaceCollector 才是 ScCollector — 用 AddRules(SelectionIntentRule[])，无 Add()/AddRule()
                            builder.BooleanTool.FacePlaneTool.ToolFaces.FaceCollector.AddRules(
                                new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)rules });
                        }

                        dynamic feature = builder.CommitFeature();
                        string featureName = feature != null ? feature.Name : "SplitBody";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["body_input"] = bodyName;
                        result["tool"] = toolName;
                        return ToolHelpers.Ok(
                            string.Format("Split body '{0}' with tool '{1}'.", bodyName, toolName),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_split_body failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 18. nx_offset_surface
    // ========================================================================

    /// <summary>
    /// Offset faces of a body by a distance.
    ///
    /// Parameters:
    ///   faces    (string[], required) -- List of face names to offset
    ///   distance (number, required)   -- Offset distance (positive = outward, negative = inward)
    /// </summary>
    public class OffsetSurfaceTool : IToolHandler
    {
        public string Name { get { return "nx_offset_surface"; } }
        public string Description { get { return "Offset faces of a body by a distance."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> faces = ToolHelpers.GetStringArray(parameters, "faces");
                double distance = ToolHelpers.GetDouble(parameters, "distance", 1.0);

                if (faces.Count == 0)
                    return ToolHelpers.Fail("Parameter 'faces' is required.");

                using (var mark = new UndoMarkScope(session, "OffsetSurface"))
                {
                    dynamic builder = workPart.Features.CreateOffsetSurfaceBuilder(null);
                    try
                    {
                        builder.Radius.RightHandSide = distance.ToString(); /* NX2412: Distance renamed to Radius */

                        // NX2412: use FaceCollector via SCCollector instead of AddFace()
                        var faceCollector = workPart.ScCollectors.CreateCollector();
                        var faceList = new System.Collections.Generic.List<dynamic>();
                        var resolvedFaces = new List<string>();
                        foreach (string faceName in faces)
                        {
                            dynamic face = ModelingHelpers.FindFaceByName(workPart, faceName);
                            if (face != null)
                            {
                                faceList.Add(face);
                                resolvedFaces.Add(faceName);
                            }
                            else
                            {
                                return ToolHelpers.Fail(string.Format("Face '{0}' not found.", faceName));
                            }
                        }

                        // NX2412: set up face collector for OffsetSurfaceBuilder via FaceSets
                        if (faceList.Count > 0)
                        {
                            var faceArr = faceList.ConvertAll(f => (NXOpen.Face)f).ToArray();
                            dynamic faceRule = workPart.ScRuleFactory.CreateRuleFaceDumb(faceArr);
                            faceCollector.ReplaceRules(new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)faceRule }, false);
                            builder.FaceSets.Add(faceCollector);
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "OffsetSurface";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["faces"] = new JArray(resolvedFaces);
                        result["distance"] = distance;
                        return ToolHelpers.Ok(
                            string.Format("Offset {0} face(s) by {1} mm.", resolvedFaces.Count, distance),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_offset_surface failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 19. nx_sew
    // ========================================================================

    /// <summary>
    /// Sew multiple sheet bodies into one.
    ///
    /// Parameters:
    ///   sheets    (string[], required) -- List of sheet body names to sew
    ///   tolerance (number, optional)   -- Sewing tolerance (default 0.01)
    /// </summary>
    public class SewTool : IToolHandler
    {
        public string Name { get { return "nx_sew"; } }
        public string Description { get { return "Sew multiple sheet bodies into one."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                List<string> sheets = ToolHelpers.GetStringArray(parameters, "sheets");
                double tolerance = ToolHelpers.GetDouble(parameters, "tolerance", 0.01);

                if (sheets.Count == 0)
                    return ToolHelpers.Fail("Parameter 'sheets' is required.");

                using (var mark = new UndoMarkScope(session, "Sew"))
                {
                    dynamic builder = workPart.Features.CreateSewBuilder(null);
                    try
                    {
                        builder.Tolerance = tolerance;

                        var resolvedSheets = new List<string>();
                        foreach (string sheetName in sheets)
                        {
                            dynamic body = ToolHelpers.FindBodyByName(workPart, sheetName);
                            if (body != null)
                            {
                                builder.TargetBodies.Add(body);
                                resolvedSheets.Add(sheetName);
                            }
                            else
                            {
                                return ToolHelpers.Fail(string.Format("Sheet body '{0}' not found.", sheetName));
                            }
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Sew";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["sheets"] = new JArray(resolvedSheets);
                        result["tolerance"] = tolerance;
                        return ToolHelpers.Ok(
                            string.Format("Sewed {0} sheet body(ies) with tolerance {1}.",
                                resolvedSheets.Count, tolerance),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_sew failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 20. nx_thicken
    // ========================================================================

    /// <summary>
    /// Thicken a sheet body into a solid.
    ///
    /// Parameters:
    ///   sheet_body (string, required) -- Name of the sheet body to thicken
    ///   thickness  (number, required) -- Thickness value
    ///   direction  (string, optional) -- Thicken direction: X, Y, Z (default Z)
    /// </summary>
    public class ThickenTool : IToolHandler
    {
        public string Name { get { return "nx_thicken"; } }
        public string Description { get { return "Thicken a sheet body into a solid."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string sheetBody = ToolHelpers.GetString(parameters, "sheet_body", null);
                double thickness = ToolHelpers.GetDouble(parameters, "thickness", 1.0);
                string direction = ToolHelpers.GetString(parameters, "direction", "Z");

                if (string.IsNullOrEmpty(sheetBody))
                    return ToolHelpers.Fail("Parameter 'sheet_body' is required.");

                // Resolve sheet body
                dynamic targetBody = ToolHelpers.FindBodyByName(workPart, sheetBody);
                if (targetBody == null)
                    return ToolHelpers.Fail(string.Format("Sheet body '{0}' not found.", sheetBody));

                dynamic dirVec = ModelingHelpers.GetDirectionVector(session, direction);

                using (var mark = new UndoMarkScope(session, "Thicken"))
                {
                    dynamic builder = workPart.Features.CreateThickenBuilder(null);
                    try
                    {
                        // NX2412 (probe 2026-08-17): ThickenBuilder has FirstOffset/SecondOffset
                        // (Expression) + FaceCollector (read-only ScCollector) — no Thickness/FacesToThicken.
                        builder.FirstOffset.RightHandSide = thickness.ToString();
                        // NX2412: ThickenBuilder has no Direction property; direction parameter is ignored.
                        dynamic facesOnBody = targetBody.GetFaces();
                        var faceList = new System.Collections.Generic.List<NXOpen.Face>();
                        foreach (var face in facesOnBody) { try { faceList.Add((NXOpen.Face)face); } catch { } }
                        if (faceList.Count > 0)
                        {
                            dynamic faceRule = workPart.ScRuleFactory.CreateRuleFaceDumb(faceList.ToArray());
                            builder.FaceCollector.ReplaceRules(
                                new NXOpen.SelectionIntentRule[] { (NXOpen.SelectionIntentRule)faceRule }, false);
                        }

                        dynamic feature = builder.Commit();
                        string featureName = feature != null ? feature.Name : "Thicken";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        ToolHelpers.AddFeatureResult(result, feature);
                        result["sheet_body"] = sheetBody;
                        result["thickness"] = thickness;
                        result["direction"] = direction;
                        return ToolHelpers.Ok(
                            string.Format("Thickened '{0}' by {1} mm along {2}.",
                                sheetBody, thickness, direction),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_thicken failed: {0}", ex.Message));
            }
        }
    }

    // ========================================================================
    // 21. nx_helix
    // ========================================================================

    /// <summary>
    /// Create a helix curve.
    ///
    /// Parameters:
    ///   pitch       (number, required) -- Pitch distance between turns
    ///   height      (number, required) -- Total height of the helix
    ///   radius      (number, required) -- Radius of the helix
    ///   axis        (string, optional) -- Axis direction: X, Y, Z (default Z)
    ///   start_angle (number, optional) -- Start angle in degrees (default 0)
    /// </summary>
    public class HelixTool : IToolHandler
    {
        public string Name { get { return "nx_helix"; } }
        public string Description { get { return "Create a helix curve."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                double pitch = ToolHelpers.GetDouble(parameters, "pitch", 5.0);
                double height = ToolHelpers.GetDouble(parameters, "height", 10.0);
                double radius = ToolHelpers.GetDouble(parameters, "radius", 5.0);
                string axis = ToolHelpers.GetString(parameters, "axis", "Z");
                double startAngle = ToolHelpers.GetDouble(parameters, "start_angle", 0.0);

                using (var mark = new UndoMarkScope(session, "Helix"))
                {
                    dynamic builder = workPart.Features.CreateHelixBuilder(null);
                    try
                    {
                        // Verified NX2412 real API (2026-08-04, 反编译 + 实测):
                        //   SizeLaw/PitchLaw = LawBuilder (LawType = Type.Constant + Value.Value)
                        //   SizeOption = SizeOptions.Radius, LengthMethod = LengthMethods.Turns
                        //   NumberOfTurns (string), StartAngle (Expression, degrees)
                        //   CoordinateSystem (Origin + Matrix) controls axis
                        builder.SizeOption = NXOpen.Features.HelixBuilder.SizeOptions.Radius;
                        builder.SizeLaw.LawType = NXOpen.GeometricUtilities.LawBuilder.Type.Constant;
                        builder.SizeLaw.Value.Value = radius;
                        builder.PitchLaw.LawType = NXOpen.GeometricUtilities.LawBuilder.Type.Constant;
                        builder.PitchLaw.Value.Value = pitch;
                        builder.LengthMethod = NXOpen.Features.HelixBuilder.LengthMethods.Turns;
                        builder.NumberOfTurns = (height / pitch).ToString("0.###");
                        builder.StartAngle.Value = startAngle;

                        // Axis orientation via coordinate system rotation (Matrix3x3 = 9 double fields Xx..Zz, no Identity):
                        //   default Z-up = identity; X = rotate about Y by 90deg; Y = rotate about X by 90deg
                        NXOpen.Matrix3x3 m = new NXOpen.Matrix3x3();
                        m.Xx = 1.0; m.Yy = 1.0; m.Zz = 1.0; // identity base
                        if (axis == "X")
                        {
                            // Z-axis of helix csys points along +X
                            m.Xx = 0.0; m.Xy = 0.0; m.Xz = -1.0;
                            m.Zx = 1.0; m.Zy = 0.0; m.Zz = 0.0;
                        }
                        else if (axis == "Y")
                        {
                            // Z-axis of helix csys points along +Y
                            m.Yx = 0.0; m.Yy = 0.0; m.Yz = -1.0;
                            m.Zx = 0.0; m.Zy = 1.0; m.Zz = 0.0;
                        }
                        // Verified 2026-08-04: 3-param overload CreateCoordinateSystem(Point3d, Matrix3x3, bool isTemporary)
                        dynamic csys = workPart.CoordinateSystems.CreateCoordinateSystem(
                            new NXOpen.Point3d(0.0, 0.0, 0.0), m, false);
                        builder.CoordinateSystem = csys;

                        dynamic curve = builder.Commit();
                        string curveName = curve != null ? curve.Name : "Helix";
                        builder.Destroy();

                        // W6 (2026-09-03, A6): HELIX 输出 Name 为空且非 sketch → 下游 tube/sweep
                        // 无法按名引用 ("Guide curve not found")。命名 "HELIXn" (n 递增, ASCII),
                        // ResolveObjectByName 已扩展可解析 feature 名。
                        string helixHandle = "HELIX" + (CountHelixLike(workPart) + 1);
                        try { curve.SetName(helixHandle); curveName = helixHandle; } catch { }

                        mark.Commit();
                        var result = new JObject();
                        result["feature"] = curveName;
                        result["journal_id"] = ToolHelpers.GetJournalId(curve);
                        result["name"] = curveName;
                        result["pitch"] = pitch;
                        result["height"] = height;
                        result["radius"] = radius;
                        result["axis"] = axis;
                        result["start_angle"] = startAngle;
                        result["guide_ref"] = "nx_tube/sweep 请传 guide_curve=" + helixHandle;
                        return ToolHelpers.Ok(
                            string.Format("Created helix '{0}' (pitch={1}, height={2}, radius={3}, axis={4}); guide_ref={5}.",
                                curveName, pitch, height, radius, axis, helixHandle),
                            result);
                    }
                    catch (Exception ex)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_helix failed: {0}", ex.Message));
            }
        }

        /// <summary>统计已存在的 HELIXn 命名对象 (feature/curve), 供 W6 命名递增。</summary>
        private static int CountHelixLike(dynamic workPart)
        {
            int n = 0;
            try
            {
                foreach (dynamic feat in workPart.Features)
                {
                    if (feat == null) continue;
                    try { if (feat.Name.ToString().StartsWith("HELIX")) n++; } catch { }
                }
            }
            catch { }
            try
            {
                foreach (dynamic c in workPart.Curves)
                {
                    if (c == null) continue;
                    try { if (c.Name.ToString().StartsWith("HELIX")) n++; } catch { }
                }
            }
            catch { }
            return n;
        }
    }

    // ========================================================================
    // 22. nx_text_curve
    // ========================================================================

    /// <summary>
    /// Create planar text as curves (TextBuilder, Types.Planar).
    /// Verified 2026-08-04 (反编译 + 实测):
    ///   Features.CreateTextBuilder(null) — NXOpen.Features.TextBuilder
    ///   TextString (string), Type = Types.Planar, SelectFont(font, script)
    ///   PlanarFrame = RectangularFrameBuilder (CoordinateSystem + Length/Height Expression + UpdateOnCoordinateSystem)
    /// </summary>
    public class TextCurveTool : IToolHandler
    {
        public string Name { get { return "nx_text_curve"; } }
        public string Description { get { return "Create planar text curves (TextBuilder, Types.Planar)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolHelpers.Fail("No active work part.");

                string text = ToolHelpers.GetString(parameters, "text", "");
                if (string.IsNullOrEmpty(text))
                    return ToolHelpers.Fail("Parameter 'text' is required.");

                string font = ToolHelpers.GetString(parameters, "font", "blockfont");
                double height = ToolHelpers.GetDouble(parameters, "height", 10.0);
                double length = ToolHelpers.GetDouble(parameters, "length", height * 2.0);
                double ox = ToolHelpers.GetDouble(parameters, "origin_x", 0.0);
                double oy = ToolHelpers.GetDouble(parameters, "origin_y", 0.0);
                double oz = ToolHelpers.GetDouble(parameters, "origin_z", 0.0);

                using (var mark = new UndoMarkScope(session, "TextCurve"))
                {
                    dynamic builder = workPart.Features.CreateTextBuilder(null);
                    try
                    {
                        builder.Type = NXOpen.Features.TextBuilder.Types.Planar;
                        builder.TextString = text;
                        // Font is optional — NX font names (blockfont etc.) differ from OS names; default font is safe.
                        if (!string.IsNullOrEmpty(font) && font != "blockfont")
                            builder.SelectFont(font, NXOpen.Features.TextBuilder.ScriptOptions.Western);

                        // Placement frame: text box size + coordinate system
                        // Verified 2026-08-04: Planar text needs SectionPlane (placement plane) set first;
                        //   RectangularFrameBuilder.CoordinateSystem has a SETTER — create csys then assign
                        NXOpen.Matrix3x3 m = new NXOpen.Matrix3x3();
                        m.Xx = 1.0; m.Yy = 1.0; m.Zz = 1.0;
                        dynamic plane = workPart.Planes.CreateFixedPlane(
                            new NXOpen.Point3d(ox, oy, oz), m);
                        builder.SectionPlane = plane;
                        dynamic frame = builder.PlanarFrame;
                        frame.Length.Value = length;
                        frame.Height.Value = height;
                        dynamic csys = workPart.CoordinateSystems.CreateCoordinateSystem(
                            new NXOpen.Point3d(ox, oy, oz), m, false);
                        frame.CoordinateSystem = csys;
                        frame.UpdateOnCoordinateSystem();

                        dynamic feature = builder.Commit();
                        string name = feature != null ? feature.Name : "Text";
                        builder.Destroy();

                        mark.Commit();
                        var result = new JObject();
                        result["feature"] = name;
                        result["text"] = text;
                        result["font"] = font;
                        result["height"] = height;
                        result["origin"] = new JObject() { { "x", ox }, { "y", oy }, { "z", oz } };
                        return ToolHelpers.Ok(string.Format("Created text curves '{0}': \"{1}\"", name, text), result);
                    }
                    catch (Exception)
                    {
                        try { builder.Destroy(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolHelpers.Fail(string.Format("nx_text_curve failed: {0}", ex.Message));
            }
        }
    }
}
