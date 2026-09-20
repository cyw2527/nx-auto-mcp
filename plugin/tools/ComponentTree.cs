using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools
{
    /// <summary>
    /// 装配组件树共享遍历底座 (GAP-07+08, 2026-09-07)
    ///
    /// 消除工具层各处重复的单级 RootComponent.GetChildren() 循环 (改造前 AssemblyTools 8+ 处 +
    /// WaveTools 各自一份)。全部 125 工具 + Wave 复用。
    ///
    /// 三种形态:
    ///   Hierarchy(part)  — 嵌套树 (level 递归, 保父子结构与装配路径)
    ///   Flatten(part)    — 全树扁平数组 (带 level/路径/位姿), BOM 归并与多级读回
    ///   FindByName(...)  — 递归按名查找 (exact → contains, 与旧 FindChild 兼容)
    ///
    /// 数量语义 (NX BOM 标准): 部件数量 = 其作为组件在全树出现的 occ 实例总数
    ///   (含子装配内重复引用: 子装配出现 2 次 → 其下零件 quantity ×2)。
    /// 实现走 GetChildren 递归 (能保层级/路径), AskAllPartOccChildren 扁平化作为
    ///   后续 fast-path 候选 (当前 A3 降级路径 GetChildren 递归 = 本实现主路径)。
    ///
    /// 消费方: AssemblyTools (list_components V4 recursive / bom_extract / interference 全树),
    ///         WaveTools (按名解析)。
    /// </summary>
    public static class ComponentTree
    {
        /// <summary>树节点 — 独立于 NXOpen.Component 的纯数据视图</summary>
        public class Node
        {
            public string Name;              // 组件名 (Component.Name)
            public string FullPath;          // 原型部件全路径 (Prototype.FullPath)
            public string Path;              // 装配内路径 "A/B/C" (root 相对, 直接子件 = "A")
            public int Level;                // 深度 (0 = 装配直接子件)
            public string ReferenceSet;      // 引用集
            public bool IsSuppressed;        // 抑制态
            public double Ox, Oy, Oz;        // 世界位姿 (装配坐标)
            public double[] Rotation;        // 9 元素行序 {xx,xy,xz, yx,yy,yz, zx,zy,zz}
            public List<Node> Children;      // Hierarchy 用; Flatten 恒 null
            public NXOpen.Assemblies.Component Component;  // 原组件引用 (消费方操作 occ 用; 不入 JSON)
        }

        /// <summary>
        /// 仅直接子件 (单级, 兼容 nx_list_components 旧行为)。节点 Children 会带子树, 由调用方决定是否输出。
        /// </summary>
        public static List<Node> DirectChildren(NXOpen.Part part)
        {
            var result = new List<Node>();
            if (part == null || part.ComponentAssembly == null) return result;
            NXOpen.Assemblies.Component root = part.ComponentAssembly.RootComponent;
            if (root == null) return result;
            foreach (NXOpen.Assemblies.Component child in root.GetChildren())
            {
                Node node = BuildNode(child, child.Name, 0);
                if (node != null) result.Add(node);
            }
            return result;
        }

        /// <summary>
        /// 嵌套树 (level 递归)。无装配/无子件 → 空表 (容错, 与旧工具行为一致)。
        /// </summary>
        public static List<Node> Hierarchy(NXOpen.Part part)
        {
            var result = new List<Node>();
            if (part == null || part.ComponentAssembly == null) return result;
            NXOpen.Assemblies.Component root = part.ComponentAssembly.RootComponent;
            if (root == null) return result;
            foreach (NXOpen.Assemblies.Component child in root.GetChildren())
            {
                Node node = BuildNode(child, child.Name, 0);
                if (node != null) result.Add(node);
            }
            return result;
        }

        /// <summary>
        /// 全树扁平数组 (Hierarchy 的深度优先投影, 节点带 level/path, Children=null)。
        /// </summary>
        public static List<Node> Flatten(NXOpen.Part part)
        {
            var flat = new List<Node>();
            FlattenInto(Hierarchy(part), flat);
            return flat;
        }

        private static void FlattenInto(List<Node> nodes, List<Node> flat)
        {
            foreach (Node n in nodes)
            {
                flat.Add(n);
                if (n.Children != null && n.Children.Count > 0)
                    FlattenInto(n.Children, flat);
            }
        }

        /// <summary>
        /// 递归按名查找 (exact OrdinalIgnoreCase 优先 → contains 宽松)。
        /// recursive=false 时仅直接子件 (兼容旧 FindChild 语义)。
        /// 未命中返回 null。
        /// </summary>
        public static Node FindByName(NXOpen.Part part, string name, bool recursive = true)
        {
            if (part == null || string.IsNullOrEmpty(name)) return null;
            List<Node> roots = Hierarchy(part);
            Node hit = FindExact(roots, name);
            if (hit != null) return hit;
            return recursive ? FindContains(roots, name) : null;
        }

        private static Node FindExact(List<Node> nodes, string name)
        {
            foreach (Node n in nodes)
            {
                if (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) return n;
                if (n.Children != null && n.Children.Count > 0)
                {
                    Node hit = FindExact(n.Children, name);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        private static Node FindContains(List<Node> nodes, string name)
        {
            foreach (Node n in nodes)
            {
                if (n.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return n;
                if (n.Children != null && n.Children.Count > 0)
                {
                    Node hit = FindContains(n.Children, name);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 内部构造
        // ------------------------------------------------------------------

        private static Node BuildNode(NXOpen.Assemblies.Component comp, string parentPath, int level)
        {
            var node = new Node();
            node.Name = comp.Name;
            node.Path = parentPath;
            node.Level = level;
            node.ReferenceSet = comp.ReferenceSet;
            node.IsSuppressed = comp.IsSuppressed;
            node.Component = comp;

            NXOpen.Part proto = comp.Prototype as NXOpen.Part;
            node.FullPath = proto != null ? proto.FullPath : null;

            try
            {
                NXOpen.Point3d org; NXOpen.Matrix3x3 rot;
                comp.GetPosition(out org, out rot);      // typed out (A0-2 定案, NX2412 实证)
                node.Ox = org.X; node.Oy = org.Y; node.Oz = org.Z;
                node.Rotation = new double[] { rot.Xx, rot.Xy, rot.Xz,
                                               rot.Yx, rot.Yy, rot.Yz,
                                               rot.Zx, rot.Zy, rot.Zz };
            }
            catch (Exception)
            {
                node.Rotation = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            }

            NXOpen.Assemblies.Component[] kids = null;
            try { kids = comp.GetChildren(); } catch (Exception) { }
            if (kids != null && kids.Length > 0)
            {
                node.Children = new List<Node>();
                foreach (NXOpen.Assemblies.Component kid in kids)
                    node.Children.Add(BuildNode(kid, parentPath + "/" + kid.Name, level + 1));
            }
            return node;
        }

        // ------------------------------------------------------------------
        // JSON 投影 (字段名与 nx_list_components V2 对齐, 保消费方兼容)
        // ------------------------------------------------------------------

        public static JObject NodeToJson(Node n, bool includeChildren = true)
        {
            var o = new JObject();
            o["name"] = n.Name;
            o["full_path"] = n.FullPath;
            o["level"] = n.Level;
            o["path"] = n.Path;
            o["reference_set"] = n.ReferenceSet;
            o["is_suppressed"] = n.IsSuppressed;
            var rot = new JObject();
            if (n.Rotation != null && n.Rotation.Length == 9)
            {
                rot["xx"] = n.Rotation[0]; rot["xy"] = n.Rotation[1]; rot["xz"] = n.Rotation[2];
                rot["yx"] = n.Rotation[3]; rot["yy"] = n.Rotation[4]; rot["yz"] = n.Rotation[5];
                rot["zx"] = n.Rotation[6]; rot["zy"] = n.Rotation[7]; rot["zz"] = n.Rotation[8];
            }
            o["rotation"] = rot;
            o["origin"] = new JObject { { "x", n.Ox }, { "y", n.Oy }, { "z", n.Oz } };
            if (includeChildren && n.Children != null && n.Children.Count > 0)
            {
                var arr = new JArray();
                foreach (Node c in n.Children) arr.Add(NodeToJson(c, true));
                o["children"] = arr;
            }
            return o;
        }

        /// <summary>BOM 归并: 按原型 FullPath (兜底 Name) 分组的 occ 总数 + 首次深度</summary>
        public static Dictionary<string, BomRow> BuildBom(NXOpen.Part part)
        {
            var rows = new Dictionary<string, BomRow>(StringComparer.OrdinalIgnoreCase);
            foreach (Node n in Flatten(part))
            {
                string key = !string.IsNullOrEmpty(n.FullPath) ? n.FullPath : n.Name;
                BomRow row;
                if (!rows.TryGetValue(key, out row))
                {
                    row = new BomRow();
                    row.PartName = n.Name;
                    row.FullPath = n.FullPath;
                    row.Level = n.Level;
                    row.ReferenceSet = n.ReferenceSet;
                    row.InstancePaths = new List<string>();
                    rows[key] = row;
                }
                row.Quantity++;
                if (n.IsSuppressed) row.SuppressedCount++;
                if (n.Level < row.Level) row.Level = n.Level;
                row.InstancePaths.Add(n.Path);
            }
            return rows;
        }

        /// <summary>BOM 归并结果 → JSON 数组 (零孤儿: 结构变更工具返回快照用, 与 nx_bom_extract 同构)</summary>
        public static JArray BuildBomJson(NXOpen.Part part)
        {
            var bom = new JArray();
            foreach (KeyValuePair<string, BomRow> kv in BuildBom(part))
            {
                BomRow r = kv.Value;
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
            return bom;
        }

        /// <summary>BOM 行 (BuildBom 产物)</summary>
        public class BomRow
        {
            public string PartName;
            public string FullPath;
            public int Quantity;
            public int SuppressedCount;
            public int Level;                 // 首次出现深度
            public string ReferenceSet;
            public List<string> InstancePaths;
        }
    }
}
