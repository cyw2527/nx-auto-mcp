using System;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.Features;

namespace NxMcpPlugin.Tools.SheetMetal
{
    public class ContourFlangeTool : IToolHandler
    {
        public string Name { get { return "nx_sheet_metal_flange"; } }
        public string Description { get { return "Create sheet metal flange from sketch. sketch_name, thickness, bend_radius."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                string sketchName = p.Value<string>("sketch_name") ?? "";
                double thickness = p.Value<double?>("thickness") ?? 1.0;
                double bendRadius = p.Value<double?>("bend_radius") ?? thickness;
                dynamic smMgr = ((dynamic)wp.Features).SheetmetalManager;
                dynamic builder = smMgr.CreateContourFlangeBuilder(null);
                if (!string.IsNullOrEmpty(sketchName))
                {
                    dynamic sketch = wp.Sketches.FindObject(sketchName);
                    if (sketch != null) builder.SetSketch(sketch);
                }
                try { builder.SetSweepSide(0); } catch { }
                try { builder.SetThicknessSide(0); } catch { }
                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("thickness", thickness);
                data.Add("bend_radius", bendRadius);
                return ToolResult.Ok(string.Format("Flange t={0} r={1}.", thickness, bendRadius), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_sheet_metal_flange: " + ex.Message).ToJson(); }
        }
    }

    public class FlatPatternTool : IToolHandler
    {
        public string Name { get { return "nx_sheet_metal_flat"; } }
        public string Description { get { return "Toggle flat pattern. action: show/hide/create."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                string action = (p.Value<string>("action") ?? "show").Trim().ToLowerInvariant();
                dynamic smMgr = ((dynamic)wp.Features).SheetmetalManager;
                try { if (action == "create" || action == "show") smMgr.ShowFlatPattern(); else smMgr.HideFlatPattern(); } catch { }
                var data = new JObject();
                data.Add("action", action);
                return ToolResult.Ok("Flat pattern: " + action + ".", data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_sheet_metal_flat: " + ex.Message).ToJson(); }
        }
    }

    public class SheetMetalBendTool : IToolHandler
    {
        public string Name { get { return "nx_sheet_metal_bend"; } }
        public string Description { get { return "Add bend to sheet metal. bend_radius, angle."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                double radius = p.Value<double?>("bend_radius") ?? 1.0;
                double angle = p.Value<double?>("angle") ?? 90.0;
                dynamic smMgr = ((dynamic)wp.Features).SheetmetalManager;
                dynamic builder = smMgr.CreateBendBuilder(null);
                try { builder.SetBendRadius(radius); } catch { }
                try { builder.SetBendAngle(angle); } catch { }
                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("bend_radius", radius);
                data.Add("angle", angle);
                return ToolResult.Ok(string.Format("Bend R={0} deg={1}.", radius, angle), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_sheet_metal_bend: " + ex.Message).ToJson(); }
        }
    }
}
