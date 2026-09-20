using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools
{
    /// <summary>
    /// Work Part 守卫 — 工具调用前后快照/恢复工作部件 (2026-09-10)
    ///
    /// 【背景 — M-005b 实证】
    ///   `PartCollection.OpenBaseDisplay()` 会把 work part 切到刚打开的部件。
    ///   这与记忆 M-005「OpenDisplay 不改 work」**不矛盾**: 两者是不同的 NX API —
    ///   - `PartCollection.OpenDisplay()`     → 只改 DISPLAY part (M-005 结论成立)
    ///   - `PartCollection.OpenBaseDisplay()` → 实测**会改 WORK part** (M-005b 新增)
    ///
    ///   代价: 探针 view_dims_probe.cs 用 OpenBaseDisplay 打开第二个件后 work part 被切走,
    ///   后续工具操作到错误的部件 → 返回空结果的**假故障**。
    ///   逐个探针自觉恢复不可靠 — 根治在架构层。
    ///
    /// 【★为什么必须同时快照 DISPLAY — 2026-09-10 自测实证, 这曾是个真 bug】
    ///   `SetWork(p)` **只在 p 位于当前 display 件的装配上下文内时才成立**;
    ///   否则抛 `部件或事例不是根部件的下游`。
    ///   而任何真实的「切走 work part」操作都**必然先切 display**
    ///   (`SetDisplay(part, maintainWorkPart:false, ...)` 本身就会把 work 一并设过去)。
    ///   ⇒ **只调 SetWork 的恢复对真实污染工具必然失败**。
    ///   故: 快照存 {Work, Display} 两个; 恢复时**先 SetDisplay 再 SetWork**。
    ///   (本缺陷由 M-007 专项自测首轮抓出 — 见 ISSUES.md M-007)
    ///
    /// 【例外 — ExemptTools】
    ///   以「切换活动部件」为职责的工具, 守卫完全不介入。
    ///   - 漏登记 → 该工具被静默回退 (安全, 但表现为「改的件没生效」)
    ///   - 多登记 → 少一层保护 (无害)
    ///   新增工具若会切 work part, **必须**登记到这里并写明理由。
    ///
    /// 【错误可见性】
    ///   本类**不吞异常**: 失败写进返回 note (restore_error / display_restore_error)
    ///   交给 TcpServer 落日志。静默 catch 会掩盖 DLR 绑定错误 — 那正是本守卫要防的同类问题。
    /// </summary>
    internal static class WorkPartGuard
    {
        /// <summary>
        /// 显式声明「本工具会切换 work part」。每条都附证据 (源码位置 / 职责理由)。
        /// </summary>
        private static readonly HashSet<string> ExemptTools =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nx_create_part",         // FileOpsTools.cs:76  MakeDisplayedPart=true — 新建件即活动件, 回退会把用户卡在原地
            "nx_open_part",           // FileOpsTools.cs:126 OpenDisplay — 打开部件本身即「改活动件」操作
            "nx_close_part",          // FileOpsTools.cs:229 关闭后 work part 合法地变为 null / 其它件
            "nx_save_as",             // FileOpsTools.cs:209 SaveAs 可重指 work part 的文件身份
            "nx_run_journal",         // 任意用户代码 (探针/建模脚本) — 不替它做主; ★本守卫诞生正是因为这类脚本
            "nx_open",                // 拉起 NX / 带 part_path 启动
            "nx_create_component",    // 新建 .prt 组件 (New Component) 可能改活动件
            "nx_bodies_to_assembly",  // 多体转装配会新建部件文件
        };

        /// <summary>该工具是否已声明「会切换 work part」(声明者守卫不介入)。</summary>
        public static bool IsExempt(string toolName)
        {
            return !string.IsNullOrEmpty(toolName) && ExemptTools.Contains(toolName);
        }

        /// <summary>已声明的例外工具清单 (诊断/自检用)。</summary>
        public static string[] ExemptList
        {
            get
            {
                var arr = new List<string>(ExemptTools);
                arr.Sort(StringComparer.OrdinalIgnoreCase);
                return arr.ToArray();
            }
        }

        /// <summary>守卫快照 — work 与 display 都要存 (只存 work 的恢复会失败, 见类注释)。</summary>
        internal sealed class Snapshot
        {
            public Part Work;
            public Part Display;
        }

        private static string Short(Exception ex)
        {
            return ex.GetType().Name + ": " + (ex.Message ?? "").Split('\n')[0];
        }

        /// <summary>
        /// ★ 取 PartCollection — 注意入参是 **NXOpen.Session**, 不是 PartCollection 本身。
        ///   (2026-09-10 自测抓到: 曾直接 `(PartCollection)session` → InvalidCastException
        ///    "无法将类型为 NXOpen.Session 的对象强制转换为 NXOpen.PartCollection",
        ///    表现为快照静默失败、守卫整轮不介入。)
        /// </summary>
        private static NXOpen.PartCollection PartsOf(object session)
        {
            dynamic s = session;
            return (NXOpen.PartCollection)s.Parts;
        }

        private static string Name(Part p)
        {
            if (p == null) return "(null)";
            try { return (string)p.Name; } catch { return "(unreadable)"; }
        }

        /// <summary>
        /// 快照当前 work + display。异常降级为 null (= 守卫不介入), 绝不影响工具本身。
        /// error 非 null 表示快照有部分失败 — 调用方应落日志。
        ///
        /// ★ session 声明为 object 而非 dynamic: 调用方 _session 是 dynamic, 若本方法也收
        ///   dynamic 则整个调用动态绑定 — 本项目有 DLR + out 参数的翻车史 (nx2412-dynamic-out-pitfall)。
        ///   调用方传 (object)_session 强制静态绑定, 内部再转回 dynamic。
        /// </summary>
        public static Snapshot Take(object session, out string error)
        {
            error = null;
            if (session == null) { error = "session is null"; return null; }

            var snap = new Snapshot();
            try
            {
                var pc = PartsOf(session);
                try { snap.Work = pc.Work as Part; }
                catch (Exception ex) { error = "Work: " + Short(ex); }
                try { snap.Display = pc.Display as Part; }
                catch (Exception ex) { error = (error == null ? "" : error + " | ") + "Display: " + Short(ex); }
            }
            catch (Exception ex) { error = Short(ex); return null; }

            return snap;
        }

        /// <summary>
        /// 同一部件的判定。用 FullPath 主判 (NX 不会两次打开同一文件) + Name 兜底。
        /// 避免依赖 NXObject.Tag 的装箱/转换差异。
        /// </summary>
        private static bool SamePart(Part a, Part b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            string pa = null, pb = null;
            try { pa = (string)a.FullPath; } catch { }
            try { pb = (string)b.FullPath; } catch { }
            if (!string.IsNullOrEmpty(pa) && !string.IsNullOrEmpty(pb))
                return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
            try { return string.Equals((string)a.Name, (string)b.Name, StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>
        /// work part 若被换掉则恢复。
        /// 返回 null = 未介入 (常态: 没被动过 / 快照失败);
        /// 否则返回标注对象 — 成功: {from, restored_to}; 失败: {from, restore_error}。
        /// 若 display 也被换过, 会先恢复 display 并附 {display_restored_to} 或 {display_restore_error}。
        /// </summary>
        public static JObject Restore(object session, Snapshot before)
        {
            if (before == null || before.Work == null || session == null) return null;

            var pc = PartsOf(session);

            Part curWork = null, curDisplay = null;
            try { curWork = pc.Work as Part; } catch { }
            try { curDisplay = pc.Display as Part; } catch { }

            if (SamePart(before.Work, curWork)) return null;   // 没被动过 — 绝大多数调用走这里

            var note = new JObject();
            note["from"] = Name(curWork);                      // 工具跑完时残留的活动件

            // ★① 先恢复 display 上下文 — SetWork 只在 display 件的装配上下文内有效。
            //    真实污染工具必然先切 display, 所以这一步是恢复成功的前提。
            if (before.Display != null && !SamePart(before.Display, curDisplay))
            {
                try
                {
                    PartLoadStatus st;
                    pc.SetDisplay(before.Display, false, false, out st);
                    try { st.Dispose(); } catch { }
                    note["display_restored_to"] = Name(before.Display);
                }
                catch (Exception ex)
                {
                    note["display_restore_error"] = Short(ex);  // 不吞 — display 恢复失败会连带 work 恢复失败
                }
            }

            // ★② 再恢复 work part
            try
            {
                pc.SetWork(before.Work);
                note["restored_to"] = Name(before.Work);
            }
            catch (Exception ex)
            {
                note["restore_error"] = Short(ex);              // 不吞: 恢复失败比不恢复更值得知道
            }

            return note;
        }
    }
}
