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
                // FileNew.Units is NXOpen.Part.Units (NOT BasePart.Units) — verified
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
    public class OpenPartTool : IToolHandler
    {
        public string Name { get { return "nx_open_part"; } }
        public string Description { get { return "Open an existing NX part file."; } }

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

                var saveStatus = workPart.Save(NXOpen.BasePart.SaveComponents.True, NXOpen.BasePart.CloseAfterSave.False);
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
    /// Maps to BasePart.Close(CloseWholeTree.False, CloseModified.UseResponses, null).
    /// </summary>
    public class ClosePartTool : IToolHandler
    {
        public string Name { get { return "nx_close_part"; } }
        public string Description { get { return "Close the currently active (work) part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string partName = (string)workPart.Name;
                string partPath = (string)workPart.FullPath;

                var bp = (NXOpen.BasePart)((object)workPart);
                bp.Close(NXOpen.BasePart.CloseWholeTree.False,
                         NXOpen.BasePart.CloseModified.UseResponses,
                         null);

                var data = new JObject() { { "path", partPath }, { "name", partName } };

                try
                {
                    object partsObj = ((dynamic)session).Parts;
                    var pc = (NXOpen.PartCollection)partsObj;
                    var stillOpen = pc.ToArray();
                    data["remaining_open_parts"] = stillOpen == null ? 0 : stillOpen.Length;
                }
                catch (Exception ex)
                {
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
    /// </summary>
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
                    return ToolResult.Fail(string.Format("format '{0}' is not supported on NX2412. Use format=step or nx_export_xt for Parasolid.", format)).ToJson();

                string inputFile = FileOpsParams.GetParamString(parameters, "input_file");
                if (string.IsNullOrEmpty(inputFile))
                    inputFile = (string)workPart.FullPath;
                if (!File.Exists(inputFile))
                    return ToolResult.Fail(string.Format("Source part not found on disk: '{0}'. Save the part first (nx_save_part) and retry.", inputFile)).ToJson();

                string ap = FileOpsParams.GetParamString(parameters, "ap", "203");
                string settingsFile = FileOpsParams.GetParamString(parameters, "settings_file");
                if (string.IsNullOrEmpty(settingsFile))
                    settingsFile = ResolveStepDefFile(ap);
                if (!File.Exists(settingsFile))
                    return ToolResult.Fail(string.Format("STEP settings file not found: '{0}'.", settingsFile)).ToJson();

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
                    return ToolResult.Fail(string.Format("Translation finished but no file appeared at '{0}'.", path)).ToJson();

                string description = "STEP AP" + (ap == "214" ? "214" : "203");
                var data = new JObject() { { "path", path }, { "format", fmtKey }, { "description", description }, { "input_file", inputFile } };
                return new ToolResult { Success = true, Message = string.Format("Exported {0} ({1}) at: {2}", description, inputFile, path), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_export_step failed: {0}", ex.Message)).ToJson();
            }
        }

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
                        return ToolResult.Fail(string.Format("Unsupported import file extension: '{0}'.", ext)).ToJson();
                }

                var data = new JObject() { { "path", path }, { "extension", ext } };
                return new ToolResult { Success = true, Message = string.Format("Imported geometry from: {0}", path), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_import_geometry failed: {0}", ex.Message)).ToJson();
            }
        }

        private static string ResolveImportDefFile()
        {
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
