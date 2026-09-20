using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools
{
    /// <summary>
    /// 工具注册表 — 统一管理所有 NX 操作工具
    ///
    /// 设计:
    ///   1. 每个工具注册为 IToolHandler 实现
    ///   2. TCP 请求通过 tool_name 路由到对应 handler
    ///   3. handler 接收 JObject 参数，返回 JObject 结果
    ///
    /// 工具分类 (参考 NX MCP):
    ///   - modeling: 21 个建模工具 (extrude, revolve, sweep, blend, chamfer, hole, ...)
    ///   - sketch: 12 个草图工具 (create_sketch, line, arc, rectangle, circle, ...)
    ///   - file_ops: 8 个文件操作 (create_part, open_part, save, export, import, ...)
    ///   - datum: 4 个基准工具 (datum_plane, datum_axis, datum_csys, point)
    ///   - assembly: 9 个装配工具 (add_component, mate, pattern, interference, ...)
    ///   - measure: 7 个测量工具 (distance, angle, volume, mass, perimeter, area, section)
    ///   - display: 4 个显示工具 (layer, color, transparency)
    ///   - utility: 8 个实用工具 (fit_view, undo, screenshot, journal, ...)
    ///   - validate: 2 个验证工具 (validate_model, validate_feature)
    ///   - inspect: 5 个检查工具 (topology, geometry, feature_tree, xt_export, xt_node)
    ///   - correct: 6 个修正工具 (face_geometry, rebuild_feature, rollback, ...)
    /// </summary>
    public class ToolRegistry
    {
        private static ToolRegistry _instance;
        public static ToolRegistry Instance { get { return _instance ?? (_instance = new ToolRegistry()); } }

        private readonly Dictionary<string, IToolHandler> _tools = new Dictionary<string, IToolHandler>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 注册所有工具
        /// </summary>
        public void RegisterAll()
        {
            // Modeling (24 tools)
            Register(new Modeling.ExtrudeTool());
            Register(new Modeling.RevolveTool());
            Register(new Modeling.SweepTool());
            Register(new Modeling.BlendTool());
            Register(new Modeling.ChamferTool());
            Register(new Modeling.HoleTool());
            Register(new Modeling.PatternTool());
            Register(new Modeling.BooleanTool());
            Register(new Modeling.UniteAllTool());   // M0-20260914 A4: 多体合并
            Register(new Modeling.DeleteFeatureTool());
            Register(new Modeling.RebuildModelTool());
            Register(new Modeling.EditFeatureTool());
            Register(new Modeling.MirrorBodyTool());
            Register(new Modeling.ShellTool());
            Register(new Modeling.DraftTool());
            Register(new Modeling.TrimBodyTool());
            Register(new Modeling.LoftTool());
            Register(new Modeling.TubeTool());
            Register(new Modeling.SplitBodyTool());
            Register(new Modeling.OffsetSurfaceTool());
            Register(new Modeling.SewTool());
            Register(new Modeling.ThickenTool());
            Register(new Modeling.HelixTool());
            Register(new Modeling.TextCurveTool());

            // Synchronous Modeling (6 tools) — P0 gap: MoveFace/ResizeFace/DeleteFace/ReplaceBlend/ResizeBlend/OffsetRegion
            Register(new Synchronous.MoveFaceTool());
            Register(new Synchronous.ResizeFaceTool());
            Register(new Synchronous.DeleteFaceSyncTool());
            Register(new Synchronous.ReplaceBlendTool());
            Register(new Synchronous.ResizeBlendTool());
            Register(new Synchronous.OffsetRegionTool());

            // Sheet Metal (3 tools) — P0 gap: ContourFlange/FlatPattern/Bend
            Register(new SheetMetal.ContourFlangeTool());
            Register(new SheetMetal.FlatPatternTool());
            Register(new SheetMetal.SheetMetalBendTool());

            // Surface Modeling (5 tools) — P1+P2: ThroughCurves/Ruled/StudioSurface/ExtendSurface/MidSurface
            Register(new Surface.ThroughCurvesTool());
            Register(new Surface.RuledSurfaceTool());
            Register(new Surface.StudioSurfaceTool());
            Register(new Surface.ExtendSurfaceTool());
            Register(new Surface.MidSurfaceTool());

            // Curve Operations (3 tools) — P1 gap: ProjectCurve/OffsetCurve/IntersectionCurve
            Register(new Curve.ProjectCurveTool());
            Register(new Curve.OffsetCurveTool());
            Register(new Curve.IntersectionCurveTool());

            // WAVE / Associative (2 tools) — P1 gap: WaveLink/ExtractGeometry
            Register(new Wave.WaveLinkTool());
            Register(new Wave.ExtractGeometryTool());

            // Sketch (18 tools)
            Register(new Sketch.ListSketchesTool());
            Register(new Sketch.CreateSketchTool());
            Register(new Sketch.SketchLineTool());
            Register(new Sketch.SketchArcTool());
            Register(new Sketch.SketchRectangleTool());
            Register(new Sketch.SketchCircleTool());
            Register(new Sketch.SketchConstraintTool());
            Register(new Sketch.FinishSketchTool());
            Register(new Sketch.SketchTrimTool());
            Register(new Sketch.SketchSplineTool());
            Register(new Sketch.SketchMirrorTool());
            Register(new Sketch.SketchEllipseTool());
            Register(new Sketch.SketchOffsetTool());
            Register(new Sketch.SketchFilletTool());
            Register(new Sketch.SketchChamferTool());
            // M1 readback tools (EXECUTION-LOOP-TODO)
            Register(new Sketch.SketchStatusTool());
            Register(new Sketch.SketchConstraintsTool());
            Register(new Sketch.IntentSnapshotTool());

            // File Ops (8 tools)
            Register(new FileOps.CreatePartTool());
            Register(new FileOps.OpenPartTool());
            Register(new FileOps.SavePartTool());
            Register(new FileOps.SaveAsTool());
            Register(new FileOps.ClosePartTool());
            Register(new FileOps.ExportStepTool());
            Register(new FileOps.ImportGeometryTool());
            Register(new FileOps.ListOpenPartsTool());

            // Datum (4 tools)
            Register(new Datum.CreateDatumPlaneTool());
            Register(new Datum.CreateDatumAxisTool());
            Register(new Datum.CreateDatumCsysTool());
            Register(new Datum.CreatePointTool());

            // Assembly (14 tools)
            Register(new Assembly.AddComponentTool());
            Register(new Assembly.MateComponentTool());
            Register(new Assembly.ListComponentsTool());
            Register(new Assembly.AsmConstraintsTool());   // V3 读回 (Eye-TODO A2, 2026-09-05)
            Register(new Assembly.RepositionComponentTool());
            Register(new Assembly.RemoveComponentTool());
            Register(new Assembly.SuppressComponentTool());
            Register(new Assembly.ComponentPatternTool());
            Register(new Assembly.InterferenceCheckTool());
            Register(new Assembly.ExplodeAssemblyTool());
            Register(new Assembly.BomExtractTool());   // GAP-07 (2026-09-07)
            Register(new Assembly.CreateComponentTool());  // GAP-12 自顶向下 New Component (2026-09-08)
            Register(new Assembly.FindSameBodiesTool());   // GAP-14 19.1 相同体 (2026-09-08)
            Register(new Assembly.BodiesToAssemblyTool()); // GAP-14 多体→装配 (2026-09-08)

            // Measure (7 tools)
            Register(new Measure.MeasureDistanceTool());
            Register(new Measure.MeasureAngleTool());
            Register(new Measure.MeasureVolumeTool());
            Register(new Measure.MeasureMassPropertiesTool());
            Register(new Measure.MeasurePerimeterTool());
            Register(new Measure.MeasureAreaTool());
            Register(new Measure.SectionAnalysisTool());

            // Display (4 tools)
            Register(new Display.SetLayerVisibilityTool());
            Register(new Display.MoveToLayerTool());
            Register(new Display.SetObjectColorTool());
            Register(new Display.SetObjectTransparencyTool());

            // TODO: 工程图 (Drawing) 工具组 —— 待开发中 (37 个工具待补)。

            // Utility (12 tools)
            Register(new Utility.ProbeBuilderTool());
            Register(new Utility.FitViewTool());
            Register(new Utility.SetViewTool());
            Register(new Utility.UndoTool());
            Register(new Utility.ScreenshotTool());
            Register(new Utility.RunJournalTool());
            Register(new Utility.RecordStartTool());
            Register(new Utility.RecordStopTool());
            Register(new Utility.ListUndoMarksTool());
            Register(new Utility.OpenNxTool());
            Register(new Utility.CloseNxTool());
            Register(new Utility.ActivateViewTool());

            // Validate (2 tools)
            Register(new Validate.ValidateModelTool());
            Register(new Validate.ValidateFeatureTool());

            // Inspect (6 tools)
            Register(new Inspect.InspectTopologyTool());
            Register(new Inspect.EvaluateFaceTool());
            Register(new Inspect.InspectGeometryTool());
            Register(new Inspect.InspectFeatureTreeTool());
            Register(new Inspect.ExportXtTool());
            Register(new Inspect.InspectXtNodeTool());
            Register(new Inspect.InspectSelectionTool());

            // Correct (4 tools)
            Register(new Correct.CorrectFaceGeometryTool());
            Register(new Correct.RebuildFeatureTool());
            Register(new Correct.RollbackToMarkTool());
            Register(new Correct.ApplySuggestionTool());
        }

        public void Register(IToolHandler tool)
        {
            _tools[tool.Name] = tool;
        }

        public IToolHandler GetTool(string name)
        {
            IToolHandler tool;
            _tools.TryGetValue(name, out tool);
            return tool;
        }

        public IReadOnlyList<string> ListToolNames()
        {
            return new List<string>(_tools.Keys);
        }

        public int ToolCount { get { return _tools.Count; } }
    }
}
