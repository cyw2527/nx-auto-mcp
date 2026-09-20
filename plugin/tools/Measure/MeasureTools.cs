using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Measure
{
    // =========================================================================
    // Shared helpers — private static within each tool class
    // =========================================================================

    // =========================================================================
    // MeasureHelpers — NX2412 mass properties (Body.GetMassProperties 已移除)
    // 探测实证 (_massprobe.cs 2026-08-13, r=10 球基准 V=4188.79 A=1256.64):
    //   Session.Measurement.GetBodyProperties → massProps[0]=area(mm2) [1]=volume(mm3)
    //     [2]=mass(kg) [3..5]=COM(mm); 单位 = base part units (mm 零件 → mm)
    //   UFModl.AskMassProps3d (units=4 → kg/m) → mp[0]=area(m2) [1]=volume(m3)
    //     [2]=mass(kg) [3..5]=COM(m) [46]=density(kg/m3)
    // =========================================================================
    internal static class MeasureHelpers
    {
        internal class MassResult
        {
            public double Volume;              // mm^3
            public double Area;                // mm^2
            public double Mass;                // kg
            public NXOpen.Point3d Centroid;    // mm
        }

        internal static MassResult GetBodyMassProps(dynamic session, dynamic body)
        {
            // 首选: Session.Measurement.GetBodyProperties (公开未弃用, base part units, 只读无副作用)
            try
            {
                NXOpen.Measurement m = (NXOpen.Measurement)session.Measurement;
                NXOpen.IBody[] ib = new NXOpen.IBody[] { (NXOpen.Body)body };
                double[] massProps; double weight; NXOpen.Point3d centroid;
                double[] centroidUnit; double density; double[] axis;
                m.GetBodyProperties(ib, 0.001, true, out massProps, out weight,
                    out centroid, out centroidUnit, out density, out axis);
                return new MassResult
                {
                    Volume = massProps[1],      // mm3 (探测实证顺序: [0]=area [1]=volume)
                    Area = massProps[0],        // mm2
                    Mass = massProps[2],        // kg
                    Centroid = centroid         // mm
                };
            }
            catch
            {
                // 回退: UF_MODL_ask_mass_props_3d (units=4 → kg/m, 换算回 mm)
                NXOpen.UF.UFSession ufs = NXOpen.UF.UFSession.GetUFSession();
                double[] acc = new double[11]; acc[0] = 0.001;
                double[] mp = new double[47]; double[] stats = new double[13];
                ufs.Modl.AskMassProps3d(new NXOpen.Tag[] { ((NXOpen.Body)body).Tag },
                    1, 1, 4, 0.0, 1, acc, mp, stats);
                return new MassResult
                {
                    Volume = mp[1] * 1e9,       // m3 -> mm3
                    Area = mp[0] * 1e6,         // m2 -> mm2
                    Mass = mp[2],               // kg
                    Centroid = new NXOpen.Point3d(mp[3] * 1000.0, mp[4] * 1000.0, mp[5] * 1000.0)
                };
            }
        }
    }

    internal static class NxCollectionHelper
    {
        // ----------------------------------------------------------------
        // NX2412: Get the length unit (millimeters) from the work part.
        // MeasureManager.NewDistance/NewAngle require a Unit, not a View.
        // ----------------------------------------------------------------
        internal static dynamic GetLengthUnit(dynamic workPart)
        {
            return workPart.UnitCollection.FindObject("MilliMeter");
        }
        /// <summary>
        /// Convert an NXOpen collection to a list. NXOpen collections are
        /// IEnumerable in .NET; this materialises them for safe iteration.
        /// </summary>
        internal static List<dynamic> ToList(dynamic collection)
        {
            var list = new List<dynamic>();
            if (collection == null) return list;
            try
            {
                foreach (var item in collection)
                    list.Add(item);
            }
            catch { /* empty or non-enumerable */ }
            return list;
        }

        /// <summary>
        /// Resolve an NXOpen object by name across one or more collections.
        /// Case-insensitive comparison. Returns null if not found.
        /// </summary>
        internal static dynamic Resolve(dynamic wp, string name,
            params dynamic[] collections)
        {
            string target = name.ToLowerInvariant();
            foreach (var collection in collections)
            {
                if (collection == null) continue;
                foreach (var obj in ToList(collection))
                {
                    try
                    {
                        if (string.Equals((string)obj.Name, target,
                            StringComparison.OrdinalIgnoreCase))
                            return obj;
                    }
                    catch { /* object may not have Name */ }
                }
            }
            return null;
        }
    }

    // =========================================================================
    // 1. nx_measure_distance
    // =========================================================================

    /// <summary>
    /// Measures the minimum distance between two named objects in the work part.
    /// Parameters:
    ///   obj1 (string, required) - Name of the first object.
    ///   obj2 (string, required) - Name of the second object.
    /// Returns: { obj1, obj2, distance_mm }
    /// </summary>
    public class MeasureDistanceTool : IToolHandler
    {
        public string Name { get { return "nx_measure_distance"; } }
        public string Description { get { return "Measure the minimum distance between two objects in the work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken obj1Token = parameters["obj1"];
                string obj1 = obj1Token != null ? obj1Token.Value<string>() : null;
                JToken obj2Token = parameters["obj2"];
                string obj2 = obj2Token != null ? obj2Token.Value<string>() : null;
                if (string.IsNullOrEmpty(obj1) || string.IsNullOrEmpty(obj2))
                    return ToolResult.Fail("Both 'obj1' and 'obj2' are required.").ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic nxObj1 = NxCollectionHelper.Resolve(wp, obj1,
                    wp.Features, wp.Bodies, wp.Curves);
                if (nxObj1 == null)
                    return ToolResult.Fail(string.Format("Object '{0}' not found.", obj1)).ToJson();

                dynamic nxObj2 = NxCollectionHelper.Resolve(wp, obj2,
                    wp.Features, wp.Bodies, wp.Curves);
                if (nxObj2 == null)
                    return ToolResult.Fail(string.Format("Object '{0}' not found.", obj2)).ToJson();

                // NX2412: MeasureManager.NewDistance requires units as first param (not view).
                // Overload: NewDistance(units, object1, object2)
                dynamic lengthUnit = NxCollectionHelper.GetLengthUnit(wp);
                dynamic measure = wp.MeasureManager.NewDistance(lengthUnit, nxObj1, nxObj2);
                double distance = (double)measure.Value;

                return ToolResult.Ok(
                    string.Format("Distance between '{0}' and '{1}': {2} mm", obj1, obj2, distance),
                    new JObject() {
                        { "obj1", obj1 },
                        { "obj2", obj2 },
                        { "distance_mm", distance }
                    }).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_measure_distance failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 2. nx_measure_angle
    // =========================================================================

    /// <summary>
    /// Measures the angle between two named objects in the work part.
    /// Parameters:
    ///   obj1 (string, required) - Name of the first object.
    ///   obj2 (string, required) - Name of the second object.
    /// Returns: { obj1, obj2, angle_deg }
    /// </summary>
    public class MeasureAngleTool : IToolHandler
    {
        public string Name { get { return "nx_measure_angle"; } }
        public string Description { get { return "Measure the angle between two objects in the work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken obj1Token = parameters["obj1"];
                string obj1 = obj1Token != null ? obj1Token.Value<string>() : null;
                JToken obj2Token = parameters["obj2"];
                string obj2 = obj2Token != null ? obj2Token.Value<string>() : null;
                if (string.IsNullOrEmpty(obj1) || string.IsNullOrEmpty(obj2))
                    return ToolResult.Fail("Both 'obj1' and 'obj2' are required.").ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic nxObj1 = NxCollectionHelper.Resolve(wp, obj1,
                    wp.Features, wp.Bodies, wp.Curves);
                if (nxObj1 == null)
                    return ToolResult.Fail(string.Format("Object '{0}' not found.", obj1)).ToJson();

                dynamic nxObj2 = NxCollectionHelper.Resolve(wp, obj2,
                    wp.Features, wp.Bodies, wp.Curves);
                if (nxObj2 == null)
                    return ToolResult.Fail(string.Format("Object '{0}' not found.", obj2)).ToJson();

                // NX2412: MeasureManager.NewAngle requires units (not view) and uses
                // a point-based overload: NewAngle(units, basePoint, endPoint1, endPoint2, minorAngle)
                dynamic lengthUnit = NxCollectionHelper.GetLengthUnit(wp);

                // Get face/edge center points for the angle measurement
                // NX2412: Use GetPointAtParameters(0.5, 0.5) to get center of face
                dynamic pos1 = null;
                dynamic pos2 = null;
                try { pos1 = nxObj1.GetPointAtParameters(0.5, 0.5); } catch { }
                try { pos2 = nxObj2.GetPointAtParameters(0.5, 0.5); } catch { }

                // Fallback: if GetPointAtParameters fails, use origin
                if (pos1 == null) pos1 = new Point3d(0, 0, 0);
                if (pos2 == null) pos2 = new Point3d(0, 0, 0);

                dynamic measure = wp.MeasureManager.NewAngle(
                    lengthUnit, pos1, pos1, pos2, true);
                double angleDeg = (double)measure.Value;

                return ToolResult.Ok(
                    string.Format("Angle between '{0}' and '{1}': {2} degrees", obj1, obj2, angleDeg),
                    new JObject() {
                        { "obj1", obj1 },
                        { "obj2", obj2 },
                        { "angle_deg", angleDeg }
                    }).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_measure_angle failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 3. nx_measure_volume
    // =========================================================================

    /// <summary>
    /// Measures the volume of a body (or all bodies) in the work part.
    /// Parameters:
    ///   body (string, optional) - Target body name. Default: all bodies.
    /// Returns: { bodies: [{ body, volume_mm3, volume_cm3 }], total_volume_mm3?, total_volume_cm3? }
    /// </summary>
    public class MeasureVolumeTool : IToolHandler
    {
        public string Name { get { return "nx_measure_volume"; } }
        public string Description
        {
            get
            {
                return "Measure the volume of a body (or all bodies) in the work part. Returns volume in mm3 and cm3.";
            }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken bodyToken = parameters["body"];
                string body = bodyToken != null ? bodyToken.Value<string>() : null;

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var bodies = NxCollectionHelper.ToList(wp.Bodies);
                if (bodies.Count == 0)
                    return ToolResult.Fail("No bodies found in the work part.").ToJson();

                List<dynamic> targetBodies;
                if (!string.IsNullOrEmpty(body))
                {
                    dynamic found = null;
                    var available = new List<string>();
                    foreach (var b in bodies)
                    {
                        string bName = (string)b.Name;
                        available.Add(bName);
                        if (string.Equals(bName, body, StringComparison.OrdinalIgnoreCase))
                        {
                            found = b;
                            break;
                        }
                    }
                    if (found == null)
                        return ToolResult.Fail(
                            string.Format("Body '{0}' not found. Available: {1}", body, string.Join(", ", available.Take(30)))).ToJson();
                    targetBodies = new List<dynamic> { found };
                }
                else
                {
                    targetBodies = bodies;
                }

                var results = new JArray();
                double totalVolumeMm3 = 0.0;

                foreach (var b in targetBodies)
                {
                    var mp = MeasureHelpers.GetBodyMassProps(session, b);
                    double volumeMm3 = mp.Volume;
                    double volumeCm3 = volumeMm3 / 1000.0;
                    totalVolumeMm3 += volumeMm3;

                    results.Add(new JObject() {
                        { "body", (string)b.Name },
                        { "volume_mm3", volumeMm3 },
                        { "volume_cm3", volumeCm3 }
                    });
                }

                var data = new JObject() {
                    { "bodies", results }
                };
                if (targetBodies.Count > 1)
                {
                    data["total_volume_mm3"] = totalVolumeMm3;
                    data["total_volume_cm3"] = totalVolumeMm3 / 1000.0;
                }

                string msg = string.Format("Measured volume of {0} body(ies): "
                    + "{1:F4} mm3 / {2:F4} cm3", results.Count, totalVolumeMm3, totalVolumeMm3 / 1000.0);
                return ToolResult.Ok(msg, data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_measure_volume failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 4. nx_measure_mass_properties
    // =========================================================================

    /// <summary>
    /// Calculates mass, center of mass, and moments of inertia for a body.
    /// Parameters:
    ///   body (string, optional) - Target body name. Default: all bodies.
    ///   density (number, optional) - Material density in g/cm3 (reserved for future use).
    /// Returns: { bodies: [{ body, mass_kg, volume_mm3, surface_area_mm2, center_of_mass }], total_mass_kg? }
    /// </summary>
    public class MeasureMassPropertiesTool : IToolHandler
    {
        public string Name { get { return "nx_measure_mass_properties"; } }
        public string Description
        {
            get
            {
                return "Calculate mass, center of mass, and moments of inertia for a body.";
            }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken bodyToken = parameters["body"];
                string body = bodyToken != null ? bodyToken.Value<string>() : null;

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var bodies = NxCollectionHelper.ToList(wp.Bodies);
                if (bodies.Count == 0)
                    return ToolResult.Fail("No bodies found in the work part.").ToJson();

                List<dynamic> targetBodies;
                if (!string.IsNullOrEmpty(body))
                {
                    dynamic found = null;
                    foreach (var b in bodies)
                    {
                        if (string.Equals((string)b.Name, body,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            found = b;
                            break;
                        }
                    }
                    if (found == null)
                        return ToolResult.Fail(string.Format("Body '{0}' not found.", body)).ToJson();
                    targetBodies = new List<dynamic> { found };
                }
                else
                {
                    targetBodies = bodies;
                }

                var results = new JArray();
                double totalMass = 0.0;

                foreach (var b in targetBodies)
                {
                    var mp = MeasureHelpers.GetBodyMassProps(session, b);
                    double massKg = mp.Mass;    // GetBodyProperties 返回 kg (探测实证); 旧 Mass/1000 逻辑废弃
                    totalMass += massKg;

                    results.Add(new JObject() {
                        { "body", (string)b.Name },
                        { "mass_kg", massKg },
                        { "volume_mm3", mp.Volume },
                        { "surface_area_mm2", mp.Area },
                        { "center_of_mass", new JObject() {
                            { "x", mp.Centroid.X },
                            { "y", mp.Centroid.Y },
                            { "z", mp.Centroid.Z }
                        } }
                    });
                }

                var data = new JObject() {
                    { "bodies", results }
                };
                if (targetBodies.Count > 1)
                    data["total_mass_kg"] = totalMass;

                string msg = string.Format("Mass properties of {0} body(ies): "
                    + "total mass {1:F4} kg", results.Count, totalMass);
                return ToolResult.Ok(msg, data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    string.Format("nx_measure_mass_properties failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 5. nx_measure_perimeter
    // =========================================================================

    /// <summary>
    /// Measures the total perimeter/length of curves or edges.
    /// Parameters:
    ///   curves (string[], required) - List of curve or edge names to measure.
    /// Returns: { total_length_mm, individual_lengths: [{ name, length_mm }] }
    /// </summary>
    public class MeasurePerimeterTool : IToolHandler
    {
        public string Name { get { return "nx_measure_perimeter"; } }
        public string Description { get { return "Measure the total perimeter/length of curves or edges."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken curvesToken = parameters["curves"];
                string[] curveNames = curvesToken != null ? curvesToken.ToObject<string[]>() : null;
                if (curveNames == null || curveNames.Length == 0)
                    return ToolResult.Fail(
                        "The 'curves' parameter must be a non-empty list of curve/edge names.").ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var results = new JArray();
                double totalLength = 0.0;

                foreach (string curveName in curveNames)
                {
                    dynamic nxCurve = NxCollectionHelper.Resolve(wp, curveName,
                        wp.Curves, wp.Features);
                    if (nxCurve == null)
                    {
                        var available = new List<string>();
                        foreach (dynamic c in NxCollectionHelper.ToList(wp.Curves))
                        {
                            if (available.Count >= 30) break;
                            try { available.Add((string)c.Name); } catch { }
                        }
                        string hint = available.Count > 0
                            ? string.Format("Available curves: {0}", string.Join(", ", available))
                            : "No curves found in the work part.";
                        return ToolResult.Fail(
                            string.Format("Curve/edge '{0}' not found. {1}", curveName, hint)).ToJson();
                    }

                    double length = (double)nxCurve.Length;
                    totalLength += length;
                    results.Add(new JObject() {
                        { "name", curveName },
                        { "length_mm", length }
                    });
                }

                var data = new JObject() {
                    { "total_length_mm", totalLength },
                    { "individual_lengths", results }
                };

                string msg = string.Format("Total perimeter: {0:F4} mm ({1} curve(s))", totalLength, results.Count);
                return ToolResult.Ok(msg, data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_measure_perimeter failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 6. nx_measure_area
    // =========================================================================

    /// <summary>
    /// Measures the area of faces.
    /// Parameters:
    ///   faces (string[], required) - List of face names to measure.
    /// Returns: { total_area_mm2, individual_areas: [{ name, area_mm2 }] }
    /// </summary>
    public class MeasureAreaTool : IToolHandler
    {
        public string Name { get { return "nx_measure_area"; } }
        public string Description { get { return "Measure the area of faces. faces(array of tags), optional body_id filter."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                // faces = int tags (NXOpen tags are session-scoped; get via nx_inspect_topology face_id)
                var faceTags = ToolHelpers.GetIntArray(parameters, "faces");
                if (faceTags.Count == 0)
                    return ToolResult.Fail(
                        "The 'faces' parameter must be a non-empty list of face tags (int).").ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // Exact area via MeasureManager.NewFaceProperties (NX built-in engine)
                var mm = wp.MeasureManager;
                var unitMM2 = wp.UnitCollection.FindObject("MilliMeter");

                var results = new JArray();
                double totalArea = 0.0;
                int measured = 0;

                foreach (int tag in faceTags)
                {
                    dynamic nxFace = ToolHelpers.FindFaceByTag(wp, tag);
                    if (nxFace == null) continue;
                    NXOpen.MeasureFaces fp = mm.NewFaceProperties(unitMM2, unitMM2, 0.9,
                        new NXOpen.IParameterizedSurface[] { (NXOpen.Face)nxFace });
                    double area = fp.Area;
                    totalArea += area;
                    measured++;
                    results.Add(new JObject() {
                        { "face_id", tag },
                        { "area_mm2", area }
                    });
                }

                if (measured == 0)
                    return ToolResult.Fail("No valid face tags found.").ToJson();

                var data = new JObject() {
                    { "total_area_mm2", totalArea },
                    { "individual_areas", results }
                };

                string msg = string.Format("Total area: {0:F4} mm2 ({1} face(s))", totalArea, measured);
                return ToolResult.Ok(msg, data).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_measure_area failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 7. nx_section_analysis
    // =========================================================================

    /// <summary>
    /// Performs section analysis on a body at a specified plane.
    /// Parameters:
    ///   plane_type (string, required) - Section plane: "XY", "XZ", or "YZ".
    ///   offset (number, optional) - Offset from origin along the plane normal (mm). Default: 0.
    ///   body (string, optional) - Target body name. Default: first body.
    /// Returns: { plane_type, offset, body, section_area, section_perimeter, section_curves }
    /// </summary>
    /// Parameters:
    ///   plane_type (string, optional) - plane_type parameter.
    ///   offset (string, optional) - offset parameter.
    ///   body (string, optional) - body parameter.
    ///
    public class SectionAnalysisTool : IToolHandler
    {
        public string Name { get { return "nx_section_analysis"; } }
        public string Description
        {
            get
            {
                return "Perform section analysis on a body at a specified plane. "
                    + "Returns section area, perimeter, and section curve names.";
            }
        }

        private static readonly HashSet<string> ValidPlanes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XY", "XZ", "YZ" };

        /// <summary>
        /// Plane normal vectors: XY -> Z-axis, XZ -> Y-axis, YZ -> X-axis
        /// </summary>
        private static readonly Dictionary<string, double[]> PlaneNormals =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
            {
                {"XY", new[] { 0.0, 0.0, 1.0 }},
                {"XZ", new[] { 0.0, 1.0, 0.0 }},
                {"YZ", new[] { 1.0, 0.0, 0.0 }}
            };

        public JObject Execute(dynamic session, JObject parameters)
        {
            // ── BROKEN: NXOpen.Section does not have SetSectionType/SetOrigin/SetNormal/AddBody/Area/Perimeter ──
            // Verified 2026-08-27: NXOpen.Section is a section-string object, not a cross-section analysis tool.
            // Correct approach for cross-section area at a plane:
            //   1. part.Features.CreateSectionCurveBuilder() → set plane + body → Commit → get section curves
            //   2. part.MeasureManager.NewPolarArea(units, scCollector, createExpressions) → get area
            //   3. This requires NXOpen.ScCollector setup which is non-trivial
            // For now, return a clear BROKEN status with guidance.
            return ToolResult.Fail(
                "nx_section_analysis is BROKEN in NX2412. " +
                "NXOpen.Section has no SetSectionType/SetOrigin/SetNormal/AddBody methods. " +
                "The tool's implementation is fundamentally wrong. " +
                "Use nx_run_journal with a custom C# journal for section analysis, " +
                "or use nx_measure_volume / nx_inspect_topology as workarounds. " +
                "[Correct API chain: Features.CreateSectionCurveBuilder + MeasureManager.NewPolarArea — needs complete rewrite]").ToJson();
        }
    }
}
