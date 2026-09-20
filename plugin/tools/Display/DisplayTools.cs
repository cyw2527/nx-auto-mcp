using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NxMcpPlugin.Tools.Display
{
    // =========================================================================
    // Shared helpers
    // =========================================================================

    internal static class DisplayHelpers
    {
        /// <summary>
        /// Convert an NXOpen collection to a list.
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

        /// <summary>
        /// Parse a color value (hex string or [r,g,b] JArray) into an NXOpen Color.
        /// Accepts: "#FF0000", "FF0000", or a JArray [255, 0, 0].
        /// Returns the NXOpen Color via CreateRgb, or null if invalid.
        /// </summary>
        internal static dynamic ParseColor(JToken colorToken)
        {
            if (colorToken == null) return null;

            JArray arr = colorToken as JArray;
            if (arr != null && arr.Count == 3)
            {
                int r = arr[0].Value<int>();
                int g = arr[1].Value<int>();
                int b = arr[2].Value<int>();
                if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255)
                    return null;
                // NX2412: Color.CreateRgb doesn't exist. Use integer color value.
                return r * 65536 + g * 256 + b; // RGB to int
            }

            string hex = colorToken.Value<string>();
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return null;

            int r2;
            if (!int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out r2)) return null;
            int g2;
            if (!int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out g2)) return null;
            int b2;
            if (!int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out b2)) return null;

            return r2 * 65536 + g2 * 256 + b2; // RGB to int
        }

        /// <summary>
        /// Parse a hex or JArray color into (r, g, b) tuple for output reporting.
        /// </summary>
        internal static void ParseColorRgb(JToken colorToken, out int r, out int g, out int b)
        {
            JArray arr = colorToken as JArray;
            if (arr != null && arr.Count == 3)
            {
                r = arr[0].Value<int>();
                g = arr[1].Value<int>();
                b = arr[2].Value<int>();
                return;
            }

            string hex = colorToken.Value<string>();
            if (hex != null)
                hex = hex.TrimStart('#');
            else
                hex = "000000";

            int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                null, out r);
            int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                null, out g);
            int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                null, out b);
        }
    }

    // =========================================================================
    // 1. nx_set_layer_visibility
    // =========================================================================

    /// <summary>
    /// Sets the visibility of a layer (show or hide).
    /// Parameters:
    ///   layer (int, required) - Layer number (1-256).
    ///   visible (bool, required) - True to show, False to hide.
    /// Returns: { layer, visible }
    /// </summary>
    public class SetLayerVisibilityTool : IToolHandler
    {
        public string Name { get { return "nx_set_layer_visibility"; } }
        public string Description { get { return "Set the visibility of a layer (show or hide)."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken layerToken = parameters["layer"];
                int? layer = layerToken != null ? layerToken.Value<int>() : (int?)null;
                JToken visibleToken = parameters["visible"];
                bool? visible = visibleToken != null ? visibleToken.Value<bool>() : (bool?)null;

                if (layer == null || visible == null)
                    return ToolResult.Fail("Both 'layer' (int) and 'visible' (bool) are required.").ToJson();

                if (layer < 1 || layer > 256)
                    return ToolResult.Fail(
                        string.Format("Layer number {0} is out of valid range (1-256).", layer)).ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                // NXOpen: Layer.Visible / Layer.Hidden, Layers.SetState(layer, state)
                var state = visible.Value
                    ? NXOpen.Layer.State.Visible
                    : NXOpen.Layer.State.Hidden;
                wp.Layers.SetState(layer.Value, state);

                string visStr = visible.Value ? "visible" : "hidden";
                return ToolResult.Ok(
                    string.Format("Layer {0} is now {1}.", layer, visStr),
                    new JObject() {
                        { "layer", layer.Value },
                        { "visible", visible.Value }
                    }).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    string.Format("nx_set_layer_visibility failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 2. nx_move_to_layer
    // =========================================================================

    /// <summary>
    /// Moves objects to a specified layer.
    /// Parameters:
    ///   objects (string[], required) - List of object names to move.
    ///   layer (int, required) - Target layer number (1-256).
    /// Returns: { objects, layer, moved_count }
    /// </summary>
    public class MoveToLayerTool : IToolHandler
    {
        public string Name { get { return "nx_move_to_layer"; } }
        public string Description { get { return "Move objects to a specified layer."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken objectsToken = parameters["objects"];
                string[] objectNames = objectsToken != null ? objectsToken.ToObject<string[]>() : null;
                JToken layerToken = parameters["layer"];
                int? layer = layerToken != null ? layerToken.Value<int>() : (int?)null;

                if (objectNames == null || objectNames.Length == 0)
                    return ToolResult.Fail(
                        "The 'objects' parameter must be a non-empty list of object names.").ToJson();
                if (layer == null)
                    return ToolResult.Fail("The 'layer' parameter is required.").ToJson();
                if (layer < 1 || layer > 256)
                    return ToolResult.Fail(
                        string.Format("Layer number {0} is out of valid range (1-256).", layer)).ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var movedNames = new List<string>();

                foreach (string objName in objectNames)
                {
                    dynamic nxObj = DisplayHelpers.Resolve(wp, objName,
                        wp.Bodies, wp.Features, wp.Curves);
                    if (nxObj == null)
                        return ToolResult.Fail(
                            string.Format("Object '{0}' not found.", objName)).ToJson();

                    // NXOpen: Layers.MoveToLayer(objectArray, layer)
                    wp.Layers.MoveToLayer(new object[] { nxObj }, layer.Value);
                    movedNames.Add(objName);
                }

                return ToolResult.Ok(
                    string.Format("Moved {0} object(s) to layer {1}.", movedNames.Count, layer),
                    new JObject() {
                        { "objects", new JArray(movedNames) },
                        { "layer", layer.Value },
                        { "moved_count", movedNames.Count }
                    }).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(string.Format("nx_move_to_layer failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 3. nx_set_object_color
    // =========================================================================

    /// <summary>
    /// Sets the display color of objects.
    /// Parameters:
    ///   objects (string[], required) - List of object names to color.
    ///   color (string or [r,g,b], required) - Hex string "#FF0000" or [r,g,b] array.
    /// Returns: { objects, color: { r, g, b }, colored_count }
    /// </summary>
    public class SetObjectColorTool : IToolHandler
    {
        public string Name { get { return "nx_set_object_color"; } }
        public string Description
        {
            get { return "Set the display color of objects. Accepts RGB hex string (#FF0000) or [r,g,b] array."; }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken objectsToken = parameters["objects"];
                string[] objectNames = objectsToken != null ? objectsToken.ToObject<string[]>() : null;
                JToken colorToken = parameters["color"];

                if (objectNames == null || objectNames.Length == 0)
                    return ToolResult.Fail(
                        "The 'objects' parameter must be a non-empty list of object names.").ToJson();
                if (colorToken == null)
                    return ToolResult.Fail("The 'color' parameter is required.").ToJson();

                // NXOpen: Color.CreateRgb(r, g, b)
                dynamic nxColor = DisplayHelpers.ParseColor(colorToken);
                if (nxColor == null)
                    return ToolResult.Fail(
                        string.Format("Invalid color value. Use hex string (#RRGGBB) or [r,g,b] array (0-255).")).ToJson();

                int r, g, b;
                DisplayHelpers.ParseColorRgb(colorToken, out r, out g, out b);

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var coloredNames = new List<string>();

                foreach (string objName in objectNames)
                {
                    dynamic nxObj = DisplayHelpers.Resolve(wp, objName,
                        wp.Bodies, wp.Features, wp.Curves);
                    if (nxObj == null)
                        return ToolResult.Fail(
                            string.Format("Object '{0}' not found.", objName)).ToJson();

                    nxObj.Color = nxColor;
                    coloredNames.Add(objName);
                }

                return ToolResult.Ok(
                    string.Format("Set color to RGB({0}, {1}, {2}) on {3} object(s).", r, g, b, coloredNames.Count),
                    new JObject() {
                        { "objects", new JArray(coloredNames) },
                        { "color", new JObject {
                            { "r", r },
                            { "g", g },
                            { "b", b }
                        }},
                        { "colored_count", coloredNames.Count }
                    }).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    string.Format("nx_set_object_color failed: {0}", ex.Message)).ToJson();
            }
        }
    }

    // =========================================================================
    // 4. nx_set_object_transparency
    // =========================================================================

    /// <summary>
    /// Sets the transparency of objects.
    /// Parameters:
    ///   objects (string[], required) - List of object names to modify.
    ///   transparency (double, required) - 0.0 = opaque, 1.0 = fully transparent.
    /// Returns: { objects, transparency, modified_count }
    /// </summary>
    /// Parameters:
    ///   objects (string, optional) - objects parameter.
    ///   transparency (string, optional) - transparency parameter.
    ///
    public class SetObjectTransparencyTool : IToolHandler
    {
        public string Name { get { return "nx_set_object_transparency"; } }
        public string Description
        {
            get { return "Set the transparency of objects. 0.0 = opaque, 1.0 = fully transparent."; }
        }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                JToken objectsToken = parameters["objects"];
                string[] objectNames = objectsToken != null ? objectsToken.ToObject<string[]>() : null;
                JToken transparencyToken = parameters["transparency"];
                double? transparency = transparencyToken != null ? transparencyToken.Value<double>() : (double?)null;

                if (objectNames == null || objectNames.Length == 0)
                    return ToolResult.Fail(
                        "The 'objects' parameter must be a non-empty list of object names.").ToJson();
                if (transparency == null)
                    return ToolResult.Fail("The 'transparency' parameter is required.").ToJson();
                if (transparency < 0.0 || transparency > 1.0)
                    return ToolResult.Fail(
                        string.Format("Invalid transparency value: {0}. "
                        + "Must be between 0.0 (opaque) and 1.0 (fully transparent).", transparency)).ToJson();

                dynamic wp = session.Parts.Work;
                if (wp == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var modifiedNames = new List<string>();

                foreach (string objName in objectNames)
                {
                    dynamic nxObj = DisplayHelpers.Resolve(wp, objName,
                        wp.Bodies, wp.Features, wp.Curves);
                    if (nxObj == null)
                        return ToolResult.Fail(
                            string.Format("Object '{0}' not found.", objName)).ToJson();

                    nxObj.Transparency = transparency.Value;
                    modifiedNames.Add(objName);
                }

                return ToolResult.Ok(
                    string.Format("Set transparency to {0:F2} on {1} object(s).", transparency.Value, modifiedNames.Count),
                    new JObject() {
                        { "objects", new JArray(modifiedNames) },
                        { "transparency", transparency.Value },
                        { "modified_count", modifiedNames.Count }
                    }).ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    string.Format("nx_set_object_transparency failed: {0}", ex.Message)).ToJson();
            }
        }
    }
}
