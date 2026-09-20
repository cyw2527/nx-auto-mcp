using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;

namespace NxMcpPlugin.Tools.Utility
{
    /// <summary>
    /// Orientation map for nx_set_view — maps user-friendly names to NXOpen ViewOrientation enum values.
    /// </summary>
    internal static class ViewOrientationMap
    {
        public static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"front", "kFront"},
            {"back", "kBack"},
            {"top", "kTop"},
            {"bottom", "kBottom"},
            {"left", "kLeft"},
            {"right", "kRight"},
            {"isometric", "kIsometric"},
            {"trimetric", "kTrimetric"},
        };
    }

    // ========================================================================
    // 1. nx_fit_view
    // ========================================================================

    /// <summary>
    /// Fit all visible objects in the graphics viewport.
    /// Maps to NXOpen Part.ModelingViews.WorkView.Fit().
    /// </summary>
    public class FitViewTool : IToolHandler
    {
        public string Name { get { return "nx_fit_view"; } }
        public string Description { get { return "Fit all visible objects in the graphics viewport."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                workPart.ModelingViews.WorkView.Fit();
                return ToolResult.Ok("View fitted — all objects visible.").ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_fit_view failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 2. nx_set_view
    // ========================================================================

    /// <summary>
    /// Set the graphics view orientation (front, back, top, bottom, left, right, isometric, trimetric).
    /// Maps to NXOpen View.Orient() with the corresponding ViewOrientation enum.
    /// </summary>
    public class SetViewTool : IToolHandler
    {
        public string Name { get { return "nx_set_view"; } }
        public string Description { get { return "Set the graphics view orientation (front, back, top, bottom, left, right, isometric, trimetric)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string orientation = parameters.Value<string>("orientation");
                if (string.IsNullOrEmpty(orientation))
                    return ToolResult.Fail("Parameter 'orientation' is required.").ToJson();

                string key = orientation.Trim().ToLowerInvariant();
                if (!ViewOrientationMap.Map.ContainsKey(key))
                {
                    string valid = string.Join(", ", ViewOrientationMap.Map.Keys);
                    return ToolResult.Fail(string.Format("Invalid orientation '{0}'. Use one of: {1}.", orientation, valid)).ToJson();
                }

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                NXOpen.View.Canned cannedView;
                switch (key)
                {
                    case "front":     cannedView = NXOpen.View.Canned.Front; break;
                    case "back":      cannedView = NXOpen.View.Canned.Back; break;
                    case "top":       cannedView = NXOpen.View.Canned.Top; break;
                    case "bottom":    cannedView = NXOpen.View.Canned.Bottom; break;
                    case "left":      cannedView = NXOpen.View.Canned.Left; break;
                    case "right":     cannedView = NXOpen.View.Canned.Right; break;
                    case "isometric": cannedView = NXOpen.View.Canned.Isometric; break;
                    case "trimetric": cannedView = NXOpen.View.Canned.Trimetric; break;
                    default:
                        return ToolResult.Fail("Invalid orientation.").ToJson();
                }
                workPart.ModelingViews.WorkView.Orient(cannedView, NXOpen.View.ScaleAdjustment.Fit);

                var data = new JObject();
                data["orientation"] = key;
                return new ToolResult { Success = true, Message = string.Format("View set to {0}.", key), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_set_view failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 3. nx_undo
    // ========================================================================

    /// <summary>
    /// Undo the last visible operation.
    /// NX2412: UndoLastNVisibleMarks requires 3 parameters: (int n, out bool marksRecycled, out bool undoUnavailable).
    /// Falls back to UndoToLastVisibleMark() if the 3-param version is not available.
    /// </summary>
    public class UndoTool : IToolHandler
    {
        public string Name { get { return "nx_undo"; } }
        public string Description { get { return "Undo the last visible operation."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                bool marksRecycled = false;
                bool undoUnavailable = false;
                session.UndoLastNVisibleMarks(1, out marksRecycled, out undoUnavailable);

                if (undoUnavailable)
                    return ToolResult.Fail("Undo is not available — no visible marks to undo.").ToJson();

                return ToolResult.Ok("Undo successful.").ToJson();
            }
            catch (Exception)
            {
                try
                {
                    session.UndoToLastVisibleMark();
                    return ToolResult.Ok("Undo successful (via UndoToLastVisibleMark).").ToJson();
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail(string.Format("nx_undo failed: {0}", ex.Message)).ToJson();
                }
            }
        }
    }

    // ========================================================================
    // 4. nx_screenshot
    // ========================================================================

    /// <summary>
    /// Capture the current graphics viewport as a PNG image.
    /// </summary>
    public class ScreenshotTool : IToolHandler
    {
        public string Name { get { return "nx_screenshot"; } }
        public string Description { get { return "Capture the current graphics viewport as a PNG image. Requires the NX window to be visible (NOT minimized)."; } }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private static bool IsMainWindowMinimized()
        {
            try
            {
                IntPtr h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                return h != IntPtr.Zero && IsIconic(h);
            }
            catch { return false; }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = parameters.Value<string>("path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                NXOpen.Gateway.ImageExportBuilder b =
                    (NXOpen.Gateway.ImageExportBuilder)workPart.Views.CreateImageExportBuilder();
                b.RegionMode = false;
                b.FileFormat = NXOpen.Gateway.ImageExportBuilder.FileFormats.Png;
                b.FileName = path;
                b.BackgroundOption = NXOpen.Gateway.ImageExportBuilder.BackgroundOptions.CustomColor;
                b.SetCustomBackgroundColor(new double[] { 1.0, 1.0, 1.0 });
                b.EnhanceEdges = false;
                b.Commit();
                b.Destroy();

                bool produced = false;
                for (int i = 0; i < 5 && !produced; i++)
                {
                    produced = System.IO.File.Exists(path);
                    if (!produced) System.Threading.Thread.Sleep(200);
                }
                if (!produced)
                {
                    return ToolResult.Fail(string.Format(
                        "Image export reported no error but produced no file: {0}.{1}",
                        path, WindowHint())).ToJson();
                }

                var data = new JObject();
                data["path"] = path;
                return new ToolResult { Success = true, Message = string.Format("Screenshot saved to {0}.", path), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_screenshot failed: {0}{1}", ex.Message, WindowHint())).ToJson();
            }
        }

        private static string WindowHint()
        {
            return IsMainWindowMinimized()
                ? " ⚠️ NX 主窗口当前处于【最小化】状态 —— 图形窗口无可渲染表面, 这是导出失败最常见的原因。请还原 NX 窗口后重试。"
                : " 请确认 NX 主窗口可见(未最小化)、图形窗口已显示该部件, 且目标目录可写。";
        }
    }

    // ========================================================================
    // 5. nx_run_journal
    // ========================================================================

    /// <summary>
    /// Execute an NX journal file.
    /// NX2412: Session.ExecuteJournal() was removed. Fallback strategy:
    ///   - .NET journals (.cs): Use JournalManager.PlayDotNetJournal()
    ///   - Python journals (.py): Not supported on NX2412+.
    /// </summary>
    public class RunJournalTool : IToolHandler
    {
        public string Name { get { return "nx_run_journal"; } }
        public string Description { get { return "Execute an NX .NET journal (.cs) file. NX2412: Python (.py) journals are not supported."; } }

        private static readonly object _consoleLock = new object();

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = parameters.Value<string>("path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                if (!File.Exists(path))
                    return ToolResult.Fail(string.Format("Journal file not found: {0}", path)).ToJson();

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".py" && ext != ".cs")
                    return ToolResult.Fail("Journal file must be a .cs (.NET) file, got '" + ext + "'.").ToJson();

                bool executed = false;
                string journalStdout = "";

                if (ext == ".cs")
                {
                    try
                    {
                        dynamic journalMgr = session.JournalManager;
                        var sw = new System.IO.StringWriter();
                        string stdoutFile = null;
                        try
                        {
                            stdoutFile = Path.Combine(Path.GetTempPath(), "nx_journal_stdout_" + System.Diagnostics.Process.GetCurrentProcess().Id + ".txt");
                            System.Environment.SetEnvironmentVariable("NX_JOURNAL_STDOUT", stdoutFile);
                        }
                        catch { }

                        lock (_consoleLock)
                        {
                            var origOut = Console.Out;
                            Console.SetOut(sw);
                            try
                            {
                                var result = journalMgr.PlayDotNetJournal(path, new string[0]);
                                string errMsg = (string)result.ErrorMessage;
                                if (!string.IsNullOrEmpty(errMsg))
                                    return ToolResult.Fail("PlayDotNetJournal error: " + errMsg).ToJson();
                            }
                            finally
                            {
                                journalStdout = sw.ToString();
                                Console.SetOut(origOut);
                            }
                        }
                        if (stdoutFile != null && File.Exists(stdoutFile))
                        {
                            try
                            {
                                string fileOut = File.ReadAllText(stdoutFile);
                                journalStdout = (journalStdout + fileOut).Trim();
                                File.Delete(stdoutFile);
                            }
                            catch { }
                        }
                        executed = true;
                    }
                    catch (Exception ex)
                    {
                        string hint = "";
                        if (journalStdout.Length > 0)
                            hint = " | journal stdout: " + journalStdout.Trim();
                        return ToolResult.Fail("PlayDotNetJournal failed for '" + path + "': " + ex.Message + hint).ToJson();
                    }
                }
                else
                {
                    try
                    {
                        session.ExecuteJournal(path);
                        executed = true;
                    }
                    catch
                    {
                    }
                }

                if (!executed)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Message = "Python journal execution is not available via API in NX2412. " +
                                  "To run this journal, use NX Menu > Tools > Journal > Play, or convert it to a .NET journal (.cs)."
                    }.ToJson();
                }

                var data = new JObject();
                data["path"] = path;
                if (journalStdout.Length > 0)
                {
                    data["stdout"] = journalStdout.Trim();
                    return new ToolResult
                    {
                        Success = true,
                        Message = "Journal executed: " + path + "\n--- stdout ---\n" + journalStdout.Trim(),
                        Data = data
                    }.ToJson();
                }
                return new ToolResult { Success = true, Message = "Journal executed: " + path, Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_run_journal failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 6. nx_record_start
    // ========================================================================

    public class RecordStartTool : IToolHandler
    {
        public string Name { get { return "nx_record_start"; } }
        public string Description { get { return "[未实现] 开始录制 NX Journal。NX2412 已移除 Session.BeginJournalRecording()。"; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            return new ToolResult
            {
                Success = false,
                Message = "nx_record_start is not available in NX2412 via managed API.",
                Data = new JObject() {
                    { "error_code", "NX_NOT_AVAILABLE" },
                    { "suggestion", "Use NX Menu > Tools > Journal > Record." }
                }
            }.ToJson();
        }
    }

    // ========================================================================
    // 7. nx_record_stop
    // ========================================================================

    public class RecordStopTool : IToolHandler
    {
        public string Name { get { return "nx_record_stop"; } }
        public string Description { get { return "[未实现] 停止录制 NX Journal。NX2412 已移除 Session.EndJournalRecording()。"; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            return new ToolResult
            {
                Success = false,
                Message = "nx_record_stop is not available in NX2412 via managed API.",
                Data = new JObject() {
                    { "error_code", "NX_NOT_AVAILABLE" },
                    { "suggestion", "Use NX Menu > Tools > Journal > Stop." }
                }
            }.ToJson();
        }
    }

    // ========================================================================
    // 8. nx_list_undo_marks
    // ========================================================================

    public class ListUndoMarksTool : IToolHandler
    {
        public string Name { get { return "nx_list_undo_marks"; } }
        public string Description { get { return "List all available undo marks (pmarks) for rollback."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var allMarks = session.GetAllVisibleUndoMarks();
                var marks = new JArray();
                int count = 0;

                if (allMarks != null)
                {
                    foreach (var mark in allMarks)
                    {
                        count++;
                        var markObj = new JObject();
                        string name = null, vis = null;
                        int? id = null;
                        try { id = Convert.ToInt32(mark.Id); } catch { }
                        if (id != null)
                        {
                            try { name = Convert.ToString(session.GetUndoMarkName(mark.Id)); } catch { }
                            if (string.IsNullOrEmpty(name))
                            {
                                try { name = Convert.ToString(session.GetUndoMarkName(id.Value)); } catch { }
                            }
                        }
                        try { vis = Convert.ToString(mark.Visibility); } catch { }
                        markObj["mark_id"] = id ?? count;
                        markObj["name"] = !string.IsNullOrEmpty(name)
                            ? name
                            : mark.ToString();
                        if (!string.IsNullOrEmpty(vis))
                            markObj["visibility"] = vis;
                        marks.Add(markObj);
                    }
                }

                var data = new JObject();
                data["marks"] = marks;
                data["count"] = count;
                return new ToolResult { Success = true, Message = "Found " + count + " undo mark(s).", Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_list_undo_marks failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 9. nx_open
    // ========================================================================

    public class OpenNxTool : IToolHandler
    {
        public string Name { get { return "nx_open"; } }
        public string Description { get { return "Launch Siemens NX via PowerShell with the MCP plugin loaded."; } }

        private static string FindPowerShell()
        {
            foreach (var path in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Microsoft\WindowsApps\pwsh.exe"),
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                @"C:\Program Files\PowerShell\7-preview\pwsh.exe",
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
            })
            {
                try { if (File.Exists(path)) return path; } catch { }
            }
            return null;
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string nxPath = parameters.Value<string>("nx_path") ?? NxPaths.UgrafPath();
                if (string.IsNullOrEmpty(nxPath))
                    return ToolResult.Fail("找不到 ugraf.exe。请显式传 nx_path, 或设 NX_ROOT 指向 NX 安装根目录。").ToJson();
                string partPath = parameters.Value<string>("part_path");
                string customDir = parameters.Value<string>("custom_dir");

                if (!File.Exists(nxPath))
                    return ToolResult.Fail("NX not found at: " + nxPath).ToJson();

                var nxProcesses = System.Diagnostics.Process.GetProcessesByName("ugraf");
                if (nxProcesses.Length > 0)
                {
                    var data = new JObject();
                    data["status"] = "already_running";
                    data["pid"] = nxProcesses[0].Id;
                    return new ToolResult
                    {
                        Success = true,
                        Message = "NX is already running (PID: " + nxProcesses[0].Id + ").",
                        Data = data
                    }.ToJson();
                }

                if (string.IsNullOrEmpty(customDir))
                {
                    string pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string candidate = Path.Combine(pluginDir, "..", "..", "custom_dirs.dat");
                    if (File.Exists(candidate))
                        customDir = Path.GetFullPath(candidate);
                }

                string arg = "Start-Process -FilePath '" + nxPath.Replace("'", "''") + "'";
                if (!string.IsNullOrEmpty(partPath))
                    arg += " -ArgumentList '" + partPath.Replace("'", "''") + "'";
                if (!string.IsNullOrEmpty(customDir))
                    arg += " -WorkingDirectory '" + Path.GetDirectoryName(nxPath).Replace("'", "''") + "'";

                string psExe = FindPowerShell();
                if (psExe == null)
                    return ToolResult.Fail("PowerShell not found (pwsh.exe / powershell.exe).").ToJson();

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = psExe,
                    Arguments = "-NoProfile -Command \"" + arg + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrEmpty(customDir))
                    startInfo.EnvironmentVariables["UGII_CUSTOM_DIRECTORY_FILE"] = customDir;

                System.Diagnostics.Process.Start(startInfo);

                var result = new JObject();
                result["status"] = "launched";
                result["nx_path"] = nxPath;
                if (!string.IsNullOrEmpty(partPath))
                    result["part_path"] = partPath;
                result["via"] = Path.GetFileName(psExe);
                return new ToolResult
                {
                    Success = true,
                    Message = "NX launched via " + Path.GetFileName(psExe) + ". Waiting for plugin TCP server on port 1977...",
                    Data = result
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_open failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 10. nx_close
    // ========================================================================

    public class CloseNxTool : IToolHandler
    {
        public string Name { get { return "nx_close"; } }
        public string Description { get { return "Close all open parts and exit Siemens NX, discarding all unsaved changes."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                bool force = parameters.Value<bool?>("force") ?? false;

                if (force)
                {
                    var nxProcesses = System.Diagnostics.Process.GetProcessesByName("ugraf");
                    if (nxProcesses.Length == 0)
                        return ToolResult.Fail("NX is not running.").ToJson();

                    int pid = nxProcesses[0].Id;
                    nxProcesses[0].Kill();

                    var data = new JObject();
                    data["status"] = "killed";
                    data["pid"] = pid;
                    return new ToolResult
                    {
                        Success = true,
                        Message = "NX process killed (PID: " + pid + ").",
                        Data = data
                    }.ToJson();
                }

                try
                {
                    session.Parts.CloseAll();
                    session.Exit();

                    var result = new JObject();
                    result["status"] = "closed";
                    return new ToolResult
                    {
                        Success = true,
                        Message = "NX closed gracefully.",
                        Data = result
                    }.ToJson();
                }
                catch
                {
                    var nxProcesses = System.Diagnostics.Process.GetProcessesByName("ugraf");
                    if (nxProcesses.Length > 0)
                    {
                        int pid = nxProcesses[0].Id;
                        nxProcesses[0].Kill();

                        var data = new JObject();
                        data["status"] = "killed_fallback";
                        data["pid"] = pid;
                        return new ToolResult
                        {
                            Success = true,
                            Message = "Graceful close failed. NX process killed (PID: " + pid + ").",
                            Data = data
                        }.ToJson();
                    }

                    return ToolResult.Fail("NX is not running.").ToJson();
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_close failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 11. nx_activate_view
    // ========================================================================

    public class ActivateViewTool : IToolHandler
    {
        public string Name { get { return "nx_activate_view"; } }
        public string Description
        {
            get
            {
                return "Activate a modeling view as WorkView, or list all views. "
                    + "NX2412: Use View.MakeWork().";
            }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                ModelingViewCollection views = workPart.ModelingViews;
                string viewName = parameters.Value<string>("view_name");

                bool switchedModule = false;
                try
                {
                    ModelingView wv = views.WorkView;
                }
                catch
                {
                    try
                    {
                        session.ApplicationSwitchImmediate("UG_APP_MODELING");
                        switchedModule = true;
                    }
                    catch { }
                }

                var modelingViews = new System.Collections.Generic.List<ModelingView>();
                string workViewName = "";
                try
                {
                    ModelingView wv = views.WorkView;
                    workViewName = wv.Name;
                }
                catch { workViewName = "(unknown)"; }

                foreach (var v in views.ToArray())
                {
                    try
                    {
                        ModelingView mv = v as ModelingView;
                        if (mv != null)
                        {
                            modelingViews.Add(mv);
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(viewName))
                {
                    var viewList = new JArray();
                    foreach (var v in modelingViews)
                    {
                        var item = new JObject();
                        item["name"] = v.Name;
                        item["is_work"] = (v.Name == workViewName);
                        viewList.Add(item);
                    }
                    var listResult = new JObject();
                    listResult["views"] = viewList;
                    listResult["active_view"] = workViewName;
                    listResult["count"] = modelingViews.Count;
                    return new ToolResult
                    {
                        Success = true,
                        Message = string.Format("{0} views, active: {1}", modelingViews.Count, workViewName),
                        Data = listResult
                    }.ToJson();
                }

                ModelingView target = null;
                foreach (var v in modelingViews)
                {
                    if (string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = v;
                        break;
                    }
                }

                if (target == null)
                {
                    string available = string.Join(", ", modelingViews.ConvertAll(v => v.Name).ToArray());
                    return ToolResult.Fail(
                        string.Format("View '{0}' not found. Available: {1}", viewName, available)).ToJson();
                }

                if (target.Name == workViewName)
                {
                    var data = new JObject();
                    data["activated"] = target.Name;
                    data["already_active"] = true;
                    data["previous_view"] = workViewName;
                    return new ToolResult
                    {
                        Success = true,
                        Message = string.Format("View '{0}' is already the active WorkView.", target.Name),
                        Data = data
                    }.ToJson();
                }

                string previousName = workViewName;
                target.MakeWork();

                ModelingView newWorkView = views.WorkView;
                bool ok = (newWorkView.Name == target.Name);

                var activateResult = new JObject();
                activateResult["activated"] = target.Name;
                activateResult["previous_view"] = previousName;
                activateResult["verified"] = ok;

                if (!ok)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Message = string.Format("MakeWork() returned but WorkView did not change. Expected: {0}, Got: {1}",
                            target.Name, newWorkView.Name),
                        Data = activateResult
                    }.ToJson();
                }

                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Activated view '{0}' (was '{1}').", target.Name, previousName),
                    Data = activateResult
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_activate_view failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}

namespace NxMcpPlugin.Tools.Utility
{
    // ========================================================================
    // RuntimeProbe — 反射列出一个 Builder 的真实属性/方法
    // ========================================================================

    /// <summary>
    /// Probe a builder's real API via reflection.
    /// Params: factory (string) — e.g. "CreateAdmResizeFaceBuilder"
    /// Returns: type name, all properties (name + type), all methods (name + sig)
    /// </summary>
    public class ProbeBuilderTool : IToolHandler
    {
        public string Name { get { return "nx_probe_builder"; } }
        public string Description { get { return "Probe builder runtime API via reflection. factory (string) e.g. CreateAdmResizeFaceBuilder."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                string factory = parameters.Value<string>("factory");
                if (string.IsNullOrEmpty(factory))
                    return ToolResult.Fail("factory required. e.g. CreateAdmResizeFaceBuilder").ToJson();

                string[] parts = factory.Split('|');
                string methodName = parts.Length > 1 ? parts[1] : factory;
                dynamic feats = wp.Features;

                if (factory == "type" || methodName == "type")
                {
                    var td = new JObject();
                    td.Add("features_type", feats.GetType().FullName);
                    var chain = new JArray();
                    Type t = feats.GetType();
                    while (t != null) { chain.Add(t.FullName); t = t.BaseType; }
                    td.Add("inheritance", chain);
                    var waveMethods = new JArray();
                    foreach (var m in feats.GetType().GetMethods())
                        if (m.Name.Contains("Wave") || m.Name.Contains("Link"))
                            waveMethods.Add(m.Name + " : " + (m.DeclaringType != null ? m.DeclaringType.Name : "?"));
                    td.Add("wave_link_methods", waveMethods);
                    return ToolResult.Ok("Type dump.", td).ToJson();
                }

                if (parts.Length > 1)
                {
                    string path = parts[0];
                    if (path.StartsWith("wp."))
                    {
                        dynamic obj = wp;
                        foreach (var seg in path.Substring(3).Split('.'))
                        {
                            if (seg.Length == 0) continue;
                            var prop = obj.GetType().GetProperty(seg);
                            if (prop == null) return ToolResult.Fail("Path not found: " + path).ToJson();
                            obj = prop.GetValue(obj, null);
                        }
                        feats = obj;
                    }
                }
                var featsType = feats.GetType();
                var method = featsType.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy);
                if (method == null)
                    method = featsType.GetMethod(methodName);
                if (method == null)
                {
                    return ToolResult.Fail("Factory method not found: " + factory).ToJson();
                }

                object builder = null;
                var parms = method.GetParameters();
                var args = new object[parms.Length];
                for (int i = 0; i < parms.Length; i++) args[i] = null;
                try { builder = method.Invoke(feats, args); }
                catch (Exception ex) { return ToolResult.Fail("Invoke failed: " + ex.Message).ToJson(); }

                if (builder == null) return ToolResult.Fail("Builder is null.").ToJson();

                var bType = builder.GetType();
                var data = new JObject();
                data.Add("builder_type", bType.FullName);

                var props = new JArray();
                foreach (var p in bType.GetProperties())
                {
                    var po = new JObject();
                    po.Add("name", p.Name);
                    po.Add("type", p.PropertyType.Name);
                    po.Add("can_read", p.CanRead);
                    po.Add("can_write", p.CanWrite);
                    props.Add(po);
                }
                data.Add("properties", props);

                var methods = new JArray();
                foreach (var m in bType.GetMethods())
                {
                    if (m.IsSpecialName) continue;
                    if (m.DeclaringType == typeof(object)) continue;
                    var mo = new JObject();
                    mo.Add("name", m.Name);
                    mo.Add("sig", m.ToString());
                    methods.Add(mo);
                }
                data.Add("methods", methods);

                return ToolResult.Ok(string.Format("Probed {0}: {1} props, {2} methods.",
                    builder.GetType().Name,
                    props.Count, methods.Count), data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_probe_builder: " + ex.Message).ToJson();
            }
        }
    }
}
