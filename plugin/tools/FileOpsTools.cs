using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;

namespace NxMcpPlugin.Tools.FileOps
{
    // ========================================================================
    // Parameter helpers
    // ========================================================================
    internal static class FileOpsParams
    {
        public static string GetParamString(JObject p, string key, string defaultValue = "")
        {
            var token = (p == null ? null : p[key]);
            return token != null && token.Type != JTokenType.Null ? token.Value<string>() : defaultValue;
        }

        public static double GetParamDouble(JObject p, string key, double defaultValue = 0.0)
        {
            var token = (p == null ? null : p[key]);
            return token != null && token.Type != JTokenType.Null ? token.Value<double>() : defaultValue;
        }

        public static int GetParamInt(JObject p, string key, int defaultValue = 0)
        {
            var token = (p == null ? null : p[key]);
            return token != null && token.Type != JTokenType.Null ? token.Value<int>() : defaultValue;
        }

        public static bool GetParamBool(JObject p, string key, bool defaultValue = false)
        {
            var token = (p == null ? null : p[key]);
            return token != null && token.Type != JTokenType.Null ? token.Value<bool>() : defaultValue;
        }

        public static JArray GetParamArray(JObject p, string key)
        {
            var token = (p == null ? null : p[key]);
            JArray arr = token as JArray;
            if (arr != null) return arr;
            return null;
        }
    }

    // ========================================================================
    // 1. nx_create_part
    // ========================================================================

    /// <summary>
    /// Create a new NX part file at the specified path.
    /// Maps to Session.Parts.FileNew() with blank template.
    ///
    /// Parameters:
    ///   path  (string, required) -- Full path for the new part file (e.g., "C:/temp/test.prt").
    ///   units (string, optional) -- mm (default) or inches.
    /// </summary>
    public class CreatePartTool : IToolHandler
    {
        public string Name { get { return "nx_create_part"; } }
        public string Description { get { return "Create a new NX part file at the specified path."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = FileOpsParams.GetParamString(parameters, "path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                string units = FileOpsParams.GetParamString(parameters, "units", "mm");

                dynamic fileNew = session.Parts.FileNew();
                fileNew.NewFileName = path;
                fileNew.MakeDisplayedPart = true;
                fileNew.UseBlankTemplate = true;
                // Fix 2026-08-04: units param was read but NEVER applied — CAE mixed-units errors traced here.
                // Default NX template is Inches; must set FileNew.Units explicitly.
                // FileNew.Units is NXOpen.Part.Units (NOT BasePart.Units) — verified 反编译
                fileNew.Units = (units.ToLowerInvariant() == "in" || units.ToLowerInvariant() == "inches")
                    ? NXOpen.Part.Units.Inches : NXOpen.Part.Units.Millimeters;
                fileNew.Commit();
                fileNew.Destroy();

                dynamic part = session.Parts.Display;

                var data = new JObject() { { "path", (string)part.FullPath }, { "name", (string)part.Name } };
                return new ToolResult { Success = true, Message = string.Format("Created new part: {0}", part.Name), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_part failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 2. nx_open_part
    // ========================================================================

    /// <summary>
    /// Open an existing NX part file.
    /// NX2412: OpenBaseDisplay(string) 1-param overload removed.
    /// Strategy: try OpenBaseDisplay(string, string[]) first, then Open(string).
    /// </summary>
    /// Parameters:
    ///   path (string, optional) - path parameter.
    ///
    public class OpenPartTool : IToolHandler
    {
        public string Name { get { return "nx_open_part"; } }
        public string Description { get { return "Open an existing NX part file. ⚠️WORK/DISPLAY 语义 (M-005, 2026-09-03 实测): 底层是 PartCollection.OpenDisplay — 打开后该件成为 DISPLAY part, 不自动成为 WORK part (nx_list_open_parts 里 is_work 仍指原工作件)。影响: nx_inspect_feature_tree / nx_inspect_topology / nx_inspect_geometry 以 UI 当前部件(display)为目标, 而 nx_run_journal 的 measure/dump 脚本以 session.Parts.Work 为目标 — 目标不一致时先切 work part (journal: session.Parts.SetWork(part) + SetDisplay(part)), 或直接 nx_open 启动时带 part_path。副作用: 打开新件时非显示的其他已开件可能被关闭 (文件无损, 需重开)。"; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = FileOpsParams.GetParamString(parameters, "path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                // NX2412: dynamic can't bind 'out' params → cast to concrete PartCollection
                var pc = (NXOpen.PartCollection)session.Parts;
                NXOpen.PartLoadStatus loadStatus;
                NXOpen.Part part = pc.OpenDisplay(path, out loadStatus);
                try { loadStatus.Dispose(); } catch { }
                dynamic dpart = part;

                var data = new JObject() { { "path", (string)dpart.FullPath }, { "name", (string)dpart.Name } };
                return new ToolResult { Success = true, Message = string.Format("Opened part: {0}", dpart.Name), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_open_part failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 3. nx_save_part
    // ========================================================================

    /// <summary>
    /// Save the currently active (work) part.
    /// Maps to Part.Save(SaveComponents.True, CloseAfterSave.False).
    /// </summary>
    public class SavePartTool : IToolHandler
    {
        public string Name { get { return "nx_save_part"; } }
        public string Description { get { return "Save the currently active (work) part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // Source: 实测验证 (2026-08-04) — NX2412 real API:
                //   Part.Save(SaveComponents, CloseAfterSave) — bool overload does NOT exist
                //   Old code used Save(true,false) → exception swallowed by catch → false "Saved"
                var saveStatus = workPart.Save(NXOpen.BasePart.SaveComponents.True, NXOpen.BasePart.CloseAfterSave.False);
                // Verify file actually written (fail loudly, don't fake success)
                bool fileExists = false;
                try { fileExists = System.IO.File.Exists((string)workPart.FullPath); } catch { }

                var data = new JObject() { { "path", (string)workPart.FullPath }, { "name", (string)workPart.Name }, { "file_exists", fileExists } };
                if (!fileExists)
                    return new ToolResult { Success = false, Message = string.Format("Save reported OK but file NOT found: {0}", workPart.FullPath), Data = data }.ToJson();
                return new ToolResult { Success = true, Message = string.Format("Saved part: {0}", workPart.Name), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_save_part failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 4. nx_save_as
    // ========================================================================

    /// <summary>
    /// Save the current work part to a new file path.
    /// Maps to Part.SaveAs(path).
    /// </summary>
    /// Parameters:
    ///   path (string, optional) - path parameter.
    ///
    public class SaveAsTool : IToolHandler
    {
        public string Name { get { return "nx_save_as"; } }
        public string Description { get { return "Save the current work part to a new file path."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = FileOpsParams.GetParamString(parameters, "path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                workPart.SaveAs(path);

                var data = new JObject() { { "path", (string)workPart.FullPath }, { "name", (string)workPart.Name } };
                return new ToolResult { Success = true, Message = string.Format("Saved part as: {0}", path), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_save_as failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 5. nx_close_part
    // ========================================================================

    /// <summary>
    /// Close the currently active (work) part.
    /// ⚠️契约 (M-008, 2026-09-10 实测): 只能关当前 work 件; 关后 session.Parts.Work 变 null,
    /// NX 不会把剩余打开件自动提升为 work (再操作前需先开/切 work part);
    /// 不保存且会丢弃未保存修改 — 调用方负责先 nx_save_part。
    /// Maps to BasePart.Close(CloseWholeTree.False, CloseModified.UseResponses, null).
    /// </summary>
    public class ClosePartTool : IToolHandler
    {
        public string Name { get { return "nx_close_part"; } }
        public string Description { get { return "Close the currently active (work) part. ⚠️契约 (M-008): 只能关当前 work 件; 关后 Parts.Work 变 null, NX 不自动提升 — 再操作前先开/切 work part; ⚠️不保存且丢弃未保存修改 — 用前先 nx_save_part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // Read-only scan — never save.
                string partName = (string)workPart.Name;
                string partPath = (string)workPart.FullPath;

                // ★强类型直调 (2026-09-10, issue M-008)
                //   旧实现用反射找 Close(CloseWholeTree, CloseModified, NXObject) — 第 3 参
                //   真名是 PartCloseResponses, 故 GetMethod 恒返回 null, 每次都落到
                //   UF Part.CloseAll() 兜底 ⇒ **关掉所有已开部件** (实测: 2 件打开 → 调用后
                //   0 件), 而回执只报了一个件名 — 工具描述与行为不符。
                //   教训: 「找不到目标重载」时的兜底必须是更弱的动作。CloseAll 不是
                //   「关 work 件」的降级版, 它是另一件破坏性大得多的事, 不能拿来做 fallback。
                //   下面失败即抛给外层 → ToolResult.Fail 如实报错, 不再静默升级。
                //
                // ★签名的确切语义 (实测 get_method_detail 实证, 别再靠猜):
                //   void BasePart.Close(CloseWholeTree, CloseModified, PartCloseResponses)
                //   - wholeTree=false → 只卸载顶层件, 不动其组件 (这正是本工具想要的)
                //   - responses 参数**仅当** closeModified=UseResponses 时生效; 传 null 时,
                //     所有「候选被关闭件」**不论是否已修改都会被关闭** — 不提示、不保存。
                //   ⇒ **本工具不保存、也会丢弃未保存修改**。调用方负责先存盘 (见 SKILL 铁律 37)。
                //   注: 旧反射实现从未真正走到这里 (恒 null → CloseAll), 故这组语义此前从未生效过。
                var bp = (NXOpen.BasePart)((object)workPart);
                bp.Close(NXOpen.BasePart.CloseWholeTree.False,
                         NXOpen.BasePart.CloseModified.UseResponses,
                         null);

                var data = new JObject() { { "path", partPath }, { "name", partName } };

                // 如实回报「还剩几个打开件」— 让「到底关了谁」可被调用方核验。
                // (M-008 之所以能藏住, 就是因为回执不可核验)
                try
                {
                    object partsObj = ((dynamic)session).Parts;
                    var pc = (NXOpen.PartCollection)partsObj;
                    var stillOpen = pc.ToArray();
                    data["remaining_open_parts"] = stillOpen == null ? 0 : stillOpen.Length;
                }
                catch (Exception ex)
                {
                    // 不吞: 核验字段本身失败也要说出原因, 否则又是一个静默盲区
                    data["remaining_open_parts_error"] =
                        ex.GetType().Name + ": " + (ex.Message ?? "").Split('\n')[0];
                }

                return new ToolResult { Success = true, Message = string.Format("Closed part: {0}", partName), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_close_part failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 6. nx_export_step
    // ========================================================================

    /// <summary>
    /// Export a part to STEP format (AP203 default / AP214 optional).
    /// Uses Session.DexManager.CreateStepCreator() — legacy file→file translator.
    /// Fixed 2026-09-04 from a recorded GUI journal (headless replay verified):
    ///   NX2412 StepCreator has NO Apply() — must Commit(); InputFile is REQUIRED (else the
    ///   translator silently logs "UG to STEP No parts in current input file" and produces nothing);
    ///   ObjectTypes.Solids / SettingsFile / LayerMask / ProcessHoldFlag must be set like the GUI.
    /// </summary>
    /// Parameters:
    ///   path          (string, required) - output .stp/.step file path.
    ///   format        (string, optional) - "step" only (verified 2026-09-04); iges/stl/parasolid removed (Apply() gone in NX2412 — use nx_export_xt for Parasolid).
    ///   input_file    (string, optional) - source .prt path; default = work part FullPath (must be saved on disk).
    ///   ap            (string, optional) - "203" (default) or "214".
    ///   settings_file (string, optional) - STEP .def settings file; default auto-resolved from UGII_BASE_DIR (ugstep203.def/ugstep214.def).
    ///
    public class ExportStepTool : IToolHandler
    {
        public string Name { get { return "nx_export_step"; } }
        public string Description { get { return "Export a part to STEP (AP203/AP214). Legacy file->file translator via CreateStepCreator + Commit."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = FileOpsParams.GetParamString(parameters, "path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                string format = FileOpsParams.GetParamString(parameters, "format", "step");
                string fmtKey = format.Trim().ToLowerInvariant();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                if (fmtKey != "step")
                    return ToolResult.Fail(string.Format("format '{0}' is not supported on NX2412: legacy creators lost Apply(). Use format=step (verified 2026-09-04) or nx_export_xt for Parasolid.", format)).ToJson();

                // Legacy STEP translator is file->file: it reads the source part from DISK.
                // Source part must exist on disk (save first if the session has unsaved changes).
                string inputFile = FileOpsParams.GetParamString(parameters, "input_file");
                if (string.IsNullOrEmpty(inputFile))
                    inputFile = (string)workPart.FullPath;
                if (!File.Exists(inputFile))
                    return ToolResult.Fail(string.Format("Source part not found on disk: '{0}'. The legacy STEP translator is file->file — save the part first (nx_save_part) and retry.", inputFile)).ToJson();

                string ap = FileOpsParams.GetParamString(parameters, "ap", "203");
                string settingsFile = FileOpsParams.GetParamString(parameters, "settings_file");
                if (string.IsNullOrEmpty(settingsFile))
                    settingsFile = ResolveStepDefFile(ap);
                if (!File.Exists(settingsFile))
                    return ToolResult.Fail(string.Format("STEP settings file not found: '{0}'. Pass settings_file explicitly or check the NX install (UGII_BASE_DIR).", settingsFile)).ToJson();

                // Mirror of GUI File->Export->STEP journal (2026-09-04, NX2412) — headless replay verified
                dynamic creator = session.DexManager.CreateStepCreator();
                creator.ExportAs = (ap == "214")
                    ? NXOpen.StepCreator.ExportAsOption.Ap214
                    : NXOpen.StepCreator.ExportAsOption.Ap203;
                creator.SettingsFile = settingsFile;
                creator.ObjectTypes.Solids = true;
                creator.ColorAndLayers = true;
                creator.InputFile = inputFile;
                creator.OutputFile = path;
                creator.FileSaveFlag = false;
                creator.LayerMask = "1-256";
                creator.ProcessHoldFlag = true;
                creator.Commit();
                creator.Destroy();

                if (!File.Exists(path))
                    return ToolResult.Fail(string.Format("Translation finished but no file appeared at '{0}' — check the translator log (created next to output, *.log) for 'No parts in current input file'.", path)).ToJson();

                string description = "STEP AP" + (ap == "214" ? "214" : "203");
                var data = new JObject() { { "path", path }, { "format", fmtKey }, { "description", description }, { "input_file", inputFile } };
                return new ToolResult { Success = true, Message = string.Format("Exported {0} ({1}) at: {2}", description, inputFile, path), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_export_step failed: {0}", ex.Message)).ToJson();
            }
        }

        /// <summary>Auto-resolve the STEP translator settings .def for the given AP.
        /// Mirrors the GUI journal path: &lt;NX root&gt;/STEP203UG/ugstep203.def (AP203) or STEP214UG/ugstep214.def (AP214).</summary>
        private static string ResolveStepDefFile(string ap)
        {
            string nxRoot = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
            if (string.IsNullOrEmpty(nxRoot))
                nxRoot = "C:\\Program Files\\Siemens\\NX2412";
            return (ap == "214")
                ? Path.Combine(nxRoot, "STEP214UG", "ugstep214.def")
                : Path.Combine(nxRoot, "STEP203UG", "ugstep203.def");
        }
    }

    // ========================================================================
    // 7. nx_import_geometry
    // ========================================================================

    /// <summary>
    /// Import geometry from STEP, IGES, or Parasolid files into the current work part.
    /// Uses Session.DexManager.CreateStepImporter(), CreateIgesImporter(),
    /// or CreateParasolidImporter() based on file extension.
    /// </summary>
    /// Parameters:
    ///   path (string, optional) - path parameter.
    ///
    public class ImportGeometryTool : IToolHandler
    {
        public string Name { get { return "nx_import_geometry"; } }
        public string Description { get { return "Import geometry from STEP, IGES, or Parasolid files into the current work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string path = FileOpsParams.GetParamString(parameters, "path");
                if (string.IsNullOrEmpty(path))
                    return ToolResult.Fail("Parameter 'path' is required.").ToJson();

                string ext = Path.GetExtension(path).ToLowerInvariant();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                if (!File.Exists(path))
                    return ToolResult.Fail(string.Format("Input file not found: '{0}'.", path)).ToJson();

                switch (ext)
                {
                    case ".stp":
                    case ".step":
                        {
                            // NX2412 recipe = official Siemens sample (GeometryImporter.cs / CAMSetupImport):
                            //   CreateStepImporter does NOT exist in NX2412 DexManager → CreateStep214Importer.
                            //   KEY: OutputFile MUST be workPart.FullPath (legacy importer writes the geometry
                            //   into the part file given there; without it NX errors "无法将选定的文件导入至工作部件").
                            //   Apply() is gone → Commit() + Destroy(). 2026-09-04 e2e-verified.
                            string stepDir = (string)session.GetEnvironmentVariableValue("STEP214UG_DIR");
                            string settingsFile = !string.IsNullOrEmpty(stepDir)
                                ? Path.Combine(stepDir, "step214ug.def")
                                : ResolveImportDefFile();
                            dynamic importer = session.DexManager.CreateStep214Importer();
                            importer.SimplifyGeometry = true;
                            importer.LayerDefault = 1;
                            importer.ObjectTypes.Curves = true;
                            importer.ObjectTypes.Surfaces = true;
                            importer.ObjectTypes.Solids = true;
                            importer.ObjectTypes.PmiData = true;
                            if (File.Exists(settingsFile))
                                importer.SettingsFile = settingsFile;
                            importer.InputFile = path;
                            importer.OutputFile = (string)workPart.FullPath;
                            importer.FileOpenFlag = false;
                            importer.Commit();
                            importer.Destroy();
                        }
                        break;
                    case ".igs":
                    case ".iges":
                        {
                            dynamic importer = session.DexManager.CreateIgesImporter();
                            importer.InputFile = path;
                            importer.Commit();
                            importer.Destroy();
                        }
                        break;
                    case ".x_t":
                    case ".x_b":
                        {
                            dynamic importer = session.DexManager.CreateParasolidImporter();
                            importer.InputFile = path;
                            importer.Commit();
                            importer.Destroy();
                        }
                        break;
                    default:
                        return ToolResult.Fail(string.Format("Unsupported import file extension: '{0}'. Use one of: .stp, .step, .igs, .iges, .x_t, .x_b.", ext)).ToJson();
                }

                var data = new JObject() { { "path", path }, { "extension", ext } };
                return new ToolResult { Success = true, Message = string.Format("Imported geometry from: {0}", path), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_import_geometry failed: {0}", ex.Message)).ToJson();
            }
        }

        /// <summary>Fallback STEP import .def: &lt;NX root&gt;/STEP214UG/step214ug.def (mirror of STEP214UG_DIR).</summary>
        private static string ResolveImportDefFile()
        {
            // 探测顺序: UGII_BASE_DIR → NX_ROOT → 扫 Program Files (见 NxPaths)
            string nxRoot = NxPaths.BestInstallRoot();
            if (string.IsNullOrEmpty(nxRoot)) return null;
            return Path.Combine(nxRoot, "STEP214UG", "step214ug.def");
        }
    }

    // ========================================================================
    // 8. nx_list_open_parts
    // ========================================================================

    /// <summary>
    /// List all currently open parts in the NX session.
    /// Iterates Session.Parts and reports name, path, and work-part status.
    /// </summary>
    public class ListOpenPartsTool : IToolHandler
    {
        public string Name { get { return "nx_list_open_parts"; } }
        public string Description { get { return "List all currently open parts in the NX session."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string workName = null;
                if (session.Parts.Work != null)
                    workName = (string)session.Parts.Work.Name;

                var partsArray = new JArray();
                int count = 0;

                foreach (dynamic part in session.Parts)
                {
                    string pName = (string)part.Name;
                    string pPath = (string)part.FullPath;
                    bool isWork = (workName != null && pName == workName);

                    partsArray.Add(new JObject() { { "name", pName }, { "path", pPath }, { "is_work", isWork } });
                    count++;
                }

                var data = new JObject() { { "parts", partsArray }, { "count", count } };
                return new ToolResult { Success = true, Message = string.Format("Open parts: {0}", count), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_list_open_parts failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}
