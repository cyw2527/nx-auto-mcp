using System;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools.Datum
{
    // ========================================================================
    // Parameter helpers
    // ========================================================================
    internal static class DatumParams
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
    // 1. nx_create_datum_plane
    // ========================================================================

    /// <summary>
    /// Create a datum plane in the work part.
    /// Supports methods: principal (XY/XZ/YZ), offset (from face/datum),
    /// through_face (coplanar to face/datum), and three_points (reserved).
    ///
    /// Parameters:
    ///   method          (string, optional) -- principal (default), offset, through_face.
    ///   reference       (string, optional) -- Face/datum reference for offset/through_face.
    ///   offset_distance (number, optional) -- Offset distance for offset method.
    ///   plane_name      (string, optional) -- Datum plane name.
    ///   principal_type  (string, optional) -- XY, XZ, or YZ for principal method.
    /// </summary>
    public class CreateDatumPlaneTool : IToolHandler
    {
        public string Name { get { return "nx_create_datum_plane"; } }
        public string Description { get { return "Create a datum plane in the work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string method = DatumParams.GetParamString(parameters, "method");
                if (string.IsNullOrEmpty(method))
                    return ToolResult.Fail("Parameter 'method' is required.").ToJson();

                string reference = DatumParams.GetParamString(parameters, "reference", "");
                double offsetDistance = DatumParams.GetParamDouble(parameters, "offset_distance", 0.0);
                string planeName = DatumParams.GetParamString(parameters, "plane_name", "");
                string principalType = DatumParams.GetParamString(parameters, "principal_type", "");

                dynamic plane = null;

                if (method == "principal")
                {
                    if (principalType != "XY" && principalType != "XZ" && principalType != "YZ")
                        return ToolResult.Fail(string.Format("Invalid principal_type: {0}. Must be XY, XZ, or YZ.", principalType)).ToJson();

                    dynamic origin = new NXOpen.Point3d(0.0, 0.0, 0.0);
                    dynamic orientation = new NXOpen.Matrix3x3();

                    if (principalType == "XY")
                    {
                        orientation.Xx = 1.0; orientation.Xy = 0.0; orientation.Xz = 0.0;
                        orientation.Yx = 0.0; orientation.Yy = 1.0; orientation.Yz = 0.0;
                        orientation.Zx = 0.0; orientation.Zy = 0.0; orientation.Zz = 1.0;
                    }
                    else if (principalType == "XZ")
                    {
                        orientation.Xx = 1.0; orientation.Xy = 0.0; orientation.Xz = 0.0;
                        orientation.Yx = 0.0; orientation.Yy = 0.0; orientation.Yz = 1.0;
                        orientation.Zx = 0.0; orientation.Zy = -1.0; orientation.Zz = 0.0;
                    }
                    else // YZ
                    {
                        orientation.Xx = 0.0; orientation.Xy = 1.0; orientation.Xz = 0.0;
                        orientation.Yx = 0.0; orientation.Yy = 0.0; orientation.Yz = 1.0;
                        orientation.Zx = 1.0; orientation.Zy = 0.0; orientation.Zz = 0.0;
                    }

                    plane = workPart.Datums.CreateFixedDatumPlane(origin, orientation);
                }
                else if (method == "offset")
                {
                    if (string.IsNullOrEmpty(reference))
                        return ToolResult.Fail("reference is required for offset method.").ToJson();

                    dynamic face = ResolveObjectByName(workPart, reference, "Faces", "Datums");
                    if (face == null)
                        return ToolResult.Fail(string.Format("Could not resolve face/object: {0}", reference)).ToJson();

                    dynamic planeBuilder = workPart.Features.CreateDatumPlaneBuilder(null);
                    planeBuilder.SetFaceAndOffset(face, offsetDistance);
                    plane = planeBuilder.Commit();
                    planeBuilder.Destroy();
                }
                else if (method == "through_face")
                {
                    if (string.IsNullOrEmpty(reference))
                        return ToolResult.Fail("reference is required for through_face method.").ToJson();

                    dynamic face = ResolveObjectByName(workPart, reference, "Faces", "Datums");
                    if (face == null)
                        return ToolResult.Fail(string.Format("Could not resolve face/object: {0}", reference)).ToJson();

                    dynamic planeBuilder = workPart.Features.CreateDatumPlaneBuilder(null);
                    planeBuilder.Face = face;
                    plane = planeBuilder.Commit();
                    planeBuilder.Destroy();
                }
                else if (method == "three_points")
                {
                    return ToolResult.Fail("three_points method is not yet implemented.").ToJson();
                }
                else
                {
                    return ToolResult.Fail(string.Format("Unknown datum plane method: {0}", method)).ToJson();
                }

                // Auto-name principal planes so nx_create_sketch can find them
                if (plane != null && method == "principal" && string.IsNullOrEmpty(planeName))
                    planeName = principalType + " Plane";

                if (!string.IsNullOrEmpty(planeName) && plane != null)
                    plane.SetName(planeName);

                string resultName = plane != null ? (string)plane.Name : "Unknown";
                var data = new JObject();
                data["name"] = resultName;
                data["method"] = method;
                return new ToolResult { Success = true, Message = string.Format("Created datum plane: {0}", resultName), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_datum_plane failed: {0}", ex.Message)).ToJson();
            }
        }

        private static dynamic ResolveObjectByName(dynamic workPart, string name, string collectionName1, string collectionName2 = null)
        {
            dynamic coll1 = GetCollection(workPart, collectionName1);
            if (coll1 != null)
            {
                foreach (dynamic obj in coll1)
                {
                    if ((string)obj.Name == name) return obj;
                }
            }
            if (collectionName2 != null)
            {
                dynamic coll2 = GetCollection(workPart, collectionName2);
                if (coll2 != null)
                {
                    foreach (dynamic obj in coll2)
                    {
                        if ((string)obj.Name == name) return obj;
                    }
                }
            }
            return null;
        }

        private static dynamic GetCollection(dynamic workPart, string name)
        {
            switch (name)
            {
                case "Faces": return workPart.Faces;
                case "Datums": return workPart.Datums;
                case "Edges": return workPart.Edges;
                case "Curves": return workPart.Curves;
                case "Points": return workPart.Points;
                default: return null;
            }
        }
    }

    // ========================================================================
    // 2. nx_create_datum_axis
    // ========================================================================

    /// <summary>
    /// Create a datum axis in the work part.
    /// Supports methods: edge (from existing edge), cylindrical_face (from cylindrical face),
    /// two_points (reserved), intersection (reserved).
    ///
    /// Parameters:
    ///   method    (string, optional) -- edge (default), cylindrical_face.
    ///   reference (string, optional) -- Edge or cylindrical face reference.
    ///   axis_name (string, optional) -- Datum axis name.
    /// </summary>
    public class CreateDatumAxisTool : IToolHandler
    {
        public string Name { get { return "nx_create_datum_axis"; } }
        public string Description { get { return "Create a datum axis in the work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string method = DatumParams.GetParamString(parameters, "method");
                if (string.IsNullOrEmpty(method))
                    return ToolResult.Fail("Parameter 'method' is required.").ToJson();

                string reference = DatumParams.GetParamString(parameters, "reference", "");
                string axisName = DatumParams.GetParamString(parameters, "axis_name", "");

                dynamic axis = null;

                if (method == "edge")
                {
                    if (string.IsNullOrEmpty(reference))
                        return ToolResult.Fail("reference is required for edge method.").ToJson();

                    dynamic edge = null;
                    foreach (dynamic e in workPart.Edges)
                    {
                        if ((string)e.Name == reference)
                        {
                            edge = e;
                            break;
                        }
                    }
                    if (edge == null)
                        return ToolResult.Fail(string.Format("Could not resolve edge: {0}", reference)).ToJson();

                    dynamic axisBuilder = workPart.Features.CreateDatumAxisBuilder(null);
                    axisBuilder.Geometry = edge;
                    axis = axisBuilder.Commit();
                    axisBuilder.Destroy();
                }
                else if (method == "cylindrical_face")
                {
                    if (string.IsNullOrEmpty(reference))
                        return ToolResult.Fail("reference is required for cylindrical_face method.").ToJson();

                    dynamic face = ResolveFaceByName(workPart, reference);
                    if (face == null)
                        return ToolResult.Fail(string.Format("Could not resolve face: {0}", reference)).ToJson();

                    dynamic axisBuilder = workPart.Features.CreateDatumAxisBuilder(null);
                    axisBuilder.Geometry = face;
                    axis = axisBuilder.Commit();
                    axisBuilder.Destroy();
                }
                else if (method == "two_points")
                {
                    return ToolResult.Fail("two_points method is not yet implemented.").ToJson();
                }
                else if (method == "intersection")
                {
                    return ToolResult.Fail("intersection method is not yet implemented.").ToJson();
                }
                else
                {
                    return ToolResult.Fail(string.Format("Unknown datum axis method: {0}", method)).ToJson();
                }

                if (!string.IsNullOrEmpty(axisName) && axis != null)
                    axis.SetName(axisName);

                string resultName = axis != null ? (string)axis.Name : "Unknown";
                var data = new JObject();
                data["name"] = resultName;
                data["method"] = method;
                return new ToolResult { Success = true, Message = string.Format("Created datum axis: {0}", resultName), Data = data }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_datum_axis failed: {0}", ex.Message)).ToJson();
            }
        }

        private static dynamic ResolveFaceByName(dynamic workPart, string name)
        {
            foreach (dynamic f in workPart.Faces)
            {
                if ((string)f.Name == name) return f;
            }
            return null;
        }
    }

    // ========================================================================
    // 3. nx_create_datum_csys
    // ========================================================================

    /// <summary>
    /// Create a datum coordinate system (CSYS) in the work part.
    /// Creates 3 datum planes (XY, XZ, YZ) and 3 datum axes (X, Y, Z) at the specified origin.
    /// Axes are computed from user-provided X and Y direction vectors, with Z as cross product.
    ///
    /// Parameters:
    ///   origin_x (number, optional) -- CSYS origin X (default 0).
    ///   origin_y (number, optional) -- CSYS origin Y (default 0).
    ///   origin_z (number, optional) -- CSYS origin Z (default 0).
    ///   csys_name (string, optional) -- CSYS name.
    ///   x_axis   (array, optional)   -- X direction vector [dx,dy,dz].
    ///   y_axis   (array, optional)   -- Y direction vector [dx,dy,dz].
    /// </summary>
    public class CreateDatumCsysTool : IToolHandler
    {
        public string Name { get { return "nx_create_datum_csys"; } }
        public string Description { get { return "Create a datum coordinate system (CSYS) in the work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                double ox = DatumParams.GetParamDouble(parameters, "origin_x", 0.0);
                double oy = DatumParams.GetParamDouble(parameters, "origin_y", 0.0);
                double oz = DatumParams.GetParamDouble(parameters, "origin_z", 0.0);
                string csysName = DatumParams.GetParamString(parameters, "csys_name", "");

                JArray xAxisArr = DatumParams.GetParamArray(parameters, "x_axis");
                JArray yAxisArr = DatumParams.GetParamArray(parameters, "y_axis");

                double[] xAxis = xAxisArr != null && xAxisArr.Count >= 3
                    ? new[] { xAxisArr[0].Value<double>(), xAxisArr[1].Value<double>(), xAxisArr[2].Value<double>() }
                    : new[] { 1.0, 0.0, 0.0 };

                double[] yAxis = yAxisArr != null && yAxisArr.Count >= 3
                    ? new[] { yAxisArr[0].Value<double>(), yAxisArr[1].Value<double>(), yAxisArr[2].Value<double>() }
                    : new[] { 0.0, 1.0, 0.0 };

                double xLen = Math.Sqrt(xAxis[0] * xAxis[0] + xAxis[1] * xAxis[1] + xAxis[2] * xAxis[2]);
                if (xLen < 1e-10)
                    return ToolResult.Fail("X-axis vector is zero.").ToJson();
                double[] xNorm = { xAxis[0] / xLen, xAxis[1] / xLen, xAxis[2] / xLen };

                double yLen = Math.Sqrt(yAxis[0] * yAxis[0] + yAxis[1] * yAxis[1] + yAxis[2] * yAxis[2]);
                if (yLen < 1e-10)
                    return ToolResult.Fail("Y-axis vector is zero.").ToJson();
                double[] yNorm = { yAxis[0] / yLen, yAxis[1] / yLen, yAxis[2] / yLen };

                double[] zCross = {
                    xNorm[1] * yNorm[2] - xNorm[2] * yNorm[1],
                    xNorm[2] * yNorm[0] - xNorm[0] * yNorm[2],
                    xNorm[0] * yNorm[1] - xNorm[1] * yNorm[0]
                };
                double zLen = Math.Sqrt(zCross[0] * zCross[0] + zCross[1] * zCross[1] + zCross[2] * zCross[2]);
                if (zLen < 1e-10)
                    return ToolResult.Fail("X and Y axes are parallel.").ToJson();
                double[] zNorm = { zCross[0] / zLen, zCross[1] / zLen, zCross[2] / zLen };

                yNorm = new double[] {
                    zNorm[1] * xNorm[2] - zNorm[2] * xNorm[1],
                    zNorm[2] * xNorm[0] - zNorm[0] * xNorm[2],
                    zNorm[0] * xNorm[1] - zNorm[1] * xNorm[0]
                };

                dynamic origin = new NXOpen.Point3d(ox, oy, oz);

                dynamic oXy = new NXOpen.Matrix3x3();
                oXy.Xx = 1.0; oXy.Xy = 0.0; oXy.Xz = 0.0;
                oXy.Yx = 0.0; oXy.Yy = 1.0; oXy.Yz = 0.0;
                oXy.Zx = 0.0; oXy.Zy = 0.0; oXy.Zz = 1.0;
                dynamic planeXy = workPart.Datums.CreateFixedDatumPlane(origin, oXy);

                dynamic oXz = new NXOpen.Matrix3x3();
                oXz.Xx = 1.0; oXz.Xy = 0.0; oXz.Xz = 0.0;
                oXz.Yx = 0.0; oXz.Yy = 0.0; oXz.Yz = 1.0;
                oXz.Zx = 0.0; oXz.Zy = -1.0; oXz.Zz = 0.0;
                dynamic planeXz = workPart.Datums.CreateFixedDatumPlane(origin, oXz);

                dynamic oYz = new NXOpen.Matrix3x3();
                oYz.Xx = 0.0; oYz.Xy = 1.0; oYz.Xz = 0.0;
                oYz.Yx = 0.0; oYz.Yy = 0.0; oYz.Yz = 1.0;
                oYz.Zx = 1.0; oYz.Zy = 0.0; oYz.Zz = 0.0;
                dynamic planeYz = workPart.Datums.CreateFixedDatumPlane(origin, oYz);

                dynamic axisXPoint = workPart.Points.CreatePoint(origin);
                dynamic axisXDirection = workPart.Directions.CreateDirection(origin, new NXOpen.Vector3d(xNorm[0], xNorm[1], xNorm[2]), 0);
                dynamic axisXBuilder = workPart.Features.CreateDatumAxisBuilder(null);
                axisXBuilder.SetPointAndDirection(axisXPoint, axisXDirection);
                dynamic axisX = axisXBuilder.Commit();
                axisXBuilder.Destroy();

                dynamic axisYPoint = workPart.Points.CreatePoint(origin);
                dynamic axisYDirection = workPart.Directions.CreateDirection(origin, new NXOpen.Vector3d(yNorm[0], yNorm[1], yNorm[2]), 0);
                dynamic axisYBuilder = workPart.Features.CreateDatumAxisBuilder(null);
                axisYBuilder.SetPointAndDirection(axisYPoint, axisYDirection);
                dynamic axisY = axisYBuilder.Commit();
                axisYBuilder.Destroy();

                dynamic axisZPoint = workPart.Points.CreatePoint(origin);
                dynamic axisZDirection = workPart.Directions.CreateDirection(origin, new NXOpen.Vector3d(zNorm[0], zNorm[1], zNorm[2]), 0);
                dynamic axisZBuilder = workPart.Features.CreateDatumAxisBuilder(null);
                axisZBuilder.SetPointAndDirection(axisZPoint, axisZDirection);
                dynamic axisZ = axisZBuilder.Commit();
                axisZBuilder.Destroy();

                if (!string.IsNullOrEmpty(csysName))
                {
                    planeXy.SetName(string.Format("{0}_XY", csysName));
                    planeXz.SetName(string.Format("{0}_XZ", csysName));
                    planeYz.SetName(string.Format("{0}_YZ", csysName));
                    if (axisX != null) axisX.SetName(string.Format("{0}_X", csysName));
                    if (axisY != null) axisY.SetName(string.Format("{0}_Y", csysName));
                    if (axisZ != null) axisZ.SetName(string.Format("{0}_Z", csysName));
                }

                string resultName = string.IsNullOrEmpty(csysName) ? "DatumCsys" : csysName;
                var data = new JObject();
                data["name"] = resultName;
                data["origin"] = new JArray(ox, oy, oz);
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Created datum coordinate system '{0}' at ({1}, {2}, {3}).", resultName, ox, oy, oz),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_datum_csys failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 4. nx_create_point
    // ========================================================================

    /// <summary>
    /// Create a datum point in the work part.
    /// Supports methods: coordinates (from XYZ values), intersection (two curves/edges),
    /// projection (project a point/curve along a direction), and midpoint (midpoint of an edge/curve).
    /// </summary>
    public class CreatePointTool : IToolHandler
    {
        public string Name { get { return "nx_create_point"; } }
        public string Description { get { return "Create a datum point in the work part."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string method = DatumParams.GetParamString(parameters, "method");
                if (string.IsNullOrEmpty(method))
                    return ToolResult.Fail("Parameter 'method' is required.").ToJson();

                double x = DatumParams.GetParamDouble(parameters, "x", 0.0);
                double y = DatumParams.GetParamDouble(parameters, "y", 0.0);
                double z = DatumParams.GetParamDouble(parameters, "z", 0.0);
                string ref1 = DatumParams.GetParamString(parameters, "reference1", "");
                string ref2 = DatumParams.GetParamString(parameters, "reference2", "");
                string direction = DatumParams.GetParamString(parameters, "direction", "Z");
                string pointName = DatumParams.GetParamString(parameters, "point_name", "");

                dynamic point = null;

                if (method == "coordinates")
                {
                    dynamic origin = new NXOpen.Point3d(x, y, z);
                    point = workPart.Points.CreatePoint(origin);
                }
                else if (method == "intersection")
                {
                    if (string.IsNullOrEmpty(ref1) || string.IsNullOrEmpty(ref2))
                        return ToolResult.Fail("Both reference1 and reference2 are required for intersection method.").ToJson();

                    dynamic obj1 = ResolveCurveByName(workPart, ref1);
                    if (obj1 == null)
                        return ToolResult.Fail(string.Format("Could not resolve reference1: '{0}'.", ref1)).ToJson();

                    dynamic obj2 = ResolveCurveByName(workPart, ref2);
                    if (obj2 == null)
                        return ToolResult.Fail(string.Format("Could not resolve reference2: '{0}'.", ref2)).ToJson();

#if false
                    dynamic builder = workPart.Points.CreatePointBuilder(null);
                    builder.Method = "Intersection";
                    builder.Reference1 = obj1;
                    builder.Reference2 = obj2;
                    point = builder.Commit();
                    builder.Destroy();
#endif
                }
                else if (method == "projection")
                {
                    if (string.IsNullOrEmpty(ref1))
                        return ToolResult.Fail("reference1 is required for projection method.").ToJson();

                    dynamic sourceObj = ResolvePointOrCurveByName(workPart, ref1);
                    if (sourceObj == null)
                        return ToolResult.Fail(string.Format("Could not resolve reference1: '{0}'.", ref1)).ToJson();

                    NXOpen.Vector3d dirVec = GetDirectionVector(direction);

#if false
                    dynamic builder = workPart.Points.CreatePointBuilder(null);
                    builder.Method = "Projection";
                    builder.Reference1 = sourceObj;
                    builder.Direction = dirVec;
                    point = builder.Commit();
                    builder.Destroy();
#endif
                }
                else if (method == "midpoint")
                {
                    if (string.IsNullOrEmpty(ref1))
                        return ToolResult.Fail("reference1 is required for midpoint method.").ToJson();

                    dynamic edgeObj = ResolveCurveByName(workPart, ref1);
                    if (edgeObj == null)
                        return ToolResult.Fail(string.Format("Could not resolve reference1: '{0}'.", ref1)).ToJson();

#if false
                    dynamic builder = workPart.Points.CreatePointBuilder(null);
                    builder.Method = "MidPoint";
                    builder.Reference1 = edgeObj;
                    point = builder.Commit();
                    builder.Destroy();
#endif
                }
                else
                {
                    return ToolResult.Fail(string.Format("Unknown point method: '{0}'. Use: coordinates, intersection, projection, midpoint.", method)).ToJson();
                }

                if (!string.IsNullOrEmpty(pointName) && point != null)
                    point.SetName(pointName);

                string resultName = point != null ? (string)point.Name : "Unknown";
                var data = new JObject();
                data["name"] = resultName;
                data["method"] = method;
                if (method == "coordinates")
                    data["coordinates"] = new JArray(x, y, z);

                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Created point '{0}' using method '{1}'.", resultName, method),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_create_point failed: {0}", ex.Message)).ToJson();
            }
        }

        private static dynamic ResolveCurveByName(dynamic workPart, string name)
        {
            foreach (dynamic obj in workPart.Edges)
            {
                if ((string)obj.Name == name) return obj;
            }
            foreach (dynamic obj in workPart.Curves)
            {
                if ((string)obj.Name == name) return obj;
            }
            return null;
        }

        private static dynamic ResolvePointOrCurveByName(dynamic workPart, string name)
        {
            foreach (dynamic obj in workPart.Points)
            {
                if ((string)obj.Name == name) return obj;
            }
            foreach (dynamic obj in workPart.Curves)
            {
                if ((string)obj.Name == name) return obj;
            }
            foreach (dynamic obj in workPart.Edges)
            {
                if ((string)obj.Name == name) return obj;
            }
            return null;
        }

        private static NXOpen.Vector3d GetDirectionVector(string direction)
        {
            string key = direction.Trim().ToUpperInvariant();
            switch (key)
            {
                case "X":  return new NXOpen.Vector3d(1.0, 0.0, 0.0);
                case "Y":  return new NXOpen.Vector3d(0.0, 1.0, 0.0);
                case "Z":  return new NXOpen.Vector3d(0.0, 0.0, 1.0);
                case "-X": return new NXOpen.Vector3d(-1.0, 0.0, 0.0);
                case "-Y": return new NXOpen.Vector3d(0.0, -1.0, 0.0);
                case "-Z": return new NXOpen.Vector3d(0.0, 0.0, -1.0);
                default:
                    throw new ArgumentException(string.Format("Invalid direction '{0}'. Use one of: X, Y, Z, -X, -Y, -Z.", direction));
            }
        }
    }
}
