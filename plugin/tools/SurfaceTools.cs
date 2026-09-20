using System;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Surface
{
    /// <summary>
    /// Common helper: create a Section from sketch/curve tags via SelectionIntentRule.
    /// </summary>
    internal static class SectionHelper
    {
        private static readonly string LogPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "nx-mcp-surface-debug.log");

        public static void Log(string msg) {
            try { System.IO.File.AppendAllText(LogPath,
                DateTime.Now.ToString("s") + " " + msg + "\n"); } catch { }
        }

        /// <summary>Create Section from a sketch name.</summary>
        public static dynamic CreateSectionFromSketch(dynamic wp, string sketchName)
        {
            if (wp == null || string.IsNullOrEmpty(sketchName)) { Log("NULL wp/name"); return null; }
            try
            {
                dynamic found = null; int sketchCount = 0;
                foreach (dynamic sk in wp.Sketches)
                { sketchCount++; if (sk != null) { try { if (sk.Name == sketchName || sk.Name.ToString().EndsWith(sketchName)) { found = sk; break; } } catch { } } }
                if (found == null) { Log("SKETCH_NOT_FOUND: " + sketchName + " (checked " + sketchCount + " sketches)"); return null; }
                Log("SKETCH_FOUND: " + sketchName);

                dynamic section = wp.Sections.CreateSection(0.00095, 0.001, 0.01);
                Log("SECTION_CREATED: " + sketchName);

                var curveList = new System.Collections.Generic.List<dynamic>();
                try { dynamic sc = found.GetAllGeometry(); foreach (dynamic c in sc) { if (c != null) curveList.Add(c); } }
                catch (Exception ex) { SectionHelper.Log("GETALLGEOM_EXCEPTION: " + sketchName + " -> " + ex.Message); }
                if (curveList.Count == 0) { Log("GETALLGEOM_EMPTY: " + sketchName); return null; }
                Log("GETALLGEOM_OK: " + sketchName + " -> " + curveList.Count + " curves");

                var iBaseCurves = new System.Collections.Generic.List<NXOpen.IBaseCurve>();
                foreach (dynamic curve in curveList) { try { iBaseCurves.Add((NXOpen.IBaseCurve)curve); } catch { } }
                if (iBaseCurves.Count == 0) { Log("IBASECURVE_CAST_FAIL: " + sketchName + " (had " + curveList.Count + " curves)"); return null; }
                Log("IBASECURVE_OK: " + sketchName + " -> " + iBaseCurves.Count);

                NXOpen.IBaseCurve[] curveArray = iBaseCurves.ToArray();
                dynamic rule = wp.ScRuleFactory.CreateRuleBaseCurveDumb(curveArray);
                NXOpen.SelectionIntentRule selRule = (NXOpen.SelectionIntentRule)rule;
                NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { selRule };
                NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                NXOpen.NXObject refObj = (NXOpen.NXObject)curveArray[0];
                section.AddToSection(selRules, refObj, null, null, helpPoint, 0, false);
                Log("SECTION_COMPLETE: " + sketchName);

                return section;
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("s") + " CreateSectionFromSketch FAILED for " + sketchName + ": " + ex.ToString() + "\n"); } catch { }
                return null;
            }
        }

        /// <summary>
        /// Fill a builder-owned section (FirstSection/SecondSection of Builder1 types) with a sketch's curves.
        /// </summary>
        public static bool FillBuilderSection(dynamic section, dynamic wp, string sketchName)
        {
            try
            {
                if (section == null) { Log("FILL_SECTION_NULL: " + sketchName); return false; }
                dynamic found = null;
                foreach (dynamic sk in wp.Sketches)
                {
                    try { if (sk.Name == sketchName || sk.Name.ToString().EndsWith(sketchName)) { found = sk; break; } } catch { }
                }
                if (found == null) { Log("FILL_SKETCH_NOT_FOUND: " + sketchName); return false; }

                var iBaseCurves = new System.Collections.Generic.List<NXOpen.IBaseCurve>();
                try
                {
                    dynamic sc = found.GetAllGeometry();
                    foreach (dynamic c in sc)
                    {
                        try { iBaseCurves.Add((NXOpen.IBaseCurve)c); } catch { }
                    }
                }
                catch (Exception ex) { SectionHelper.Log("FILL_GETALLGEOM_EXCEPTION: " + ex.Message); return false; }
                if (iBaseCurves.Count == 0) { Log("FILL_GEOM_EMPTY: " + sketchName); return false; }

                NXOpen.IBaseCurve[] curveArray = iBaseCurves.ToArray();
                dynamic rule = wp.ScRuleFactory.CreateRuleBaseCurveDumb(curveArray);
                NXOpen.SelectionIntentRule selRule = (NXOpen.SelectionIntentRule)rule;
                NXOpen.SelectionIntentRule[] selRules = new NXOpen.SelectionIntentRule[] { selRule };
                NXOpen.Point3d helpPoint = new NXOpen.Point3d(0.0, 0.0, 0.0);
                NXOpen.NXObject refObj = (NXOpen.NXObject)curveArray[0];
                section.AddToSection(selRules, refObj, null, null, helpPoint, 0, false);
                Log("FILL_SECTION_COMPLETE: " + sketchName);
                return true;
            }
            catch (Exception ex)
            {
                Log("FILL_SECTION_EXCEPTION: " + sketchName + " -> " + ex.Message);
                return false;
            }
        }

        /// <summary>Create Section from raw curve/edge tag.</summary>
        public static dynamic CreateSectionFromCurveTag(dynamic wp, int tag)
        {
            if (wp == null || tag <= 0) return null;
            try
            {
                var curve = ToolHelpers.FindEdgeByTag(wp, tag);
                if (curve == null) return null;

                dynamic section = wp.Sections.CreateSection(0.001, 0.001, 0.001);
                var ruleFactory = wp.ScRuleFactory;
                var curveArr = Array.CreateInstance(curve.GetType(), 1);
                curveArr.SetValue(curve, 0);
                var rule = ruleFactory.CreateRuleCurveDumb(curveArr);
                var ruleArr = Array.CreateInstance(rule.GetType(), 1);
                ruleArr.SetValue(rule, 0);
                section.AddToSection(ruleArr, curve, null, NXOpen.Section.Mode.Create, false);
                return section;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Through Curves — loft through multiple section sketches.
    /// </summary>
    public class ThroughCurvesTool : IToolHandler
    {
        public string Name { get { return "nx_through_curves"; } }
        public string Description { get { return "Loft through section curves. sketch_names(array), body_preference(solid/sheet), closed(bool)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray sketches = p["sketch_names"] as JArray;
                if (sketches == null || sketches.Count < 2)
                    return ToolResult.Fail("sketch_names array with >=2 sketches required.").ToJson();
                string bodyPref = (p.Value<string>("body_preference") ?? "solid").Trim().ToLowerInvariant();
                bool closed = p.Value<bool?>("closed") ?? false;

                dynamic builder = ((dynamic)wp.Features).CreateThroughCurvesBuilder(null);
                int added = 0;
                foreach (JToken s in sketches)
                {
                    string skName = s.Value<string>();
                    try
                    {
                        SectionHelper.Log("THRU_LOOP " + skName);
                        dynamic secObj = SectionHelper.CreateSectionFromSketch(wp, skName);
                        SectionHelper.Log("THRU_SECTION " + skName);
                        if (secObj != null)
                        {
                            builder.SectionsList.Append((NXOpen.Section)secObj);
                            added++;
                            SectionHelper.Log("THRU_APPEND_OK " + skName + " added=" + added);
                        }
                    }
                    catch (Exception ex)
                    {
                        SectionHelper.Log("THRU_ERR " + skName + " " + ex.GetType().Name + " " + ex.Message);
                    }
                }
                SectionHelper.Log("THRU added=" + added + " of " + sketches.Count);
                if (added < 2) return ToolResult.Fail("Need >=2 valid sketches. Found " + added + ".").ToJson();

                try { builder.SetBodyPreference(bodyPref == "sheet" ? 0 : 1); } catch { }
                try { builder.SetClosedInV(closed); } catch { }
                try { builder.SetPositionTolerance(0.001); } catch { }

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("section_count", added);
                data.Add("body_preference", bodyPref);
                return ToolResult.Ok(string.Format("Through Curves: {0} sections -> {1}.", added, bodyPref), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_through_curves: " + ex.Message).ToJson(); }
        }
    }

    /// <summary>
    /// Ruled Surface — simple ruled surface between two section curves.
    /// </summary>
    public class RuledSurfaceTool : IToolHandler
    {
        public string Name { get { return "nx_ruled"; } }
        public string Description { get { return "Create ruled surface between two section curves. sketch1, sketch2, body_preference(solid/sheet)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                string s1 = p.Value<string>("sketch1") ?? "";
                string s2 = p.Value<string>("sketch2") ?? "";
                if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                    return ToolResult.Fail("sketch1 and sketch2 required.").ToJson();
                string bodyPref = (p.Value<string>("body_preference") ?? "solid").Trim().ToLowerInvariant();

                dynamic builder = wp.Features.FreeformSurfaceCollection.CreateRuledBuilder1(null);
                bool ok1 = SectionHelper.FillBuilderSection(builder.FirstSection, wp, s1);
                if (!ok1) return ToolResult.Fail("Fill section failed: " + s1).ToJson();
                bool ok2 = SectionHelper.FillBuilderSection(builder.SecondSection, wp, s2);
                if (!ok2) return ToolResult.Fail("Fill section failed: " + s2).ToJson();
                builder.PositionTolerance = 0.001;
                try { builder.SetBodyPreference(bodyPref == "sheet" ? 0 : 1); } catch { }

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("sketch1", s1);
                data.Add("sketch2", s2);
                return ToolResult.Ok(string.Format("Ruled surface: {0} -> {1}.", s1, s2), data).ToJson();
            }
            catch (Exception ex)
            {
                string inner = ex.InnerException != null ? " | inner: " + ex.InnerException.Message : "";
                string stack = ex.StackTrace != null && ex.StackTrace.Contains("Section") ? " | " + ex.StackTrace.Split('\n')[0].Trim() : "";
                return ToolResult.Fail("nx_ruled: " + ex.Message + inner + stack).ToJson();
            }
        }
    }

    /// <summary>
    /// Studio Surface — freeform surface through section and guide curves.
    /// </summary>
    public class StudioSurfaceTool : IToolHandler
    {
        public string Name { get { return "nx_studio_surface"; } }
        public string Description { get { return "Create studio surface. section_sketches(array), guide_sketches(array, optional)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray sections = p["section_sketches"] as JArray;
                if (sections == null || sections.Count == 0)
                    return ToolResult.Fail("section_sketches array required.").ToJson();

                dynamic builder = ((dynamic)wp.Features).CreateStudioSurfaceBuilder(null);
                int added = 0;
                foreach (JToken s in sections)
                {
                    try
                    {
                        var section = SectionHelper.CreateSectionFromSketch(wp, s.Value<string>());
                        if (section != null) { builder.SectionList.Append((NXOpen.Section)section); added++; }
                    }
                    catch (Exception ex)
                    {
                        SectionHelper.Log("STUDIO_Append FAILED: " + ex.Message);
                    }
                }
                if (added == 0) return ToolResult.Fail("No valid sections found.").ToJson();

                JArray guides = p["guide_sketches"] as JArray;
                int gadded = 0;
                if (guides != null)
                {
                    foreach (JToken g in guides)
                    {
                        try
                        {
                            var section = SectionHelper.CreateSectionFromSketch(wp, g.Value<string>());
                            if (section != null) { builder.GuideList.Append(section); gadded++; }
                        }
                        catch { }
                    }
                }
                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("sections", added);
                data.Add("guides", gadded);
                return ToolResult.Ok(string.Format("Studio Surface: {0} sections, {1} guides.", added, gadded), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_studio_surface: " + ex.Message).ToJson(); }
        }
    }

    /// <summary>
    /// Extend Surface — extend sheet body edges.
    /// </summary>
    public class ExtendSurfaceTool : IToolHandler
    {
        public string Name { get { return "nx_extend_surface"; } }
        public string Description { get { return "Extend a sheet face. face_tags(array), length(mm)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                var faceTags = ToolHelpers.GetIntArray(p, "face_tags");
                if (faceTags.Count == 0) return ToolResult.Fail("face_tags array required.").ToJson();
                double length = ToolHelpers.GetDouble(p, "length", 10.0);

                dynamic workView = null;
                try { workView = wp.Views.WorkView; } catch (Exception ex) { SectionHelper.Log("view lookup: " + ex.Message); }

                int added = 0;
                foreach (int tag in faceTags)
                {
                    try
                    {
                        var face = ToolHelpers.FindFaceByTag(wp, tag);
                        if (face == null) { SectionHelper.Log("face not found: " + tag); continue; }
                        dynamic builder = ((dynamic)wp.Features).CreateExtensionBuilder(null);
                        builder.Tolerance = 0.01;
                        builder.Type = NXOpen.Features.ExtensionBuilder.Types.Edge;
                        builder.ExtendType = NXOpen.Features.ExtensionBuilder.Extension.Tangential;
                        builder.DistanceType = NXOpen.Features.ExtensionBuilder.Distance.ByLength;
                        builder.Length.RightHandSide = length.ToString();
                        double[] bb = new double[6];
                        try
                        {
                            var ufs = NXOpen.UF.UFSession.GetUFSession();
                            ufs.Sf.FaceAskBoundingBox(face.Tag, bb);
                        }
                        catch (Exception ex) { SectionHelper.Log("bbox: " + ex.Message); }
                        NXOpen.Point3d pt = new NXOpen.Point3d((bb[0] + bb[3]) / 2.0, (bb[1] + bb[4]) / 2.0, (bb[2] + bb[5]) / 2.0);
                        builder.Selection.SetValue(face, workView, pt);
                        builder.Commit(); builder.Destroy();
                        added++;
                    }
                    catch (Exception ex) { SectionHelper.Log("extend face " + tag + ": " + ex.Message); }
                }
                if (added == 0) return ToolResult.Fail("No valid faces.").ToJson();
                var data = new JObject();
                data.Add("face_count", added);
                data.Add("length", length);
                return ToolResult.Ok(string.Format("Surface extended: {0} face(s) by {1}mm.", added, length), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_extend_surface: " + ex.Message).ToJson(); }
        }
    }

    /// <summary>
    /// Mid Surface — create midsurface between opposing faces.
    /// </summary>
    public class MidSurfaceTool : IToolHandler
    {
        public string Name { get { return "nx_midsurface"; } }
        public string Description { get { return "Create midsurface between face pairs. face_tags1(array), face_tags2(array)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                var tags1 = ToolHelpers.GetIntArray(p, "face_tags1");
                var tags2 = ToolHelpers.GetIntArray(p, "face_tags2");
                if (tags1.Count == 0 && tags2.Count == 0)
                    return ToolResult.Fail("face_tags1 or face_tags2 required.").ToJson();
                bool autoStitch = ToolHelpers.GetBool(p, "auto_stitch", false);
                double tolerance = ToolHelpers.GetDouble(p, "tolerance", 1.0);

                dynamic builder = ((dynamic)wp.Features).CreateMidSurfaceBuilder(null);
                builder.AutoStitchEdges = autoStitch;
                builder.DistanceTolerance = tolerance;
                dynamic fp = builder.FacePairing;

                var allTags = new System.Collections.Generic.List<int>(tags1);
                allTags.AddRange(tags2);
                var bodies = new System.Collections.Generic.List<dynamic>();
                foreach (int tag in allTags)
                {
                    try
                    {
                        var face = ToolHelpers.FindFaceByTag(wp, tag);
                        if (face == null) continue;
                        foreach (dynamic b in wp.Bodies)
                        {
                            foreach (dynamic f in b.GetFaces())
                            {
                                try { if ((int)Convert.ToDouble(f.Tag) == tag) { if (!bodies.Contains(b)) bodies.Add(b); break; } } catch { }
                            }
                        }
                    }
                    catch { }
                }
                if (bodies.Count > 0)
                {
                    var arr = Array.CreateInstance(bodies[0].GetType(), bodies.Count);
                    for (int i = 0; i < bodies.Count; i++) arr.SetValue(bodies[i], i);
                    dynamic rule = wp.ScRuleFactory.CreateRuleBodyDumb(arr);
                    var rules = Array.CreateInstance(rule.GetType(), 1);
                    rules.SetValue(rule, 0);
                    fp.InputSolidBodies.ReplaceRules(rules, false);
                }

                int pairCount = 0;
                foreach (int tag in allTags)
                {
                    try
                    {
                        var face = ToolHelpers.FindFaceByTag(wp, tag);
                        if (face != null) { fp.IncludeFace(face); pairCount++; }
                    }
                    catch (Exception ex) { SectionHelper.Log("midsurface IncludeFace " + tag + ": " + ex.Message); }
                }
                if (pairCount == 0) { builder.Destroy(); return ToolResult.Fail("No valid faces for midsurface.").ToJson(); }

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("pair_count", pairCount);
                return ToolResult.Ok(string.Format("Mid surface: {0} faces paired.", pairCount), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_midsurface: " + ex.Message).ToJson(); }
        }
    }
}
