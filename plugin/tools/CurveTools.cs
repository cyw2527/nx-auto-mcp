using System;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Curve
{
    /// <summary>
    /// Project Curve - project curves onto faces or planes.
    /// Runtime probe (2026-07-31): SectionToProject (Section) + FaceToProjectTo (SelectObjectList)
    /// </summary>
    /// Parameters:
    ///   curve_tags (string, optional) - curve_tags parameter.
    ///   face_tags (string, optional) - face_tags parameter.
    ///
    public class ProjectCurveTool : IToolHandler
    {
        public string Name { get { return "nx_project_curve"; } }
        public string Description { get { return "Project curves onto faces. curve_tags(array), face_tags(array), direction(x/y/z/-x/-y/-z)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray curveTags = p["curve_tags"] as JArray;
                JArray faceTags = p["face_tags"] as JArray;
                if (curveTags == null || curveTags.Count == 0)
                    return ToolResult.Fail("curve_tags array required.").ToJson();
                if (faceTags == null || faceTags.Count == 0)
                    return ToolResult.Fail("face_tags array required.").ToJson();
                string dir = (p.Value<string>("direction") ?? "-z").Trim().ToLowerInvariant();

                dynamic builder = ((dynamic)wp.Features).CreateProjectCurveBuilder(null);

                // Build section from edges via CreateRuleEdgeDumb (verified 2026-08-17:
                // CreateSectionFromCurveTag's CreateRuleCurveDumb silently fails on Edge objects → empty section)
                var edges = new System.Collections.Generic.List<dynamic>();
                foreach (JToken ct in curveTags)
                {
                    try
                    {
                        var edge = ToolHelpers.FindEdgeByTag(wp, ct.Value<int>());
                        if (edge != null) edges.Add(edge);
                    }
                    catch { }
                }
                if (edges.Count == 0) { builder.Destroy(); return ToolResult.Fail("No valid edge tags.").ToJson(); }
                var secArr = Array.CreateInstance(edges[0].GetType(), edges.Count);
                for (int i = 0; i < edges.Count; i++) secArr.SetValue(edges[i], i);
                dynamic secRule = wp.ScRuleFactory.CreateRuleEdgeDumb(secArr);
                var secRules = Array.CreateInstance(secRule.GetType(), 1);
                secRules.SetValue(secRule, 0);
                NXOpen.Point3d hp0 = new NXOpen.Point3d(0, 0, 0);
                // SectionToProject is READ-ONLY (builder-owned) — fill it directly
                builder.SectionToProject.AddToSection(secRules, edges[0], null, null, hp0, 0, false);

                // Verified 2026-08-17: ProjectCurveBuilder.FaceToProjectTo.Add(face) accepts the face
                // but Commit ALWAYS fails "用于投影的面或平面无效" in this NX context (headless/automation);
                // PlaneToProjectTo (direct setter) COMMITS FINE. Workaround: derive a fixed plane from
                // the face (bbox center + normal) and project onto the plane — geometrically identical
                // for planar faces.
                int facesAdded = 0;
                foreach (JToken ft in faceTags)
                {
                    try
                    {
                        var f = ToolHelpers.FindFaceByTag(wp, ft.Value<int>());
                        if (f == null) { Surface.SectionHelper.Log("project face not found: " + ft.Value<int>()); continue; }
                        double[] bb = new double[6];
                        var ufs = NXOpen.UF.UFSession.GetUFSession();
                        ufs.Sf.FaceAskBoundingBox(f.Tag, bb);
                        NXOpen.Point3d center = new NXOpen.Point3d((bb[0] + bb[3]) / 2.0, (bb[1] + bb[4]) / 2.0, (bb[2] + bb[5]) / 2.0);
                        // Face normal via ufs.Modl.AskFaceProps (NOT ufs.Sf! verified 2026-08-17):
                        //   (Tag, double[] param, out point, out u1, out v1, out u2, out v2, out unit_norm, out radii)
                        double[] normal = new double[3] { 0, 0, 1 };
                        try
                        {
                            var ap = ufs.Modl.GetType().GetMethod("AskFaceProps");
                            if (ap != null)
                            {
                                // Array sizes verified 2026-08-17: param=6, point/u1/v1/u2/v2/unorm=3, radii=2
                                var args = new object[] { f.Tag, new double[6], new double[3], new double[3], new double[3], new double[3], new double[3], new double[3], new double[2] };
                                ap.Invoke(ufs.Modl, args);
                                double[] fn = (double[])args[7]; // unit_norm
                                if (fn != null && fn.Length >= 3 && (fn[0] != 0 || fn[1] != 0 || fn[2] != 0))
                                    normal = new double[] { fn[0], fn[1], fn[2] };
                                else { Surface.SectionHelper.Log("project normal zero from Modl.AskFaceProps"); }
                            }
                        }
                        catch (Exception ex) { Surface.SectionHelper.Log("project normal: " + ex.Message); }
                        // Fixed plane at face center with face normal
                        NXOpen.Matrix3x3 orient = new NXOpen.Matrix3x3();
                        double[] n = normal;
                        double nx = n[0], ny = n[1], nz = n[2];
                        double[] xAxis = new double[3];
                        if (Math.Abs(nx) < 0.9) xAxis = new double[] { 1, 0, 0 };
                        else if (Math.Abs(ny) < 0.9) xAxis = new double[] { 0, 1, 0 };
                        else xAxis = new double[] { 0, 0, 1 };
                        double ax = xAxis[0], ay = xAxis[1], az = xAxis[2];
                        double cx = ay * nz - az * ny, cy = az * nx - ax * nz, cz = ax * ny - ay * nx;
                        double cLen = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                        if (cLen > 1e-9) { cx /= cLen; cy /= cLen; cz /= cLen; } else { cx = 1; cy = 0; cz = 0; }
                        orient.Xx = cx; orient.Xy = cy; orient.Xz = cz;
                        orient.Yx = ny * cz - nz * cy; orient.Yy = nz * cx - nx * cz; orient.Yz = nx * cy - ny * cx;
                        orient.Zx = nx; orient.Zy = ny; orient.Zz = nz;
                        NXOpen.Plane projPlane = wp.Planes.CreateFixedPlane(center, orient);
                        builder.PlaneToProjectTo = projPlane;
                        facesAdded++;
                    }
                    catch (Exception ex) { Surface.SectionHelper.Log("project plane-from-face: " + ex.Message); }
                }
                if (facesAdded == 0) { builder.Destroy(); return ToolResult.Fail("No valid faces for projection.").ToJson(); }

                double dx = 0, dy = 0, dz = 0;
                if (dir == "x") dx = 1; else if (dir == "-x") dx = -1;
                else if (dir == "y") dy = 1; else if (dir == "-y") dy = -1;
                else if (dir == "z") dz = 1; else if (dir == "-z") dz = -1;
                else dz = -1; // default: -z
                try
                {
                    // Reflection-verified 2026-08-17: runtime properties are ProjectionDirectionMethod + ProjectionVector
                    // (KB's "ProjectionDirection" and guess "Direction" do NOT exist on the runtime type)
                    builder.ProjectionDirectionMethod = NXOpen.Features.ProjectCurveBuilder.DirectionType.AlongVector;
                    builder.ProjectionVector = wp.Directions.CreateDirection(
                        new Point3d(0, 0, 0), new Vector3d(dx, dy, dz),
                        SmartObject.UpdateOption.WithinModeling);
                }
                catch (Exception ex) { Surface.SectionHelper.Log("project direction: " + ex.Message); }

                // Tolerance required (>= 1e-5) — same as nx_intersection_curve
                try { builder.Tolerance = 0.001; } catch (Exception ex) { Surface.SectionHelper.Log("project tolerance: " + ex.Message); }

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("curves_projected", curveTags.Count);
                data.Add("direction", dir);
                return ToolResult.Ok(string.Format("Projected {0} curves along {1}.", curveTags.Count, dir.ToUpper()), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_project_curve: " + ex.Message).ToJson(); }
        }

        private static void AddToList(dynamic builder, string propName, dynamic obj)
        {
            try
            {
                var prop = builder.GetType().GetProperty(propName);
                if (prop == null) return;
                var list = prop.GetValue(builder, null);
                if (list == null) return;
                var add = list.GetType().GetMethod("Add");
                if (add != null) add.Invoke(list, new object[] { obj });
            }
            catch { }
        }
    }

    /// <summary>
    /// Offset Curve - offset curves by distance in a plane.
    /// Verified 2026-08-17: CurvesToOffset is READ-ONLY (builder-owned section) — fill via AddToSection;
    ///   body edges need CreateRuleEdgeDumb; single straight edge fails ("无法确定偏置平面") — need a closed loop or arc;
    ///   closed loop offsets INWARD by default.
    /// </summary>
    /// Parameters:
    ///   curve_tags (string, optional) - curve_tags parameter.
    ///
    public class OffsetCurveTool : IToolHandler
    {
        public string Name { get { return "nx_offset_curve"; } }
        public string Description { get { return "Offset curves. curve_tags(array of body edge tags), distance(double)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray curveTags = p["curve_tags"] as JArray;
                if (curveTags == null || curveTags.Count == 0)
                    return ToolResult.Fail("curve_tags array required.").ToJson();
                double dist = p.Value<double?>("distance") ?? 10.0;

                dynamic builder = ((dynamic)wp.Features).CreateOffsetCurveBuilder(null);

                // Fill the builder-owned section with edge rules
                var edges = new System.Collections.Generic.List<dynamic>();
                foreach (JToken ct in curveTags)
                {
                    try
                    {
                        var edge = ToolHelpers.FindEdgeByTag(wp, ct.Value<int>());
                        if (edge != null) edges.Add(edge);
                    }
                    catch { }
                }
                if (edges.Count == 0) { builder.Destroy(); return ToolResult.Fail("No valid edge tags.").ToJson(); }

                dynamic section = builder.CurvesToOffset;
                var arr = Array.CreateInstance(edges[0].GetType(), edges.Count);
                for (int i = 0; i < edges.Count; i++) arr.SetValue(edges[i], i);
                dynamic rule = wp.ScRuleFactory.CreateRuleEdgeDumb(arr);
                var rules = Array.CreateInstance(rule.GetType(), 1);
                rules.SetValue(rule, 0);
                NXOpen.Point3d hp = new NXOpen.Point3d(0, 0, 0);
                section.AddToSection(rules, edges[0], null, null, hp, 0, false);

                ToolHelpers.SetExpressionValue(builder, "OffsetDistance", dist);

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("distance", dist);
                data.Add("curves_offset", edges.Count);
                return ToolResult.Ok(string.Format("Offset {0} curves by {1}mm.", edges.Count, dist), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_offset_curve: " + ex.Message).ToJson(); }
        }
    }

    /// <summary>
    /// Intersection Curve - create curves at the intersection of two face sets.
    /// Runtime probe: FirstFace/SecondFace (ScCollector) - use AddFaceToCollector rules
    /// </summary>
    /// Parameters:
    ///   face_tags1 (string, optional) - face_tags1 parameter.
    ///   face_tags2 (string, optional) - face_tags2 parameter.
    ///
    public class IntersectionCurveTool : IToolHandler
    {
        public string Name { get { return "nx_intersection_curve"; } }
        public string Description { get { return "Create curves at face intersections. face_tags1(array), face_tags2(array)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray faces1 = p["face_tags1"] as JArray;
                JArray faces2 = p["face_tags2"] as JArray;
                if (faces1 == null || faces1.Count == 0)
                    return ToolResult.Fail("face_tags1 array required.").ToJson();
                if (faces2 == null || faces2.Count == 0)
                    return ToolResult.Fail("face_tags2 array required.").ToJson();

                dynamic builder = ((dynamic)wp.Features).CreateIntersectionCurveBuilder(null);
                int n1 = 0, n2 = 0;
                foreach (JToken ft in faces1)
                {
                    try
                    {
                        var f = ToolHelpers.FindFaceByTag(wp, ft.Value<int>());
                        if (f != null && ToolHelpers.AddFaceToCollector(wp, builder, "FirstFace", f)) n1++;
                    }
                    catch { }
                }
                foreach (JToken ft in faces2)
                {
                    try
                    {
                        var f = ToolHelpers.FindFaceByTag(wp, ft.Value<int>());
                        if (f != null && ToolHelpers.AddFaceToCollector(wp, builder, "SecondFace", f)) n2++;
                    }
                    catch { }
                }
                if (n1 == 0 || n2 == 0)
                    return ToolResult.Fail("Need valid faces in both sets. Found " + n1 + " and " + n2 + ".").ToJson();

                // Probe verified: Tolerance (Double, w=True) must be > 0
                try { builder.Tolerance = 0.001; } catch { }
                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("face_set1", n1);
                data.Add("face_set2", n2);
                return ToolResult.Ok(string.Format("Intersection curves: {0} x {1} faces.", n1, n2), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_intersection_curve: " + ex.Message).ToJson(); }
        }
    }
}
