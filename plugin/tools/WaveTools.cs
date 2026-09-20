using System;
using Newtonsoft.Json.Linq;
using NXOpen;

namespace NxMcpPlugin.Tools.Wave
{
    /// <summary>
    /// WAVE Link - create associative linked copy of geometry from another part.
    /// Forum-verified (2026-07-31): WAVE link requires OCCURRENCE body, not prototype.
    /// Path: workPart.BaseFeatures.CreateWaveLinkBuilder → ExtractFaceBuilder.BodyToExtract.Add(occurrenceBody)
    /// Assembly mode: component.FindOccurrence(prototypeBody) → occurrence
    /// Single-part mode: body directly via ExtractFaceBuilder (same-part WAVE)
    /// </summary>
    /// Parameters:
    ///   body_tags (string, optional) - body_tags parameter.
    ///
    public class WaveLinkTool : IToolHandler
    {
        public string Name { get { return "nx_wave_link"; } }
        public string Description { get { return "Create WAVE linked geometry. body_tags(array), type(body/sheet/face), associative(bool)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray bodyTags = p["body_tags"] as JArray;
                if (bodyTags == null || bodyTags.Count == 0)
                    return ToolResult.Fail("body_tags array required.").ToJson();
                bool associative = p.Value<bool?>("associative") ?? true;

                // Forum-verified: workPart.BaseFeatures.CreateWaveLinkBuilder
                dynamic builder = wp.BaseFeatures.CreateWaveLinkBuilder(null);

                // Set type: BodyLink — try enum value (1), enum name, then SetType method
                try { builder.Type = 1; } catch { }
                try { var tProp = builder.GetType().GetProperty("Type"); var tEnum = Enum.ToObject(tProp.PropertyType, 1); tProp.SetValue(builder, tEnum, null); } catch { }

                // Forum-verified: bodies go through ExtractFaceBuilder.BodyToExtract
                dynamic extractFaceBuilder = null;
                try { extractFaceBuilder = builder.ExtractFaceBuilder; } catch { }
                if (extractFaceBuilder == null)
                    return ToolResult.Fail("WaveLinkBuilder.ExtractFaceBuilder not available.").ToJson();

                // Set parent part to OtherPart for inter-part WAVE
                try { extractFaceBuilder.ParentPart = 1; } catch { } // 1=OtherPart

                int added = 0;
                foreach (JToken bt in bodyTags)
                {
                    try
                    {
                        var prototypeBody = ToolHelpers.FindBodyByTag(wp, bt.Value<int>());
                        // L2: direct iteration fallback
                        if (prototypeBody == null)
                        {
                            foreach (dynamic b in wp.Bodies)
                            {
                                try { if ((int)Convert.ToDouble(b.Tag) == bt.Value<int>()) { prototypeBody = b; break; } } catch { }
                            }
                        }
                        if (prototypeBody == null) continue;

                        // Forum-verified: need OCCURRENCE body in assembly context.
                        // Step 1: find the component that owns this body
                        dynamic occurrenceBody = null;
                        try
                        {
                            // Try to get occurrence via assembly component hierarchy
                            var compRoot = wp.ComponentAssembly.RootComponent;
                            if (compRoot != null)
                            {
                                dynamic comps = compRoot.GetChildren();
                                foreach (dynamic comp in comps)
                                {
                                    try
                                    {
                                        // FindOccurrence returns the body in the context of the target work part
                                        occurrenceBody = comp.FindOccurrence(prototypeBody);
                                        if (occurrenceBody != null) break;
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        // Fallback: if no assembly context, use prototype body directly (same-part WAVE)
                        if (occurrenceBody == null)
                            occurrenceBody = prototypeBody;

                        // Forum-verified: add to ExtractFaceBuilder.BodyToExtract
                        var bodyToExtract = extractFaceBuilder.BodyToExtract;
                        if (bodyToExtract != null)
                        {
                            bodyToExtract.Add(occurrenceBody);
                            added++;
                        }
                    }
                    catch { }
                }
                if (added == 0) return ToolResult.Fail("No valid bodies found.").ToJson();

                try { builder.Associative = associative; } catch { }

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("bodies_linked", added);
                data.Add("associative", associative);
                return ToolResult.Ok(string.Format("WAVE linked {0} bodies.", added), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_wave_link: " + ex.Message).ToJson(); }
        }
    }

    /// <summary>
    /// Extract Geometry - extract faces from a body as separate geometry.
    /// Runtime probe: FacesToExtract (SelectFaceList) + Associative (bool writable)
    /// </summary>
    /// Parameters:
    ///   face_tags (string, optional) - face_tags parameter.
    ///
    public class ExtractGeometryTool : IToolHandler
    {
        public string Name { get { return "nx_extract_geometry"; } }
        public string Description { get { return "Extract faces from a body. face_tags(array), associative(bool)."; } }
        public JObject Execute(dynamic session, JObject p)
        {
            try
            {
                dynamic wp = session.Parts.Work;
                if (wp == null) return ToolResult.Fail("No work part.").ToJson();
                JArray faceTags = p["face_tags"] as JArray;
                if (faceTags == null || faceTags.Count == 0)
                    return ToolResult.Fail("face_tags array required.").ToJson();
                bool associative = p.Value<bool?>("associative") ?? true;

                dynamic builder = ((dynamic)wp.Features).CreateExtractFaceBuilder(null);
                int added = 0;
                foreach (JToken ft in faceTags)
                {
                    try
                    {
                        var f = ToolHelpers.FindFaceByTag(wp, ft.Value<int>());
                        // Verified 2026-08-17: FacesToExtract is SelectFaceList — use Add(face) directly
                        if (f != null) { builder.FacesToExtract.Add(f); added++; }
                        else { Surface.SectionHelper.Log("extract face not found: " + ft.Value<int>()); }
                    }
                    catch (Exception ex) { Surface.SectionHelper.Log("extract add face: " + ex.Message); }
                }
                if (added == 0) return ToolResult.Fail("No valid faces found.").ToJson();

                try { builder.Associative = associative; } catch (Exception ex) { Surface.SectionHelper.Log("extract associative: " + ex.Message); }

                builder.Commit(); builder.Destroy();
                var data = new JObject();
                data.Add("faces_extracted", added);
                data.Add("associative", associative);
                return ToolResult.Ok(string.Format("Extracted {0} faces.", added), data).ToJson();
            }
            catch (Exception ex) { return ToolResult.Fail("nx_extract_geometry: " + ex.Message).ToJson(); }
        }
    }
}
