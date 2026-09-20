using System;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Synchronous
{
    /// <summary>Shared face-finding helper — delegates to ToolHelpers.</summary>
    internal static class SyncHelper
    {
        public static dynamic FindFaceByTag(dynamic workPart, int tag)
        {
            return ToolHelpers.FindFaceByTag(workPart, tag);
        }
    }

    /// <summary>
    /// Move faces using synchronous modeling (MoveFaceBuilder).
    ///
    /// Parameters:
    ///   face_tags (integer[], required) -- Face tags to move.
    ///   face_tag  (integer, optional)   -- Single face tag (alternative to face_tags).
    ///   distance  (number, optional)    -- Move distance (default 10).
    ///   direction (string, optional)    -- x, y, z, -x, -y, -z (default z).
    /// </summary>
    public class MoveFaceTool : IToolHandler
    {
        public string Name { get { return "nx_move_face"; } }
        public string Description { get { return "Move faces using synchronous modeling. face_tags(int array), distance(double), direction(x/y/z/-x/-y/-z)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray fts = p["face_tags"] as JArray;
                if (fts == null || fts.Count == 0)
                {
                    int? st = p.Value<int?>("face_tag");
                    if (st != null) { fts = new JArray(); fts.Add(st.Value); }
                    else return ToolResult.Fail("face_tags or face_tag required.").ToJson();
                }
                double dist = p.Value<double?>("distance") ?? 10.0;
                string d = (p.Value<string>("direction") ?? "z").Trim().ToLowerInvariant();

                dynamic b = ((dynamic)wp.Features).CreateMoveFaceBuilder(null);
                try { b.SetType(0); } catch { }

                int added = 0;
                foreach (JToken ft in fts)
                {
                    try
                    {
                        var f = SyncHelper.FindFaceByTag(wp, ft.Value<int>());
                        if (f != null && ToolHelpers.AddFaceToCollector(wp, b, "MoveFaceCollector", f)) added++;
                    }
                    catch { }
                }

                if (added == 0) return ToolResult.Fail("No valid faces found. Face tags may have changed after restart.").ToJson();

                ToolHelpers.SetExpressionValue(b, "Distance", dist);

                double dx = 0, dy = 0, dz = 1;
                if (d == "x") dx = 1; else if (d == "-x") dx = -1;
                else if (d == "y") dy = 1; else if (d == "-y") dy = -1;
                else if (d == "z") dz = 1; else if (d == "-z") dz = -1;
                try
                {
                    var dir = wp.Directions.CreateDirection(
                        new Point3d(0, 0, 0), new Vector3d(dx, dy, dz),
                        SmartObject.UpdateOption.WithinModeling);
                    b.SetDirection(dir);
                }
                catch { }

                b.Commit(); b.Destroy();
                var data = new JObject();
                data.Add("distance", dist);
                data.Add("direction", d);
                data.Add("faces_moved", added);
                return ToolResult.Ok(string.Format("Moved {0} faces {1}mm along {2}.", added, dist, d.ToUpper()), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_move_face: " + ex.Message).ToJson(); }
        }
    }

    public class ResizeFaceTool : IToolHandler
    {
        public string Name { get { return "nx_resize_face"; } }
        public string Description { get { return "Resize cylindrical face by diameter. face_tag(int), diameter(double)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                int ft = p.Value<int?>("face_tag") ?? 0;
                if (ft == 0) return ToolResult.Fail("face_tag required.").ToJson();
                double dia = p.Value<double?>("diameter") ?? 0;
                if (dia <= 0) return ToolResult.Fail("diameter required.").ToJson();

                dynamic b = ((dynamic)wp.Features).CreateAdmResizeFaceBuilder(null);
                var f = SyncHelper.FindFaceByTag(wp, ft);
                if (f == null) return ToolResult.Fail("Face not found: " + ft + ". Use nx_inspect_topology to list face tags.").ToJson();
                if (!ToolHelpers.AddFaceToCollector(wp, b, "FaceToResize.FaceCollector", f))
                    return ToolResult.Fail("AddToCollector(FaceToResize) failed.").ToJson();
                if (!ToolHelpers.SetExpressionValue(b, "Diameter", dia))
                    return ToolResult.Fail("SetExpressionValue(Diameter) failed.").ToJson();
                b.Commit(); b.Destroy();
                var data = new JObject();
                data.Add("diameter", dia);
                return ToolResult.Ok(string.Format("Resized face {0} to D={1}.", ft, dia), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_resize_face: " + ex.Message).ToJson(); }
        }
    }

    public class DeleteFaceSyncTool : IToolHandler
    {
        public string Name { get { return "nx_delete_face_sync"; } }
        public string Description { get { return "Delete faces with healing. face_tags(int array), heal(bool)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray fts = p["face_tags"] as JArray;
                if (fts == null || fts.Count == 0) return ToolResult.Fail("face_tags required.").ToJson();
                bool heal = p.Value<bool?>("heal") ?? true;

                dynamic b = ((dynamic)wp.Features).CreateDeleteFaceBuilder(null);
                try { b.SetHeal(heal); } catch { }
                int added = 0;
                foreach (JToken ft in fts)
                {
                    try
                    {
                        var f = SyncHelper.FindFaceByTag(wp, ft.Value<int>());
                        if (f != null && ToolHelpers.AddFaceToCollector(wp, b, "FaceCollector", f)) added++;
                    }
                    catch { }
                }
                if (added == 0) return ToolResult.Fail("No valid faces found.").ToJson();
                b.Commit(); b.Destroy();
                var data = new JObject();
                data.Add("faces_deleted", added);
                return ToolResult.Ok(string.Format("Deleted {0} faces.", added), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_delete_face_sync: " + ex.Message).ToJson(); }
        }
    }

    public class ReplaceBlendTool : IToolHandler
    {
        public string Name { get { return "nx_replace_blend"; } }
        public string Description { get { return "Replace blend face. face_tag(int), radius(double optional)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                int ft = p.Value<int?>("face_tag") ?? 0;
                if (ft == 0) return ToolResult.Fail("face_tag required.").ToJson();
                bool inherit = !p.ContainsKey("radius");

                dynamic b = ((dynamic)wp.Features).CreateReplaceBlendBuilder(null);
                var f = SyncHelper.FindFaceByTag(wp, ft);
                if (f == null) return ToolResult.Fail("Face not found: " + ft).ToJson();
                if (!ToolHelpers.AddFaceToCollector(wp, b, "FaceToReblend", f))
                    return ToolResult.Fail("AddToCollector(FaceToReblend) failed.").ToJson();
                try { b.InheritRadiusFromFace = inherit; } catch { }
                b.Commit(); b.Destroy();
                var data = new JObject();
                data.Add("face_tag", ft);
                data.Add("inherit_radius", inherit);
                return ToolResult.Ok(string.Format("Replaced blend on face {0} (inherit={1}).", ft, inherit), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_replace_blend: " + ex.Message).ToJson(); }
        }
    }

    public class ResizeBlendTool : IToolHandler
    {
        public string Name { get { return "nx_resize_blend"; } }
        public string Description { get { return "Change blend radius. face_tag(int), radius(double)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                int ft = p.Value<int?>("face_tag") ?? 0;
                if (ft == 0) return ToolResult.Fail("face_tag required.").ToJson();
                double r = p.Value<double?>("radius") ?? 0;
                if (r <= 0) return ToolResult.Fail("radius required.").ToJson();

                dynamic b = ((dynamic)wp.Features).CreateResizeBlendBuilder(null);
                var f = SyncHelper.FindFaceByTag(wp, ft);
                if (f == null) return ToolResult.Fail("Face not found: " + ft).ToJson();
                if (!ToolHelpers.AddFaceToCollector(wp, b, "BlendFace", f))
                    return ToolResult.Fail("AddToCollector(BlendFace) failed.").ToJson();
                ToolHelpers.SetExpressionValue(b, "Radius", r);
                b.Commit(); b.Destroy();
                var data = new JObject();
                data.Add("radius", r);
                return ToolResult.Ok(string.Format("Resized blend {0} to R={1}.", ft, r), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_resize_blend: " + ex.Message).ToJson(); }
        }
    }

    /// <summary>
    /// Offset a face region (AdmOffsetRegionBuilder).
    ///
    /// Parameters:
    ///   face_tags (integer[], required) -- Face tags to offset.
    ///   offset    (number, required)   -- Offset distance in mm (0 is rejected).
    /// </summary>
    public class OffsetRegionTool : IToolHandler
    {
        public string Name { get { return "nx_offset_region"; } }
        public string Description { get { return "Offset face region. face_tags(int array), offset(double)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray fts = p["face_tags"] as JArray;
                if (fts == null || fts.Count == 0) return ToolResult.Fail("face_tags required.").ToJson();
                double off = p.Value<double?>("offset") ?? 0;
                if (off == 0) return ToolResult.Fail("offset required.").ToJson();

                dynamic b = ((dynamic)wp.Features).CreateAdmOffsetRegionBuilder(null);
                int added = 0;
                foreach (JToken ft in fts)
                {
                    try
                    {
                        var f = SyncHelper.FindFaceByTag(wp, ft.Value<int>());
                        if (f != null && ToolHelpers.AddFaceToCollector(wp, b, "FaceToOffset.FaceCollector", f)) added++;
                    }
                    catch { }
                }
                if (added == 0) return ToolResult.Fail("No valid faces found.").ToJson();
                ToolHelpers.SetExpressionValue(b, "Distance", off);
                b.Commit(); b.Destroy();
                var data = new JObject();
                data.Add("offset", off);
                return ToolResult.Ok(string.Format("Offset {0} faces by {1}mm.", added, off), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_offset_region: " + ex.Message).ToJson(); }
        }
    }
}
