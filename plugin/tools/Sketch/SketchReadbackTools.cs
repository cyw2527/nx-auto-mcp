using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Sketch
{
    /// <summary>
    /// M1 readback tools (EXECUTION-LOOP-TODO 2026-08-21): expose the D-Cubed solver
    /// state that V1-V9 experiments proved readable to 1e-6. This is the "eyes" half
    /// of the 招式带靶 execution loop: 招式 -> 执行 -> 读回 -> 判定.
    ///
    /// Verified API surface (_const_v9.cs, 2026-08-19):
    ///   sketch.CalculateStatus(); Sketch.Status st = sketch.GetStatus(out int dof);
    ///   sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, Sketch.ConstraintType.X)
    /// 反编译验证的 members (NXOpen.dll, 2026-08-21):
    ///   SketchConstraint.ConstraintType (property)
    ///   SketchGeometricConstraint.GetGeometry() -> Sketch.ConstraintGeometry[]
    ///   SketchDimensionalConstraint.AssociatedExpression / GetDimensionGeometry()
    ///   Sketch.GetStatus(out int) / UpdateScope / GetAllGeometry() -> NXObject[]
    ///
    /// NX2412 quirk: CalculateStatus() + GetStatus() require the sketch to be
    /// ACTIVE. If it's deactivated, these throw "草图未初始化".
    /// The tools auto-activate for the query and re-deactivate afterwards.
    ///
    /// Namespace note: this file lives in NxMcpPlugin.Tools.Sketch, so bare
    /// "Sketch.Status" etc. resolve to the wrong type. All NXOpen.Sketch inner
    /// types (Status, ConstraintType, ConstraintClass) are fully qualified.
    /// </summary>
    internal static class SketchReadback
    {
        /// <summary>
        /// Resolve target sketch: explicit 'sketch_name' param wins; otherwise the
        /// active sketch (NX2412 has no Part.Sketches.ActiveSketch property).
        /// On failure, error message lists available sketches.
        /// </summary>
        public static NXOpen.Sketch ResolveSketch(dynamic workPart, JObject p, out string error)
        {
            error = null;
            string name = SketchHelpers.GetParamString(p, "sketch_name");

            if (!string.IsNullOrEmpty(name))
            {
                foreach (dynamic s in workPart.Sketches)
                {
                    if (string.Equals((string)s.Name, name, StringComparison.OrdinalIgnoreCase))
                        return (NXOpen.Sketch)s;
                }
                error = string.Format("Sketch '{0}' not found.", name);
            }
            else
            {
                foreach (dynamic s in workPart.Sketches)
                {
                    if (s.IsActive)
                        return (NXOpen.Sketch)s;
                }
                error = "No active sketch. Pass 'sketch_name' or activate a sketch first.";
            }

            var names = new List<string>();
            try
            {
                foreach (dynamic s in workPart.Sketches)
                {
                    try { names.Add((string)s.Name); } catch { }
                }
            }
            catch { }
            error += names.Count > 0
                ? " Available: " + string.Join(", ", names.ToArray())
                : " (no sketches in part)";
            return null;
        }

        /// <summary>
        /// Ensure sketch is active for solver queries. Returns true if it was
        /// already active; false means we activated it (caller should deactivate).
        /// </summary>
        public static bool EnsureActive(NXOpen.Sketch sk)
        {
            if (sk.IsActive) return true;
            sk.Activate(NXOpen.Sketch.ViewReorient.False);
            return false;
        }

        /// <summary>
        /// Deactivate sketch if we activated it.
        /// </summary>
        public static void DeactivateIfWeActivated(NXOpen.Sketch sk, bool wasActive)
        {
            if (!wasActive)
            {
                try
                {
                    sk.Deactivate(
                        NXOpen.Sketch.ViewReorient.False,
                        NXOpen.Sketch.UpdateLevel.Model);
                }
                catch { }
            }
        }

        /// <summary>
        /// Read the solver state (V9 verified call sequence).
        /// Auto-activates the sketch if needed (NX2412: CalculateStatus requires active sketch).
        /// Returns { sketch, status, dof, geometry_count, update_scope, hint?, read_errors? }.
        /// </summary>
        public static JObject ReadStatus(NXOpen.Sketch sk)
        {
            var errs = new JArray();
            bool wasActive = EnsureActive(sk);
            try
            {
                sk.CalculateStatus();
                int dof;
                NXOpen.Sketch.Status st = sk.GetStatus(out dof);

                string scope;
                try { scope = sk.UpdateScope.ToString(); }
                catch (Exception ex) { scope = "Unknown"; errs.Add("UpdateScope: " + ex.Message.Split('\n')[0]); }

                int geomCount = 0;
                try { geomCount = sk.GetAllGeometry().Length; }
                catch (Exception ex) { errs.Add("GetAllGeometry: " + ex.Message.Split('\n')[0]); }

                var o = new JObject();
                o["sketch"] = sk.Name;
                o["status"] = st.ToString();
                o["dof"] = dof;
                o["geometry_count"] = geomCount;
                o["update_scope"] = scope;
                // V2 lesson (correction table T-08): default UpdateScope=SketchOnly does
                // NOT propagate the solve to model geometry.
                if (st == NXOpen.Sketch.Status.NotEvaluated && scope != "Model")
                    o["hint"] = "status=NotEvaluated and UpdateScope!=Model - edits need sketch.UpdateScope=Model to propagate (V2/T-08).";
                if (errs.Count > 0) o["read_errors"] = errs;
                return o;
            }
            finally
            {
                DeactivateIfWeActivated(sk, wasActive);
            }
        }

        /// <summary>
        /// Enumerate constraints by type (V9 verified API: GetAllConstraintsOfType).
        /// Auto-activates the sketch if needed.
        /// Returns { total, geometric, dimensional, counts, constraints?, read_errors? }.
        /// </summary>
        public static JObject ReadConstraints(NXOpen.Sketch sk, bool includeDetails)
        {
            var counts = new JObject();
            var items = new JArray();
            var errs = new JArray();
            int total = 0, geometric = 0, dimensional = 0;

            bool wasActive = EnsureActive(sk);
            try
            {
                foreach (NXOpen.Sketch.ConstraintType ct in Enum.GetValues(typeof(NXOpen.Sketch.ConstraintType)))
                {
                    if (ct == NXOpen.Sketch.ConstraintType.NoCon || ct == NXOpen.Sketch.ConstraintType.LastConType)
                        continue;

                    NXOpen.SketchConstraint[] cons = null;
                    try { cons = sk.GetAllConstraintsOfType(NXOpen.Sketch.ConstraintClass.Any, ct); }
                    catch (Exception ex) { errs.Add("GetAllConstraintsOfType(" + ct + "): " + ex.Message.Split('\n')[0]); continue; }
                    if (cons == null || cons.Length == 0) continue;

                    counts[ct.ToString()] = cons.Length;
                    total += cons.Length;

                    if (!includeDetails) continue;
                    foreach (NXOpen.SketchConstraint c in cons)
                    {
                        var item = new JObject();
                        item["type"] = ct.ToString();

                        NXOpen.SketchGeometricConstraint gc = c as NXOpen.SketchGeometricConstraint;
                        if (gc != null)
                        {
                            geometric++;
                            var geoArr = new JArray();
                            try
                            {
                                NXOpen.Sketch.ConstraintGeometry[] geos = gc.GetGeometry();
                                if (geos != null)
                                    foreach (NXOpen.Sketch.ConstraintGeometry g in geos)
                                        geoArr.Add(GeometryLabel(g.Geometry, g.PointType.ToString()));
                            }
                            catch (Exception ex) { errs.Add("GetGeometry(" + ct + "): " + ex.Message.Split('\n')[0]); }
                            item["geometry"] = geoArr;
                        }

                        NXOpen.SketchDimensionalConstraint dc = c as NXOpen.SketchDimensionalConstraint;
                        if (dc != null)
                        {
                            dimensional++;
                            try
                            {
                                NXOpen.Expression e = dc.AssociatedExpression;
                                if (e != null)
                                {
                                    var ex = new JObject();
                                    ex["name"] = e.Name;
                                    try { ex["value"] = e.Value; } catch { }
                                    try { ex["rhs"] = e.RightHandSide; } catch { }
                                    item["expression"] = ex;
                                }
                            }
                            catch (Exception ex) { errs.Add("AssociatedExpression(" + ct + "): " + ex.Message.Split('\n')[0]); }
                            try
                            {
                                var geoArr = new JArray();
                                NXOpen.Sketch.DimensionGeometry[] dgs = dc.GetDimensionGeometry();
                                if (dgs != null)
                                    foreach (NXOpen.Sketch.DimensionGeometry dg in dgs)
                                        geoArr.Add(GeometryLabel(dg.Geometry, dg.AssocType.ToString()));
                                item["dimension_geometry"] = geoArr;
                            }
                            catch (Exception ex) { errs.Add("GetDimensionGeometry(" + ct + "): " + ex.Message.Split('\n')[0]); }
                        }

                        items.Add(item);
                    }
                }
            }
            finally
            {
                DeactivateIfWeActivated(sk, wasActive);
            }

            var o = new JObject();
            o["total"] = total;
            o["geometric"] = geometric;
            o["dimensional"] = dimensional;
            o["counts"] = counts;
            if (includeDetails)
                o["constraints"] = items;
            if (errs.Count > 0) o["read_errors"] = errs;
            return o;
        }

        /// <summary>Compact, stable label for a constraint-referenced object.</summary>
        private static string GeometryLabel(NXObject g, string assoc)
        {
            if (g == null) return "(null)";
            string n = g.Name;
            if (string.IsNullOrEmpty(n)) n = "tag:" + g.Tag;
            return string.Format("{0}[{1}]", n, assoc);
        }
    }

    // ========================================================================
    // 17. nx_sketch_status
    // ========================================================================

    /// <summary>
    /// Read sketch solver status: status enum, dof, constraint total, update scope.
    /// Parameters:
    ///   sketch_name (string, optional) - Name of the sketch to query. If omitted, uses the active sketch.
    /// Returns: { sketch, status, dof, geometry_count, update_scope, constraint_total }
    /// </summary>
    public class SketchStatusTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_status"; } }
        public string Description { get { return "Read sketch solver status: status enum, dof, constraint total, update scope."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string error;
                NXOpen.Sketch sk = SketchReadback.ResolveSketch(workPart, p, out error);
                if (sk == null)
                    return ToolResult.Fail(error).ToJson();

                JObject status = SketchReadback.ReadStatus(sk);
                try { status["constraint_total"] = SketchReadback.ReadConstraints(sk, false)["total"]; }
                catch { }

                return ToolResult.Ok(
                    string.Format("Sketch '{0}': {1}, dof={2}, constraints={3}.",
                        (string)status["sketch"], (string)status["status"],
                        (int)status["dof"], status["constraint_total"] != null ? (int)status["constraint_total"] : -1),
                    status).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_status failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 18. nx_sketch_constraints
    // ========================================================================

    /// <summary>
    /// Enumerate sketch constraints: per-type counts, geometry, and driving expressions.
    /// Parameters:
    ///   sketch_name (string, optional) - Name of the sketch to query. If omitted, uses the active sketch.
    ///   details (bool, optional) - Include per-constraint geometry and expression details (default: true).
    /// Returns: { sketch, total, geometric, dimensional, counts, constraints? }
    /// </summary>
    public class SketchConstraintsTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_constraints"; } }
        public string Description { get { return "Enumerate sketch constraints: per-type counts, geometry, and driving expressions."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string error;
                NXOpen.Sketch sk = SketchReadback.ResolveSketch(workPart, p, out error);
                if (sk == null)
                    return ToolResult.Fail(error).ToJson();

                bool details = SketchHelpers.GetParamBool(p, "details", true);
                JObject result = SketchReadback.ReadConstraints(sk, details);
                result["sketch"] = sk.Name;

                return ToolResult.Ok(
                    string.Format("Sketch '{0}': {1} constraints ({2} geometric, {3} dimensional).",
                        sk.Name, (int)result["total"], (int)result["geometric"], (int)result["dimensional"]),
                    result).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_constraints failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 19. nx_intent_snapshot
    // ========================================================================

    /// <summary>
    /// Composite snapshot: feature tree, expressions, sketch status, constraint counts, optional body measure.
    /// Parameters:
    ///   sketch_name (string, optional) - Name of the sketch to query. If omitted, uses the active sketch.
    ///   include_expressions (bool, optional) - Include expressions in snapshot (default: true).
    ///   include_measure (bool, optional) - Include body mass properties (default: false).
    /// Returns: { sketch?, features, expressions?, sketch_status?, constraints?, measure? }
    /// </summary>
    /// Parameters:
    ///   include_expressions (boolean, optional) - include_expressions parameter.
    ///   include_measure (boolean, optional) - include_measure parameter.
    ///
    public class IntentSnapshotTool : IToolHandler
    {
        public string Name { get { return "nx_intent_snapshot"; } }
        public string Description { get { return "Composite snapshot: feature tree, expressions, sketch status, constraint counts, optional body measure."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                bool includeExpressions = SketchHelpers.GetParamBool(p, "include_expressions", true);
                bool includeMeasure = SketchHelpers.GetParamBool(p, "include_measure", true);
                var errs = new JArray();

                var data = new JObject();
                data["part"] = workPart.Name;
                data["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // -- Feature tree summary --
                var features = new JArray();
                try
                {
                    foreach (dynamic feat in workPart.Features)
                    {
                        try
                        {
                            features.Add(new JObject
                            {
                                { "name", feat.Name.ToString() },
                                { "type", feat.FeatureType.ToString() },
                                { "timestamp", feat.Timestamp.ToString() }
                            });
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { errs.Add("Features: " + ex.Message.Split('\n')[0]); }
                data["feature_count"] = features.Count;
                data["feature_tree"] = features;

                // -- Expressions --
                if (includeExpressions)
                {
                    var exprs = new JObject();
                    try
                    {
                        foreach (dynamic e in workPart.Expressions)
                        {
                            try
                            {
                                string eName = e.Name.ToString();
                                JToken eVal;
                                try { eVal = new JValue(e.Value); }
                                catch { eVal = new JValue(e.RightHandSide.ToString()); }
                                exprs[eName] = eVal;
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex) { errs.Add("Expressions: " + ex.Message.Split('\n')[0]); }
                    data["expressions"] = exprs;
                }

                // -- Sketch status --
                try
                {
                    string error;
                    NXOpen.Sketch sk = SketchReadback.ResolveSketch(workPart, p, out error);
                    if (sk != null)
                    {
                        JObject skStatus = SketchReadback.ReadStatus(sk);
                        JObject skConstraints = SketchReadback.ReadConstraints(sk, false);
                        skStatus["constraint_total"] = skConstraints["total"];
                        data["sketch"] = skStatus;
                        data["constraint_counts"] = skConstraints["counts"];
                    }
                    else
                    {
                        data["sketch"] = JValue.CreateNull();
                    }
                }
                catch (Exception ex) { errs.Add("Sketch: " + ex.Message.Split('\n')[0]); data["sketch"] = JValue.CreateNull(); }

                // -- Body measure (best effort: first body only) --
                if (includeMeasure)
                {
                    try
                    {
                        var bodies = NxMcpPlugin.Tools.Measure.NxCollectionHelper.ToList(workPart.Bodies);
                        if (bodies.Count > 0)
                        {
                            var mp = NxMcpPlugin.Tools.Measure.MeasureHelpers.GetBodyMassProps(session, bodies[0]);
                            var measure = new JObject();
                            measure["body"] = (string)bodies[0].Name;
                            measure["volume_mm3"] = mp.Volume;
                            measure["area_mm2"] = mp.Area;
                            measure["mass_kg"] = mp.Mass;
                            data["measure"] = measure;
                        }
                        else
                        {
                            data["measure"] = JValue.CreateNull();
                        }
                    }
                    catch (Exception ex) { errs.Add("Measure: " + ex.Message.Split('\n')[0]); data["measure"] = JValue.CreateNull(); }
                }

                if (errs.Count > 0) data["read_errors"] = errs;

                string skInfo = "none";
                try
                {
                    if (data["sketch"] != null && data["sketch"].Type != JTokenType.Null)
                        skInfo = (string)data["sketch"]["sketch"] + "/" + (string)data["sketch"]["status"];
                }
                catch { }

                return ToolResult.Ok(
                    string.Format("Snapshot '{0}': {1} feature(s), sketch={2}.",
                        (string)data["part"], (int)data["feature_count"], skInfo),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_intent_snapshot failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}
