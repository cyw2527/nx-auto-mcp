using System;
using System.Collections.Generic;
using System.Globalization;
using NxMcpPlugin.Protocol;
using NxMcpPlugin.Tools;

namespace NxMcpPlugin
{
    /// <summary>
    /// Context menu and keyboard shortcut callback handler
    ///
    /// Workflow:
    ///   1. User selects object → Right-click "Ask AI" or Ctrl+Shift+A
    ///   2. Get current selection list
    ///   3. Show input dialog (fallback: NXMessageBox)
    ///   4. User enters command → TCP send to MCP Server
    ///   5. Display execution result
    ///
    /// Verified API (NXOpenUI.xml):
    ///   NXMessageBox.Show(string title, DialogType type, string message) → int
    ///   DialogType: Error, Warning, Information, Question
    ///   UI.NXMessageBox → NXMessageBox instance
    /// </summary>
    public class ContextMenuHandler
    {
        private dynamic _session;
        private dynamic _ui;
        private SelectionMonitor _monitor;

        public ContextMenuHandler(dynamic session, dynamic ui, SelectionMonitor monitor)
        {
            _session = session;
            _ui = ui;
            _monitor = monitor;
        }

        /// <summary>
        /// "Ask AI" entry — triggered by context menu or shortcut
        ///
        /// Now no longer directly executes modification intent, but writes current selection + workpiece context to
        /// machine-readable request file, for Claude Code to read via MCP tool and continue conversation to modify part.
        /// </summary>
        public void OnAskAi()
        {
            Log("Ask AI triggered");

            // Step 1: Get current selection
            var selection = _monitor.GetSelection();
            if (selection == null || selection.Count == 0)
            {
                ShowWarning("Please select at least one geometric object (Face/Edge/Body) before triggering Ask AI.");
                return;
            }

            // Step 2: Refresh and build readable summary
            try { _monitor.RefreshSelection(); } catch { }
            selection = _monitor.GetSelection() ?? selection;
            string selectionDesc = BuildSelectionDescription(selection);
            Log(string.Format("Selected objects: {0}", selectionDesc));

            try
            {
                // Step 3: Generate request ID + collect context
                string requestId = Guid.NewGuid().ToString("N");
                string timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                dynamic workPart = null;
                try { workPart = _session.Parts.Work; } catch { }

                var bodyNames = new List<string>();
                foreach (var obj in selection)
                {
                    if (!string.IsNullOrEmpty(obj.BodyName) && !bodyNames.Contains(obj.BodyName))
                        bodyNames.Add(obj.BodyName);
                }

                var objectsJson = new List<object>();
                foreach (var obj in selection)
                {
                    objectsJson.Add(new
                    {
                        tag = obj.Tag,
                        type = obj.Type,
                        face_type = obj.FaceType,
                        area = obj.Area,
                        normal = obj.Normal,
                        length = obj.Length,
                        body_type = obj.BodyType,
                        name = obj.Name,
                        body_name = obj.BodyName,
                    });
                }

                var payload = new
                {
                    request_id = requestId,
                    timestamp_utc = timestamp,
                    status = "pending",
                    selection_description = selectionDesc,
                    selected_objects = objectsJson,
                    involved_bodies = bodyNames,
                    model = new
                    {
                        file_name = GetPartFileName(workPart),
                        part_name = GetPartName(workPart),
                    },
                };

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        Formatting = Newtonsoft.Json.Formatting.Indented,
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    });

                // Step 4: Write to shared request file (independent file, no overwrite)
                // v2: Write to requests/ directory, one file per request
                string requestDir = GetAskAiRequestsDir();
                System.IO.Directory.CreateDirectory(requestDir);
                string requestFilePath = System.IO.Path.Combine(requestDir, requestId + ".json");
                System.IO.File.WriteAllText(requestFilePath, json, System.Text.Encoding.UTF8);

                // Also update session.json (for Claude Code to read)
                UpdateSessionJson(requestId, timestamp, selectionDesc, bodyNames, workPart);

                // Backward compatibility: also write to old path (for legacy MCP Server to read)
                try { System.IO.File.WriteAllText(GetAskAiRequestPath(), json, System.Text.Encoding.UTF8); } catch { }

                Log(string.Format("Ask AI request written: {0} -> {1}", requestId, requestFilePath));
                ShowInfo(string.Format(
                    "Ask AI request generated\n\nRequest ID: {0}\nSelected objects: {1}\n\nPlease return to Claude Code to continue the conversation. I will read the request and help modify the part.",
                    requestId,
                    selectionDesc));
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to generate Ask AI request: {0}", ex.Message));
                ShowWarning(string.Format("Failed to generate Ask AI request: {0}", ex.Message));
            }
        }

        public void OnStartServer()
        {
            Log("Start Server triggered (TCP service already started at Startup)");
            ShowInfo("MCP TCP service is already running in background.\nPort: 1977\nStatus: Listening");
        }

        public void OnShowStatus()
        {
            var selection = _monitor.GetSelection();
            int count = selection != null ? selection.Count : 0;
            var registry = ToolRegistry.Instance;
            int toolCount = registry != null ? registry.ToolCount : 0;
            string status = string.Format(
                "NX MCP Plugin v{0}\n\n" +
                "TCP Port: 1977\n" +
                "Tool count: {1}\n" +
                "Selected objects: {2}",
                PluginVersion.Full,
                toolCount,
                count);
            ShowInfo(status);
        }

        /// <summary>
        /// Hover capture — Read UI button/tooltip info at mouse hover position and write to file
        /// Trigger: NX menu "NX MCP Plugin → Capture Hover" or bound shortcut
        /// Output: %TEMP%\nx-mcp-hover-out.txt
        /// Purpose: User hovers over unfamiliar NX button → shortcut → Claude reads file and explains button purpose
        /// </summary>
        public void OnHoverCapture()
        {
            Log("HoverCapture triggered");
            string OUT = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nx-mcp-hover-out.txt");
            try
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    var p = System.Windows.Forms.Cursor.Position;
                    var pt = new System.Windows.Point(p.X, p.Y);
                    sb.AppendLine(string.Format("== Cursor at ({0}, {1}) {2:HH:mm:ss} ==", p.X, p.Y, DateTime.Now));
                    var root = System.Windows.Automation.AutomationElement.FromPoint(pt);
                    if (root != null)
                    {
                        sb.AppendLine("Name: [" + root.Current.Name + "]");
                        sb.AppendLine("Type: " + root.Current.ControlType.ProgrammaticName);
                        sb.AppendLine("Class: " + root.Current.ClassName);
                        sb.AppendLine("AutomationId: [" + root.Current.AutomationId + "]");
                        try { sb.AppendLine("HelpText: [" + root.Current.HelpText + "]"); } catch { sb.AppendLine("HelpText: N/A"); }
                        sb.AppendLine("-- Parent chain --");
                        var cur = root;
                        for (int i = 0; i < 8 && cur != null; i++)
                        {
                            try
                            {
                                string nm = cur.Current.Name;
                                string aid = cur.Current.AutomationId;
                                if (!string.IsNullOrEmpty(nm) || !string.IsNullOrEmpty(aid))
                                    sb.AppendLine(string.Format("  [{0}] type={1} name=[{2}] id=[{3}]", i,
                                        cur.Current.ControlType.ProgrammaticName, nm, aid));
                            }
                            catch { }
                            cur = System.Windows.Automation.TreeWalker.ControlViewWalker.GetParent(cur);
                        }
                        var cond = new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.ControlTypeProperty,
                            System.Windows.Automation.ControlType.ToolTip);
                        var tips = System.Windows.Automation.AutomationElement.RootElement.FindAll(
                            System.Windows.Automation.TreeScope.Children, cond);
                        sb.AppendLine("-- ToolTips: " + tips.Count + " --");
                        foreach (System.Windows.Automation.AutomationElement t in tips)
                            sb.AppendLine("  ToolTip: [" + t.Current.Name + "]");
                    }
                    else
                    {
                        sb.AppendLine("No element at cursor");
                    }
                }
                catch (Exception exInner)
                {
                    sb.AppendLine("CAPTURE_ERR: " + exInner.Message);
                }
                System.IO.File.WriteAllText(OUT, sb.ToString(), System.Text.Encoding.UTF8);
                ShowInfo("Hover capture complete!\nMouse position info written to:\n" + OUT);
            }
            catch (Exception ex)
            {
                Log(string.Format("HoverCapture failed: {0}", ex.Message));
                ShowWarning("Hover capture failed: " + ex.Message);
            }
        }

        // ================================================================
        // Internal methods
        // ================================================================

        private string BuildSelectionDescription(List<SelectionObject> selection)
        {
            var parts = new List<string>();
            foreach (var obj in selection)
            {
                string desc = string.Format("{0}#{1}", obj.Type, obj.Tag);
                if (obj.Type == "Face" && !string.IsNullOrEmpty(obj.FaceType))
                    desc += string.Format(" ({0})", obj.FaceType);
                else if (obj.Type == "Edge" && obj.Length.HasValue)
                    desc += string.Format(" (L={0:F1}mm)", obj.Length.Value);
                else if (obj.Type == "Body" && !string.IsNullOrEmpty(obj.Name))
                    desc += string.Format(" ({0})", obj.Name);
                parts.Add(desc);
            }
            return string.Join(", ", parts);
        }

        // ================================================================
        // UI helpers — uses verified NXMessageBox.Show API
        // NXOpenUI.xml line 12124
        // ================================================================

        private void ShowInfo(string message)
        {
            try
            {
                // NXOpenUI.xml line 12124:
                // NX2412: NXOpen.NXMessageBox removed, use dynamic enum value
                _ui.NXMessageBox.Show("NX MCP", (dynamic)1 /* Information */, message);
            }
            catch
            {
                Log(string.Format("[INFO] {0}", message));
            }
        }

        private void ShowWarning(string message)
        {
            try
            {
                _ui.NXMessageBox.Show("NX MCP", (dynamic)2 /* Warning */, message);
            }
            catch
            {
                Log(string.Format("[WARN] {0}", message));
            }
        }

        private void Log(string msg)
        {
            try
            {
                Console.WriteLine(string.Format("[NX-MCP-ContextMenu] {0:HH:mm:ss} {1}", DateTime.Now, msg));
                // Also write file log for debugging
                var logPath = NxPaths.StartupFile("tcp_server.log");
                System.IO.File.AppendAllText(logPath,
                    string.Format("[{0:HH:mm:ss.fff}] [ContextMenu] {1}\n", DateTime.Now, msg));
            }
            catch { /* silent */ }
        }

        private static string GetAskAiRequestPath()
        {
            return NxPaths.StartupFile("nx_ask_ai_request.json");
        }

        /// <summary>
        /// v2: Request file directory (one file per request, no overwrite)
        /// Modeled after FreeCAD MCP's state management pattern
        /// </summary>
        private static string GetAskAiRequestsDir()
        {
            // Prefer writing to project workspace/requests/ directory
            string projectWorkspace = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "..", "..", "..", "..", "workspace", "requests");
            projectWorkspace = System.IO.Path.GetFullPath(projectWorkspace);

            if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(projectWorkspace)))
                return projectWorkspace;

            // Fallback: write to requests subdirectory under NX startup dir
            return System.IO.Path.Combine(NxPaths.StartupDir, "requests");
        }

        /// <summary>
        /// Update session.json (for Claude Code cross-editor state sharing)
        /// Modeled after agentcad manifest.json's state tracking pattern
        /// </summary>
        private void UpdateSessionJson(string requestId, string timestamp, string selectionDesc,
            List<string> bodyNames, dynamic workPart)
        {
            try
            {
                string workspaceDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "..", "..", "..", "..", "workspace");
                workspaceDir = System.IO.Path.GetFullPath(workspaceDir);
                System.IO.Directory.CreateDirectory(workspaceDir);

                string sessionPath = System.IO.Path.Combine(workspaceDir, "session.json");

                // Read existing session or create new one
                var session = new Dictionary<string, object>();
                if (System.IO.File.Exists(sessionPath))
                {
                    try
                    {
                        string existing = System.IO.File.ReadAllText(sessionPath);
                        session = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(existing)
                            ?? new Dictionary<string, object>();
                    }
                    catch { }
                }

                // Update active_request
                var activeRequest = new Dictionary<string, object>
                {
                    { "id", requestId },
                    { "timestamp", timestamp },
                    { "status", "pending" },
                    { "source", "ask_ai" }
                };

                var selection = new Dictionary<string, object>
                {
                    { "description", selectionDesc },
                    { "bodies", bodyNames }
                };

                var model = new Dictionary<string, object>();
                try
                {
                    if (workPart != null)
                    {
                        model["file_name"] = GetPartFileName(workPart);
                        model["part_name"] = GetPartName(workPart);
                    }
                }
                catch { }

                selection["model"] = model;
                activeRequest["selection"] = selection;
                session["active_request"] = activeRequest;

                // Add to history
                var history = session.ContainsKey("history")
                    ? session["history"] as List<object> ?? new List<object>()
                    : new List<object>();
                var historyEntry = new Dictionary<string, object>
                {
                    { "id", requestId },
                    { "timestamp", timestamp },
                    { "status", "pending" },
                    { "model", model.ContainsKey("file_name") ? model["file_name"] : "" }
                };
                history.Add(historyEntry);
                // Keep only the last 20 entries
                while (history.Count > 20) history.RemoveAt(0);
                session["history"] = history;

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(session,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        Formatting = Newtonsoft.Json.Formatting.Indented,
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    });
                System.IO.File.WriteAllText(sessionPath, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to update session.json: {0}", ex.Message));
            }
        }

        private static string GetPartFileName(dynamic workPart)
        {
            try
            {
                if (workPart == null) return null;
                string fullPath = workPart.FullPath;
                return string.IsNullOrEmpty(fullPath) ? null : System.IO.Path.GetFileName(fullPath);
            }
            catch { return null; }
        }

        private static string GetPartName(dynamic workPart)
        {
            try
            {
                if (workPart == null) return null;
                return workPart.Name;
            }
            catch { return null; }
        }
    }
}
