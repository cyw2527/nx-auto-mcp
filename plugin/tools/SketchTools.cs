using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;

namespace NxMcpPlugin.Tools.Sketch
{
    /// <summary>
    /// NX2412 Sketch tool handlers — converts Python NX MCP sketch.py to C# NXOpen .NET API.
    ///
    /// NX2412 quirks handled:
    ///   - No Part.Sketches.ActiveSketch property — must iterate Sketches and check IsActive.
    ///   - Sketch.Deactivate() requires two arguments: ViewReorient + UpdateLevel.
    ///   - CreateNewSketchInPlaceBuilder(None) for sketch creation.
    ///   - Curves.CreateArc with xDir/yDir vectors for arc/circle creation.
    ///   - Curves.CreateSplineByPoints for spline creation.
    ///
    /// All 12 tools from the Python sketch.py are implemented here.
    /// </summary>
    public static class SketchHelpers
    {
        // ---- Parameter helpers ----

        public static string GetParamString(JObject p, string key, string def = null)
        {
            var token = (p != null) ? p[key] : (JToken)null;
            if (token == null || token.Type == JTokenType.Null) return def;
            return token.ToString();
        }

        public static double GetParamDouble(JObject p, string key, double def = 0.0)
        {
            var token = (p != null) ? p[key] : (JToken)null;
            if (token == null || token.Type == JTokenType.Null) return def;
            return token.ToObject<double>();
        }

        public static int GetParamInt(JObject p, string key, int def = 0)
        {
            var token = (p != null) ? p[key] : (JToken)null;
            if (token == null || token.Type == JTokenType.Null) return def;
            return token.ToObject<int>();
        }

        public static bool GetParamBool(JObject p, string key, bool def = false)
        {
            var token = (p != null) ? p[key] : (JToken)null;
            if (token == null || token.Type == JTokenType.Null) return def;
            return token.ToObject<bool>();
        }

        public static JArray GetParamArray(JObject p, string key)
        {
            var token = (p != null) ? p[key] as JArray : null;
            return token;
        }

        // ---- NX2412 helpers ----

        /// <summary>
        /// Get the active sketch in NX2412 (no ActiveSketch property).
        /// Iterates work_part.Sketches and checks IsActive.
        /// </summary>
        public static dynamic GetActiveSketch(dynamic workPart)
        {
            foreach (dynamic s in workPart.Sketches)
            {
                if (s.IsActive) return s;
            }
            return null;
        }

        /// <summary>
        /// Resolve a curve by name from work_part.Curves collection.
        /// </summary>
        public static dynamic FindCurveByName(dynamic workPart, string name)
        {
            foreach (dynamic c in workPart.Curves)
            {
                if (c.Name == name) return c;
            }
            return null;
        }

        /// <summary>
        /// Resolve multiple curves by name from work_part.Curves collection.
        /// Returns null and sets error if any name is not found.
        /// </summary>
        public static List<dynamic> FindCurvesByName(dynamic workPart, JArray names, out string error)
        {
            error = null;
            var result = new List<dynamic>();
            foreach (var token in names)
            {
                string name = token.ToString();
                var curve = FindCurveByName(workPart, name);
                if (curve == null)
                {
                    error = string.Format("Curve '{0}' not found.", name);
                    return null;
                }
                result.Add(curve);
            }
            return result;
        }

        /// <summary>
        /// Valid sketch plane names and their NXOpen Vector3d normal directions.
        /// </summary>
        public static readonly Dictionary<string, double[]> PlaneNormals =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
            {
                {"XY", new[] { 0.0, 0.0, 1.0 }},
                {"XZ", new[] { 0.0, 1.0, 0.0 }},
                {"YZ", new[] { 1.0, 0.0, 0.0 }},
            };

        /// <summary>
        /// Map from plane key to datum plane name for finding in work_part.Datums.
        /// </summary>
        public static readonly Dictionary<string, string> PlaneDatumNameMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"XY", "XY Plane"},
                {"XZ", "XZ Plane"},
                {"YZ", "YZ Plane"},
            };

        /// <summary>
        /// Plane orientation matrix for a principal plane. Matrix ROWS are the plane's
        /// in-plane X/Y axes and the Z row is the plane normal:
        ///   XY: X=(1,0,0) Y=(0,1,0) normal=(0,0,1)
        ///   XZ: X=(1,0,0) Y=(0,0,1) normal=(0,1,0)
        ///   YZ: X=(0,1,0) Y=(0,0,1) normal=(1,0,0)
        /// Used with Planes.CreateFixedPlane for nx_create_sketch (TODO-1, 2026-08-20).
        /// </summary>
        public static NXOpen.Matrix3x3 PlaneMatrix(string key)
        {
            NXOpen.Matrix3x3 m = new NXOpen.Matrix3x3();
            switch (key.Trim().ToUpperInvariant())
            {
                case "XZ":
                    m.Xx = 1.0; m.Xy = 0.0; m.Xz = 0.0;
                    m.Yx = 0.0; m.Yy = 0.0; m.Yz = 1.0;
                    m.Zx = 0.0; m.Zy = 1.0; m.Zz = 0.0;
                    break;
                case "YZ":
                    m.Xx = 0.0; m.Xy = 1.0; m.Xz = 0.0;
                    m.Yx = 0.0; m.Yy = 0.0; m.Yz = 1.0;
                    m.Zx = 1.0; m.Zy = 0.0; m.Zz = 0.0;
                    break;
                default: // XY
                    m.Xx = 1.0; m.Xy = 0.0; m.Xz = 0.0;
                    m.Yx = 0.0; m.Yy = 1.0; m.Yz = 0.0;
                    m.Zx = 0.0; m.Zy = 0.0; m.Zz = 1.0;
                    break;
            }
            return m;
        }

        /// <summary>
        /// Sketch X-axis (horizontal reference) direction in world for a principal plane.
        /// XY: world X; XZ: world X; YZ: world Y.
        /// </summary>
        public static NXOpen.Vector3d PlaneXAxis(string key)
        {
            if (key.Trim().ToUpperInvariant() == "YZ")
                return new NXOpen.Vector3d(0.0, 1.0, 0.0);
            return new NXOpen.Vector3d(1.0, 0.0, 0.0);
        }

        /// <summary>
        /// Map sketch-local (u, v) to a world Point3d using the active sketch's plane.
        /// world = sketch.Origin + u * sketchX + v * sketchY, where sketchX/sketchY are the
        /// first two ROWS of sketch.Orientation (NXMatrix.Element). Handles XY/XZ/YZ/arbitrary planes.
        /// </summary>
        public static NXOpen.Point3d MapToWorld(dynamic sketch, double u, double v)
        {
            NXOpen.Point3d origin = (NXOpen.Point3d)sketch.Origin;
            NXOpen.Matrix3x3 m = ((NXOpen.NXMatrix)sketch.Orientation).Element;
            double x = origin.X + u * m.Xx + v * m.Yx;
            double y = origin.Y + u * m.Xy + v * m.Yy;
            double z = origin.Z + u * m.Xz + v * m.Yz;
            return new NXOpen.Point3d(x, y, z);
        }

        /// <summary>
        /// Get the active sketch's in-plane X/Y axes as world vectors (rows of Orientation matrix).
        /// </summary>
        public static void GetSketchAxes(dynamic sketch, out NXOpen.Vector3d xAxis, out NXOpen.Vector3d yAxis)
        {
            NXOpen.Matrix3x3 m = ((NXOpen.NXMatrix)sketch.Orientation).Element;
            xAxis = new NXOpen.Vector3d(m.Xx, m.Xy, m.Xz);
            yAxis = new NXOpen.Vector3d(m.Yx, m.Yy, m.Yz);
        }

        /// <summary>
        /// Create a ConstraintGeometry from a curve for sketch constraint operations.
        /// NX2412: Uses Sketch.ConstraintGeometry with default point type.
        /// </summary>
        public static NXOpen.Sketch.ConstraintGeometry CreateConstraintGeometry(dynamic curve)
        {
            return new NXOpen.Sketch.ConstraintGeometry(
                (NXOpen.NXObject)curve,
                0 /* NotSet */,
                0);
        }

        /// <summary>
        /// Corner point of two curves for fillet help points: exact 2D line-line
        /// intersection when both are lines (also handles shared-endpoint corners);
        /// falls back to midpoint of closest endpoints otherwise.
        /// </summary>
        public static NXOpen.Point3d ComputeCornerPoint(NXOpen.Curve c1, NXOpen.Curve c2)
        {
            NXOpen.Line l1 = c1 as NXOpen.Line;
            NXOpen.Line l2 = c2 as NXOpen.Line;
            if (l1 != null && l2 != null)
            {
                // 3D parametric closest-point intersection of two coplanar lines —
                // works on ANY sketch plane (XY/XZ/YZ). Previously used world XY only (z=0 hardcoded).
                double ax = l1.StartPoint.X, ay = l1.StartPoint.Y, az = l1.StartPoint.Z;
                double bx = l1.EndPoint.X,   by = l1.EndPoint.Y,   bz = l1.EndPoint.Z;
                double cx = l2.StartPoint.X, cy = l2.StartPoint.Y, cz = l2.StartPoint.Z;
                double dx = l2.EndPoint.X,   dy = l2.EndPoint.Y,   dz = l2.EndPoint.Z;
                double v1x = bx - ax, v1y = by - ay, v1z = bz - az;
                double v2x = dx - cx, v2y = dy - cy, v2z = dz - cz;
                double w0x = ax - cx, w0y = ay - cy, w0z = az - cz;
                double a_ = v1x * v1x + v1y * v1y + v1z * v1z;
                double b_ = v1x * v2x + v1y * v2y + v1z * v2z;
                double c_ = v2x * v2x + v2y * v2y + v2z * v2z;
                double d_ = v1x * w0x + v1y * w0y + v1z * w0z;
                double e_ = v2x * w0x + v2y * w0y + v2z * w0z;
                double denom = a_ * c_ - b_ * b_;
                if (denom > 1e-12)
                {
                    double t = (b_ * e_ - c_ * d_) / denom;
                    if (t >= -0.05 && t <= 1.05)
                        return new NXOpen.Point3d(ax + t * v1x, ay + t * v1y, az + t * v1z);
                }
            }

            NXOpen.Point3d[] e1 = GetCurveEndpoints(c1);
            NXOpen.Point3d[] e2 = GetCurveEndpoints(c2);
            NXOpen.Point3d best = new NXOpen.Point3d(0, 0, 0);
            double bestDist = double.MaxValue;
            foreach (NXOpen.Point3d a in e1)
            {
                foreach (NXOpen.Point3d b in e2)
                {
                    double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                    double d = dx * dx + dy * dy + dz * dz;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = new NXOpen.Point3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// First point of a curve (line/arc start point) for Section seed help points.
        /// </summary>
        public static NXOpen.Point3d FirstPoint(NXOpen.Curve c)
        {
            NXOpen.Point3d[] eps = GetCurveEndpoints(c);
            return eps.Length > 0 ? eps[0] : new NXOpen.Point3d(0, 0, 0);
        }

        /// <summary>
        /// A point ON the curve near the target (line: projection clamped + nudged 5% toward
        /// interior; arc: radial projection). Used for Sketch.Fillet help points — verified
        /// 2026-08-15 that exactly-at-corner help points fail to solve.
        /// </summary>
        public static NXOpen.Point3d PointOnCurveNear(NXOpen.Curve c, NXOpen.Point3d target)
        {
            NXOpen.Line l = c as NXOpen.Line;
            if (l != null)
            {
                // 3D projection — works on any sketch plane (XY/XZ/YZ).
                double x1 = l.StartPoint.X, y1 = l.StartPoint.Y, z1 = l.StartPoint.Z;
                double x2 = l.EndPoint.X,   y2 = l.EndPoint.Y,   z2 = l.EndPoint.Z;
                double dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
                double len2 = dx * dx + dy * dy + dz * dz;
                double t = len2 > 1e-12 ? ((target.X - x1) * dx + (target.Y - y1) * dy + (target.Z - z1) * dz) / len2 : 0.5;
                if (t < 0.001) t = 0.05;
                else if (t > 0.999) t = 0.95;
                else if (Math.Abs(t - 0.5) < 0.001) t = 0.45;
                return new NXOpen.Point3d(x1 + t * dx, y1 + t * dy, z1 + t * dz);
            }
            NXOpen.Arc a = c as NXOpen.Arc;
            if (a != null)
            {
                // Angle computed in the arc's own plane (Matrix rows = in-plane axes).
                NXOpen.Matrix3x3 m = ((NXOpen.NXMatrix)a.Matrix).Element;
                double tdx = target.X - a.CenterPoint.X;
                double tdy = target.Y - a.CenterPoint.Y;
                double tdz = target.Z - a.CenterPoint.Z;
                double ux = tdx * m.Xx + tdy * m.Xy + tdz * m.Xz;
                double uy = tdx * m.Yx + tdy * m.Yy + tdz * m.Yz;
                double ang = Math.Atan2(uy, ux);
                double cosA = Math.Cos(ang), sinA = Math.Sin(ang);
                return new NXOpen.Point3d(
                    a.CenterPoint.X + a.Radius * (cosA * m.Xx + sinA * m.Yx),
                    a.CenterPoint.Y + a.Radius * (cosA * m.Xy + sinA * m.Yy),
                    a.CenterPoint.Z + a.Radius * (cosA * m.Xz + sinA * m.Yz));
            }
            return target;
        }

        private static NXOpen.Point3d[] GetCurveEndpoints(NXOpen.Curve c)
        {
            try
            {
                NXOpen.Line ln = c as NXOpen.Line;
                if (ln != null) return new NXOpen.Point3d[] { ln.StartPoint, ln.EndPoint };
                NXOpen.Arc arc = c as NXOpen.Arc;
                if (arc != null) return new NXOpen.Point3d[] { arc.CenterPoint, arc.CenterPoint };
            }
            catch { }
            return new NXOpen.Point3d[] { new NXOpen.Point3d(0, 0, 0) };
        }
    }

    // ========================================================================
    // 1. nx_create_sketch
    // ========================================================================

    /// <summary>
    /// Create a new sketch on the specified reference plane (XY, XZ, or YZ).
    /// Uses NX2412 CreateNewSketchInPlaceBuilder(None) and iterates Datums
    /// to find the matching datum plane reference.
    /// Parameters:
    ///   plane (string, optional) - Reference plane: "XY" (default), "XZ", or "YZ".
    ///   name (string, optional) - Sketch name (default: auto-generated).
    ///
    /// NX2412 quirks:
    ///   - Activate() requires Sketch.ViewReorient enum, NOT a boolean.
    ///   - PlaneReference must be set to a valid datum plane before Commit().
    ///   - Datum names vary by locale; use case-insensitive Contains matching
    ///     with both "XY Plane" and "Datum Plane" patterns.
    ///   - Error 4360002 = invalid builder state (null PlaneReference or wrong Activate arg type).
    /// </summary>
    public class CreateSketchTool : IToolHandler
    {
        public string Name { get { return "nx_create_sketch"; } }
        public string Description { get { return "Create a new sketch on the specified reference plane (XY, XZ, or YZ)."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string plane = SketchHelpers.GetParamString(p, "plane", "XY");
                string name = SketchHelpers.GetParamString(p, "name");

                string key = plane.Trim().ToUpperInvariant();
                if (!SketchHelpers.PlaneNormals.ContainsKey(key))
                {
                    string valid = string.Join(", ", SketchHelpers.PlaneNormals.Keys);
                    return ToolResult.Fail(string.Format("Invalid plane '{0}'. Use one of: {1}.", plane, valid)).ToJson();
                }

                // NX2412 TODO-1 fix (2026-08-20, journal-verified):
                // CreateNewSketchInPlaceBuilder IGNORES PlaneReference for non-XY planes —
                // sketch silently lands on work plane XY even with PlaneOption=ExistingPlane
                // (实测: 6 配置全落在 XY, 加 AxisReference 反而报"目标面损坏").
                // ✅ 两段式 (journal 实测 SKETCH_004): 先 XY 默认提交 → CreateSketchInPlaceBuilder2 重附着:
                //   PlaneOption=ExistingPlane + PlaneReference=CreateFixedPlane(origin,matrix) + AxisReference.
                dynamic builder = workPart.Sketches.CreateNewSketchInPlaceBuilder(null);
                dynamic sketch = builder.Commit();
                builder.Destroy();

                if (key != "XY")
                {
                    NXOpen.SketchInPlaceBuilder b2 = workPart.Sketches.CreateSketchInPlaceBuilder2((NXOpen.Sketch)sketch);
                    NXOpen.Plane planeRef = workPart.Planes.CreateFixedPlane(
                        new NXOpen.Point3d(0.0, 0.0, 0.0), SketchHelpers.PlaneMatrix(key));
                    b2.PlaneOption = NXOpen.Sketch.PlaneOption.ExistingPlane;
                    b2.PlaneReference = planeRef;
                    b2.AxisReference = workPart.Directions.CreateDirection(
                        new NXOpen.Point3d(0.0, 0.0, 0.0), SketchHelpers.PlaneXAxis(key),
                        NXOpen.SmartObject.UpdateOption.WithinModeling);
                    NXOpen.NXObject r2 = b2.Commit();
                    b2.Destroy();
                }

                string sketchName = (sketch != null ? sketch.Name : null) ?? name ?? "Sketch";

                // Rename if requested
                if (!string.IsNullOrEmpty(name) && sketch != null)
                {
                    sketch.SetName(name);
                    sketchName = name;
                }

                // Activate the sketch so geometry can be drawn
                // NX2412: Activate() requires Sketch.ViewReorient enum, NOT a boolean!
                // Passing true/bool causes error 4360002 (invalid argument type).
                if (sketch != null)
                {
                    sketch.Activate(NXOpen.Sketch.ViewReorient.True);
                }

                var data = new JObject();
                data["sketch"] = sketchName;
                data["plane"] = key;
                return ToolResult.Ok(string.Format("Created sketch '{0}' on {1} plane.", sketchName, key), data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_sketch failed: {0}", ex.Message)).ToJson();
            }
        }

        /// <summary>
        /// Helper: enumerate datum names for error messages.
        /// </summary>
        private static string EnumerateDatumNames(dynamic workPart)
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (dynamic datum in workPart.Datums)
                {
                    if (datum.Name != null)
                        names.Add(datum.Name);
                }
            }
            catch { /* ignore enumeration errors */ }
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "(none found)";
        }
    }

    // ========================================================================
    // 2. nx_sketch_line
    // ========================================================================

    /// <summary>
    /// Create a line in the active sketch from (x1, y1) to (x2, y2).
    /// Uses Curves.CreateLine and sketch.AddGeometry.
    /// Re-activates the sketch after each operation to keep it active.
    /// Parameters:
    ///   x1 (number, required) - Start point X coordinate (sketch-local).
    ///   y1 (number, required) - Start point Y coordinate (sketch-local).
    ///   x2 (number, required) - End point X coordinate (sketch-local).
    ///   y2 (number, required) - End point Y coordinate (sketch-local).
    ///   name (string, optional) - Curve name (set AFTER AddGeometry, for nx_sketch_fillet/chamfer resolution).
    /// </summary>
    public class SketchLineTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_line"; } }
        public string Description { get { return "Create a line in the active sketch from (x1, y1) to (x2, y2)."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                double x1 = SketchHelpers.GetParamDouble(p, "x1");
                double y1 = SketchHelpers.GetParamDouble(p, "y1");
                double x2 = SketchHelpers.GetParamDouble(p, "x2");
                double y2 = SketchHelpers.GetParamDouble(p, "y2");

                dynamic startPt = SketchHelpers.MapToWorld(sketch, x1, y1);
                dynamic endPt = SketchHelpers.MapToWorld(sketch, x2, y2);

                dynamic line = workPart.Curves.CreateLine(startPt, endPt);
                sketch.AddGeometry(line);

                // Fix 2026-08-04: honor custom name so nx_sketch_fillet/chamfer can resolve curves by name
                // Verified: AddGeometry RESETS the name — SetName must come AFTER AddGeometry.
                // NXObject.Name is READ-ONLY — use SetName(string) (反编译验证)
                string nm = SketchHelpers.GetParamString(p, "name", null);
                if (!string.IsNullOrEmpty(nm))
                    line.SetName(nm);

                // NX2412: Re-activate sketch for subsequent operations using enum, NOT boolean
                sketch.Activate(NXOpen.Sketch.ViewReorient.False);

                var data = new JObject();
                data["curve"] = line.Name;
                data["start"] = new JArray(x1, y1);
                data["end"] = new JArray(x2, y2);
                return ToolResult.Ok(string.Format("Created line from ({0}, {1}) to ({2}, {3}).", x1, y1, x2, y2), data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_line failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 3. nx_sketch_arc
    // ========================================================================

    /// <summary>
    /// Create an arc in the active sketch given center, radius, start and end angles.
    /// Uses Curves.CreateArc(center, xDir, yDir, radius, startRad, endRad).
    /// Angles are converted from degrees to radians.
    /// Parameters:
    ///   cx (number, required) - Center X coordinate (sketch-local).
    ///   cy (number, required) - Center Y coordinate (sketch-local).
    ///   radius (number, required) - Arc radius (sketch-local units).
    ///   start_angle (number, required) - Start angle in degrees.
    ///   end_angle (number, required) - End angle in degrees.
    /// </summary>
    public class SketchArcTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_arc"; } }
        public string Description { get { return "Create an arc in the active sketch given center, radius, start and end angles."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                double cx = SketchHelpers.GetParamDouble(p, "cx");
                double cy = SketchHelpers.GetParamDouble(p, "cy");
                double radius = SketchHelpers.GetParamDouble(p, "radius");
                double startAngle = SketchHelpers.GetParamDouble(p, "start_angle");
                double endAngle = SketchHelpers.GetParamDouble(p, "end_angle");

                dynamic center = SketchHelpers.MapToWorld(sketch, cx, cy);
                NXOpen.Vector3d xDir, yDir;
                SketchHelpers.GetSketchAxes(sketch, out xDir, out yDir);

                double startRad = startAngle * Math.PI / 180.0;
                double endRad = endAngle * Math.PI / 180.0;

                dynamic curve = workPart.Curves.CreateArc(center, xDir, yDir, radius, startRad, endRad);
                sketch.AddGeometry(curve);
                sketch.Activate(NXOpen.Sketch.ViewReorient.False);

                string curveName = (curve != null ? curve.Name : null) ?? "Arc";

                var data = new JObject();
                data["curve"] = curveName;
                data["center"] = new JArray(cx, cy);
                data["radius"] = radius;
                data["start_angle"] = startAngle;
                data["end_angle"] = endAngle;
                return ToolResult.Ok(
                    string.Format("Created arc at ({0}, {1}) with radius {2}, from {3} deg to {4} deg.", cx, cy, radius, startAngle, endAngle),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_arc failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 4. nx_sketch_rectangle
    // ========================================================================

    /// <summary>
    /// Create a rectangle in the active sketch from corner (x1, y1) to (x2, y2).
    /// Constructs four lines connecting the corners in order.
    ///
    /// Parameters:
    ///   x1 (number, required) -- First corner X (sketch-local).
    ///   y1 (number, required) -- First corner Y (sketch-local).
    ///   x2 (number, required) -- Opposite corner X (sketch-local).
    ///   y2 (number, required) -- Opposite corner Y (sketch-local).
    /// </summary>
    public class SketchRectangleTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_rectangle"; } }
        public string Description { get { return "Create a rectangle in the active sketch from corner (x1, y1) to (x2, y2)."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                double x1 = SketchHelpers.GetParamDouble(p, "x1");
                double y1 = SketchHelpers.GetParamDouble(p, "y1");
                double x2 = SketchHelpers.GetParamDouble(p, "x2");
                double y2 = SketchHelpers.GetParamDouble(p, "y2");

                // Four corners of the rectangle
                double[][] corners = new[]
                {
                    new[] { x1, y1 },
                    new[] { x2, y1 },
                    new[] { x2, y2 },
                    new[] { x1, y2 },
                };

                var createdLines = new JArray();

                for (int i = 0; i < 4; i++)
                {
                    double sx = corners[i][0];
                    double sy = corners[i][1];
                    double ex = corners[(i + 1) % 4][0];
                    double ey = corners[(i + 1) % 4][1];

                    dynamic startPt = SketchHelpers.MapToWorld(sketch, sx, sy);
                    dynamic endPt = SketchHelpers.MapToWorld(sketch, ex, ey);

                    dynamic line = workPart.Curves.CreateLine(startPt, endPt);
                    sketch.AddGeometry(line);
                    sketch.Activate(NXOpen.Sketch.ViewReorient.False);
                    createdLines.Add(line.Name);
                }

                var data = new JObject();
                data["curves"] = createdLines;
                data["corner1"] = new JArray(x1, y1);
                data["corner2"] = new JArray(x2, y2);
                data["width"] = Math.Abs(x2 - x1);
                data["height"] = Math.Abs(y2 - y1);
                return ToolResult.Ok(string.Format("Created rectangle from ({0}, {1}) to ({2}, {3}).", x1, y1, x2, y2), data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_rectangle failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 5. nx_sketch_circle
    // ========================================================================

    /// <summary>
    /// Create a circle in the active sketch from center point and radius.
    /// Uses Curves.CreateArc with a full 2*PI sweep (circle is a 360-degree arc).
    ///
    /// Parameters:
    ///   cx     (number, required) -- Center X (sketch-local).
    ///   cy     (number, required) -- Center Y (sketch-local).
    ///   radius (number, required) -- Circle radius (sketch-local units).
    /// </summary>
    public class SketchCircleTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_circle"; } }
        public string Description { get { return "Create a circle in the active sketch from center point and radius."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                // M0-20260912: reject unknown/legacy param names instead of silent fallback to (0,0).
                // Real params are cx/cy/radius (tool-schemas.json); a 'center' key means caller used
                // a stale contract — fail loudly so the caller can retry with correct names.
                foreach (string bad in new[] { "center", "center_x", "center_y", "centre" })
                {
                    if (p[bad] != null)
                        return ToolResult.Fail(
                            string.Format("nx_sketch_circle: 未知参数 '{0}'。真实参数名: cx / cy / radius (见 mcp/tool-schemas.json)。", bad)).ToJson();
                }

                double cx = SketchHelpers.GetParamDouble(p, "cx");
                double cy = SketchHelpers.GetParamDouble(p, "cy");
                double radius = SketchHelpers.GetParamDouble(p, "radius");

                dynamic center = SketchHelpers.MapToWorld(sketch, cx, cy);
                NXOpen.Vector3d xDir, yDir;
                SketchHelpers.GetSketchAxes(sketch, out xDir, out yDir);

                // Full circle = arc from 0 to 2*PI
                dynamic arc = workPart.Curves.CreateArc(center, xDir, yDir, radius, 0.0, 2.0 * Math.PI);
                sketch.AddGeometry(arc);
                sketch.Activate(NXOpen.Sketch.ViewReorient.False);

                var data = new JObject();
                data["name"] = arc.Name;
                data["center"] = new JArray(cx, cy);
                data["radius"] = radius;
                return ToolResult.Ok(string.Format("Created circle '{0}' radius={1} at ({2}, {3}).", arc.Name, radius, cx, cy), data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_circle failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 6. nx_sketch_constraint
    // ========================================================================

    /// <summary>
    /// Apply a constraint to sketch geometry.
    /// Supports geometric constraints: horizontal, vertical, parallel, perpendicular,
    /// tangent, equal_length, fix, coincident, midpoint, concentric.
    /// Supports dimensional constraints: dimension (requires 'value' parameter).
    /// Parameters:
    ///   type (string, required) - Constraint type: horizontal, vertical, parallel, perpendicular, tangent, equal_length, fix, coincident, midpoint, concentric, dimension.
    ///   geometry (array, required) - Array of curve names to constrain.
    ///   value (number, optional) - Dimension value (required for type=dimension).
    /// </summary>
    /// Parameters:
    ///   constraint_type (string, optional) - constraint_type parameter.
    ///   targets (array, optional) - targets parameter.
    ///   value (string, optional) - value parameter.
    ///
    public class SketchConstraintTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_constraint"; } }
        public string Description { get { return "Apply a constraint to sketch geometry (horizontal, vertical, parallel, etc.)."; } }

        private static readonly HashSet<string> GeometricConstraintTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "horizontal", "vertical", "parallel", "perpendicular",
                "tangent", "equal_length", "fix", "coincident",
                "midpoint", "concentric"
            };

        private static readonly HashSet<string> DimensionalConstraintTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dimension" };

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string constraintType = SketchHelpers.GetParamString(p, "constraint_type");
                if (string.IsNullOrEmpty(constraintType))
                    return ToolResult.Fail("Parameter 'constraint_type' is required.").ToJson();

                JArray targetsArray = SketchHelpers.GetParamArray(p, "targets");
                if (targetsArray == null || targetsArray.Count == 0)
                    return ToolResult.Fail("Parameter 'targets' is required (array of curve names).").ToJson();

                double? value = null;
                var valueToken = p["value"];
                if (valueToken != null && valueToken.Type != JTokenType.Null)
                    value = valueToken.ToObject<double>();

                string key = constraintType.Trim().ToLowerInvariant();

                // Validate constraint type
                if (!GeometricConstraintTypes.Contains(key) && !DimensionalConstraintTypes.Contains(key))
                {
                    var allValid = new List<string>(GeometricConstraintTypes);
                    allValid.AddRange(DimensionalConstraintTypes);
                    allValid.Sort();
                    return ToolResult.Fail(
                        string.Format("Invalid constraint type '{0}'. Use one of: {1}.", constraintType, string.Join(", ", allValid))
                    ).ToJson();
                }

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                if (GeometricConstraintTypes.Contains(key))
                {
                    // Resolve target curves
                    var curves = new List<dynamic>();
                    for (int i = 0; i < targetsArray.Count; i++)
                    {
                        string curveName = targetsArray[i].ToString();
                        dynamic curve = SketchHelpers.FindCurveByName(workPart, curveName);
                        if (curve == null)
                            return ToolResult.Fail(string.Format("Curve '{0}' not found.", curveName)).ToJson();
                        curves.Add(curve);
                    }

                    // Apply constraint using specific NXOpen methods
                    switch (key)
                    {
                        case "horizontal":
                            if (curves.Count >= 1)
                                sketch.CreateHorizontalConstraint(SketchHelpers.CreateConstraintGeometry(curves[0]));
                            break;
                        case "vertical":
                            if (curves.Count >= 1)
                                sketch.CreateVerticalConstraint(SketchHelpers.CreateConstraintGeometry(curves[0]));
                            break;
                        case "parallel":
                            if (curves.Count >= 2)
                                sketch.CreateParallelConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]),
                                    SketchHelpers.CreateConstraintGeometry(curves[1]));
                            break;
                        case "perpendicular":
                            if (curves.Count >= 2)
                                sketch.CreatePerpendicularConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]),
                                    SketchHelpers.CreateConstraintGeometry(curves[1]));
                            break;
                        case "tangent":
                            if (curves.Count >= 2)
                            {
                                // NX2412: Sketch.ConstraintGeometryHelpType.Curvature enum not available
                                // in NX2412 managed API — use raw integer value 4 (Curvature).
                                var help1 = new NXOpen.Sketch.ConstraintGeometryHelp(
                                    (NXOpen.Sketch.ConstraintGeometryHelpType)4 /* Curvature */,
                                    new NXOpen.Point3d(0, 0, 0), 0.0);
                                var help2 = new NXOpen.Sketch.ConstraintGeometryHelp(
                                    (NXOpen.Sketch.ConstraintGeometryHelpType)4 /* Curvature */,
                                    new NXOpen.Point3d(0, 0, 0), 0.0);
                                sketch.CreateTangentConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]), help1,
                                    SketchHelpers.CreateConstraintGeometry(curves[1]), help2);
                            }
                            break;
                        case "equal_length":
                            if (curves.Count >= 2)
                                sketch.CreateEqualLengthConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]),
                                    SketchHelpers.CreateConstraintGeometry(curves[1]));
                            break;
                        case "fix":
                            // M0-20260912: iterate ALL targets — old code only fixed curves[0],
                            // silently leaving the rest under-constrained (DOF>0 假成功).
                            foreach (var c in curves)
                                sketch.CreateFixedConstraint(SketchHelpers.CreateConstraintGeometry(c));
                            break;
                        case "coincident":
                            if (curves.Count >= 2)
                                sketch.CreateCoincidentConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]),
                                    SketchHelpers.CreateConstraintGeometry(curves[1]));
                            break;
                        case "midpoint":
                            if (curves.Count >= 2)
                                sketch.CreateMidpointConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]),
                                    SketchHelpers.CreateConstraintGeometry(curves[1]));
                            break;
                        case "concentric":
                            if (curves.Count >= 2)
                                sketch.CreateConcentricConstraint(
                                    SketchHelpers.CreateConstraintGeometry(curves[0]),
                                    SketchHelpers.CreateConstraintGeometry(curves[1]));
                            break;
                    }
                }
                else
                {
                    // Dimensional constraint
                    if (targetsArray.Count < 2)
                        return ToolResult.Fail("Dimensional constraints require 2 target curves.").ToJson();

                    var targetArray = new NXOpen.NXObject[targetsArray.Count];
                    for (int i = 0; i < targetsArray.Count; i++)
                    {
                        string curveName = targetsArray[i].ToString();
                        dynamic curve = SketchHelpers.FindCurveByName(workPart, curveName);
                        if (curve == null)
                            return ToolResult.Fail(string.Format("Curve '{0}' not found.", curveName)).ToJson();
                        targetArray[i] = (NXObject)curve;
                    }

                    // NX2412: Sketch.CreateDimension(NXObject[], double) overload does not exist.
                    // Use UF_SKET_create_dimension (book ch9 pattern) — requires
                    // InitializeSketch first and AssocType.EndPoint on dim objects (probed 2026-08-15).
                    UFSession uf = UFSession.GetUFSession();
                    string skName = sketch.Name;
                    NXOpen.Tag skTag = NXOpen.Tag.Null;
                    uf.Sket.InitializeSketch(ref skName, out skTag);
                    try
                    {
                        NXOpen.UF.UFSket.DimObject dimObj1 = new NXOpen.UF.UFSket.DimObject();
                        dimObj1.object_tag = targetArray[0].Tag;
                        dimObj1.object_assoc_type = NXOpen.UF.UFSket.AssocType.EndPoint;
                        NXOpen.UF.UFSket.DimObject dimObj2 = new NXOpen.UF.UFSket.DimObject();
                        dimObj2.object_tag = targetArray[1].Tag;
                        dimObj2.object_assoc_type = NXOpen.UF.UFSket.AssocType.EndPoint;
                        double[] dimOrigin = new double[] { 0.0, 0.0, 0.0 };
                        NXOpen.Tag dimTag = NXOpen.Tag.Null;
                        uf.Sket.CreateDimension(
                            skTag,
                            NXOpen.UF.UFSket.ConType.HorizontalDim,
                            ref dimObj1, ref dimObj2,
                            dimOrigin,
                            out dimTag);
                    }
                    finally
                    {
                        uf.Sket.TerminateSketch();
                        // TerminateSketch deactivates the sketch — re-activate for
                        // subsequent drawing operations (verified 2026-08-15).
                        sketch.Activate(NXOpen.Sketch.ViewReorient.False);
                    }
                }

                var data = new JObject();
                data["constraint_type"] = key;
                data["targets"] = targetsArray;
                if (value != null)
                    data["value"] = value.Value;

                return ToolResult.Ok(string.Format("Applied '{0}' constraint to {1} target(s).", key, targetsArray.Count), data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_constraint failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 7. nx_finish_sketch
    // ========================================================================

    /// <summary>
    /// Finish (exit) the currently active sketch.
    /// NX2412: Deactivate requires two arguments — ViewReorient and UpdateLevel.
    /// </summary>
    public class FinishSketchTool : IToolHandler
    {
        public string Name { get { return "nx_finish_sketch"; } }
        public string Description { get { return "Finish (exit) the currently active sketch."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic activeSketch = SketchHelpers.GetActiveSketch(workPart);
                if (activeSketch != null)
                {
                    // NX2412: Deactivate requires two enum arguments, NOT raw bool/int
                    activeSketch.Deactivate(
                        NXOpen.Sketch.ViewReorient.False,
                        NXOpen.Sketch.UpdateLevel.Model);
                }

                return ToolResult.Ok("Sketch finished successfully.").ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_finish_sketch failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 8. nx_sketch_trim
    // ========================================================================

    /// <summary>
    /// Trim sketch curves to their intersection points with boundary curves.
    /// Uses Features.CreateTrimCurveBuilder for each curve.
    ///
    /// Parameters:
    ///   curves_to_trim  (string[], required) -- Curves to trim (by name).
    ///   boundary_curves (string[], optional) -- Boundary curves (default: all other curves).
    /// </summary>
    public class SketchTrimTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_trim"; } }
        public string Description { get { return "Trim sketch curves to their intersection points with boundary curves."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                JArray trimNames = SketchHelpers.GetParamArray(p, "curves_to_trim");
                if (trimNames == null || trimNames.Count == 0)
                    return ToolResult.Fail("Parameter 'curves_to_trim' is required (array of curve names).").ToJson();

                JArray boundaryNames = SketchHelpers.GetParamArray(p, "boundary_curves");
                if (boundaryNames == null || boundaryNames.Count == 0)
                    return ToolResult.Fail("Parameter 'boundary_curves' is required (array of curve names).").ToJson();

                // Resolve curves to trim
                string error;
                var trimCurves = SketchHelpers.FindCurvesByName(workPart, trimNames, out error);
                if (trimCurves == null)
                    return ToolResult.Fail(error).ToJson();

                // Resolve boundary curves
                var boundaryCurves = SketchHelpers.FindCurvesByName(workPart, boundaryNames, out error);
                if (boundaryCurves == null)
                    return ToolResult.Fail(error).ToJson();

                // NX2412: Features.CreateTrimCurveBuilder expects a TrimCurve FEATURE, not a
                // raw curve. Use the sketch-native QuickTrimBuilder (probed 2026-08-15).
                dynamic builder = workPart.Sketches.CreateQuickTrimBuilder();
                var trimmed = new JArray();
                try
                {
                    foreach (dynamic curve in trimCurves)
                        builder.TrimmedCurves.Add((NXOpen.Curve)curve);
                    foreach (dynamic b in boundaryCurves)
                        builder.BoundaryCurves.Add((NXOpen.Curve)b);

                    dynamic result = builder.Commit();
                    builder.Destroy();
                    foreach (dynamic curve in trimCurves)
                        trimmed.Add(curve.Name);
                }
                catch (Exception)
                {
                    try { builder.Destroy(); } catch { }
                    throw;
                }

                sketch.Update();

                var data = new JObject();
                data["trimmed"] = trimmed;
                data["boundaries"] = boundaryNames;
                return ToolResult.Ok(
                    string.Format("Trimmed {0} curve(s) to {1} boundary curve(s).", trimmed.Count, boundaryNames.Count),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_trim failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 9. nx_sketch_spline
    // ========================================================================

    /// <summary>
    /// Create a spline curve through a series of points in the active sketch.
    /// Uses Curves.CreateSplineByPoints with a Point3d array.
    ///
    /// Parameters:
    ///   points (array, required)  -- List of [x, y] points (sketch-local) the spline passes through.
    ///   closed (boolean, optional) -- Close the spline (default false).
    /// </summary>
    public class SketchSplineTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_spline"; } }
        public string Description { get { return "Create a spline curve through a series of points in the active sketch."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                JArray pointsArray = SketchHelpers.GetParamArray(p, "points");
                if (pointsArray == null || pointsArray.Count < 2)
                    return ToolResult.Fail("Spline requires at least 2 points.").ToJson();

                bool closed = SketchHelpers.GetParamBool(p, "closed", false);

                // Build point array (all points lie in the sketch plane, z=0)
                var nxPoints = new NXOpen.Point3d[pointsArray.Count];
                var pointsJArray = new JArray();
                for (int i = 0; i < pointsArray.Count; i++)
                {
                    JArray pt = pointsArray[i] as JArray;
                    if (pt == null || pt.Count < 2)
                        return ToolResult.Fail(string.Format("Point {0} must be an [x, y] array.", i)).ToJson();

                    double px = pt[0].ToObject<double>();
                    double py = pt[1].ToObject<double>();
                    nxPoints[i] = SketchHelpers.MapToWorld(sketch, px, py);
                    pointsJArray.Add(new JArray(px, py));
                }

                // NX2412: no CurveCollection spline API; UF struct marshaling broken (both probed
                // 2026-08-15). Use StudioSplineBuilder ThroughPoints — verified end-to-end.
                dynamic ssb = workPart.Features.CreateStudioSplineBuilder(null);
                NXOpen.Spline spline = null;
                try
                {
                    // Tag watermark: StudioSplineBuilder.Commit() returns null — find the
                    // created spline as the highest-tagged NEW spline after commit.
                    ulong maxBefore = 0;
                    foreach (dynamic c in workPart.Curves)
                    {
                        try
                        {
                            ulong t = ulong.Parse(c.Tag.ToString());
                            if (t > maxBefore) maxBefore = t;
                        }
                        catch { }
                    }

                    ssb.SplineMethod = NXOpen.Features.StudioSplineBuilder.Method.ThroughPoints;
                    ssb.Degree = 3;
                    ssb.IsPeriodic = closed;
                    dynamic mgr = ssb.ConstraintManager;
                    foreach (NXOpen.Point3d pt in nxPoints)
                    {
                        dynamic ptObj = workPart.Points.CreatePoint(pt);
                        dynamic conData = mgr.CreateGeometricConstraintData();
                        conData.Point = ptObj;
                        mgr.Append(conData);
                    }
                    ssb.Commit();
                    ssb.Destroy();

                    foreach (dynamic c in workPart.Curves)
                    {
                        try
                        {
                            if (!(c is NXOpen.Spline)) continue;
                            ulong t = ulong.Parse(c.Tag.ToString());
                            if (t > maxBefore) spline = (NXOpen.Spline)c;
                        }
                        catch { }
                    }
                }
                catch (Exception)
                {
                    try { ssb.Destroy(); } catch { }
                    throw;
                }
                if (spline == null)
                    return ToolResult.Fail("Spline creation failed.").ToJson();

                sketch.AddGeometry(spline);

                // Re-activate sketch for subsequent operations
                sketch.Activate(NXOpen.Sketch.ViewReorient.False);

                var data = new JObject();
                data["curve"] = spline.Name;
                data["points"] = pointsJArray;
                data["closed"] = closed;
                data["degree"] = pointsArray.Count - 1;
                return ToolResult.Ok(
                    string.Format("Created spline '{0}' through {1} point(s), closed={2}.", spline.Name, pointsArray.Count, closed),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_spline failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 10. nx_sketch_mirror
    // ========================================================================

    /// <summary>
    /// Mirror sketch geometry across a centerline.
    /// Uses Curves.CreateMirrorCurve for each curve, then adds results to the sketch.
    ///
    /// Parameters:
    ///   curves     (string[], required) -- Curves to mirror (by name).
    ///   centerline (string, required)   -- Centerline curve name (mirror axis).
    /// </summary>
    public class SketchMirrorTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_mirror"; } }
        public string Description { get { return "Mirror sketch geometry across a centerline."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                JArray curveNames = SketchHelpers.GetParamArray(p, "curves");
                if (curveNames == null || curveNames.Count == 0)
                    return ToolResult.Fail("Parameter 'curves' is required (array of curve names).").ToJson();

                string centerlineName = SketchHelpers.GetParamString(p, "centerline");
                if (string.IsNullOrEmpty(centerlineName))
                    return ToolResult.Fail("Parameter 'centerline' is required.").ToJson();

                // Resolve curves to mirror
                string error;
                var mirrorCurves = SketchHelpers.FindCurvesByName(workPart, curveNames, out error);
                if (mirrorCurves == null)
                    return ToolResult.Fail(error).ToJson();

                // Resolve centerline
                dynamic centerlineCurve = SketchHelpers.FindCurveByName(workPart, centerlineName);
                if (centerlineCurve == null)
                    return ToolResult.Fail(string.Format("Centerline '{0}' not found.", centerlineName)).ToJson();

                // NX2412: Curves.CreateMirrorCurve does not exist. Use the associative
                // SketchMirrorPatternBuilder (probed 2026-08-15). NOTE: Section property is
                // READ-ONLY — add curves via builder.Section.AddToSection() (verified 2026-08-15).
                var ruleCurves = new NXOpen.Curve[mirrorCurves.Count];
                for (int i = 0; i < mirrorCurves.Count; i++)
                    ruleCurves[i] = (NXOpen.Curve)mirrorCurves[i];
                var rules = new NXOpen.SelectionIntentRule[]
                    { workPart.ScRuleFactory.CreateRuleCurveDumb(ruleCurves) };
                NXOpen.Point3d seedHelp = SketchHelpers.FirstPoint(ruleCurves[0]);

                dynamic mb = workPart.Sketches.CreateSketchMirrorPatternBuilder(null);
                var mirroredNames = new JArray();
                try
                {
                    mb.Section.AddToSection(rules, ruleCurves[0], null, null, seedHelp,
                        NXOpen.Section.Mode.Create, false);
                    // DirectionObject is a SINGLE SelectNXObject — assign via .Value (2026-08-15)
                    mb.DirectionObject.Value = (NXOpen.NXObject)centerlineCurve;
                    mb.CreateConstraint = true;
                    dynamic result = mb.Commit();
                    mb.Destroy();
                    if (result != null)
                        mirroredNames.Add(result.Name);
                }
                catch (Exception)
                {
                    try { mb.Destroy(); } catch { }
                    throw;
                }

                sketch.Update();

                var data = new JObject();
                data["mirrored"] = mirroredNames;
                data["original"] = curveNames;
                data["centerline"] = centerlineName;
                return ToolResult.Ok(
                    string.Format("Mirrored {0} curve(s) across '{1}'.", mirroredNames.Count, centerlineName),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_mirror failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 11. nx_sketch_ellipse
    // ========================================================================

    /// <summary>
    /// Create an ellipse in the active sketch.
    /// Uses Curves.CreateEllipseBuilder with center, major/minor radii, and rotation angle.
    /// Rotation angle is converted from degrees to radians.
    ///
    /// Parameters:
    ///   cx             (number, required) -- Center X (sketch-local).
    ///   cy             (number, required) -- Center Y (sketch-local).
    ///   major_radius   (number, required) -- Major radius.
    ///   minor_radius   (number, required) -- Minor radius.
    ///   rotation_angle (number, optional) -- Rotation in degrees (default 0).
    /// </summary>
    public class SketchEllipseTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_ellipse"; } }
        public string Description { get { return "Create an ellipse in the active sketch."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                double cx = SketchHelpers.GetParamDouble(p, "cx");
                double cy = SketchHelpers.GetParamDouble(p, "cy");
                double majorRadius = SketchHelpers.GetParamDouble(p, "major_radius");
                double minorRadius = SketchHelpers.GetParamDouble(p, "minor_radius");
                double rotationAngle = SketchHelpers.GetParamDouble(p, "rotation_angle", 0.0);

                dynamic center = SketchHelpers.MapToWorld(sketch, cx, cy);
                double rotationRad = rotationAngle * Math.PI / 180.0;

                // NX2412: CreateEllipseBuilder does not exist — use Curves.CreateEllipse (probed 2026-08-15).
                // Rotation is applied INSIDE the sketch plane: xDir/yDir = sketch axes rotated by rotationRad.
                NXOpen.Vector3d sx, sy;
                SketchHelpers.GetSketchAxes(sketch, out sx, out sy);
                double cosR = Math.Cos(rotationRad), sinR = Math.Sin(rotationRad);
                dynamic xDir = new NXOpen.Vector3d(
                    sx.X * cosR + sy.X * sinR,
                    sx.Y * cosR + sy.Y * sinR,
                    sx.Z * cosR + sy.Z * sinR);
                dynamic yDir = new NXOpen.Vector3d(
                    -sx.X * sinR + sy.X * cosR,
                    -sx.Y * sinR + sy.Y * cosR,
                    -sx.Z * sinR + sy.Z * cosR);
                dynamic curve = workPart.Curves.CreateEllipse(
                    center, xDir, yDir, majorRadius, minorRadius, 0.0, 2.0 * Math.PI);
                string curveName = (curve != null ? curve.Name : null) ?? "Ellipse";

                sketch.AddGeometry(curve);

                // Re-activate sketch for subsequent operations
                sketch.Activate(NXOpen.Sketch.ViewReorient.False);

                var data = new JObject();
                data["curve"] = curveName;
                data["center"] = new JArray(cx, cy);
                data["major_radius"] = majorRadius;
                data["minor_radius"] = minorRadius;
                data["rotation_angle"] = rotationAngle;
                return ToolResult.Ok(
                    string.Format("Created ellipse '{0}' with major radius {1}, minor radius {2} at ({3}, {4}), rotation={5} deg.",
                        curveName, majorRadius, minorRadius, cx, cy, rotationAngle),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_ellipse failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 12. nx_sketch_offset
    // ========================================================================

    /// <summary>
    /// Offset sketch curves by a specified distance.
    /// Uses Curves.CreateOffsetCurveBuilder for each curve.
    /// Side parameter: 'left', 'right', or 'both' (default 'right').
    ///
    /// Parameters:
    ///   curves   (string[], required) -- Curves to offset (by name).
    ///   distance (number, required)   -- Offset distance (mm; sign depends on side).
    ///   side     (string, optional)   -- 'left', 'right', or 'both' (default 'right').
    /// </summary>
    public class SketchOffsetTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_offset"; } }
        public string Description { get { return "Offset sketch curves by a specified distance."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                JArray curveNames = SketchHelpers.GetParamArray(p, "curves");
                if (curveNames == null || curveNames.Count == 0)
                    return ToolResult.Fail("Parameter 'curves' is required (array of curve names).").ToJson();

                double distance = SketchHelpers.GetParamDouble(p, "distance");
                if (distance == 0.0)
                    return ToolResult.Fail("Parameter 'distance' is required and must be non-zero.").ToJson();

                string side = SketchHelpers.GetParamString(p, "side", "right");
                string sideKey = side.Trim().ToLowerInvariant();
                if (sideKey != "left" && sideKey != "right" && sideKey != "both")
                    return ToolResult.Fail(string.Format("Invalid side '{0}'. Use 'left', 'right', or 'both'.", side)).ToJson();

                // Resolve curves to offset
                string error;
                var offsetCurves = SketchHelpers.FindCurvesByName(workPart, curveNames, out error);
                if (offsetCurves == null)
                    return ToolResult.Fail(error).ToJson();

                // NX2412: Features.CreateOffsetCurveBuilder expects a Feature (null = new);
                // passing a Curve caused the overload mismatch. Side is implicit:
                // negative OffsetDistance offsets to the other side (probed 2026-08-15).
                double[] signedDistances;
                if (sideKey == "left")
                    signedDistances = new double[] { -Math.Abs(distance) };
                else if (sideKey == "both")
                    signedDistances = new double[] { Math.Abs(distance), -Math.Abs(distance) };
                else
                    signedDistances = new double[] { Math.Abs(distance) };

                var offsetNames = new JArray();
                foreach (dynamic curve in offsetCurves)
                {
                    foreach (double signed in signedDistances)
                    {
                        // Tag watermark: OffsetCurveBuilder.Commit returns a FEATURE, not curves —
                        // find the created offset curves as new curves after commit (2026-08-15).
                        ulong maxBefore = 0;
                        foreach (dynamic c in workPart.Curves)
                        {
                            try
                            {
                                ulong t = ulong.Parse(c.Tag.ToString());
                                if (t > maxBefore) maxBefore = t;
                            }
                            catch { }
                        }

                        var ruleCurves = new NXOpen.Curve[] { (NXOpen.Curve)curve };
                        var rules = new NXOpen.SelectionIntentRule[]
                            { workPart.ScRuleFactory.CreateRuleCurveDumb(ruleCurves) };

                        dynamic builder = workPart.Features.CreateOffsetCurveBuilder(null);
                        try
                        {
                            // CurvesToOffset is a READ-ONLY Section — add via AddToSection (2026-08-15)
                            builder.CurvesToOffset.AddToSection(rules, ruleCurves[0], null, null,
                                SketchHelpers.FirstPoint(ruleCurves[0]), NXOpen.Section.Mode.Create, false);
                            // Single straight line has no offset plane — provide a plane point
                            // NOT collinear with the line (errors verified 2026-08-15:
                            // "输入的曲线构成一直线" / "定义的点与输入的线串共线").
                            NXOpen.Point3d planePt = SketchHelpers.FirstPoint(ruleCurves[0]);
                            NXOpen.Line ln0 = ruleCurves[0] as NXOpen.Line;
                            if (ln0 != null)
                            {
                                double dx = ln0.EndPoint.X - ln0.StartPoint.X;
                                double dy = ln0.EndPoint.Y - ln0.StartPoint.Y;
                                double len = Math.Sqrt(dx * dx + dy * dy);
                                if (len > 1e-9)
                                    planePt = new NXOpen.Point3d(
                                        ln0.StartPoint.X - dy / len * 10.0,
                                        ln0.StartPoint.Y + dx / len * 10.0, 0);
                            }
                            builder.PointOnOffsetPlane = workPart.Points.CreatePoint(planePt);
                            builder.OffsetDistance.RightHandSide = signed.ToString("F3");
                            builder.Commit();
                            builder.Destroy();

                            // The offset FEATURE commit exits the sketch environment
                            // ("草图未初始化") — re-activate before AddGeometry (2026-08-15).
                            sketch.Activate(NXOpen.Sketch.ViewReorient.False);

                            foreach (dynamic c in workPart.Curves)
                            {
                                try
                                {
                                    ulong t = ulong.Parse(c.Tag.ToString());
                                    if (t <= maxBefore) continue;
                                    sketch.AddGeometry(c);
                                    offsetNames.Add(c.Name);
                                }
                                catch { }
                            }
                        }
                        catch (Exception)
                        {
                            try { builder.Destroy(); } catch { }
                            throw;
                        }
                    }
                }

                sketch.Update();

                var data = new JObject();
                data["offset_curves"] = offsetNames;
                data["original"] = curveNames;
                data["distance"] = distance;
                data["side"] = sideKey;
                return ToolResult.Ok(
                    string.Format("Offset {0} curve(s) by {1} ({2}).", offsetNames.Count, distance, sideKey),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_offset failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}

namespace NxMcpPlugin.Tools.Sketch
{
    // ========================================================================
    // List all sketch names
    // ========================================================================

    public class ListSketchesTool : IToolHandler
    {
        public string Name { get { return "nx_list_sketches"; } }
        public string Description { get { return "List all sketch names in the work part."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                var arr = new JArray();
                try
                {
                    foreach (dynamic sk in wp.Sketches)
                    {
                        try { arr.Add(sk.Name.ToString()); } catch { }
                    }
                }
                catch { }
                var data = new JObject();
                data.Add("sketches", arr);
                data.Add("count", arr.Count);
                return ToolResult.Ok(arr.Count + " sketches found.", data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_list_sketches: " + ex.Message).ToJson(); }
        }
    }

    // ========================================================================
    // 15. nx_sketch_fillet
    // ========================================================================

    /// <summary>
    /// Create a fillet (rounded corner) between two sketch curves.
    /// Uses SketchCollection.CreateFilletBuilder() (NX2312+ internal API).
    /// Verified 2026-08-04 (反编译 + 实测):
    ///   CurvesToFillet = SelectNXObjectList.Add(NXObject)
    ///   FilletRadius = Expression.Value, CreateRadiusDimension (bool), LockRadius (bool)
    ///   ThirdCurve (NXObject) + DeleteThirdCurve (bool) for 3-curve fillets
    ///
    /// Parameters:
    ///   radius           (number, required)   -- Fillet radius.
    ///   curves           (string[], required) -- 2 or 3 curves to fillet (by name).
    ///   create_dimension (boolean, optional)  -- Create radius dimension (default false).
    /// </summary>
    public class SketchFilletTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_fillet"; } }
        public string Description { get { return "Create a fillet between two curves in the active sketch."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                double radius = SketchHelpers.GetParamDouble(p, "radius", 0.0);
                if (radius <= 0.0)
                    return ToolResult.Fail("Parameter 'radius' is required (positive number).").ToJson();

                JArray curveNames = SketchHelpers.GetParamArray(p, "curves");
                if (curveNames == null || curveNames.Count < 2)
                    return ToolResult.Fail("Parameter 'curves' is required (array of 2-3 curve names).").ToJson();

                string error;
                var curves = SketchHelpers.FindCurvesByName(workPart, curveNames, out error);
                if (curves == null)
                    return ToolResult.Fail(error).ToJson();

                bool createDimension = SketchHelpers.GetParamBool(p, "create_dimension", false);

                // NX2412: CreateFilletBuilder is gated by an internal feature switch
                // ("控制此功能的功能开关未激活"). Use the PUBLIC Sketch.Fillet() API
                // (probed 2026-08-15 — requires explicit typing because of the out parameter).
                // 2-curve fillet only; 3-curve overloads need extra DeleteThirdCurveOption.
                NXOpen.Sketch sk = (NXOpen.Sketch)sketch;
                NXOpen.Curve c1 = (NXOpen.Curve)curves[0];
                NXOpen.Curve c2 = (NXOpen.Curve)curves[1];
                // Help points must lie ON each curve near the corner (NOT the corner itself):
                // verified 2026-08-15 — corner/center points → "无法求解圆角", on-curve points OK.
                NXOpen.Point3d corner = SketchHelpers.ComputeCornerPoint(c1, c2);
                NXOpen.Point3d help1 = SketchHelpers.PointOnCurveNear(c1, corner);
                NXOpen.Point3d help2 = SketchHelpers.PointOnCurveNear(c2, corner);
                NXOpen.Sketch.TrimInputOption doTrim = NXOpen.Sketch.TrimInputOption.True;
                NXOpen.Sketch.CreateDimensionOption makeDim = createDimension
                    ? NXOpen.Sketch.CreateDimensionOption.True
                    : NXOpen.Sketch.CreateDimensionOption.False;

                NXOpen.Arc[] filletArcs;
                NXOpen.SketchConstraint[] cons = null;
                filletArcs = sk.Fillet(c1, c2, help1, help2, radius,
                    doTrim, makeDim, NXOpen.Sketch.AlternateSolutionOption.False, out cons);

                string name = (filletArcs != null && filletArcs.Length > 0 && filletArcs[0] != null)
                    ? filletArcs[0].Name : "Fillet";
                sketch.Update();

                var data = new JObject();
                data["fillet"] = name;
                data["radius"] = radius;
                data["curves"] = curveNames;
                return ToolResult.Ok(
                    string.Format("Created fillet '{0}' radius={1} on {2} curve(s).", name, radius, curves.Count),
                    data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_fillet failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 16. nx_sketch_chamfer
    // ========================================================================

    /// <summary>
    /// Create a chamfer between two sketch curves.
    /// Uses SketchCollection.CreateSketchChamferBuilder() (NX7.5+ public API).
    /// Verified 2026-08-04 (反编译 + 实测):
    ///   CurvesToChamfer = SelectDisplayableObjectList.Add(DisplayableObject)
    ///   ChamferOption = ChamferOptions.Symmetric/Asymmetric/OffsetandAngle
    ///   Distance1/Distance2/Angle = Expression.Value, TrimInputCurves (bool)
    ///
    /// Parameters:
    ///   curves           (string[], required) -- Curves to chamfer (by name).
    ///   option           (string, optional)   -- symmetric (default), asymmetric, offsetandangle.
    ///   distance1        (number, optional)   -- First offset (symmetric/asymmetric).
    ///   distance2        (number, optional)   -- Second offset (asymmetric).
    ///   angle            (number, optional)   -- Chamfer angle in degrees (offsetandangle).
    ///   trim_input_curves (boolean, optional) -- Trim input curves (default true).
    /// </summary>
    public class SketchChamferTool : IToolHandler
    {
        public string Name { get { return "nx_sketch_chamfer"; } }
        public string Description { get { return "Create a chamfer between two curves in the active sketch."; } }

        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic sketch = SketchHelpers.GetActiveSketch(workPart);
                if (sketch == null)
                    return ToolResult.Fail("No active sketch. Use nx_create_sketch first.").ToJson();

                JArray curveNames = SketchHelpers.GetParamArray(p, "curves");
                if (curveNames == null || curveNames.Count < 2)
                    return ToolResult.Fail("Parameter 'curves' is required (array of 2 curve names).").ToJson();

                string error;
                var curves = SketchHelpers.FindCurvesByName(workPart, curveNames, out error);
                if (curves == null)
                    return ToolResult.Fail(error).ToJson();

                string option = (SketchHelpers.GetParamString(p, "option", "symmetric") ?? "symmetric").ToLowerInvariant();
                double d1 = SketchHelpers.GetParamDouble(p, "distance1", 0.0);
                double d2 = SketchHelpers.GetParamDouble(p, "distance2", 0.0);
                double angle = SketchHelpers.GetParamDouble(p, "angle", 45.0);
                bool trim = SketchHelpers.GetParamBool(p, "trim_input_curves", true);

                // Factory is on SketchCollection (workPart.Sketches), NOT on Sketch — 反编译验证
                dynamic builder = workPart.Sketches.CreateSketchChamferBuilder();
                try
                {
                    var list = (NXOpen.SelectDisplayableObjectList)builder.CurvesToChamfer;
                    foreach (dynamic c in curves)
                        list.Add((NXOpen.DisplayableObject)c);

                    switch (option)
                    {
                        case "asymmetric":
                            builder.ChamferOption = NXOpen.SketchChamferBuilder.ChamferOptions.Asymmetric;
                            builder.Distance1.Value = d1;
                            builder.Distance2.Value = d2;
                            break;
                        case "offset_and_angle":
                            builder.ChamferOption = NXOpen.SketchChamferBuilder.ChamferOptions.OffsetandAngle;
                            builder.Distance1.Value = d1;
                            builder.Angle.Value = angle;
                            break;
                        default:
                            builder.ChamferOption = NXOpen.SketchChamferBuilder.ChamferOptions.Symmetric;
                            builder.Distance1.Value = d1;
                            break;
                    }
                    builder.TrimInputCurves = trim;

                    dynamic chamfer = builder.Commit();
                    string name = chamfer != null ? chamfer.Name : "Chamfer";
                    builder.Destroy();

                    sketch.Update();

                    var data = new JObject();
                    data["chamfer"] = name;
                    data["option"] = option;
                    data["distance1"] = d1;
                    data["curves"] = curveNames;
                    return ToolResult.Ok(
                        string.Format("Created chamfer '{0}' (option={1}, d1={2}).", name, option, d1),
                        data).ToJson();
                }
                catch (Exception)
                {
                    try { builder.Destroy(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_sketch_chamfer failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}
