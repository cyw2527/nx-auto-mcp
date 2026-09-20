using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Assembly
{
    // ========================================================================
    // nx_create_component (GAP-12, 2026-09-08)
    // 自顶向下 New Component: 工作部件现有体 → 子零件 (.prt) + 装配组件
    //
    // 配方 (P-12 探针实证, docs/zhuangpei/_gap_test/GAP12-13-PROBES.md):
    //   ★工厂 = AssemblyManager.CreateNewComponentBuilder() (现代版)
    //     ⚠️ CreateCreateComponentBuilder()→CreateComponentBuilder 是遗留薄壳 (无配置属性), 勿用
    //   ★必须显式赋值 b.NewFile = Session.Parts.FileNew() 实例 (配置 NewFileName/Units),
    //     否则 Commit 报 "内部错误：内存访问违例" (P-12d V1-V5 全灭, V6 显式赋值即通)
    //   ★NewComponentName (干净名, 无扩展名) 与 NewFile.NewFileName (全路径) 独立:
    //     组件名 = NewComponentName (P12G_SUB 实证), 文件落盘 = NewFile.NewFileName 目录 (P-12g 实证)
    //   ★ObjectForNewComponent.Add(Body) — Body→DisplayableObject 隐式; 传 NXObject 编译不过
    //   默认: ReferenceSet=Model (新件自动 MODEL 引用集) / OriginalObjectsDeleted=true (移动语义)
    //          / ComponentOrigin=Wcs / LayerOption=Original (P-12c/e 实证)
    //   Commit → NXOpen.Assemblies.Component; 新件内存态, 保存装配 (SaveComponents.True) 才写盘
    //   ★Component 无法改名 (Name 只读, 无 Rename 方法 — 运行时实证) → 命名须经 NewComponentName
    //
    // 职责边界: nx_create_part=建空件 | nx_add_component=加已存在件 | 本工具=体→新件+装配 (三者互补)
    // ========================================================================

    /// <summary>
    /// Create a new component (.prt) from bodies of the work part and add it to the assembly (top-down New Component).
    /// GAP-12 配方实证: CreateNewComponentBuilder + NewFile 显式赋值 (P-12a/b/c/g)。
    /// </summary>
    /// Parameters:
    ///   bodies (array, optional) - 体名/journal id 列表 (含匹配); 缺省 = 工作部件全部实体
    ///   names (array, optional) - 逐体组件名 (缺省 = 体名去括号后缀大写化)
    ///   dir (string, optional) - 新件目录 (缺省 = 工作部件目录)
    ///   keep_originals (boolean, optional) - true=复制保留原体; false=移动 (NX 默认, 原体删除)
    ///   fix (boolean, optional) - 创建后 Fix 组件 (默认 true, asm-06 Positioner Fix 配方)
    ///
    public class CreateComponentTool : IToolHandler
    {
        public string Name { get { return "nx_create_component"; } }
        public string Description { get { return "Create new component(s) (.prt) from work part bodies and assemble them (top-down New Component). 配方: CreateNewComponentBuilder+NewFile 显式赋值, 返回树快照+BOM."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var workPart = session.Parts.Work as NXOpen.Part;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // 结构变更先建 UndoMark (计划 §7; 失败可 nx_undo 回滚)
                using (var mark = new NxMcpPlugin.Features.UndoMarkScope(session, "CreateComponent"))
                {
                    JArray bodyNames = AssemblyParams.GetParamArray(parameters, "bodies");
                JArray nameArr = AssemblyParams.GetParamArray(parameters, "names");
                string dir = AssemblyParams.GetParamString(parameters, "dir", "");
                bool keepOriginals = AssemblyParams.GetParamBool(parameters, "keep_originals", false);
                bool fix = AssemblyParams.GetParamBool(parameters, "fix", true);

                if (string.IsNullOrEmpty(dir))
                    dir = System.IO.Path.GetDirectoryName(workPart.FullPath);
                if (!System.IO.Directory.Exists(dir))
                    return ToolResult.Fail(string.Format("Directory does not exist: {0}", dir)).ToJson();

                // ---- 解析目标体 (缺省=全部实体) ----
                List<NXOpen.Body> targets = ResolveBodies(workPart, bodyNames);
                if (targets.Count == 0)
                    return ToolResult.Fail("No target bodies resolved. Use 'bodies' (体名/journal id 含匹配) or ensure the work part has solid bodies.").ToJson();

                // ---- 逐体 New Component ----
                var created = new JArray();
                var fixStatus = new JArray();
                string lastErr = "";
                for (int i = 0; i < targets.Count; i++)
                {
                    NXOpen.Body body = targets[i];
                    string bodyId = "";
                    try { bodyId = body.JournalIdentifier; } catch { }   // ★先捕获: 创建后体被移动删除, 再访问报"不活动的对象"
                    string compName = DefaultName(body);
                    if (nameArr != null && i < nameArr.Count && !string.IsNullOrEmpty(nameArr[i].Value<string>()))
                        compName = nameArr[i].Value<string>().Trim();

                    var r = CreateOneComponent(session, workPart, body, compName, dir, keepOriginals, fix);
                    if (r.Item1 == null)
                    {
                        lastErr = string.Format("body '{0}' ({1}): {2}", bodyId, i, r.Item2);
                        break;
                    }
                    var e = new JObject();
                    e["name"] = r.Item1.Name;
                    e["part_path"] = r.Item2;
                    e["body"] = bodyId;
                    e["fix_status"] = r.Item3;
                    created.Add(e);
                    fixStatus.Add(r.Item3);
                }

                if (created.Count < targets.Count)
                {
                    // 部分失败: 回滚已创建组件? 保守: 报告已建 + 失败原因, 由调用方决定
                    var errData = new JObject();
                    errData["created"] = created;
                    errData["failed"] = lastErr;
                    mark.Commit();   // 部分成功也保留已建组件
                    return new ToolResult { Success = false, Message = string.Format("nx_create_component: {0}/{1} created; failed at: {2}", created.Count, targets.Count, lastErr), Data = errData }.ToJson();
                }

                // ---- 返回值: 树快照 + BOM (零孤儿: 消费方零改动) ----
                var data = new JObject();
                data["created"] = created;
                var tree = new JArray();
                foreach (ComponentTree.Node root in ComponentTree.Hierarchy(workPart))
                    tree.Add(ComponentTree.NodeToJson(root, true));
                data["tree"] = tree;
                data["bom"] = ComponentTree.BuildBomJson(workPart);
                data["note"] = "新件默认内存态; 保存装配 (nx_save_part) 后写盘。Component 无法改名, 命名须经 names 参数。";
                mark.Commit();   // 成功: mark 提交, Dispose 不回滚
                return new ToolResult { Success = true, Message = string.Format("Created {0} component(s) from bodies.", created.Count), Data = data }.ToJson();
            }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_component failed: {0}", ex.Message)).ToJson();
            }
        }

        // ---- 单体 New Component (P-12 配方) ----
        // 返回: (Component, partPath, fixStatus)
        private Tuple<NXOpen.Assemblies.Component, string, string> CreateOneComponent(
            dynamic session, NXOpen.Part workPart, NXOpen.Body body, string compName, string dir, bool keepOriginals, bool fix)
        {
            NXOpen.Assemblies.CreateNewComponentBuilder b = null;
            NXOpen.FileNew fnb = null;
            string partPath = System.IO.Path.Combine(dir, compName + ".prt");
            try
            {
                var am = workPart.AssemblyManager;
                b = am.CreateNewComponentBuilder();

                // ★关键: NewFile 显式赋值 (缺此 Commit 内存访问违例)
                fnb = (NXOpen.FileNew)session.Parts.FileNew();
                fnb.NewFileName = partPath;
                fnb.MakeDisplayedPart = false;
                fnb.UseBlankTemplate = true;
                fnb.Units = NXOpen.Part.Units.Millimeters;
                b.NewFile = fnb;

                // 命名 (干净名) + 选体 + 移动/复制语义
                b.NewComponentName = compName;
                b.ObjectForNewComponent.Add(body);
                b.OriginalObjectsDeleted = !keepOriginals;

                NXOpen.Assemblies.Component comp = b.Commit() as NXOpen.Assemblies.Component;
                if (comp == null)
                    return new Tuple<NXOpen.Assemblies.Component, string, string>(null, partPath, "Commit returned null");

                string fixStatus = "n/a";
                if (fix)
                    fixStatus = FixComponent(workPart, comp);

                return new Tuple<NXOpen.Assemblies.Component, string, string>(comp, partPath, fixStatus);
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

        // ---- 解析目标体 (bodies 参数含匹配 → 缺省全部实体) ----
        private static List<NXOpen.Body> ResolveBodies(NXOpen.Part part, JArray bodyNames)
        {
            var result = new List<NXOpen.Body>();
            var all = new List<NXOpen.Body>();
            foreach (NXOpen.Body bd in part.Bodies)
            {
                if (!bd.IsSolidBody) continue;
                all.Add(bd);
            }
            if (bodyNames == null || bodyNames.Count == 0)
                return all;

            foreach (var tok in bodyNames)
            {
                string key = tok.Value<string>();
                if (string.IsNullOrEmpty(key)) continue;
                NXOpen.Body hit = null;
                foreach (NXOpen.Body bd in all)
                {
                    string name = bd.Name ?? "";
                    string jid = "";
                    try { jid = bd.JournalIdentifier ?? ""; } catch { }
                    if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(jid, key, StringComparison.OrdinalIgnoreCase))
                    { hit = bd; break; }
                }
                if (hit == null)
                {
                    foreach (NXOpen.Body bd in all)
                    {
                        string name = bd.Name ?? "";
                        if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { hit = bd; break; }
                    }
                }
                if (hit != null && !result.Contains(hit)) result.Add(hit);
            }
            return result;
        }

        // 缺省组件名: 体名 "BLOCK(0)" → "BLOCK_0" 大写化 (NX 组件名无括号)
        private static string DefaultName(NXOpen.Body body)
        {
            string name = body.Name ?? "";
            if (string.IsNullOrEmpty(name)) { try { name = body.JournalIdentifier ?? "BODY"; } catch { name = "BODY"; } }
            name = name.Replace("(", "_").Replace(")", "").Replace(" ", "_");
            if (string.IsNullOrEmpty(name)) name = "COMPONENT";
            return name.ToUpperInvariant();
        }

        // ---- Fix 组件 (asm-06 Positioner Fix 配方, 2026-09-05 实证) ----
        private string FixComponent(NXOpen.Part workPart, NXOpen.Assemblies.Component comp)
        {
            NXOpen.Assemblies.ComponentAssembly asm = workPart.ComponentAssembly;
            var positioner = asm.Positioner as NXOpen.Positioning.ComponentPositioner;
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
