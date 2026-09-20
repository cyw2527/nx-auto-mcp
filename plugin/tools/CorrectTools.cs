using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;

namespace NxMcpPlugin.Tools.Correct
{
    // ========================================================================
    // Correction Helpers
    // ========================================================================

    /// <summary>
    /// Shared helpers for correction tools.
    /// </summary>
    internal static class CorrectionHelpers
    {
        /// <summary>Safely convert a value to double, returning null on failure.</summary>
        public static double? SafeFloat(object val)
        {
            try { return Math.Round(Convert.ToDouble(val), 6); }
            catch { return null; }
        }

        /// <summary>Find a face by its tag across all bodies.</summary>
        public static dynamic ResolveFace(dynamic workPart, int faceId)
        {
            foreach (dynamic body in workPart.Bodies)
            {
                foreach (dynamic face in body.GetFaces())
                {
                    double? tag = SafeFloat(face.Tag);
                    if (tag != null && (int)tag.Value == faceId)
                        return face;
                }
            }
            return null;
        }

        /// <summary>Find an edge by its tag across all bodies.</summary>
        public static dynamic ResolveEdge(dynamic workPart, int edgeId)
        {
            foreach (dynamic body in workPart.Bodies)
            {
                foreach (dynamic edge in body.GetEdges())
                {
                    double? tag = SafeFloat(edge.Tag);
                    if (tag != null && (int)tag.Value == edgeId)
                        return edge;
                }
            }
            return null;
        }

        /// <summary>Find a feature by name (case-insensitive).</summary>
        public static dynamic ResolveFeature(dynamic workPart, string featureName)
        {
            foreach (dynamic feat in workPart.Features)
            {
                if (string.Equals(feat.Name.ToString(), featureName, StringComparison.OrdinalIgnoreCase))
                    return feat;
            }
            return null;
        }
    }

    // ========================================================================
    // 1. nx_correct_face_geometry
    // ========================================================================

    /// <summary>
    /// [未实现 — NX2412 API 不可用] Correct a face's surface geometry by replacing it with a target surface type.
    /// Useful for fixing B_SURFACE issues by replacing with PLANE, CYLINDER, etc.
    /// Based on Parasolid V35 schema definitions.
    ///
    /// NX2412 Limitation: Part.Surfaces and UF.Modl.CreatePlane don't exist.
    /// CreateReplaceFaceBuilder also fails. This tool is not available in NX2412.
    /// Use manual face replacement in NX GUI instead.
    /// </summary>
    public class CorrectFaceGeometryTool : IToolHandler
    {
        public string Name { get { return "nx_correct_face_geometry"; } }
        public string Description { get { return "[未实现] 修正面的曲面类型（B_SURFACE → PLANE/CYLINDER 等）。NX2412 API 不可用，请在 NX GUI 中手动操作。"; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            // NX2412: Part.Surfaces, UF.Modl.CreatePlane, and CreateReplaceFaceBuilder all fail.
            return new ToolResult
            {
                Success = false,
                Message = "nx_correct_face_geometry is not available in NX2412.",
                Data = new JObject() {
                    { "error_code", "NX_NOT_AVAILABLE" },
                    { "suggestion", "NX2412 API limitations: Part.Surfaces, UF.Modl.CreatePlane, and CreateReplaceFaceBuilder all fail. Use manual face replacement in NX GUI." }
                }
            }.ToJson();
        }
    }

    // ========================================================================
    // 2. nx_rebuild_feature
    // ========================================================================

    /// <summary>
    /// Rebuild a feature with new parameters. Updates expression values on the existing feature.
    /// Preserves design intent by transferring unchanged parameters.
    /// NX2412: Features.DeleteFeature() and Features.Update() don't exist.
    /// Use expression update only - NX automatically updates the model.
    /// </summary>
    /// Parameters:
    ///   new_parameters (string, optional) - new_parameters parameter.
    ///
    public class RebuildFeatureTool : IToolHandler
    {
        public string Name { get { return "nx_rebuild_feature"; } }
        public string Description { get { return "Rebuild a feature with new parameters. Updates expression values to modify the feature."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string featureName = parameters.Value<string>("feature_name");
                if (string.IsNullOrEmpty(featureName))
                    return ToolResult.Fail("Parameter 'feature_name' is required.").ToJson();

                JObject newParameters = parameters["new_parameters"] as JObject;
                if (newParameters == null || newParameters.Count == 0)
                    return ToolResult.Fail("Parameter 'new_parameters' is required (key-value pairs of parameter names to new values).").ToJson();

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                dynamic target = CorrectionHelpers.ResolveFeature(workPart, featureName);
                if (target == null)
                {
                    var available = new JArray();
                    int count = 0;
                    foreach (dynamic f in workPart.Features)
                    {
                        if (count >= 30) break;
                        available.Add(f.Name.ToString());
                        count++;
                    }
                    var notFoundData = new JObject() { { "available_features", available } };
                    return new ToolResult
                    {
                        Success = false,
                        Message = string.Format("Feature '{0}' not found.", featureName),
                        Data = notFoundData
                    }.ToJson();
                }

                // Capture original parameters for intent preservation
                var originalParams = new JObject();
                var allExprs = new List<dynamic>();
                try
                {
                    foreach (dynamic expr in target.GetExpressions())
                    {
                        try
                        {
                            originalParams[expr.Name.ToString()] = expr.RightHandSide.ToString();
                            allExprs.Add(expr);
                        }
                        catch { }
                    }
                }
                catch { }

                string featureType = "";
                try { featureType = target.FeatureType.ToString(); } catch { }

                // NX2412: GetExpression(param_name) doesn't exist. Find by name from list.
                var updatedParams = new JObject();

                foreach (var prop in newParameters.Properties())
                {
                    string paramName = prop.Name;
                    string newValue = prop.Value.ToString();

                    try
                    {
                        // Find expression by name from the list
                        dynamic targetExpr = null;
                        foreach (dynamic expr in allExprs)
                        {
                            if (string.Equals(expr.Name.ToString(), paramName, StringComparison.Ordinal))
                            {
                                targetExpr = expr;
                                break;
                            }
                        }

                        if (targetExpr == null)
                        {
                            var availableParams = new JArray();
                            foreach (var k in originalParams.Properties())
                                availableParams.Add(k.Name);

                            return new ToolResult
                            {
                                Success = false,
                                Message = string.Format("Parameter '{0}' not found.", paramName),
                                Data = new JObject() { { "available_parameters", availableParams } }
                            }.ToJson();
                        }

                        targetExpr.SetFormula(newValue);
                        updatedParams[paramName] = newValue;
                    }
                    catch (Exception paramEx)
                    {
                        var availableParams = new JArray();
                        foreach (var k in originalParams.Properties())
                            availableParams.Add(k.Name);

                        return new ToolResult
                        {
                            Success = false,
                            Message = string.Format("Cannot set parameter '{0}': {1}", paramName, paramEx.Message),
                            Data = new JObject() { { "original_parameters", availableParams } }
                        }.ToJson();
                    }
                }

                // Compute preserved parameters (those not in newParameters)
                var preservedParams = new JObject();
                foreach (var prop in originalParams.Properties())
                {
                    if (newParameters[prop.Name] == null)
                        preservedParams[prop.Name] = prop.Value;
                }

                var data = new JObject();
                data["feature_name"] = featureName;
                data["feature_type"] = featureType;
                data["strategy"] = "parameter_update";
                data["original_parameters"] = originalParams;
                data["updated"] = updatedParams;
                data["preserved_parameters"] = preservedParams;
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Updated '{0}' ({1}) with {2} parameter change(s).", featureName, featureType, updatedParams.Count),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_rebuild_feature failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 3. nx_rollback_to_mark
    // ========================================================================

    /// <summary>
    /// Rollback the model to a previous undo mark (pmark).
    /// Useful for 'undo-retry' correction strategies.
    /// NX2412: Session.GetUndoMarkCount() doesn't exist.
    /// Use Session.GetAllVisibleUndoMarks() and Session.UndoToMark() instead.
    /// </summary>
    public class RollbackToMarkTool : IToolHandler
    {
        public string Name { get { return "nx_rollback_to_mark"; } }
        public string Description { get { return "Rollback the model to a previous undo mark (pmark)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                int markId = parameters.Value<int>("mark_id");
                if (markId < 1)
                    return ToolResult.Fail("Parameter 'mark_id' must be >= 1.").ToJson();

                // NX2412: Use GetAllVisibleUndoMarks (no arguments)
                var allMarks = session.GetAllVisibleUndoMarks();
                if (allMarks == null)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Message = "No undo marks available.",
                        Data = new JObject() { { "error_code", "NX_NOT_FOUND" } }
                    }.ToJson();
                }

                int markCount = 0;
                var markList = new List<dynamic>();
                foreach (var mark in allMarks)
                {
                    markCount++;
                    markList.Add(mark);
                }

                if (markCount == 0)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Message = "No undo marks available.",
                        Data = new JObject() {
                            { "error_code", "NX_NOT_FOUND" },
                            { "suggestion", "Undo marks are created automatically by operation tools." }
                        }
                    }.ToJson();
                }

                if (markId < 1 || markId > markCount)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Message = string.Format("Mark ID {0} is out of range (1-{1}).", markId, markCount),
                        Data = new JObject() {
                            { "error_code", "NX_INVALID_PARAMS" },
                            { "suggestion", string.Format("Available marks: 1 to {0}.", markCount) }
                        }
                    }.ToJson();
                }

                // Get the target mark (1-based index)
                dynamic targetMark = markList[markId - 1];

                // Undo to the target mark
                // NX2412: UndoToMark(markId: int, markName: str)
                session.UndoToMark(targetMark.Id, targetMark.ToString());

                var data = new JObject();
                data["rolled_back_to"] = markId;
                data["mark_name"] = targetMark.ToString();
                data["available_marks"] = markCount;
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Rolled back to mark {0}.", markId),
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_rollback_to_mark failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // ========================================================================
    // 4. nx_apply_suggestion
    // ========================================================================

    /// <summary>
    /// Apply a correction suggestion from nx_validate_model.
    /// Automatically fixes issues like short edges, tolerant edges, small faces.
    /// NX2412: Most auto-fixes require manual intervention (edge.GetFeature/DeleteFeature don't exist).
    /// Returns guidance for each issue type.
    /// </summary>
    /// Parameters:
    ///   parameters (string, optional) - parameters parameter.
    ///
    public class ApplySuggestionTool : IToolHandler
    {
        public string Name { get { return "nx_apply_suggestion"; } }
        public string Description { get { return "Apply a correction suggestion from nx_validate_model. Fixes issues like short edges, tolerant edges, small faces."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string issueType = parameters.Value<string>("issue_type");
                if (string.IsNullOrEmpty(issueType))
                    return ToolResult.Fail("Parameter 'issue_type' is required.").ToJson();

                int entityId = parameters.Value<int>("entity_id");
                string action = parameters.Value<string>("action");
                if (string.IsNullOrEmpty(action))
                    return ToolResult.Fail("Parameter 'action' is required.").ToJson();

                JObject fixParams = parameters["parameters"] as JObject;

                string issue = issueType.Trim().ToUpperInvariant();
                string act = action.Trim().ToLowerInvariant();

                var validIssues = new HashSet<string> { "SHORT_EDGE", "TOLERANT_EDGE", "SMALL_FACE", "NON_MANIFOLD_EDGE" };
                if (!validIssues.Contains(issue))
                {
                    string valid = string.Join(", ", validIssues);
                    return ToolResult.Fail(string.Format("Invalid issue_type '{0}'. Supported types: {1}.", issueType, valid)).ToJson();
                }

                var validActions = new HashSet<string> { "accept", "ignore", "modify" };
                if (!validActions.Contains(act))
                {
                    string valid = string.Join(", ", validActions);
                    return ToolResult.Fail(string.Format("Invalid action '{0}'. Use one of: {1}.", action, valid)).ToJson();
                }

                if (act == "ignore")
                {
                    var ignoreData = new JObject();
                    ignoreData["issue_type"] = issue;
                    ignoreData["entity_id"] = entityId;
                    ignoreData["action"] = "ignored";
                    return new ToolResult
                    {
                        Success = true,
                        Message = string.Format("Ignored {0} on entity {1}.", issue, entityId),
                        Data = ignoreData
                    }.ToJson();
                }

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // Edge-based issues
                if (issue == "SHORT_EDGE" || issue == "TOLERANT_EDGE" || issue == "NON_MANIFOLD_EDGE")
                {
                    dynamic targetEdge = CorrectionHelpers.ResolveEdge(workPart, entityId);
                    if (targetEdge == null)
                    {
                        return ToolResult.Fail(string.Format("Edge with tag {0} not found.", entityId)).ToJson();
                    }

                    if (issue == "SHORT_EDGE" && act == "accept")
                    {
                        double mergeTol = 0.01;
                        if (fixParams != null && fixParams["merge_tolerance"] != null)
                            mergeTol = fixParams.Value<double>("merge_tolerance");

                        var data = new JObject();
                        data["issue_type"] = issue;
                        data["entity_id"] = entityId;
                        data["action"] = "accepted";
                        data["merge_tolerance"] = mergeTol;
                        data["note"] = "NX2412: Auto-fix not available (edge.GetFeature/DeleteFeature don't exist). Manual fix required.";
                        return new ToolResult
                        {
                            Success = true,
                            Message = string.Format("Short edge {0} acknowledged. Manual fix required - use nx_blend or simplify geometry.", entityId),
                            Data = data
                        }.ToJson();
                    }

                    if (issue == "TOLERANT_EDGE" && act == "accept")
                    {
                        var data = new JObject();
                        data["issue_type"] = issue;
                        data["entity_id"] = entityId;
                        data["action"] = "accepted";
                        data["note"] = "NX2412: Auto-fix not available (edge.GetFeature/DeleteFeature don't exist). Manual simplification required.";
                        return new ToolResult
                        {
                            Success = true,
                            Message = string.Format("Tolerant edge {0} acknowledged. Manual fix required - simplify parent blend/offset.", entityId),
                            Data = data
                        }.ToJson();
                    }

                    if (issue == "NON_MANIFOLD_EDGE" && act == "accept")
                    {
                        var data = new JObject();
                        data["issue_type"] = issue;
                        data["entity_id"] = entityId;
                        data["action"] = "accepted";
                        data["note"] = "Non-manifold edge detected - manual intervention required.";
                        return new ToolResult
                        {
                            Success = true,
                            Message = string.Format("Non-manifold edge {0} flagged. Check boolean operations or sew.", entityId),
                            Data = data
                        }.ToJson();
                    }
                }

                // Face-based issues
                if (issue == "SMALL_FACE")
                {
                    dynamic targetFace = CorrectionHelpers.ResolveFace(workPart, entityId);
                    if (targetFace == null)
                    {
                        return ToolResult.Fail(string.Format("Face with tag {0} not found.", entityId)).ToJson();
                    }

                    if (act == "accept")
                    {
                        var data = new JObject();
                        data["issue_type"] = issue;
                        data["entity_id"] = entityId;
                        data["action"] = "accepted";
                        data["note"] = "NX2412: Auto-fix not available (face.OwningFeature doesn't exist). Manual intervention required.";
                        return new ToolResult
                        {
                            Success = true,
                            Message = string.Format("Small face {0} acknowledged. Manual fix required - consider deleting or simplifying parent feature.", entityId),
                            Data = data
                        }.ToJson();
                    }
                }

                // Default response for unhandled combinations
                var defaultData = new JObject();
                defaultData["issue_type"] = issue;
                defaultData["entity_id"] = entityId;
                defaultData["action"] = act;
                defaultData["note"] = string.Format("No automatic fix available for {0} with action '{1}'.", issue, act);
                return new ToolResult
                {
                    Success = true,
                    Message = string.Format("Recorded {0} on entity {1} as '{2}'.", issue, entityId, act),
                    Data = defaultData
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_apply_suggestion failed: {0}", ex.Message)).ToJson();
            }
        }
    }

}
