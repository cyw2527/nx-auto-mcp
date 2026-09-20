using System;
using System.Collections.Generic;

namespace NxMcpPlugin.Rules
{
    /// <summary>
    /// Rule severity level
    /// </summary>
    public enum RuleSeverity
    {
        Error,    // blocks execution
        Warning,  // warns but allows continuation
        Info      // informational
    }

    /// <summary>
    /// Single rule definition
    /// </summary>
    public class RuleDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public RuleSeverity Severity { get; set; }

        /// <summary>
        /// Which intents this rule applies to
        /// </summary>
        public List<string> AppliesToIntents { get; set; }

        /// <summary>
        /// check_type identifier from JSON config, maps to C# check functions.
        /// Populated when loading from JSON; can be null for hardcoded registration.
        /// </summary>
        public string CheckType { get; set; }

        /// <summary>
        /// Rule parameters defined in JSON config (thresholds, multipliers, etc.).
        /// Populated when loading from JSON; null for hardcoded registration.
        /// </summary>
        public Dictionary<string, object> Params { get; set; }

        public RuleDefinition()
        {
            AppliesToIntents = new List<string>();
            Params = new Dictionary<string, object>();
        }

        /// <summary>
        /// Rule check logic
        /// Params: ExecuteParams, NXOpen.Session
        /// Returns: RuleCheckResult
        /// </summary>
        public Func<dynamic, dynamic, Protocol.RuleCheckResult> CheckFn { get; set; }
    }

    /// <summary>
    /// Rule validation summary result
    /// </summary>
    public class RuleValidateResult
    {
        public bool Accepted { get; set; }
        public List<string> Blockers { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Suggestions { get; set; }
        public List<string> Corrections { get; set; }

        public RuleValidateResult()
        {
            Blockers = new List<string>();
            Warnings = new List<string>();
            Suggestions = new List<string>();
            Corrections = new List<string>();
        }
    }
}
