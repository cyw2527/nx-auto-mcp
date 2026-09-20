using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Assembly
{
    // ========================================================================
    // GAP-14 (2026-09-08): 同一部件多零件 → 一键装配 (书 15.4 第三路线)
    //   拆两工具 (计划 §4.1 推荐 A):
    //     nx_find_same_bodies  — 19.1 查找相同体 (纯几何, 独立可测)
    //     nx_bodies_to_assembly — 唯一体 New Component (GAP-12 配方) + 重复实例 add 归位 + Fix
    //
    // 19.1 算法 (书 19.1.2, book.md:8937): 体质心 → 表面采样点 → 质心到各点距离排序
    //   → 两体距离序列公差内相同即判同。签名对刚体变换不变 (平移/旋转不影响质心距序列)。
    //   实现: 每面 AskFaceData 面内点 (与 AssemblyTools NxDirZ 同源反射), 质心=AskMassProps3d。
    // ========================================================================

    /// <summary>
    /// Find bodies with identical geometry (19.1 算法: 质心+表面采样距离序列, 公差内相同)。
    /// 纯几何, 不依赖装配; 刚体变换不变。
    /// </summary>
    /// Parameters:
    ///   bodies (array, optional) - 体名/journal id 列表 (缺省=工作部件全部实体)
    ///   tolerance (number, optional) - 相对公差 (默认 0.001 = 0.1%)
    ///
    public class FindSameBodiesTool : IToolHandler
    {
        public string Name { get { return "nx_find_same_bodies"; } }
        public string Description { get { return "Find bodies with identical geometry (19.1 算法: 质心+表面采样距离序列, 公差内相同, 刚体变换不变)."; } }

        /// <summary>体内签名 (质心 + 表面采样距离序列)</summary>
        public class BodySignature
        {
            public NXOpen.Body Body;
            public double[] Centroid;       // mm
            public double Volume;           // mm3
            public double[] Distances;      // 质心 → 每面采样点距离 (排序)
            public int FaceCount;
        }

        /// <summary>计算签名 (质心 via AskMassProps3d; 每面 AskFaceData 面内点)</summary>
        public static BodySignature ComputeSignature(NXOpen.Body body)
        {
            var sig = new BodySignature();
            sig.Body = body;

            // 质心 + 体积
            try
            {
                NXOpen.UF.UFSession ufs = NXOpen.UF.UFSession.GetUFSession();
                double[] acc = new double[11]; acc[0] = 0.001;
                double[] mp = new double[47]; double[] stats = new double[13];
                ufs.Modl.AskMassProps3d(new NXOpen.Tag[] { body.Tag }, 1, 1, 4, 0.0, 1, acc, mp, stats);
                sig.Volume = mp[1] * 1e9;   // m3 -> mm3
                sig.Centroid = new double[] { mp[3] * 1000.0, mp[4] * 1000.0, mp[5] * 1000.0 };
            }
            catch
            {
                // 回退: bbox 中心
                sig.Centroid = new double[] { 0, 0, 0 };
                try
                {
                    NXOpen.UF.UFSession ufs = NXOpen.UF.UFSession.GetUFSession();
                    double[] bb = new double[6];
                    ufs.Modl.AskBoundingBox(body.Tag, bb);
                    sig.Centroid[0] = (bb[0] + bb[3]) / 2; sig.Centroid[1] = (bb[1] + bb[4]) / 2; sig.Centroid[2] = (bb[2] + bb[5]) / 2;
                }
                catch { }
            }

            // 表面采样: 每面 AskFaceData 面内点 → 质心距
            var dists = new System.Collections.Generic.List<double>();
            foreach (NXOpen.Face f in body.GetFaces())
            {
                double[] pt = FaceInteriorPoint(f);
                if (pt == null) continue;
                double dx = pt[0] - sig.Centroid[0], dy = pt[1] - sig.Centroid[1], dz = pt[2] - sig.Centroid[2];
                dists.Add(Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            dists.Sort();
            sig.Distances = dists.ToArray();
            sig.FaceCount = body.GetFaces().Length;
            return sig;
        }

        /// <summary>AskFaceData 反射面内点 (与 AssemblyTools 同源; 无 System.Linq)</summary>
        private static double[] FaceInteriorPoint(NXOpen.Face f)
        {
            try
            {
                var ufs = NXOpen.UF.UFSession.GetUFSession();
                System.Reflection.MethodInfo m = null;
                foreach (var mi in ufs.Modl.GetType().GetMethods())
                    if (mi.Name == "AskFaceData") { m = mi; break; }
                if (m == null) return null;
                object[] argv = new object[8];
                argv[0] = f.Tag; argv[1] = 0;
                argv[2] = new double[3]; argv[3] = new double[3]; argv[4] = new double[6];
                argv[5] = 0.0; argv[6] = 0.0; argv[7] = 0;
                m.Invoke(ufs.Modl, argv);
                return (double[])argv[2];
            }
            catch { return null; }
        }

        /// <summary>两体签名同判 (面数相同 + 距离序列逐元素相对误差 &lt; tolerance)</summary>
        public static bool IsSame(BodySignature a, BodySignature b, double tolerance)
        {
            if (a.FaceCount != b.FaceCount) return false;
            if (a.Distances.Length != b.Distances.Length) return false;
            if (a.Distances.Length == 0) return false;
            for (int i = 0; i < a.Distances.Length; i++)
            {
                double denom = Math.Max(Math.Abs(a.Distances[i]), Math.Abs(b.Distances[i]));
                if (denom < 1e-9) denom = 1e-9;
                if (Math.Abs(a.Distances[i] - b.Distances[i]) / denom > tolerance) return false;
            }
            return true;
        }

        /// <summary>分组: 返回同组列表 (每组 [首个=代表, 其余=实例])</summary>
        public static List<List<BodySignature>> GroupSame(List<BodySignature> sigs, double tolerance)
        {
            var groups = new List<List<BodySignature>>();
            var used = new bool[sigs.Count];
            for (int i = 0; i < sigs.Count; i++)
            {
                if (used[i]) continue;
                var g = new List<BodySignature> { sigs[i] };
                used[i] = true;
                for (int j = i + 1; j < sigs.Count; j++)
                {
                    if (used[j]) continue;
                    if (IsSame(sigs[i], sigs[j], tolerance)) { g.Add(sigs[j]); used[j] = true; }
                }
                groups.Add(g);
            }
            return groups;
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                double tolerance = AssemblyParams.GetParamDouble(parameters, "tolerance", 0.001);
                JArray bodyNames = AssemblyParams.GetParamArray(parameters, "bodies");

                var targets = new List<NXOpen.Body>();
                foreach (NXOpen.Body bd in workPart.Bodies)
                    if (bd.IsSolidBody) targets.Add(bd);
                if (bodyNames != null && bodyNames.Count > 0)
                {
                    var filtered = new List<NXOpen.Body>();
                    foreach (var tok in bodyNames)
                    {
                        string key = tok.Value<string>();
                        foreach (NXOpen.Body bd in targets)
                        {
                            string name = bd.Name ?? "";
                            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                                || name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                            { filtered.Add(bd); break; }
                        }
                    }
                    targets = filtered;
                }

                var sigs = new List<BodySignature>();
                foreach (NXOpen.Body bd in targets) sigs.Add(ComputeSignature(bd));
                List<List<BodySignature>> groups = GroupSame(sigs, tolerance);

                var groupsJson = new JArray();
                int duplicateCount = 0;
                foreach (List<BodySignature> g in groups)
                {
                    var gj = new JObject();
                    gj["representative"] = g[0].Body.JournalIdentifier;
                    gj["member_count"] = g.Count;
                    gj["volume"] = g[0].Volume;
                    var members = new JArray();
                    foreach (BodySignature s in g)
                    {
                        var mj = new JObject();
                        mj["body"] = s.Body.JournalIdentifier;
                        mj["centroid"] = new JArray(s.Centroid[0], s.Centroid[1], s.Centroid[2]);
                        members.Add(mj);
                    }
                    gj["members"] = members;
                    groupsJson.Add(gj);
                    duplicateCount += g.Count - 1;
                }

                var data = new JObject();
                data["groups"] = groupsJson;
                data["unique_count"] = groups.Count;
                data["body_count"] = targets.Count;
                data["duplicate_count"] = duplicateCount;
                data["tolerance"] = tolerance;
                data["note"] = "19.1 算法 (质心+表面采样距离序列); 刚体变换不变; 公差相对默认 0.1%";
                return new ToolResult { Success = true, Message = string.Format("{0} body(s) → {1} unique group(s), {2} duplicate instance(s).", targets.Count, groups.Count, duplicateCount), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_find_same_bodies failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // nx_bodies_to_assembly (GAP-14 组合, 2026-09-08)
    // 多体 .prt → 装配: 唯一体 New Component (GAP-12 配方) + 重复实例 add 同原型归位 + Fix 全部
    // 位置锚点 = 体质心 (组件原点归位到原体质心 → 位置对拍一致)
    // ========================================================================

    /// <summary>
    /// Convert bodies of a multi-body part into an assembly (同一部件多零件一键装配)。
    /// dedupe=true (默认): 19.1 相同体归并 → 每组唯一体 New Component, 其余按原位置 add 同原型。
    /// 依赖 GAP-12 配方 (CreateNewComponentBuilder + NewFile 显式赋值)。
    /// </summary>
    /// Parameters:
    ///   bodies (array, optional) - 体名列表 (缺省=全部实体)
    ///   dedupe (boolean, optional) - 相同体归并 (默认 true)
    ///   tolerance (number, optional) - 19.1 公差 (默认 0.001)
    ///   dir (string, optional) - 新件目录 (缺省=工作部件目录)
    ///   fix (boolean, optional) - Fix 全部组件 (默认 true)
    ///
    public class BodiesToAssemblyTool : IToolHandler
    {
        public string Name { get { return "nx_bodies_to_assembly"; } }
        public string Description { get { return "Convert bodies of a multi-body part into an assembly (19.1 相同体归并 + New Component + 归位 + Fix)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                using (var mark = new NxMcpPlugin.Features.UndoMarkScope(session, "BodiesToAssembly"))
                {
                    bool dedupe = AssemblyParams.GetParamBool(parameters, "dedupe", true);
                    bool fix = AssemblyParams.GetParamBool(parameters, "fix", true);
                    double tolerance = AssemblyParams.GetParamDouble(parameters, "tolerance", 0.001);
                    string dir = AssemblyParams.GetParamString(parameters, "dir", "");
                    if (string.IsNullOrEmpty(dir))
                        dir = System.IO.Path.GetDirectoryName(workPart.FullPath);

                    // ---- 1. 目标体 ----
                    var bodies = new List<NXOpen.Body>();
                    foreach (NXOpen.Body bd in workPart.Bodies)
                        if (bd.IsSolidBody) bodies.Add(bd);
                    JArray bodyNames = AssemblyParams.GetParamArray(parameters, "bodies");
                    if (bodyNames != null && bodyNames.Count > 0)
                    {
                        var filtered = new List<NXOpen.Body>();
                        foreach (var tok in bodyNames)
                        {
                            string key = tok.Value<string>();
                            foreach (NXOpen.Body bd in bodies)
                            {
                                string name = bd.Name ?? "";
                                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                                    || name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                                { filtered.Add(bd); break; }
                            }
                        }
                        bodies = filtered;
                    }
                    if (bodies.Count == 0)
                        return ToolResult.Fail("No solid bodies to convert.").ToJson();

                    // ---- 2. 相同体分组 (dedupe) ----
                    // plan: [(代表体, [实例体])]
                    var plans = new List<Tuple<NXOpen.Body, List<NXOpen.Body>>>();
                    if (dedupe)
                    {
                        var sigs = new List<FindSameBodiesTool.BodySignature>();
                        foreach (NXOpen.Body bd in bodies) sigs.Add(FindSameBodiesTool.ComputeSignature(bd));
                        foreach (List<FindSameBodiesTool.BodySignature> g in FindSameBodiesTool.GroupSame(sigs, tolerance))
                        {
                            var reps = new List<NXOpen.Body>();
                            foreach (var s in g) reps.Add(s.Body);
                            plans.Add(new Tuple<NXOpen.Body, List<NXOpen.Body>>(g[0].Body, reps));
                        }
                    }
                    else
                    {
                        foreach (NXOpen.Body bd in bodies)
                            plans.Add(new Tuple<NXOpen.Body, List<NXOpen.Body>>(bd, new List<NXOpen.Body> { bd }));
                    }

                    // ---- 3. 转换 ----
                    var created = new JArray();
                    string lastErr = "";
                    var fixStatus = new JArray();
                    for (int p = 0; p < plans.Count; p++)
                    {
                        NXOpen.Body rep = plans[p].Item1;
                        List<NXOpen.Body> instances = plans[p].Item2;
                        string repId = "";
                        try { repId = rep.JournalIdentifier; } catch { }   // ★先捕获: 创建后体被移动删除
                        // ★质心须在创建前算 (New Component 移动体后原体不可再读)
                        var sig = FindSameBodiesTool.ComputeSignature(rep);
                        string compName = DefaultName(rep);
                        var r = MakeComponent(session, workPart, rep, compName, dir, fix);
                        if (r.Item1 == null)
                        {
                            lastErr = string.Format("body '{0}': {1}", repId, r.Item2);
                            break;
                        }
                        NXOpen.Assemblies.Component repComp = r.Item1;
                        string repPath = r.Item2;
                        fixStatus.Add(r.Item3);

                        // 唯一体组件归位到原体质心 (New Component 组件在 WCS 原点; 质心已在创建前算好)
                        if (Math.Abs(sig.Centroid[0]) > 1e-9 || Math.Abs(sig.Centroid[1]) > 1e-9 || Math.Abs(sig.Centroid[2]) > 1e-9)
                        {
                            var delta = new NXOpen.Vector3d { X = sig.Centroid[0], Y = sig.Centroid[1], Z = sig.Centroid[2] };
                            var ident = new NXOpen.Matrix3x3 { Xx = 1, Yy = 1, Zz = 1 };
                            try { workPart.ComponentAssembly.MoveComponent(repComp, delta, ident); } catch { }
                        }

                        var pj = new JObject();
                        pj["component"] = repComp.Name;
                        pj["part_path"] = repPath;
                        pj["body"] = repId;
                        pj["instances"] = instances.Count;
                        pj["fix_status"] = r.Item3;

                        // 重复实例: AddComponent 同原型 → 原质心位置
                        var instNames = new JArray();
                        for (int k = 1; k < instances.Count; k++)
                        {
                            var s2 = FindSameBodiesTool.ComputeSignature(instances[k]);
                            string instName = compName + "_" + (k + 1);
                            NXOpen.Assemblies.Component instComp = null;
                            NXOpen.PartLoadStatus loadStatus;
                            string[] refSets = new string[] { "MODEL", "Entire Part" };
                            string lastAddErr = "";
                            foreach (string rs in refSets)
                            {
                                try
                                {
                                    instComp = workPart.ComponentAssembly.AddComponent(repPath, rs, instName,
                                        new NXOpen.Point3d(s2.Centroid[0], s2.Centroid[1], s2.Centroid[2]),
                                        new NXOpen.Matrix3x3 { Xx = 1, Yy = 1, Zz = 1 }, -1, out loadStatus);
                                    try { loadStatus.Dispose(); } catch { }
                                    break;
                                }
                                catch (Exception e) { lastAddErr = e.Message; }
                            }
                            if (instComp == null)
                            {
                                lastErr = string.Format("add instance '{0}': {1}", instName, lastAddErr);
                                break;
                            }
                            if (fix) fixStatus.Add(FixOne(workPart, instComp));
                            instNames.Add(instComp.Name);
                            // 重复体转换后从源件删除 (体在 add 时未移动, 此处显式删; 组内第 0 个=代表体已被 New Component 移动)
                            try
                            {
                                var ufs = NXOpen.UF.UFSession.GetUFSession();
                                ufs.Obj.DeleteObject(instances[k].Tag);
                            }
                            catch { }
                        }
                        pj["instance_components"] = instNames;
                        created.Add(pj);
                        if (lastErr.Length > 0) break;
                    }

                    if (lastErr.Length > 0)
                    {
                        var errData = new JObject();
                        errData["created"] = created;
                        errData["failed"] = lastErr;
                        mark.Commit();   // 部分成功也保留已转换组件
                        return new ToolResult { Success = false, Message = "nx_bodies_to_assembly partial: " + lastErr, Data = errData }.ToJson();
                    }

                    // ---- 4. 返回值: 树快照 + BOM (零孤儿) ----
                    var data = new JObject();
                    data["created"] = created;
                    var tree = new JArray();
                    foreach (ComponentTree.Node root in ComponentTree.Hierarchy(workPart))
                        tree.Add(ComponentTree.NodeToJson(root, true));
                    data["tree"] = tree;
                    data["bom"] = ComponentTree.BuildBomJson(workPart);
                    data["note"] = "位置锚点=原体质心; 新件内存态, nx_save_part 后落盘; BOM 行数=唯一体数, 数量=组内实例数";
                    mark.Commit();   // 成功: mark 提交, Dispose 不回滚
                    return new ToolResult { Success = true, Message = string.Format("Converted {0} body(s) into assembly ({1} component instance(s)).", bodies.Count, created.Count), Data = data }.ToJson();
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_bodies_to_assembly failed: {0}", ex.Message)).ToJson();
            }
        }

        // GAP-12 配方 (与 CreateComponentTool.CreateOneComponent 同源; 独立内嵌避免跨工具耦合)
        private static Tuple<NXOpen.Assemblies.Component, string, string> MakeComponent(
            dynamic session, NXOpen.Part workPart, NXOpen.Body body, string compName, string dir, bool fix)
        {
            NXOpen.Assemblies.CreateNewComponentBuilder b = null;
            NXOpen.FileNew fnb = null;
            string partPath = System.IO.Path.Combine(dir, compName + ".prt");
            try
            {
                b = workPart.AssemblyManager.CreateNewComponentBuilder();
                fnb = (NXOpen.FileNew)session.Parts.FileNew();
                fnb.NewFileName = partPath;
                fnb.MakeDisplayedPart = false;
                fnb.UseBlankTemplate = true;
                fnb.Units = NXOpen.Part.Units.Millimeters;
                b.NewFile = fnb;
                b.NewComponentName = compName;
                b.ObjectForNewComponent.Add(body);
                b.OriginalObjectsDeleted = true;   // 移动 (默认 NX 语义)
                NXOpen.Assemblies.Component comp = b.Commit() as NXOpen.Assemblies.Component;
                if (comp == null) return new Tuple<NXOpen.Assemblies.Component, string, string>(null, partPath, "Commit null");
                string fs = fix ? FixOne(workPart, comp) : "n/a";
                return new Tuple<NXOpen.Assemblies.Component, string, string>(comp, partPath, fs);
            }
            catch (Exception ex)
            {
                return new Tuple<NXOpen.Assemblies.Component, string, string>(null, partPath, ex.Message);
            }
            finally
            {
                try { if (b != null) b.Destroy(); } catch { }
                try { if (fnb != null) fnb.Destroy(); } catch { }
            }
        }

        private static string DefaultName(NXOpen.Body body)
        {
            string name = body.Name ?? "";
            if (string.IsNullOrEmpty(name)) { try { name = body.JournalIdentifier ?? "BODY"; } catch { name = "BODY"; } }
            name = name.Replace("(", "_").Replace(")", "").Replace(" ", "_");
            if (string.IsNullOrEmpty(name)) name = "COMPONENT";
            return name.ToUpperInvariant();
        }

        // asm-06 Positioner Fix 配方 (与 CreateComponentTool.FixComponent 同源)
        private static string FixOne(NXOpen.Part workPart, NXOpen.Assemblies.Component comp)
        {
            var positioner = workPart.ComponentAssembly.Positioner as NXOpen.Positioning.ComponentPositioner;
            try
            {
                NXOpen.Positioning.Constraint constraint = null;
                NXOpen.Positioning.ComponentNetwork network = null;
                try
                {
                    network = positioner.EstablishNetwork() as NXOpen.Positioning.ComponentNetwork;
                    positioner.BeginAssemblyConstraints();
                    if (network != null)
                    {
                        network.NetworkArrangementsMode = NXOpen.Positioning.ComponentNetwork.ArrangementsMode.Existing;
                        network.MoveObjectsState = true;
                    }
                    constraint = positioner.CreateConstraint(true);
                    var cc = constraint as NXOpen.Positioning.ComponentConstraint;
                    cc.ConstraintType = NXOpen.Positioning.Constraint.Type.Fix;
                    var refFixed = constraint.CreateConstraintReference(comp, null, false, false, false);
                    if (refFixed == null) return "Fix ref null";
                    refFixed.SetFixHint(true);
                    network.Solve();
                    refFixed.SetFixHint(false);
                    string st = constraint.GetConstraintStatus().ToString();
                    positioner.ClearNetwork();
                    positioner.EndAssemblyConstraints();
                    return st;
                }
                finally
                {
                    try { positioner.EndAssemblyConstraints(); } catch { }
                }
            }
            catch (Exception ex)
            {
                return "Fix FAIL: " + ex.Message;
            }
        }
    }
}
