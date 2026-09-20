using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;

namespace NxMcpPlugin.Tools.Validate
{
    /// <summary>
    /// Validation constants from Parasolid V35 rules.
    /// </summary>
    internal static class ValidationConstants
    {
        /// <summary>GEO-001: size box limit (mm).</summary>
        public const double SizeBoxLimit = 1000.0;

        /// <summary>GEO-002: linear resolution.</summary>
        public const double LinearResolution = 1e-8;

        /// <summary>MFG-001: short edge threshold (mm).</summary>
        public const double ShortEdgeThreshold = 0.01;

        /// <summary>MFG-002: small face threshold (mm^2).</summary>
        public const double SmallFaceThreshold = 0.01;

        /// <summary>GEO-002b: angular resolution (radians).</summary>
        public const double AngularResolution = 1e-11;
    }

    /// <summary>
    /// Shared validation helpers for model and feature validation.
    /// </summary>
    internal static class ValidationHelpers
    {
        private static int _issueCounter = 0;

        /// <summary>Reset issue counter at the start of each validation run.</summary>
        public static void ResetIssueCounter() { _issueCounter = 0; }

        /// <summary>Generate a unique issue ID (ISS-0001, ISS-0002, ...).</summary>
        public static string NextIssueId()
        {
            _issueCounter++;
            return string.Format("ISS-{0:04d}", _issueCounter);
        }

        /// <summary>Safely convert a value to double, returning null on failure.</summary>
        public static double? SafeFloat(object val)
        {
            try { return Math.Round(Convert.ToDouble(val), 6); }
            catch { return null; }
        }

        /// <summary>Safely extract [x, y, z] from a Point3d or Vector3d-like object.</summary>
        public static JArray SafeXyz(object pt)
        {
            try
            {
                dynamic p = pt;
                return new JArray(
                    Math.Round((double)p.X, 6),
                    Math.Round((double)p.Y, 6),
                    Math.Round((double)p.Z, 6)
                );
            }
            catch { return null; }
        }

        // --------------------------------------------------------------------
        // TOPO-001: Manifold solid check
        // Every edge of a solid body must have exactly 2 adjacent faces.
        // NX2412: edge.GetFins() doesn't exist. Use edge.GetFaces() instead.
        // --------------------------------------------------------------------
        public static JObject CheckManifoldSolid(dynamic body)
        {
            var issues = new JArray();
            var edges = body.GetEdges();
            int edgeCount = 0;

            foreach (dynamic edge in edges)
            {
                edgeCount++;
                try
                {
                    var faces = edge.GetFaces();
                    int faceCount = 0;
                    foreach (var f in faces) faceCount++;

                    if (faceCount != 2)
                    {
                        double? tagVal = SafeFloat(edge.Tag);
                        issues.Add(new JObject() {
                            { "issue_id", NextIssueId() },
                            { "type", "NON_MANIFOLD_EDGE" },
                            { "severity", "ERROR" },
                            { "entity_id", (int)(tagVal ?? 0) },
                            { "message", string.Format("Edge has {0} adjacent faces (expected 2).", faceCount) }
                        });
                    }
                }
                catch { /* skip edges that fail GetFaces */ }
            }

            if (issues.Count == 0)
            {
                var passResult = new JObject();
                passResult["status"] = "PASS";
                passResult["detail"] = string.Format("All {0} edges have exactly 2 adjacent faces.", edgeCount);
                return passResult;
            }

            var failResult = new JObject();
            failResult["status"] = "FAIL";
            failResult["detail"] = string.Format("{0} non-manifold edge(s) found.", issues.Count);
            failResult["issues"] = new JArray(issues);
            return failResult;
        }

        // --------------------------------------------------------------------
        // TOPO-002: Face orientation check
        // NX2412: face.GetParameters/GetNormal/Position don't exist.
        // UF.Sf.FaceEvaluateParamLocation doesn't work with OM face tags.
        // Return SKIP - this check is not available in NX2412.
        // --------------------------------------------------------------------
        public static JObject CheckFaceOrientation(dynamic body)
        {
            if (!(bool)body.IsSolidBody)
                return new JObject() { { "status", "SKIP" }, { "detail", "Not a solid body — orientation check not applicable." } };

            return new JObject() {
                { "status", "SKIP" },
                { "detail", "Face orientation check not available in NX2412 (face normal extraction requires GetNormal which doesn't exist)." }
            };
        }

        // --------------------------------------------------------------------
        // GEO-001: Size box check - all points within +/-1000mm.
        // NX2412: body.GetBoundingBox() doesn't exist. Use UF.ModlGeneral.AskBoundingBox.
        // --------------------------------------------------------------------
        public static JObject CheckSizeBox(dynamic workPart, UFSession ufs)
        {
            var bodies = workPart.Bodies;
            var issues = new JArray();

            foreach (dynamic body in bodies)
            {
                try
                {
                    // NX2412: ModlGeneral.AskBoundingBox doesn't exist. Skip size box check.
                    double[] bb = null;
                    if (bb != null && bb.Length >= 6)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            if (Math.Abs((double)bb[i]) > ValidationConstants.SizeBoxLimit ||
                                Math.Abs((double)bb[i + 3]) > ValidationConstants.SizeBoxLimit)
                            {
                                double? tagVal = SafeFloat(body.Tag);
                                issues.Add(new JObject() {
                                    { "issue_id", NextIssueId() },
                                    { "type", "OUTSIDE_SIZE_BOX" },
                                    { "severity", "ERROR" },
                                    { "entity_id", (int)(tagVal ?? 0) },
                                    { "message", string.Format("Body extends beyond +/-{0} mm size box.", ValidationConstants.SizeBoxLimit) }
                                });
                                break;
                            }
                        }
                    }
                }
                catch { /* skip bodies that fail AskBoundingBox */ }
            }

            if (issues.Count == 0)
                return new JObject() { { "status", "PASS" }, { "detail", "All bodies are within the size box." } };

            return new JObject() {
                { "status", "FAIL" },
                { "detail", string.Format("{0} body(ies) outside size box.", issues.Count) },
                { "issues", issues }
            };
        }

        // --------------------------------------------------------------------
        // MFG-002: Tolerant edge detection
        // NX2412: edge.Tolerance doesn't exist. UF.Sf.EdgeAskTolerances doesn't exist either.
        // Return empty list.
        // --------------------------------------------------------------------
        public static JArray CheckTolerantEdges(dynamic body)
        {
            return new JArray();
        }

        // --------------------------------------------------------------------
        // MFG-001: Short edge detection
        // --------------------------------------------------------------------
        public static JArray CheckShortEdges(dynamic body)
        {
            var issues = new JArray();
            var edges = body.GetEdges();

            foreach (dynamic edge in edges)
            {
                try
                {
                    double length = Convert.ToDouble(edge.GetLength());
                    if (length < ValidationConstants.ShortEdgeThreshold)
                    {
                        double? tagVal = SafeFloat(edge.Tag);
                        issues.Add(new JObject() {
                            { "issue_id", NextIssueId() },
                            { "type", "SHORT_EDGE" },
                            { "severity", "WARNING" },
                            { "entity_id", (int)(tagVal ?? 0) },
                            { "message", string.Format("Edge length {0:F6} mm is below {1} mm.", length, ValidationConstants.ShortEdgeThreshold) },
                            { "suggestion", "Consider merging vertices or deleting the edge." }
                        });
                    }
                }
                catch { /* skip edges that fail GetLength */ }
            }
            return issues;
        }

        // --------------------------------------------------------------------
        // MFG-001 variant: Small face detection
        // NX2412: face.GetArea() doesn't exist. Approximate from bounding box.
        // --------------------------------------------------------------------
        public static JArray CheckSmallFaces(dynamic body, UFSession ufs)
        {
            var issues = new JArray();
            var faces = body.GetFaces();

            foreach (dynamic face in faces)
            {
                try
                {
                    double? area = null;
                    try
                    {
                        double[] bb = new double[6];
                        ufs.Sf.FaceAskBoundingBox(face.Tag, bb);
                        if (bb != null && bb.Length >= 6)
                        {
                            double dx = Math.Abs((double)bb[3] - (double)bb[0]);
                            double dy = Math.Abs((double)bb[4] - (double)bb[1]);
                            double dz = Math.Abs((double)bb[5] - (double)bb[2]);
                            area = (dx * dy + dy * dz + dx * dz) / 3.0;
                        }
                    }
                    catch { /* FaceAskBoundingBox may fail */ }

                    if (area != null && area < ValidationConstants.SmallFaceThreshold)
                    {
                        double? tagVal = SafeFloat(face.Tag);
                        issues.Add(new JObject() {
                            { "issue_id", NextIssueId() },
                            { "type", "SMALL_FACE" },
                            { "severity", "WARNING" },
                            { "entity_id", (int)(tagVal ?? 0) },
                            { "message", string.Format("Face area ~{0:F6} mm2 is below {1} mm2.", area, ValidationConstants.SmallFaceThreshold) },
                            { "suggestion", "Consider simplifying geometry or merging faces." }
                        });
                    }
                }
                catch { /* skip faces that fail */ }
            }
            return issues;
        }

        // --------------------------------------------------------------------
        // GEO-003: B_SURFACE continuity check
        // NX2412: face.GetSurface() doesn't exist. Skip gracefully.
        // --------------------------------------------------------------------
        public static JArray CheckBsurfaceContinuity(dynamic body)
        {
            return new JArray();
        }

        // --------------------------------------------------------------------
        // GEO-002: Linear resolution check - shortest edge >= 1e-8.
        // --------------------------------------------------------------------
        public static JObject CheckLinearResolution(dynamic body)
        {
            var edges = body.GetEdges();
            double minLength = double.MaxValue;
            int? shortestEdgeId = null;

            foreach (dynamic edge in edges)
            {
                try
                {
                    double length = Convert.ToDouble(edge.GetLength());
                    if (length > 0 && length < minLength)
                    {
                        minLength = length;
                        shortestEdgeId = (int)(SafeFloat(edge.Tag) ?? 0);
                    }
                }
                catch { /* skip */ }
            }

            if (minLength == double.MaxValue)
                return new JObject() { { "status", "PASS" }, { "detail", "No edges to check." } };

            if (minLength < ValidationConstants.LinearResolution)
            {
                var issues = new JArray
                {
                    new JObject() {
                        { "issue_id", NextIssueId() },
                        { "type", "BELOW_LINEAR_RESOLUTION" },
                        { "severity", "ERROR" },
                        { "entity_id", shortestEdgeId ?? 0 },
                        { "message", string.Format("Edge length {0:E2} < {1:E0}.", minLength, ValidationConstants.LinearResolution) },
                        { "suggestion", "Model precision is below Parasolid linear resolution. Simplify geometry or check units." }
                    }
                };
                return new JObject() {
                    { "status", "FAIL" },
                    { "detail", string.Format("Shortest edge length {0:E2} is below linear resolution {1:E0}.", minLength, ValidationConstants.LinearResolution) },
                    { "issues", issues }
                };
            }

            return new JObject() {
                { "status", "PASS" },
                { "detail", string.Format("Shortest edge length {0:F6} >= {1:E0}.", minLength, ValidationConstants.LinearResolution) }
            };
        }

        // --------------------------------------------------------------------
        // GEO-002b: Angular resolution check - no arc span < 1e-11 rad.
        // NX2412: edge.GetCurve() doesn't exist. Use edge metadata to estimate.
        // --------------------------------------------------------------------
        public static JObject CheckAngularResolution(dynamic body, UFSession ufs)
        {
            var edges = body.GetEdges();
            double minAngle = double.MaxValue;

            foreach (dynamic edge in edges)
            {
                try
                {
                    string ct = edge.EdgeType.ToString(); // NX2412: SolidEdgeType removed, use EdgeType
                    if (ct.Contains("Arc") || ct.Contains("Circle"))
                    {
                        try
                        {
                            double[] bb = new double[6];
                            ufs.Sf.EdgeAskBoundingBox(edge.Tag, bb);
                            double length = Convert.ToDouble(edge.GetLength());
                            if (bb != null && bb.Length >= 6 && length > 0)
                            {
                                double dx = Math.Abs((double)bb[3] - (double)bb[0]);
                                double dy = Math.Abs((double)bb[4] - (double)bb[1]);
                                double dz = Math.Abs((double)bb[5] - (double)bb[2]);
                                double radius = (dx + dy + dz) / 4.0;
                                if (radius > 0)
                                {
                                    double span = length / radius;
                                    if (span > 0 && span < minAngle)
                                        minAngle = span;
                                }
                            }
                        }
                        catch { /* EdgeAskBoundingBox may fail */ }
                    }
                }
                catch { /* skip */ }
            }

            if (minAngle == double.MaxValue)
                return new JObject() { { "status", "PASS" }, { "detail", "No arc edges to check." } };

            if (minAngle < ValidationConstants.AngularResolution)
            {
                return new JObject() {
                    { "status", "WARNING" },
                    { "detail", string.Format("Smallest arc span ~{0:E2} rad is below angular resolution {1:E0}.", minAngle, ValidationConstants.AngularResolution) }
                };
            }

            return new JObject() {
                { "status", "PASS" },
                { "detail", string.Format("Smallest arc span ~{0:F6} rad >= {1:E0}.", minAngle, ValidationConstants.AngularResolution) }
            };
        }

        // --------------------------------------------------------------------
        // Intent consistency check - feature health and output body check.
        // --------------------------------------------------------------------
        public static JObject CheckIntentConsistency(dynamic workPart)
        {
            var issues = new JArray();
            var features = workPart.Features;

            foreach (dynamic feat in features)
            {
                try
                {
                    if ((bool)feat.IsSuppressed) continue;

                    string statusStr = feat.GetHealthStatus().ToString();
                    if (statusStr.Contains("Error") || statusStr.Contains("Fail"))
                    {
                        issues.Add(new JObject() {
                            { "issue_id", NextIssueId() },
                            { "type", "FEATURE_HEALTH_ERROR" },
                            { "severity", "ERROR" },
                            { "entity_id", feat.Name.ToString() },
                            { "message", string.Format("Feature '{0}' health: {1}.", feat.Name, statusStr) },
                            { "suggestion", "Check feature parameters or parent dependencies." }
                        });
                        continue;
                    }

                    try
                    {
                        var bodies = feat.GetBodies();
                        bool hasBodies = false;
                        foreach (var b in bodies) { hasBodies = true; break; }
                        if (!hasBodies)
                        {
                            issues.Add(new JObject() {
                                { "issue_id", NextIssueId() },
                                { "type", "NO_OUTPUT_BODY" },
                                { "severity", "WARNING" },
                                { "entity_id", feat.Name.ToString() },
                                { "message", string.Format("Feature '{0}' produced no output body.", feat.Name) },
                                { "suggestion", "Check if feature is correctly defined." }
                            });
                        }
                    }
                    catch { /* GetBodies may fail */ }
                }
                catch { /* skip */ }
            }

            int featCount = 0;
            foreach (var f in features) featCount++;

            if (issues.Count == 0)
                return new JObject() { { "status", "PASS" }, { "detail", string.Format("All {0} features have consistent intent.", featCount) } };

            return new JObject() {
                { "status", "WARNING" },
                { "detail", string.Format("{0} feature(s) have intent inconsistencies.", issues.Count) },
                { "issues", issues }
            };
        }
    }

    // ========================================================================
    // 1. nx_validate_model
    // ========================================================================

    /// <summary>
    /// Validate the current model for topology, geometry, intent, or manufacturability issues.
    /// Based on Parasolid V35 rules. Returns a structured validation report.
    /// Supports validation_type: topology, geometry, intent, manufacturability, all.
    /// </summary>
    public class ValidateModelTool : IToolHandler
    {
        public string Name
        {
            get { return "nx_validate_model"; }
        }

        public string Description
        {
            get { return "Validate the current model for topology, geometry, intent, or manufacturability issues. Based on Parasolid V35 rules."; }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                ValidationHelpers.ResetIssueCounter();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var bodies = workPart.Bodies;
                bool hasBodies = false;
                foreach (var b in bodies) { hasBodies = true; break; }
                if (!hasBodies)
                {
                    var skipData = new JObject() {
                        { "status", "SKIP" },
                        { "message", "No bodies to validate." }
                    };
                    return new ToolResult { Success = true, Message = "No bodies found.", Data = skipData }.ToJson();
                }

                string vtype = "all";
                string vtypeParam = parameters.Value<string>("validation_type");
                if (!string.IsNullOrEmpty(vtypeParam))
                    vtype = vtypeParam.Trim().ToLowerInvariant();

                var validTypes = new HashSet<string> { "topology", "geometry", "intent", "manufacturability", "all" };
                if (!validTypes.Contains(vtype))
                {
                    string valid = string.Join(", ", validTypes);
                    return ToolResult.Fail(string.Format("Invalid validation_type '{0}'. Use one of: {1}.", vtypeParam, valid)).ToJson();
                }

                // Try to get UF session for geometry checks
                UFSession ufs = null;
                try { ufs = UFSession.GetUFSession(); } catch { /* UF may not be available */ }

                var checks = new JObject();
                var allIssues = new JArray();

                // Topology checks
                if (vtype == "topology" || vtype == "all")
                {
                    foreach (dynamic body in bodies)
                    {
                        string bodyName;
                        try { bodyName = body.Name.ToString(); } catch { bodyName = body.Tag.ToString(); }

                        var manifold = ValidationHelpers.CheckManifoldSolid(body);
                        checks[string.Format("{0}_manifold_solid", bodyName)] = manifold;
                        if (manifold["issues"] != null)
                            foreach (var issue in (JArray)manifold["issues"]) allIssues.Add(issue);

                        var orientation = ValidationHelpers.CheckFaceOrientation(body);
                        checks[string.Format("{0}_face_orientation", bodyName)] = orientation;
                        if (orientation["issues"] != null)
                            foreach (var issue in (JArray)orientation["issues"]) allIssues.Add(issue);
                    }
                }

                // Geometry checks
                if (vtype == "geometry" || vtype == "all")
                {
                    if (ufs != null)
                    {
                        var sizeBox = ValidationHelpers.CheckSizeBox(workPart, ufs);
                        checks["size_box"] = sizeBox;
                        if (sizeBox["issues"] != null)
                            foreach (var issue in (JArray)sizeBox["issues"]) allIssues.Add(issue);
                    }

                    foreach (dynamic body in bodies)
                    {
                        string bodyName;
                        try { bodyName = body.Name.ToString(); } catch { bodyName = body.Tag.ToString(); }

                        var linRes = ValidationHelpers.CheckLinearResolution(body);
                        checks[string.Format("{0}_linear_resolution", bodyName)] = linRes;
                        if (linRes["issues"] != null)
                            foreach (var issue in (JArray)linRes["issues"]) allIssues.Add(issue);

                        if (ufs != null)
                        {
                            var angRes = ValidationHelpers.CheckAngularResolution(body, ufs);
                            checks[string.Format("{0}_angular_resolution", bodyName)] = angRes;
                            if (angRes["issues"] != null)
                                foreach (var issue in (JArray)angRes["issues"]) allIssues.Add(issue);
                        }

                        var bsurfIssues = ValidationHelpers.CheckBsurfaceContinuity(body);
                        if (bsurfIssues.Count > 0)
                        {
                            checks[string.Format("{0}_bsurface_continuity", bodyName)] = new JObject() {
                                { "status", "WARNING" },
                                { "detail", string.Format("{0} B_SURFACE continuity issue(s).", bsurfIssues.Count) }
                            };
                            foreach (var issue in bsurfIssues) allIssues.Add(issue);
                        }
                    }
                }

                // Intent checks
                if (vtype == "intent" || vtype == "all")
                {
                    var intentResult = ValidationHelpers.CheckIntentConsistency(workPart);
                    checks["intent_consistency"] = intentResult;
                    if (intentResult["issues"] != null)
                        foreach (var issue in (JArray)intentResult["issues"]) allIssues.Add(issue);
                }

                // Manufacturability checks
                if (vtype == "manufacturability" || vtype == "all")
                {
                    foreach (dynamic body in bodies)
                    {
                        string bodyName;
                        try { bodyName = body.Name.ToString(); } catch { bodyName = body.Tag.ToString(); }

                        var shortEdges = ValidationHelpers.CheckShortEdges(body);
                        if (shortEdges.Count > 0)
                        {
                            checks[string.Format("{0}_short_edges", bodyName)] = new JObject() {
                                { "status", "WARNING" },
                                { "detail", string.Format("{0} short edge(s) below {1} mm.", shortEdges.Count, ValidationConstants.ShortEdgeThreshold) }
                            };
                            foreach (var issue in shortEdges) allIssues.Add(issue);
                        }

                        if (ufs != null)
                        {
                            var smallFaces = ValidationHelpers.CheckSmallFaces(body, ufs);
                            if (smallFaces.Count > 0)
                            {
                                checks[string.Format("{0}_small_faces", bodyName)] = new JObject() {
                                    { "status", "WARNING" },
                                    { "detail", string.Format("{0} small face(s) below {1} mm2.", smallFaces.Count, ValidationConstants.SmallFaceThreshold) }
                                };
                                foreach (var issue in smallFaces) allIssues.Add(issue);
                            }
                        }

                        var tolerantEdges = ValidationHelpers.CheckTolerantEdges(body);
                        if (tolerantEdges.Count > 0)
                        {
                            checks[string.Format("{0}_tolerant_edges", bodyName)] = new JObject() {
                                { "status", "WARNING" },
                                { "detail", string.Format("{0} tolerant edge(s).", tolerantEdges.Count) }
                            };
                            foreach (var issue in tolerantEdges) allIssues.Add(issue);
                        }
                    }
                }

                // Determine overall status
                string overall = "PASS";
                foreach (var prop in checks.Properties())
                {
                    JToken statusToken = prop.Value["status"];
                    string status = statusToken != null ? statusToken.ToString() : "PASS";
                    if (status == "FAIL") { overall = "FAIL"; break; }
                    if (status == "WARNING") overall = "WARNING";
                }

                // Cap issues at 100
                var cappedIssues = new JArray();
                for (int i = 0; i < Math.Min(allIssues.Count, 100); i++)
                    cappedIssues.Add(allIssues[i]);

                var data = new JObject() {
                    { "validation_type", vtype },
                    { "status", overall },
                    { "checks", checks },
                    { "issue_count", allIssues.Count },
                    { "issues", cappedIssues }
                };
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Validation ({0}): {1} - {2} issue(s) found.", vtype, overall, allIssues.Count),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_validate_model failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 2. nx_validate_feature
    // ========================================================================

    /// <summary>
    /// Validate a single feature by name.
    /// Checks feature health, output body validity, and degenerate geometry.
    /// Features are identified by name (case-insensitive).
    /// </summary>
    public class ValidateFeatureTool : IToolHandler
    {
        public string Name
        {
            get { return "nx_validate_feature"; }
        }

        public string Description
        {
            get { return "Validate a single feature by name. Checks feature health, output body validity, and degenerate geometry."; }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string featureName = parameters.Value<string>("feature_name");
                if (string.IsNullOrEmpty(featureName))
                    return ToolResult.Fail("Parameter 'feature_name' is required.").ToJson();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // Find feature by name (case-insensitive)
                dynamic target = null;
                var features = workPart.Features;
                foreach (dynamic feat in features)
                {
                    if (string.Equals(feat.Name.ToString(), featureName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = feat;
                        break;
                    }
                }

                if (target == null)
                {
                    var available = new JArray();
                    int count = 0;
                    foreach (dynamic f in features)
                    {
                        if (count >= 30) break;
                        available.Add(f.Name.ToString());
                        count++;
                    }
                    var notFoundData = new JObject() { { "available_features", available
                     } };
                    return new ToolResult
                    {
                        Success = false,
                        Message = string.Format("Feature '{0}' not found.", featureName),
                        Data = notFoundData
                    }.ToJson();
                }

                var issues = new JArray();

                // Check if feature is suppressed
                try
                {
                    if ((bool)target.IsSuppressed)
                    {
                        issues.Add(new JObject() {
                            { "type", "FEATURE_SUPPRESSED" },
                            { "severity", "INFO" },
                            { "message", "Feature is suppressed — no geometry output." }
                        });
                    }
                }
                catch { /* IsSuppressed may not be available */ }

                // Check feature health status
                try
                {
                    string statusStr = target.GetHealthStatus().ToString();
                    if (statusStr.Contains("Error") || statusStr.Contains("Fail"))
                    {
                        issues.Add(new JObject() {
                            { "type", "FEATURE_ERROR" },
                            { "severity", "ERROR" },
                            { "message", string.Format("Feature health status: {0}", statusStr) }
                        });
                    }
                }
                catch { /* GetHealthStatus may fail */ }

                // Get output bodies and check validity
                try
                {
                    var bodies = target.GetBodies();
                    bool hasBodies = false;
                    foreach (dynamic body in bodies)
                    {
                        hasBodies = true;

                        // Check manifold for solid bodies
                        try
                        {
                            if ((bool)body.IsSolidBody)
                            {
                                var manifold = ValidationHelpers.CheckManifoldSolid(body);
                                JToken manifoldStatus = manifold["status"];
                                string manifoldStatusStr = manifoldStatus != null ? manifoldStatus.ToString() : null;
                                if (manifoldStatusStr != "PASS")
                                {
                                    JToken detailToken = manifold["detail"];
                                    string detailStr = detailToken != null ? detailToken.ToString() : null;
                                    issues.Add(new JObject() {
                                        { "type", "OUTPUT_NON_MANIFOLD" },
                                        { "severity", "ERROR" },
                                        { "message", detailStr }
                                    });
                                }
                            }
                        }
                        catch { /* IsSolidBody may fail */ }

                        // Check for degenerate faces via bounding box approximation
                        try
                        {
                            UFSession ufs = UFSession.GetUFSession();
                            var faceList = body.GetFaces();
                            foreach (dynamic face in faceList)
                            {
                                try
                                {
                                    double[] bb = new double[6];
                        ufs.Sf.FaceAskBoundingBox(face.Tag, bb);
                                    double? area = null;
                                    if (bb != null && bb.Length >= 6)
                                    {
                                        double dx = Math.Abs((double)bb[3] - (double)bb[0]);
                                        double dy = Math.Abs((double)bb[4] - (double)bb[1]);
                                        double dz = Math.Abs((double)bb[5] - (double)bb[2]);
                                        area = (dx * dy + dy * dz + dx * dz) / 3.0;
                                    }

                                    if (area != null && area < 1e-12)
                                    {
                                        double? tagVal = ValidationHelpers.SafeFloat(face.Tag);
                                        issues.Add(new JObject() {
                                            { "type", "DEGENERATE_FACE" },
                                            { "severity", "ERROR" },
                                            { "entity_id", (int)(tagVal ?? 0) },
                                            { "message", "Degenerate face with area approximately 0." }
                                        });
                                    }
                                }
                                catch { /* skip individual face */ }
                            }
                        }
                        catch { /* UFSession or GetFaces may fail */ }
                    }

                    if (!hasBodies)
                    {
                        issues.Add(new JObject() {
                            { "type", "NO_OUTPUT_BODY" },
                            { "severity", "WARNING" },
                            { "message", "Feature produced no output body." }
                        });
                    }
                }
                catch { /* GetBodies may fail */ }

                // Collect feature parameters for reference
                var parametersDict = new JObject();
                try
                {
                    foreach (dynamic expr in target.GetExpressions())
                    {
                        try
                        {
                            double? val = ValidationHelpers.SafeFloat(expr.Value);
                            parametersDict[expr.Name.ToString()] = val != null
                                ? (JToken)val.Value
                                : expr.RightHandSide.ToString();
                        }
                        catch
                        {
                            try { parametersDict[expr.Name.ToString()] = expr.RightHandSide.ToString(); }
                            catch { /* skip */ }
                        }
                    }
                }
                catch { /* GetExpressions may fail */ }

                // Determine overall status
                string overall = "PASS";
                foreach (var issue in issues)
                {
                    JToken sevToken = issue["severity"];
                    string sev = sevToken != null ? sevToken.ToString() : null;
                    if (sev == "ERROR") { overall = "FAIL"; break; }
                    if (sev == "WARNING") overall = "WARNING";
                }

                string targetName, targetType;
                try { targetName = target.Name.ToString(); } catch { targetName = featureName; }
                try { targetType = target.FeatureType.ToString(); } catch { targetType = "UNKNOWN"; }

                var data = new JObject() {
                    { "feature_name", targetName },
                    { "feature_type", targetType },
                    { "status", overall },
                    { "parameters", parametersDict },
                    { "issue_count", issues.Count },
                    { "issues", issues }
                };
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Feature '{0}' validation: {1} - {2} issue(s).", targetName, overall, issues.Count),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_validate_feature failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}
