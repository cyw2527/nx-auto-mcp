using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Protocol
{
    /// <summary>
    /// TCP message type definitions
    /// </summary>

    // ---- Response ----

    public class ResponseMessage
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("result")]
        public object Result { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }
    }

    // ---- Selection object description ----

    public class SelectionObject
    {
        [JsonProperty("tag")]
        public int Tag { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        // Face-specific
        [JsonProperty("area")]
        public double? Area { get; set; }

        [JsonProperty("normal")]
        public double[] Normal { get; set; }

        [JsonProperty("face_type")]
        public string FaceType { get; set; }

        // Edge-specific
        [JsonProperty("length")]
        public double? Length { get; set; }

        // Body-specific
        [JsonProperty("body_type")]
        public string BodyType { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("faces")]
        public int? FaceCount { get; set; }

        [JsonProperty("edges")]
        public int? EdgeCount { get; set; }

        // Common
        [JsonProperty("body_name")]
        public string BodyName { get; set; }
    }

    // ---- Execution intent ----

    public class IntentConstraint
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("value")]
        public double? Value { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }
    }

    // ---- Model summary ----

    public class ModelBodySummary
    {
        [JsonProperty("tag")]
        public int Tag { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("faces")]
        public int FaceCount { get; set; }

        [JsonProperty("edges")]
        public int EdgeCount { get; set; }

        [JsonProperty("bbox_min")]
        public double[] BBoxMin { get; set; }

        [JsonProperty("bbox_max")]
        public double[] BBoxMax { get; set; }
    }

    // ---- Rule check result ----

    public class RuleCheckResult
    {
        [JsonProperty("rule_id")]
        public string RuleId { get; set; }

        [JsonProperty("rule_name")]
        public string RuleName { get; set; }

        [JsonProperty("passed")]
        public bool Passed { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("suggestion")]
        public string Suggestion { get; set; }

        [JsonProperty("correction")]
        public object Correction { get; set; }
    }
}
