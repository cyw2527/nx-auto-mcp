using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Rules
{
    /// <summary>
    /// Rule engine — validates the legality of intents
    ///
    /// Registers 15 core rules covering 9 scenarios.
    /// Rules are divided into Error (blocks execution) and Warning (allows but alerts) levels.
    /// </summary>
    public class RuleEngine
    {
        private List<RuleDefinition> _rules = new List<RuleDefinition>();
        private dynamic _session;

        public RuleEngine(dynamic session)
        {
            _session = session;

            // Primary path: load rule metadata from JSON config file
            // Note: cannot use AppDomain.CurrentDomain.BaseDirectory — the host is NX itself,
            // which points to NX's install directory (...\NXBIN\), neither writable nor containing
            // rules_config.json, making File.Exists always false and JSON config never loaded.
            // Use the directory where this assembly resides instead.
            string assemblyDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            // When assemblyDir is null (assembly has no disk location), skip path construction
            // and fall through to the hardcoded fallback below.
            string jsonPath = string.IsNullOrEmpty(assemblyDir)
                ? null
                : Path.Combine(assemblyDir, "rules", "rules_config.json");
            if (File.Exists(jsonPath))
            {
                LoadRulesFromJson(jsonPath);
            }
            else
            {
                // Fallback: register all rules from hardcoded C# definitions
                RegisterAllRules();
            }
        }

        // ================================================================
        // JSON config loading
        // ================================================================

        /// <summary>
        /// Load rule metadata from a JSON file (id, name, description, severity, intents, params).
        /// Actual check functions are still provided by C# code, mapped via the check_type field.
        /// </summary>
        public void LoadRulesFromJson(string jsonPath)
        {
            string json = File.ReadAllText(jsonPath);
            JObject root = JObject.Parse(json);

            JArray rulesArray = root["rules"] as JArray;
            if (rulesArray == null)
                return;

            // Build check_type -> C# check function mapping
            var checkFnMap = BuildCheckFunctionMap();

            foreach (JObject ruleObj in rulesArray)
            {
                string id = ruleObj["id"] != null ? ruleObj["id"].ToString() : "";
                string checkType = ruleObj["check_type"] != null ? ruleObj["check_type"].ToString() : "";

                // Parse severity
                RuleSeverity severity = RuleSeverity.Warning;
                string sevStr = ruleObj["severity"] != null ? ruleObj["severity"].ToString().ToLower() : "warning";
                if (sevStr == "error")
                    severity = RuleSeverity.Error;
                else if (sevStr == "info")
                    severity = RuleSeverity.Info;

                // Parse applies_to_intents
                List<string> intents = new List<string>();
                JArray intentsArray = ruleObj["applies_to_intents"] as JArray;
                if (intentsArray != null)
                {
                    foreach (JToken t in intentsArray)
                        intents.Add(t.ToString());
                }

                // Parse params
                Dictionary<string, object> ruleParams = new Dictionary<string, object>();
                JObject paramsObj = ruleObj["params"] as JObject;
                if (paramsObj != null)
                {
                    foreach (var prop in paramsObj.Properties())
                        ruleParams[prop.Name] = prop.Value.ToObject<object>();
                }

                // Find the corresponding C# check function
                Func<dynamic, dynamic, Protocol.RuleCheckResult> checkFn = null;
                if (checkFnMap.ContainsKey(checkType))
                    checkFn = checkFnMap[checkType];

                var rule = new RuleDefinition
                {
                    Id = id,
                    Name = ruleObj["name"] != null ? ruleObj["name"].ToString() : id,
                    Description = ruleObj["description"] != null ? ruleObj["description"].ToString() : "",
                    Severity = severity,
                    AppliesToIntents = intents,
                    CheckType = checkType,
                    Params = ruleParams,
                    CheckFn = checkFn
                };

                _rules.Add(rule);
            }
        }

        /// <summary>
        /// Build the mapping from check_type strings to C# check functions.
        /// Add new mappings here when adding new rule types.
        /// </summary>
        private Dictionary<string, Func<dynamic, dynamic, Protocol.RuleCheckResult>> BuildCheckFunctionMap()
        {
            return new Dictionary<string, Func<dynamic, dynamic, Protocol.RuleCheckResult>>
            {
                { "hole_edge_clearance", CheckHoleEdgeClearance },
                { "hole_wall_thickness", CheckHoleWallThickness },
                { "hole_to_hole_spacing", CheckHoleToHoleSpacing },
                { "hole_diameter_range", CheckHoleDiameter },
                { "hole_depth_ratio", CheckHoleDepthRatio },
                { "chamfer_wall_thickness", CheckChamferOnThinWall },
                { "chamfer_overlap", CheckChamferOverlap },
                { "fillet_vs_wall", CheckFilletVsWall },
                { "fillet_chain", CheckFilletChain },
                { "extrude_body_exists", CheckBodyExists },
                { "extrude_positive_distance", CheckExtrudeDistance },
                { "shell_min_thickness", CheckShellThickness },
                { "draft_angle_range", CheckDraftAngle },
                { "pattern_bounds", CheckPatternBounds },
                { "thread_hole_match", CheckThreadMatch }
            };
        }

        /// <summary>
        /// Validate an intent and return a summary result
        /// </summary>
        public RuleValidateResult Validate(string intent, dynamic executeParams)
        {
            var result = new RuleValidateResult { Accepted = true };

            // Map nx_* tool names to legacy create_* intent names (backward compatibility)
            string legacyIntent = intent;
            if (intent.StartsWith("nx_"))
            {
                var intentMap = new Dictionary<string, string>
                {
                    { "nx_hole", "create_hole" },
                    { "nx_chamfer", "create_chamfer" },
                    { "nx_blend", "create_fillet" },
                    { "nx_extrude", "create_extrude" },
                    { "nx_revolve", "create_revolve" },
                    { "nx_shell", "create_shell" },
                    { "nx_draft", "create_draft" },
                    { "nx_pattern", "create_pattern" },
                    { "nx_sweep", "create_extrude" },
                };
                if (intentMap.ContainsKey(intent))
                    legacyIntent = intentMap[intent];
            }

            var applicableRules = _rules.Where(r => r.AppliesToIntents.Contains(intent) || r.AppliesToIntents.Contains(legacyIntent)).ToList();

            foreach (var rule in applicableRules)
            {
                try
                {
                    var r = rule.CheckFn(executeParams, _session);
                    if (r.Severity == "error" && !r.Passed)
                    {
                        result.Accepted = false;
                        result.Blockers.Add(r.Message);
                        if (r.Suggestion != null)
                            result.Suggestions.Add(r.Suggestion);
                    }
                    else if (r.Severity == "warning" && !r.Passed)
                    {
                        result.Warnings.Add(r.Message);
                        if (r.Correction != null)
                            result.Corrections.Add(r.Correction.ToString());
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(string.Format("Rule {0} execution error: {1}", rule.Id, ex.Message));
                }
            }

            return result;
        }

        public List<RuleDefinition> GetRules() { return _rules; }

        // ================================================================
        // 15 rule registrations
        // ================================================================

        private void RegisterAllRules()
        {
            // --- Hole-related (5 rules) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-hole-edge-clearance",
                Name = "Hole Edge Clearance Check",
                Description = "Minimum distance from hole center to part edge >= 2x hole diameter",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_hole" },
                CheckFn = CheckHoleEdgeClearance
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-hole-wall-thickness",
                Name = "Hole Wall Thickness Check",
                Description = "Minimum wall thickness from hole to outer wall >= 1.5mm",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_hole" },
                CheckFn = CheckHoleWallThickness
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-hole-to-hole",
                Name = "Hole Spacing Check",
                Description = "Center distance between any two holes - (r1+r2) >= 2mm",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_hole", "create_pattern" },
                CheckFn = CheckHoleToHoleSpacing
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-hole-diameter-valid",
                Name = "Hole Diameter Validity",
                Description = "Hole diameter must be within a reasonable range (0.5mm - 500mm)",
                Severity = RuleSeverity.Warning,
                AppliesToIntents = new List<string> { "create_hole" },
                CheckFn = CheckHoleDiameter
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-hole-depth-ratio",
                Name = "Depth-to-Diameter Ratio Check",
                Description = "Hole depth / hole diameter <= 10 (exceeding this makes machining difficult)",
                Severity = RuleSeverity.Warning,
                AppliesToIntents = new List<string> { "create_hole" },
                CheckFn = CheckHoleDepthRatio
            });

            // --- Chamfer-related (2 rules) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-chamfer-on-thin-wall",
                Name = "Chamfer Wall Thickness Check",
                Description = "Chamfer distance must not exceed half the wall thickness",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_chamfer" },
                CheckFn = CheckChamferOnThinWall
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-chamfer-overlap",
                Name = "Chamfer Overlap Check",
                Description = "Chamfer projections on adjacent edges should not overlap",
                Severity = RuleSeverity.Warning,
                AppliesToIntents = new List<string> { "create_chamfer" },
                CheckFn = CheckChamferOverlap
            });

            // --- Fillet-related (2 rules) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-fillet-vs-wall",
                Name = "Fillet Wall Thickness Check",
                Description = "Fillet radius <= wall thickness * 0.8",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_fillet" },
                CheckFn = CheckFilletVsWall
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-fillet-chain",
                Name = "Fillet Chain Compatibility",
                Description = "Fillet radius < shortest adjacent edge / 3",
                Severity = RuleSeverity.Warning,
                AppliesToIntents = new List<string> { "create_fillet" },
                CheckFn = CheckFilletChain
            });

            // --- Extrude/cut (2 rules) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-extrude-body-exists",
                Name = "Body Existence Check",
                Description = "Target body must exist for subtract/intersect operations",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_extrude", "create_pocket" },
                CheckFn = CheckBodyExists
            });

            _rules.Add(new RuleDefinition
            {
                Id = "rule-extrude-positive-distance",
                Name = "Extrude Distance Check",
                Description = "Extrude distance must be positive",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_extrude" },
                CheckFn = CheckExtrudeDistance
            });

            // --- Shell (1 rule) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-shell-min-thickness",
                Name = "Minimum Shell Thickness Check",
                Description = "Shell thickness >= 0.5mm",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_shell" },
                CheckFn = CheckShellThickness
            });

            // --- Draft (1 rule) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-draft-angle",
                Name = "Draft Angle Check",
                Description = "Draft angle must be between 0 and 30 degrees",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_draft" },
                CheckFn = CheckDraftAngle
            });

            // --- Pattern (1 rule) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-pattern-bounds",
                Name = "Pattern Bounds Check",
                Description = "Pattern instances must be within the part bounding box",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_pattern" },
                CheckFn = CheckPatternBounds
            });

            // --- Thread (1 rule) ---
            _rules.Add(new RuleDefinition
            {
                Id = "rule-thread-hole-match",
                Name = "Thread Pilot Hole Match",
                Description = "Thread specification must match pilot hole diameter and pitch (GB/T 196-2003)",
                Severity = RuleSeverity.Error,
                AppliesToIntents = new List<string> { "create_thread" },
                CheckFn = CheckThreadMatch
            });
        }

        // ================================================================
        // Rule implementations — pure parameter validation logic, no NX session needed
        // NX-related geometry validation (e.g. Body.GetFaces()) needs to be supplemented on NX machines
        // ================================================================

        private Protocol.RuleCheckResult CheckHoleEdgeClearance(dynamic p, dynamic session)
        {
            // Hole diameter — nx_hole's actual parameter name is "diameter"; can't calculate min clearance without it
            double diameter;
            if (!TryGetParamValue(p, "diameter", out diameter))
                return SkipMissing("rule-hole-edge-clearance", "Hole Edge Clearance Check", "diameter", "hole edge clearance");

            if (diameter <= 0)
                return SkipUnusable("rule-hole-edge-clearance", "Hole Edge Clearance Check", "diameter", diameter, "hole edge clearance");

            // Edge clearance (the parameter that determines the conclusion) — not provided by the parameter pack,
            // fall back to the old constraint channel
            double clearance;
            bool haveClearance = TryGetParamValue(p, "edge_clearance", out clearance);
            if (!haveClearance)
            {
                double fromConstraint = GetConstraint(p, "edge_clearance", double.NaN);
                if (!double.IsNaN(fromConstraint))
                {
                    clearance = fromConstraint;
                    haveClearance = true;
                }
            }

            double minClearance = 2.0 * diameter;

            // Edge clearance not received -> skip, never treat 0 as "insufficient clearance"
            if (!haveClearance)
            {
                return SkipMissing("rule-hole-edge-clearance", "Hole Edge Clearance Check", "edge_clearance", "hole edge clearance",
                    string.Format("Hole diameter {0:F1}mm requires minimum clearance >= {1:F1}mm (2x diameter); clearance must be measured in NX or provided by the caller", diameter, minClearance));
            }

            // Core check: clearance >= 2x diameter
            if (clearance < minClearance)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-hole-edge-clearance",
                    RuleName = "Hole Edge Clearance Check",
                    Passed = false,
                    Message = string.Format("Hole edge clearance {0:F1}mm < 2x hole diameter {1:F1}mm (diameter {2:F1}mm), insufficient edge strength", clearance, minClearance, diameter),
                    Severity = "error",
                    Suggestion = string.Format("Recommended clearance >= {0:F1}mm (2x diameter), current value {1:F1}mm is too small", minClearance, clearance)
                };
            }

            // Sufficient clearance, check passed
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-hole-edge-clearance",
                RuleName = "Hole Edge Clearance Check",
                Passed = true,
                Message = string.Format("Hole edge clearance {0:F1}mm >= 2x hole diameter {1:F1}mm, safe", clearance, minClearance),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckHoleWallThickness(dynamic p, dynamic session)
        {
            // Minimum wall thickness requirement: 1.5mm (machining safety lower limit)
            double minWallThickness = 1.5;

            // Wall thickness is the sole basis for the conclusion — no nx_* tool provides wall_thickness
            double wallThickness;
            bool haveWall = TryGetParamValue(p, "wall_thickness", out wallThickness);
            if (!haveWall)
            {
                double fromConstraint = GetConstraint(p, "wall_thickness", double.NaN);
                if (!double.IsNaN(fromConstraint))
                {
                    wallThickness = fromConstraint;
                    haveWall = true;
                }
            }

            if (!haveWall)
            {
                return SkipMissing("rule-hole-wall-thickness", "Hole Wall Thickness Check", "wall_thickness", "hole wall thickness",
                    string.Format("Actual distance from hole wall to part outer wall needs to be measured in NX (Body.GetFaces() type calculation), parameter pack does not provide wall_thickness; requirement >= {0:F1}mm", minWallThickness));
            }

            // Core check: wall thickness >= 1.5mm
            if (wallThickness < minWallThickness)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-hole-wall-thickness",
                    RuleName = "Hole Wall Thickness Check",
                    Passed = false,
                    Message = string.Format("Hole wall thickness {0:F1}mm < minimum requirement {1:F1}mm, insufficient thickness, prone to deformation or cracking during machining", wallThickness, minWallThickness),
                    Severity = "error",
                    Suggestion = string.Format("Recommended wall thickness >= {0:F1}mm, current value {1:F1}mm is too thin. Consider reducing hole diameter or adjusting hole position", minWallThickness, wallThickness)
                };
            }

            // Wall thickness passes
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-hole-wall-thickness",
                RuleName = "Hole Wall Thickness Check",
                Passed = true,
                Message = string.Format("Hole wall thickness {0:F1}mm >= minimum requirement {1:F1}mm, acceptable", wallThickness, minWallThickness),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckHoleToHoleSpacing(dynamic p, dynamic session)
        {
            // Center distance is a necessary condition for the conclusion — no nx_* tool provides center_distance
            double centerDistance;
            bool haveCenterDistance = TryGetParamValue(p, "center_distance", out centerDistance);
            if (!haveCenterDistance)
            {
                double fromConstraint = GetConstraint(p, "center_distance", double.NaN);
                if (!double.IsNaN(fromConstraint))
                {
                    centerDistance = fromConstraint;
                    haveCenterDistance = true;
                }
            }

            // Hole diameter is only used to calculate minimum spacing (secondary input):
            // if available, use 2x the larger diameter; otherwise fall back to fixed 2.0mm minimum.
            // Note: the old diameter2 <- distance fallback has been removed — no tool's "distance" means "second hole diameter".
            double diameter1, diameter2;
            if (!TryGetParamValue(p, "diameter1", out diameter1))
                TryGetParamValue(p, "diameter", out diameter1);   // nx_hole's actual parameter name is "diameter"
            TryGetParamValue(p, "diameter2", out diameter2);

            // Calculate the larger diameter
            double maxDiameter = Math.Max(diameter1, diameter2);

            // Center distance not received -> skip, never treat 0 as "center distance too small"
            if (!haveCenterDistance)
            {
                return SkipMissing("rule-hole-to-hole", "Hole Spacing Check", "center_distance", "hole spacing",
                    maxDiameter > 0
                        ? string.Format("Center distance between two holes should be >= {0:F1}mm (2x larger diameter), but center distance must be measured in NX or provided by the caller", 2.0 * maxDiameter)
                        : "Center distance between two holes must be measured in NX or provided by the caller, parameter pack does not provide center_distance");
            }

            // When diameter parameters are not provided, use fixed minimum spacing 2.0mm
            if (maxDiameter <= 0)
            {
                if (centerDistance < 2.0)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-hole-to-hole",
                        RuleName = "Hole Spacing Check",
                        Passed = false,
                        Message = string.Format("Hole center distance {0:F1}mm < minimum safe spacing 2.0mm (hole diameter unknown, using fixed value)", centerDistance),
                        Severity = "error",
                        Suggestion = "Please provide diameter parameters for precise minimum spacing calculation, or ensure center distance >= 2.0mm"
                    };
                }
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-hole-to-hole",
                    RuleName = "Hole Spacing Check",
                    Passed = true,
                    Message = string.Format("Hole center distance {0:F1}mm >= minimum safe spacing 2.0mm (hole diameter unknown)", centerDistance),
                    Severity = "info"
                };
            }

            // Core check: center distance >= 2x larger diameter
            double minDistance = 2.0 * maxDiameter;
            if (centerDistance < minDistance)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-hole-to-hole",
                    RuleName = "Hole Spacing Check",
                    Passed = false,
                    Message = string.Format("Hole center distance {0:F1}mm < 2x larger diameter {1:F1}mm (diameters {2:F1}mm / {3:F1}mm), insufficient wall material", centerDistance, minDistance, diameter1, diameter2),
                    Severity = "error",
                    Suggestion = string.Format("Recommended center distance >= {0:F1}mm (2x {1:F1}mm), current value {2:F1}mm is too small. Increase spacing or reduce hole diameter", minDistance, maxDiameter, centerDistance)
                };
            }

            // Hole spacing passes
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-hole-to-hole",
                RuleName = "Hole Spacing Check",
                Passed = true,
                Message = string.Format("Hole center distance {0:F1}mm >= 2x larger diameter {1:F1}mm, safe", centerDistance, minDistance),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckHoleDiameter(dynamic p, dynamic session)
        {
            // Cannot claim "diameter 0mm out of range" when diameter was not received
            double dia;
            if (!TryGetParamValue(p, "diameter", out dia))
                return SkipMissing("rule-hole-diameter-valid", "Hole Diameter Validity", "diameter", "hole diameter validity");

            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-hole-diameter-valid",
                RuleName = "Hole Diameter Validity",
                Passed = dia >= 0.5 && dia <= 500,
                Message = dia >= 0.5 && dia <= 500 ? "" : string.Format("Hole diameter {0}mm exceeds reasonable range (0.5-500mm)", dia),
                Severity = "warning",
                Suggestion = dia < 0.5 ? "Hole diameter cannot be less than 0.5mm" : "Hole diameter cannot exceed 500mm, please verify"
            };
        }

        private Protocol.RuleCheckResult CheckHoleDepthRatio(dynamic p, dynamic session)
        {
            // Driving parameters: hole diameter (nx_hole.diameter) + hole depth (nx_hole.depth, old rule-side name depth_value)
            double dia;
            if (!TryGetParamValue(p, "diameter", out dia))
                return SkipMissing("rule-hole-depth-ratio", "Depth-to-Diameter Ratio Check", "diameter", "depth-to-diameter ratio");

            double depth;
            if (!TryGetParamValue(p, "depth_value", out depth))
                return SkipMissing("rule-hole-depth-ratio", "Depth-to-Diameter Ratio Check", "depth_value", "depth-to-diameter ratio");

            if (dia <= 0)
                return SkipUnusable("rule-hole-depth-ratio", "Depth-to-Diameter Ratio Check", "diameter", dia, "depth-to-diameter ratio");

            // Old implementation used "default 10 / 20" which always passed; now both must be actually received to calculate
            double ratio = depth / dia;
            if (ratio > 10)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-hole-depth-ratio",
                    RuleName = "Depth-to-Diameter Ratio Check",
                    Passed = false,
                    Message = string.Format("Depth-to-diameter ratio {0:F1} (depth {1:F1}mm / diameter {2:F1}mm) exceeds recommended value 10, difficult to machine", ratio, depth, dia),
                    Severity = "warning",
                    Suggestion = "Consider reducing hole depth or increasing hole diameter"
                };
            }
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-hole-depth-ratio",
                RuleName = "Depth-to-Diameter Ratio Check",
                Passed = true,
                Message = string.Format("Depth-to-diameter ratio {0:F1} (depth {1:F1}mm / diameter {2:F1}mm) does not exceed recommended value 10", ratio, depth, dia),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckChamferOnThinWall(dynamic p, dynamic session)
        {
            // Chamfer distance is a necessary condition for the conclusion; nx_chamfer's actual parameters are offset/offset2, not distance
            double chamferDistance;
            if (!TryGetParamValue(p, "distance", out chamferDistance))
                return SkipMissing("rule-chamfer-on-thin-wall", "Chamfer Wall Thickness Check", "distance", "chamfer wall thickness",
                    "Parameter pack does not provide chamfer distance (nx_chamfer uses offset / offset2)");

            if (chamferDistance <= 0)
                return SkipUnusable("rule-chamfer-on-thin-wall", "Chamfer Wall Thickness Check", "distance", chamferDistance, "chamfer wall thickness");

            // Wall thickness — no nx_* tool provides wall_thickness
            double wallThickness;
            bool haveWall = TryGetParamValue(p, "wall_thickness", out wallThickness);
            if (!haveWall)
            {
                double fromConstraint = GetConstraint(p, "wall_thickness", double.NaN);
                if (!double.IsNaN(fromConstraint))
                {
                    wallThickness = fromConstraint;
                    haveWall = true;
                }
            }

            // Wall thickness unknown -> skip (original implementation returned "wall thickness unknown" but still logged a non-alert warning,
            // corrected to skip semantics)
            if (!haveWall)
            {
                return SkipMissing("rule-chamfer-on-thin-wall", "Chamfer Wall Thickness Check", "wall_thickness", "chamfer wall thickness",
                    string.Format("Chamfer distance {0:F1}mm is known, but wall thickness needs to be measured in NX (Body.GetFaces() type calculation), cannot determine if on thin wall", chamferDistance));
            }

            // Chamfer distance > wall thickness * 0.5, strength risk
            double maxSafe = wallThickness * 0.5;
            if (chamferDistance > maxSafe)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-chamfer-on-thin-wall",
                    RuleName = "Chamfer Wall Thickness Check",
                    Passed = false,
                    Message = string.Format("Chamfer distance {0:F1}mm > half of wall thickness {1:F1}mm ({2:F1}mm), insufficient wall thickness", chamferDistance, wallThickness, maxSafe),
                    Severity = "error",
                    Suggestion = string.Format("Recommended chamfer distance <= {0:F1}mm (half of wall thickness)", maxSafe)
                };
            }

            // Chamfer is within safe range
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-chamfer-on-thin-wall",
                RuleName = "Chamfer Wall Thickness Check",
                Passed = true,
                Message = string.Format("Chamfer distance {0:F1}mm <= half of wall thickness {1:F1}mm, safe", chamferDistance, maxSafe),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckChamferOverlap(dynamic p, dynamic session)
        {
            // Chamfer distance is a necessary condition for the conclusion; nx_chamfer's actual parameters are offset/offset2, not distance
            double distance;
            if (!TryGetParamValue(p, "distance", out distance))
                return SkipMissing("rule-chamfer-overlap", "Chamfer Overlap Check", "distance", "chamfer overlap",
                    "Parameter pack does not provide chamfer distance (nx_chamfer uses offset / offset2)");

            if (distance <= 0)
                return SkipUnusable("rule-chamfer-overlap", "Chamfer Overlap Check", "distance", distance, "chamfer overlap");

            // Adjacent edge length — no nx_* tool provides adjacent_edge_length
            double adjacentEdgeLength;
            TryGetParamValue(p, "adjacent_edge_length", out adjacentEdgeLength);

            // When adjacent edge length parameter is available, check if chamfers would overlap
            // When chamfering both sides, total projection = 2 * distance; if > adjacent edge length, they overlap
            if (adjacentEdgeLength > 0)
            {
                double totalProjection = 2.0 * distance;
                if (totalProjection > adjacentEdgeLength)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-chamfer-overlap",
                        RuleName = "Chamfer Overlap Check",
                        Passed = false,
                        Message = string.Format("Both-side chamfer projection total {0:F1}mm (2x{1:F1}mm) > adjacent edge length {2:F1}mm, chamfers will overlap", totalProjection, distance, adjacentEdgeLength),
                        Severity = "warning",
                        Suggestion = string.Format("Recommended chamfer distance <= {0:F1}mm (half of adjacent edge length), or reduce chamfer to avoid overlap", adjacentEdgeLength / 2.0)
                    };
                }

                // Chamfer distance approaches half the edge length, issue a warning
                if (totalProjection > adjacentEdgeLength * 0.8)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-chamfer-overlap",
                        RuleName = "Chamfer Overlap Check",
                        Passed = true,
                        Message = string.Format("Both-side chamfer projection {0:F1}mm approaches 80% of adjacent edge length {1:F1}mm, risk exists", totalProjection, adjacentEdgeLength),
                        Severity = "warning",
                        Suggestion = string.Format("Chamfer distance {0:F1}mm approaches safe upper limit {1:F1}mm, consider reducing", distance, adjacentEdgeLength / 2.0)
                    };
                }

                // Safe pass
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-chamfer-overlap",
                    RuleName = "Chamfer Overlap Check",
                    Passed = true,
                    Message = string.Format("Chamfer projection {0:F1}mm < 80% of adjacent edge length {1:F1}mm, no overlap", totalProjection, adjacentEdgeLength),
                    Severity = "info"
                };
            }

            // Without adjacent edge parameter, use empirical check (chamfer > 10mm usually has overlap risk)
            if (distance > 10.0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-chamfer-overlap",
                    RuleName = "Chamfer Overlap Check",
                    Passed = false,
                    Message = string.Format("Chamfer distance {0:F1}mm is too large (> 10mm), may cause overlap with adjacent edge chamfers", distance),
                    Severity = "warning",
                    Suggestion = "Consider providing adjacent_edge_length parameter for precise check, or reduce chamfer distance to within 10mm"
                };
            }

            // Chamfer distance is within reasonable range
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-chamfer-overlap",
                RuleName = "Chamfer Overlap Check",
                Passed = true,
                Message = string.Format("Chamfer distance {0:F1}mm is within reasonable range (0-10mm)", distance),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckFilletVsWall(dynamic p, dynamic session)
        {
            // Fillet radius — nx_blend's actual parameter name is "radius"
            double radius;
            if (!TryGetParamValue(p, "radius", out radius))
                return SkipMissing("rule-fillet-vs-wall", "Fillet Wall Thickness Check", "radius", "fillet wall thickness");

            // Radius must be positive — this is a check on a "truly received" radius, not a baseless alarm
            if (radius <= 0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-fillet-vs-wall",
                    RuleName = "Fillet Wall Thickness Check",
                    Passed = false,
                    Message = string.Format("Fillet radius {0:F2}mm is invalid, must be positive", radius),
                    Severity = "error",
                    Suggestion = "Please provide a valid fillet radius (> 0mm)"
                };
            }

            // Wall thickness comparison cannot be done: no tool provides wall_thickness, and we won't fabricate wall thickness here.
            // This rule actually only completed "radius validity", report honestly and skip
            // (the old "wall thickness check (not yet run in NX)" was effectively a false claim of having done the check).
            return SkipMissing("rule-fillet-vs-wall", "Fillet Wall Thickness Check", "wall_thickness", "fillet wall thickness",
                string.Format("Fillet radius {0:F1}mm validated as positive; wall thickness needs to be measured in NX (Body.GetFaces() type calculation), parameter pack does not provide wall_thickness", radius));
        }

        private Protocol.RuleCheckResult CheckFilletChain(dynamic p, dynamic session)
        {
            // Fillet radius — nx_blend's actual parameter name is "radius"
            double radius;
            if (!TryGetParamValue(p, "radius", out radius))
                return SkipMissing("rule-fillet-chain", "Fillet Chain Compatibility", "radius", "fillet chain compatibility");

            // Fillet radius is 0 or negative, invalid (checking against a value that was actually received)
            if (radius <= 0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-fillet-chain",
                    RuleName = "Fillet Chain Compatibility",
                    Passed = false,
                    Message = string.Format("Fillet radius {0:F2}mm is invalid, must be positive", radius),
                    Severity = "error",
                    Suggestion = "Please provide a valid fillet radius (> 0mm)"
                };
            }

            // Adjacent edge length / adjacent fillet radius — no nx_* tool provides these two parameters (secondary input)
            double minAdjacentEdgeLength;
            TryGetParamValue(p, "min_adjacent_edge_length", out minAdjacentEdgeLength);
            double adjacentRadius;
            TryGetParamValue(p, "adjacent_radius", out adjacentRadius);

            // When adjacent edge length is available, check radius < min_adjacent_edge_length / 3
            if (minAdjacentEdgeLength > 0)
            {
                double maxRadius = minAdjacentEdgeLength / 3.0;
                if (radius > maxRadius)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-fillet-chain",
                        RuleName = "Fillet Chain Compatibility",
                        Passed = false,
                        Message = string.Format("Fillet radius {0:F1}mm > 1/3 of shortest adjacent edge {1:F1}mm ({2:F1}mm), cannot chain fillet", radius, minAdjacentEdgeLength, maxRadius),
                        Severity = "warning",
                        Suggestion = string.Format("Recommended fillet radius <= {0:F1}mm (1/3 of shortest adjacent edge), or increase adjacent edge length", maxRadius)
                    };
                }

                // Radius approaches upper limit, warn
                if (radius > maxRadius * 0.8)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-fillet-chain",
                        RuleName = "Fillet Chain Compatibility",
                        Passed = true,
                        Message = string.Format("Fillet radius {0:F1}mm approaches 1/3 upper limit of shortest adjacent edge {1:F1}mm ({2:F1}mm)", radius, minAdjacentEdgeLength, maxRadius),
                        Severity = "warning",
                        Suggestion = string.Format("Fillet radius approaches safe upper limit, consider reducing to within {0:F1}mm", maxRadius * 0.8)
                    };
                }

                // Safe pass
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-fillet-chain",
                    RuleName = "Fillet Chain Compatibility",
                    Passed = true,
                    Message = string.Format("Fillet radius {0:F1}mm <= 1/3 of shortest adjacent edge {1:F1}mm ({2:F1}mm), can chain fillet", radius, minAdjacentEdgeLength, maxRadius),
                    Severity = "info"
                };
            }

            // No adjacent edge length, fall back to radius ratio check
            if (adjacentRadius > 0)
            {
                double ratio = radius > adjacentRadius ? radius / adjacentRadius : adjacentRadius / radius;

                if (ratio > 3.0)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-fillet-chain",
                        RuleName = "Fillet Chain Compatibility",
                        Passed = false,
                        Message = string.Format("Adjacent fillet radius ratio {0:F1}:1 ({1:F1}mm vs {2:F1}mm) exceeds recommended value 3:1, poor transition", ratio, radius, adjacentRadius),
                        Severity = "warning",
                        Suggestion = "Recommended adjacent fillet radius ratio <= 3:1, consider adding a transition fillet"
                    };
                }

                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-fillet-chain",
                    RuleName = "Fillet Chain Compatibility",
                    Passed = true,
                    Message = string.Format("Adjacent fillet radius ratio {0:F1}:1 is within acceptable range (<= 3:1)", ratio),
                    Severity = "info"
                };
            }

            // Neither adjacent edge length nor adjacent radius available — criteria cannot be established, skip
            // (original implementation logged a warning-level "not passed", which was semantically incorrect)
            return SkipMissing("rule-fillet-chain", "Fillet Chain Compatibility", "min_adjacent_edge_length or adjacent_radius", "fillet chain compatibility",
                string.Format("Fillet radius {0:F1}mm is known; adjacent edge length / adjacent fillet radius need to be queried in NX, parameter pack does not provide them", radius));
        }

        private Protocol.RuleCheckResult CheckBodyExists(dynamic p, dynamic session)
        {
            // Parameter p is the nx_* tool's JObject parameter pack (see TcpServer.cs -> _rules.Validate(toolName, jObj)).
            // Note: JObject's dynamic member access (p.Targets) throws RuntimeBinderException when the key is missing,
            // rather than returning null, and a normal nx_extrude call doesn't include targets at all.
            // Therefore we use defensive reading — missing/unparseable values are treated as "not provided",
            // never letting this rule throw an exception (otherwise every nx_extrude call would get an extra
            // "rule execution error" warning).
            var targetTags = new List<int>();
            try
            {
                JObject paramObj = p as JObject;
                JArray targetsArray = paramObj != null ? paramObj["targets"] as JArray : null;
                if (targetsArray != null)
                {
                    foreach (JToken t in targetsArray)
                    {
                        // Compatible with both [123] and [{"tag": 123}] formats
                        JToken tagToken = t.Type == JTokenType.Object ? t["tag"] : t;
                        int tag;
                        if (tagToken != null && int.TryParse(tagToken.ToString(), out tag))
                            targetTags.Add(tag);
                    }
                }
            }
            catch { }

            // No target body provided (normal form for non-boolean ops like nx_extrude) -> skip check, not an error
            if (targetTags.Count == 0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-extrude-body-exists",
                    RuleName = "Body Existence Check",
                    Passed = true,
                    Message = "No target body (targets) provided, skipping body existence check",
                    Severity = "info"
                };
            }

            // Check if each target tag is valid (> 0 means it exists in NX)
            var invalidTags = targetTags.Where(t => t <= 0).ToList();
            if (invalidTags.Count > 0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-extrude-body-exists",
                    RuleName = "Body Existence Check",
                    Passed = false,
                    Message = string.Format("Invalid target body tags: {0}, do not exist in NX", string.Join(", ", invalidTags.Select(t => t.ToString()))),
                    Severity = "error",
                    Suggestion = "Please use valid NX body tags (tag > 0)"
                };
            }

            // All target bodies are valid
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-extrude-body-exists",
                RuleName = "Body Existence Check",
                Passed = true,
                Message = string.Format("Verified {0} target body tag(s) are valid", targetTags.Count),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckExtrudeDistance(dynamic p, dynamic session)
        {
            // Cannot say "current value: 0mm" when distance was not received — this was exactly why this rule
            // used to give a false alarm on every nx_extrude call
            double dist;
            if (!TryGetParamValue(p, "distance", out dist))
                return SkipMissing("rule-extrude-positive-distance", "Extrude Distance Check", "distance", "extrude distance");

            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-extrude-positive-distance",
                RuleName = "Extrude Distance Check",
                Passed = dist > 0,
                Message = dist <= 0 ? string.Format("Extrude distance must be positive, current value: {0}mm", dist) : "",
                Severity = dist <= 0 ? "error" : "info",
                Suggestion = dist <= 0 ? "Please provide a positive extrude distance" : null
            };
        }

        private Protocol.RuleCheckResult CheckShellThickness(dynamic p, dynamic session)
        {
            // Cannot say "shell thickness 0mm < minimum 0.5mm" when thickness was not received
            double thickness;
            if (!TryGetParamValue(p, "thickness", out thickness))
                return SkipMissing("rule-shell-min-thickness", "Minimum Shell Thickness Check", "thickness", "shell thickness");

            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-shell-min-thickness",
                RuleName = "Minimum Shell Thickness Check",
                Passed = thickness >= 0.5,
                Message = thickness < 0.5 ? string.Format("Shell thickness {0}mm < minimum 0.5mm", thickness) : "",
                Severity = thickness < 0.5 ? "error" : "info",
                Suggestion = thickness < 0.5 ? string.Format("Recommended minimum shell thickness 0.5mm, current value {0}mm is too thin", thickness) : null
            };
        }

        private Protocol.RuleCheckResult CheckDraftAngle(dynamic p, dynamic session)
        {
            // Cannot say "draft angle 0° out of range" when angle was not received
            double angle;
            if (!TryGetParamValue(p, "angle", out angle))
                return SkipMissing("rule-draft-angle", "Draft Angle Check", "angle", "draft angle");

            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-draft-angle",
                RuleName = "Draft Angle Check",
                Passed = angle > 0 && angle <= 30,
                Message = angle <= 0 || angle > 30 ? string.Format("Draft angle {0}° out of range (0-30°)", angle) : "",
                Severity = angle <= 0 || angle > 30 ? "error" : "info",
                Suggestion = angle <= 0 ? "Draft angle must be positive" : "Draft angle cannot exceed 30°"
            };
        }

        private Protocol.RuleCheckResult CheckPatternBounds(dynamic p, dynamic session)
        {
            // Driving parameters: instance count (nx_pattern.count, old rule-side name instance_count) + spacing (nx_pattern.spacing)
            double countValue;
            if (!TryGetParamValue(p, "instance_count", out countValue))
                return SkipMissing("rule-pattern-bounds", "Pattern Bounds Check", "instance_count", "pattern bounds");

            double spacing;
            if (!TryGetParamValue(p, "spacing", out spacing))
                return SkipMissing("rule-pattern-bounds", "Pattern Bounds Check", "spacing", "pattern bounds");

            int instanceCount = (int)countValue;

            // Bounding box size (secondary input) — no nx_* tool provides bbox_length / bbox_width
            double bboxLength;
            TryGetParamValue(p, "bbox_length", out bboxLength);
            double bboxWidth;
            TryGetParamValue(p, "bbox_width", out bboxWidth);

            // Instance count must be > 0
            if (instanceCount <= 0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-pattern-bounds",
                    RuleName = "Pattern Bounds Check",
                    Passed = false,
                    Message = string.Format("Pattern instance count {0} is invalid, must be > 0", instanceCount),
                    Severity = "error",
                    Suggestion = "Please provide a valid instance_count (> 0)"
                };
            }

            // Spacing must be > 0
            if (spacing <= 0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-pattern-bounds",
                    RuleName = "Pattern Bounds Check",
                    Passed = false,
                    Message = string.Format("Pattern spacing {0}mm is invalid, must be > 0", spacing),
                    Severity = "error",
                    Suggestion = "Please provide a valid spacing (> 0mm)"
                };
            }

            // Calculate total pattern span: (instance count - 1) * spacing
            double totalSpan = (instanceCount - 1) * spacing;

            // When bounding box info is available, verify pattern is within bounds
            if (bboxLength > 0 || bboxWidth > 0)
            {
                // Check length direction
                if (bboxLength > 0 && totalSpan > bboxLength)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-pattern-bounds",
                        RuleName = "Pattern Bounds Check",
                        Passed = false,
                        Message = string.Format("Pattern total span {0:F1}mm ({1} instances x {2:F1}mm spacing) > bounding box length {3:F1}mm, instances will exceed part boundary", totalSpan, instanceCount, spacing, bboxLength),
                        Severity = "error",
                        Suggestion = string.Format("Suggestion: reduce instance count to {0}, or reduce spacing to within {1:F1}mm", (int)(bboxLength / spacing) + 1, bboxLength / Math.Max(1, instanceCount - 1))
                    };
                }

                // Check width direction (if bbox_width is provided)
                if (bboxWidth > 0)
                {
                    // Read row count (2D pattern)
                    int rowCount = (int)GetParamValue(p, "row_count", 1);
                    double rowSpacing = GetParamValue(p, "row_spacing", spacing);
                    double totalSpanY = (rowCount - 1) * rowSpacing;

                    if (totalSpanY > bboxWidth)
                    {
                        return new Protocol.RuleCheckResult
                        {
                            RuleId = "rule-pattern-bounds",
                            RuleName = "Pattern Bounds Check",
                            Passed = false,
                            Message = string.Format("Pattern width direction span {0:F1}mm ({1} rows x {2:F1}mm row spacing) > bounding box width {3:F1}mm", totalSpanY, rowCount, rowSpacing, bboxWidth),
                            Severity = "error",
                            Suggestion = string.Format("Suggestion: reduce row count to {0}, or reduce row spacing to within {1:F1}mm", (int)(bboxWidth / rowSpacing) + 1, bboxWidth / Math.Max(1, rowCount - 1))
                        };
                    }
                }

                // Within bounding box range
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-pattern-bounds",
                    RuleName = "Pattern Bounds Check",
                    Passed = true,
                    Message = string.Format("Pattern total span {0:F1}mm is within bounding box range ({1} instances x {2:F1}mm)", totalSpan, instanceCount, spacing),
                    Severity = "info"
                };
            }

            // No bounding box info, use empirical check (span > 500mm usually exceeds common part sizes)
            if (totalSpan > 500.0)
            {
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-pattern-bounds",
                    RuleName = "Pattern Bounds Check",
                    Passed = false,
                    Message = string.Format("Pattern total span {0:F1}mm ({1} instances x {2:F1}mm spacing) may exceed part boundary", totalSpan, instanceCount, spacing),
                    Severity = "warning",
                    Suggestion = "Please provide bbox_length parameter for precise verification, or confirm total span is within part range"
                };
            }

            // Parameters are valid, span is within reasonable range
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-pattern-bounds",
                RuleName = "Pattern Bounds Check",
                Passed = true,
                Message = string.Format("Pattern parameters valid: {0} instances, spacing {1:F1}mm, total span {2:F1}mm", instanceCount, spacing, totalSpan),
                Severity = "info"
            };
        }

        private Protocol.RuleCheckResult CheckThreadMatch(dynamic p, dynamic session)
        {
            // Standard metric thread pilot hole (tap drill) lookup table (mm)
            // Format: thread nominal diameter -> standard pilot hole diameter
            // Source: GB/T 196-2003 General Purpose Metric Screw Threads — Basic Dimensions
            var threadTable = new Dictionary<double, double>
            {
                { 2.0, 1.6 },    // M2 x 0.4
                { 2.5, 2.05 },   // M2.5 x 0.45
                { 3.0, 2.5 },    // M3 x 0.5
                { 3.5, 2.9 },    // M3.5 x 0.6
                { 4.0, 3.3 },    // M4 x 0.7
                { 5.0, 4.2 },    // M5 x 0.8
                { 6.0, 5.0 },    // M6 x 1.0
                { 8.0, 6.8 },    // M8 x 1.25
                { 10.0, 8.5 },   // M10 x 1.5
                { 12.0, 10.2 },  // M12 x 1.75
                { 14.0, 11.9 },  // M14 x 2.0
                { 16.0, 13.9 },  // M16 x 2.0
                { 18.0, 15.4 },  // M18 x 2.5
                { 20.0, 17.4 },  // M20 x 2.5
                { 24.0, 20.9 },  // M24 x 3.0
                { 30.0, 26.0 },  // M30 x 3.5
                { 36.0, 31.5 }   // M36 x 4.0
            };

            // Standard pitch lookup table (mm)
            var pitchTable = new Dictionary<double, double>
            {
                { 2.0, 0.4 },
                { 2.5, 0.45 },
                { 3.0, 0.5 },
                { 3.5, 0.6 },
                { 4.0, 0.7 },
                { 5.0, 0.8 },
                { 6.0, 1.0 },
                { 8.0, 1.25 },
                { 10.0, 1.5 },
                { 12.0, 1.75 },
                { 14.0, 2.0 },
                { 16.0, 2.0 },
                { 18.0, 2.5 },
                { 20.0, 2.5 },
                { 24.0, 3.0 },
                { 30.0, 3.5 },
                { 36.0, 4.0 }
            };

            // Thread nominal diameter — currently no nx_* tool in the toolset provides thread_diameter
            // (no thread feature tool), so this rule will always take the skip branch and never use 0
            // as the thread diameter to alarm on.
            double threadDiameter;
            if (!TryGetParamValue(p, "thread_diameter", out threadDiameter) || threadDiameter <= 0)
                return SkipMissing("rule-thread-hole-match", "Thread Pilot Hole Match", "thread_diameter", "thread pilot hole match",
                    "Current toolset has no thread feature tool, parameter pack does not provide thread_diameter (e.g. 3.0 / 4.0 / 6.0 etc.)");

            // Pilot hole diameter — alias hole_diameter -> nx_hole.diameter
            double holeDiameter;
            if (!TryGetParamValue(p, "hole_diameter", out holeDiameter) || holeDiameter <= 0)
                return SkipMissing("rule-thread-hole-match", "Thread Pilot Hole Match", "hole_diameter", "thread pilot hole match",
                    string.Format("M{0} standard pilot hole is {1}mm", threadDiameter,
                        threadTable.ContainsKey(threadDiameter) ? threadTable[threadDiameter].ToString("F1") : "refer to standard thread table"));

            // Pitch — intentionally NOT aliased: nx_pattern.pitch is the spiral pattern pitch,
            // unrelated to thread pitch (GB/T 196). If not received, skip pitch verification
            // (won't falsely report "pitch mismatch").
            double pitch;
            TryGetParamValue(p, "pitch", out pitch);

            // Check if pitch matches standard value (if pitch is provided)
            if (pitch > 0 && pitchTable.ContainsKey(threadDiameter))
            {
                double expectedPitch = pitchTable[threadDiameter];
                if (Math.Abs(pitch - expectedPitch) > 0.05)
                {
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-thread-hole-match",
                        RuleName = "Thread Pilot Hole Match",
                        Passed = false,
                        Message = string.Format("M{0} standard pitch is {1}mm, current pitch {2}mm does not match", threadDiameter, expectedPitch, pitch),
                        Severity = "error",
                        Suggestion = string.Format("Recommended M{0} standard pitch {1}mm", threadDiameter, expectedPitch)
                    };
                }
            }

            // Find standard pilot hole diameter
            if (threadTable.ContainsKey(threadDiameter))
            {
                double expectedHole = threadTable[threadDiameter];
                // Allow 0.15mm tolerance (machining tolerance)
                double tolerance = 0.15;
                if (Math.Abs(holeDiameter - expectedHole) > tolerance)
                {
                    // Determine if pilot hole is too large or too small
                    string direction = holeDiameter > expectedHole ? "too large (insufficient thread strength)" : "too small (tap may break)";
                    return new Protocol.RuleCheckResult
                    {
                        RuleId = "rule-thread-hole-match",
                        RuleName = "Thread Pilot Hole Match",
                        Passed = false,
                        Message = string.Format("M{0} thread standard pilot hole {1}mm, current pilot hole {2}mm {3} (deviation {4:F2}mm)",
                            threadDiameter, expectedHole, holeDiameter, direction, Math.Abs(holeDiameter - expectedHole)),
                        Severity = "error",
                        Suggestion = string.Format("Recommended M{0} standard pilot hole {1}mm (tolerance +/-{2}mm)", threadDiameter, expectedHole, tolerance)
                    };
                }

                // Match successful
                return new Protocol.RuleCheckResult
                {
                    RuleId = "rule-thread-hole-match",
                    RuleName = "Thread Pilot Hole Match",
                    Passed = true,
                    Message = string.Format("M{0} thread pilot hole {1}mm matches standard (standard value {2}mm)", threadDiameter, holeDiameter, expectedHole),
                    Severity = "info"
                };
            }

            // Non-standard thread specification, issue warning
            return new Protocol.RuleCheckResult
            {
                RuleId = "rule-thread-hole-match",
                RuleName = "Thread Pilot Hole Match",
                Passed = true,
                Message = string.Format("Thread diameter M{0} is a non-standard specification, skipping pilot hole match check", threadDiameter),
                Severity = "warning",
                Suggestion = "Standard thread specifications: M2, M2.5, M3, M3.5, M4, M5, M6, M8, M10, M12, M14, M16, M18, M20, M24, M30, M36"
            };
        }

        // ================================================================
        // Helper methods
        // ================================================================

        /// <summary>
        /// Parameter alias table — rules are written using old create_* intent parameter names,
        /// but what arrives here is the nx_* tool's JObject parameter pack
        /// (see TcpServer.cs -> _rules.Validate(toolName, jObj)),
        /// so key names don't match and must be bridged here.
        /// Each alias has been verified against mcp/tool-schemas.json real parameter names:
        ///   depth_value    -> nx_hole.depth
        ///   hole_diameter  -> nx_hole.diameter
        ///   instance_count -> nx_pattern.count
        ///   row_count      -> nx_pattern.y_count
        ///   row_spacing    -> nx_pattern.y_spacing
        ///
        /// Intentionally NOT aliased: nx_pattern does have a pitch parameter, but that is
        /// "spiral pattern pitch", semantically unrelated to thread pitch (GB/T 196).
        /// Aliasing it would feed pattern pitch into a thread standard check.
        /// Similarly wall_thickness / edge_clearance / center_distance / diameter1 / diameter2 /
        /// bbox_length / bbox_width / thread_diameter / adjacent_radius / adjacent_edge_length /
        /// min_adjacent_edge_length are not provided by any tool — no alias can be built,
        /// and related rules can only skip.
        /// </summary>
        private static readonly Dictionary<string, string> ParamAliases = new Dictionary<string, string>
        {
            { "depth_value", "depth" },
            { "hole_diameter", "diameter" },
            { "instance_count", "count" },
            { "row_count", "y_count" },
            { "row_spacing", "y_spacing" },
        };

        /// <summary>
        /// The sole trusted entry point for numeric parameters.
        /// true  = the parameter was actually received (key itself or its alias);
        /// false = not received (including JSON null / non-numeric type / unparseable string),
        ///         caller must skip the check.
        /// Rules use this method to determine whether to "skip" or "check" the conclusion-driving parameter.
        /// </summary>
        private bool TryGetParamValue(dynamic p, string key, out double value)
        {
            // First cast to object local variable: everything after this is static binding,
            // no more dynamic member access (JObject throws RuntimeBinderException on missing members,
            // rather than returning null)
            object raw = p;
            return TryGetParamValueCore(raw, key, out value);
        }

        /// <summary>
        /// Implementation of TryGetParamValue (object parameter = static binding, never throws).
        /// Read order: top-level key -> alias key -> nested "parameters" object (legacy ExecuteParams compatibility).
        /// </summary>
        private static bool TryGetParamValueCore(object p, string key, out double value)
        {
            value = 0;
            try
            {
                if (p == null || string.IsNullOrEmpty(key)) return false;

                // Form 1: tool parameter JObject (normal path)
                JObject root = p as JObject;
                if (root != null)
                {
                    // JObject's string indexer returns null when key is missing (no exception)
                    JToken token = root[key];
                    if (token == null)
                    {
                        // Form 1b: legacy caller wraps parameters in a nested "parameters" object
                        JObject nested = root["parameters"] as JObject;
                        if (nested != null) token = nested[key];
                    }
                    if (TryConvertToDouble(token, out value))
                        return true;

                    // Alias: rule key -> actual tool parameter name
                    string alias;
                    if (ParamAliases.TryGetValue(key, out alias))
                    {
                        JToken aliasToken = root[alias];
                        if (aliasToken == null)
                        {
                            JObject nested = root["parameters"] as JObject;
                            if (nested != null) aliasToken = nested[alias];
                        }
                        if (TryConvertToDouble(aliasToken, out value))
                            return true;
                    }

                    value = 0;
                    return false;
                }

                // Form 2: legacy ExecuteParams form — parameter dictionary
                var dict = p as IDictionary<string, object>;
                if (dict != null)
                {
                    object rawVal;
                    if (!dict.TryGetValue(key, out rawVal))
                    {
                        string alias;
                        if (!ParamAliases.TryGetValue(key, out alias) || !dict.TryGetValue(alias, out rawVal))
                        {
                            value = 0;
                            return false;
                        }
                    }
                    // Newtonsoft may deserialize numeric values as Int64/Double, or as JToken
                    JToken token = rawVal as JToken;
                    if (token == null && rawVal != null) token = new JValue(rawVal);
                    if (TryConvertToDouble(token, out value))
                        return true;
                    value = 0;
                    return false;
                }

                return false;
            }
            catch { value = 0; return false; }
        }

        /// <summary>
        /// JToken -> double. Only accepts Float / Integer / parseable String;
        /// Null, Boolean, Object, Array, Date are all treated as "not provided" (returns false),
        /// letting the caller skip the check.
        /// </summary>
        private static bool TryConvertToDouble(JToken token, out double value)
        {
            value = 0;
            if (token == null) return false;
            try
            {
                switch (token.Type)
                {
                    case JTokenType.Float:
                    case JTokenType.Integer:
                        value = token.Value<double>();
                        return true;

                    case JTokenType.String:
                        string s = token.Value<string>();
                        double parsed;
                        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                            || double.TryParse(s, out parsed))
                        {
                            value = parsed;
                            return true;
                        }
                        return false;

                    default:
                        return false;
                }
            }
            catch { value = 0; return false; }
        }

        /// <summary>
        /// Get a numeric value from parameters; returns defaultValue if not readable.
        /// Only for "optional/secondary" inputs (used to enrich hint text).
        /// Conclusion-driving parameters must use TryGetParamValue + skip branch,
        /// otherwise defaultValue may be treated as real input and produce false violations.
        /// </summary>
        private double GetParamValue(dynamic p, string key, double defaultValue)
        {
            double v;
            object raw = p;
            if (TryGetParamValueCore(raw, key, out v))
                return v;
            return defaultValue;
        }

        /// <summary>
        /// Result for a skipped check — used when driving parameters are missing.
        /// Passed=true + Severity="info": neither blocks execution nor fabricates violations
        /// (false alarms are worse than no report).
        /// </summary>
        private Protocol.RuleCheckResult SkipResult(string ruleId, string ruleName, string message)
        {
            return new Protocol.RuleCheckResult
            {
                RuleId = ruleId,
                RuleName = ruleName,
                Passed = true,
                Message = message,
                Severity = "info"
            };
        }

        /// <summary>
        /// Parameter not provided at all -> skip. Message format: "Missing &lt;key&gt;, skipping &lt;checkName&gt; check".
        /// </summary>
        private Protocol.RuleCheckResult SkipMissing(string ruleId, string ruleName, string key, string checkName, string suggestion = null)
        {
            return new Protocol.RuleCheckResult
            {
                RuleId = ruleId,
                RuleName = ruleName,
                Passed = true,
                Message = string.Format("Missing {0}, skipping {1} check", key, checkName),
                Severity = "info",
                Suggestion = suggestion
            };
        }

        /// <summary>
        /// Parameter provided but value unusable for calculation (e.g. hole diameter 0 as divisor) -> skip, not a violation.
        /// </summary>
        private Protocol.RuleCheckResult SkipUnusable(string ruleId, string ruleName, string key, double value, string checkName)
        {
            return SkipResult(ruleId, ruleName,
                string.Format("{0} parameter value {1:F2} is not positive, cannot be used for {2} check, skipping", key, value, checkName));
        }

        private double GetConstraint(dynamic p, string type, double defaultValue)
        {
            try
            {
                var constraints = p.Constraints as List<Protocol.IntentConstraint>;
                if (constraints != null)
                {
                    var c = constraints.FirstOrDefault(x => x.Type == type);
                    if (c != null && c.Value.HasValue) return c.Value.Value;
                }
                return defaultValue;
            }
            catch { return defaultValue; }
        }
    }
}
