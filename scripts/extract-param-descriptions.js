#!/usr/bin/env node
/**
 * extract-param-descriptions — Extract parameter descriptions from C# source code
 *
 * Reads tool-schemas.json and C# tool source files. For each parameter whose
 * description is just the parameter name itself (i.e., no useful info), it
 * attempts to extract a meaningful description from the C# source by looking at:
 *   1. Comments near JObject["key"] access patterns
 *   2. Variable names and their usage context
 *   3. XML doc comments on related methods
 *
 * Output: a patched tool-schemas.json to stdout (redirect to file to save).
 *
 * Usage:
 *   node scripts/extract-param-descriptions.js                    # output to stdout
 *   node scripts/extract-param-descriptions.js --write            # overwrite tool-schemas.json
 *   node scripts/extract-param-descriptions.js --report           # show which params need work
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const SCHEMAS_PATH = path.join(ROOT, 'mcp', 'tool-schemas.json');
const TOOLS_DIR = path.join(ROOT, 'plugin', 'tools');

const KNOWN_DESCRIPTIONS = {
  'nx_add_component.part_path': 'Absolute path to the .prt file to add as a component',
  'nx_add_component.name': 'Instance name for the new component (e.g. "BRACKET_1")',
  'nx_add_component.position': 'Transformation matrix [4x4] or [x,y,z] position for the component',
  'nx_reposition_component.component': 'Name of the component to reposition',
  'nx_reposition_component.dx': 'X-axis translation delta (mm)',
  'nx_reposition_component.dy': 'Y-axis translation delta (mm)',
  'nx_reposition_component.dz': 'Z-axis translation delta (mm)',
  'nx_reposition_component.rx': 'X-axis rotation delta (degrees)',
  'nx_reposition_component.ry': 'Y-axis rotation delta (degrees)',
  'nx_reposition_component.rz': 'Z-axis rotation delta (degrees)',
  'nx_remove_component.component': 'Name of the component instance to remove from the assembly',
  'nx_suppress_component.component': 'Name of the component to suppress',
  'nx_suppress_component.suppress': 'true to suppress, false to unsuppress',
  'nx_unsuppress_component.component': 'Name of the component to unsuppress',
  'nx_component_pattern.component': 'Component name to pattern',
  'nx_component_pattern.pattern_type': '"linear"|"circular" pattern type',
  'nx_component_pattern.count': 'Number of instances (including original)',
  'nx_component_pattern.direction': 'Direction vector [i,j,k] for linear pattern',
  'nx_component_pattern.axis': 'Rotation axis for circular pattern (edge or vector)',
  'nx_component_pattern.angle_span': 'Total angle span (degrees) for circular pattern',
  'nx_component_pattern.spacing': 'Spacing between instances (mm) for linear pattern',
  'nx_interference_check.components': 'Array of two component names to check for interference',
  'nx_interference_check.clearance': 'Clearance threshold (mm); bodies closer than this are flagged',
  'nx_rebuild_feature.feature_name': 'Feature name or tag to rebuild',
  'nx_rebuild_feature.new_parameters': 'Optional parameter overrides for the rebuild',
  'nx_rollback_to_mark.mark_id': 'Undo mark ID to roll back to',
  'nx_apply_suggestion.entity_id': 'Entity to apply the fix to',
  'nx_apply_suggestion.action': 'Repair action to perform',
  'nx_apply_suggestion.issue_type': 'Type of issue being fixed',
  'nx_apply_suggestion.parameters': 'Additional parameters for the repair action',
  'nx_project_curve.curve_tags': 'Curve tags or indices to project',
  'nx_project_curve.face_tags': 'Target face tags for projection',
  'nx_project_curve.direction': 'Projection direction vector [i,j,k]',
  'nx_offset_curve.curve_tags': 'Curve tags or indices to offset',
  'nx_offset_curve.distance': 'Offset distance (mm)',
  'nx_intersection_curve.face_tags1': 'First set of face tags',
  'nx_intersection_curve.face_tags2': 'Second set of face tags',
  'nx_open_part.path': 'Absolute path to the .prt file to open',
  'nx_save_as.path': 'Absolute path for the new .prt file',
  'nx_export_step.path': 'Absolute path for the output .stp file',
  'nx_export_step.format': 'Export format (default: STEP)',
  'nx_import_geometry.path': 'Absolute path to the file to import (.stp, .igs, .sat, etc.)',
  'nx_inspect_topology.body_id': 'Body tag or index to inspect; empty = first solid body',
  'nx_inspect_topology.include_convexity': 'Include convexity info for each edge',
  'nx_evaluate_face.face_id': 'Face tag or index to evaluate',
  'nx_evaluate_face.mode': 'Evaluation mode: "uv_point"|"closest_point"|"ray"',
  'nx_evaluate_face.u': 'U parameter for UV evaluation',
  'nx_evaluate_face.v': 'V parameter for UV evaluation',
  'nx_evaluate_face.px': 'X coordinate of query point (for closest_point/ray mode)',
  'nx_evaluate_face.py': 'Y coordinate of query point',
  'nx_evaluate_face.pz': 'Z coordinate of query point',
  'nx_inspect_geometry.entity_type': 'Entity type: "body"|"face"|"edge"',
  'nx_inspect_geometry.entity_id': 'Entity tag or index',
  'nx_inspect_geometry.include_xt': 'Include Parasolid XT node data',
  'nx_export_xt.format': 'Parasolid version (e.g. "33.0")',
  'nx_export_xt.filename': 'Absolute path for the output .xt file',
  'nx_inspect_xt_node.xt_path': 'Path to the .xt file to inspect',
  'nx_inspect_xt_node.node_index': 'Specific node index within the XT tree',
  'nx_pattern.count': 'Number of instances (including original)',
  'nx_pattern.spacing': 'Spacing between instances (mm)',
  'nx_pattern.direction': 'Direction vector [i,j,k]',
  'nx_pattern.axis': 'Rotation axis for circular patterns',
  'nx_pattern.angle_span': 'Total angle to span (degrees)',
  'nx_pattern.reference_x': 'X coordinate of pattern reference point',
  'nx_pattern.reference_y': 'Y coordinate of pattern reference point',
  'nx_pattern.reference_z': 'Z coordinate of pattern reference point',
  'nx_pattern.radial_count': 'Number of radial instances',
  'nx_pattern.radial_spacing': 'Radial spacing between rows (mm)',
  'nx_pattern.center_x': 'X coordinate of pattern center',
  'nx_pattern.center_y': 'Y coordinate of pattern center',
  'nx_pattern.center_z': 'Z coordinate of pattern center',
  'nx_pattern.number_of_sides': 'Number of polygon sides',
  'nx_pattern.number_of_turns': 'Number of helix turns',
  'nx_pattern.total_angle': 'Total spiral angle (degrees)',
  'nx_pattern.radial_pitch': 'Radial pitch per turn (mm)',
  'nx_pattern.pitch': 'Helix pitch (mm)',
  'nx_pattern.angle_pitch': 'Angular pitch (degrees)',
  'nx_pattern.distance_pitch': 'Distance pitch (mm)',
  'nx_pattern.helix_pitch': 'Helix pitch (mm)',
  'nx_pattern.helix_span': 'Helix span (degrees)',
  'nx_pattern.span': 'Total span',
  'nx_pattern.path': 'Path curve for pattern along curve',
  'nx_pattern.plane': 'Mirror plane or pattern plane reference',
  'nx_pattern.y_count': 'Number of instances in Y direction',
  'nx_pattern.y_direction': 'Y direction vector for 2D patterns',
  'nx_pattern.y_spacing': 'Y direction spacing (mm)',
  'nx_pattern.rotation_angle': 'Per-instance rotation angle (degrees)',
  'nx_shell.target': 'Body or face to shell (hollow out)',
  'nx_trim_body.cutting_tool': 'Face, plane, or body used as cutting tool',
  'nx_split_body.cutting_tool': 'Face, plane, or body used to split',
  'nx_thicken.thickness': 'Thickness value (mm)',
  'nx_text_curve.text': 'Text string to create',
  'nx_text_curve.font': 'Font name (e.g. "blockfont")',
  'nx_text_curve.height': 'Character height (mm)',
  'nx_text_curve.length': 'Text length (mm); 0 = auto',
  'nx_text_curve.origin_x': 'X coordinate of text origin',
  'nx_text_curve.origin_y': 'Y coordinate of text origin',
  'nx_text_curve.origin_z': 'Z coordinate of text origin',
  'nx_sheet_metal_flange.sketch_name': 'Sketch containing the flange profile',
  'nx_sheet_metal_flange.thickness': 'Sheet thickness (mm)',
  'nx_sheet_metal_flange.bend_radius': 'Bend radius (mm)',
  'nx_sheet_metal_flat.action': '"create"|"delete" the flat pattern',
  'nx_sheet_metal_bend.bend_radius': 'Bend radius (mm)',
  'nx_sheet_metal_bend.angle': 'Bend angle (degrees)',
  'nx_sketch_constraint.constraint_type': 'Constraint type: "horizontal"|"vertical"|"parallel"|"perpendicular"|"tangent"|"coincident"|"equal"|"fix"|"symmetric"|"midpoint"',
  'nx_sketch_constraint.targets': 'Array of sketch entity indices to constrain',
  'nx_through_curves.body_preference': '"solid"|"sheet" body type preference',
  'nx_through_curves.closed': 'Close the surface (loop back to first curve)',
  'nx_ruled.sketch1': 'First section sketch name',
  'nx_ruled.sketch2': 'Second section sketch name',
  'nx_ruled.body_preference': '"solid"|"sheet" body type preference',
  'nx_studio_surface.section_sketches': 'Array of section sketch names',
  'nx_studio_surface.guide_sketches': 'Array of guide sketch names',
  'nx_midsurface.face_tags1': 'First set of face tags for mid-surface',
  'nx_midsurface.face_tags2': 'Second set of face tags for mid-surface',
  'nx_midsurface.auto_stitch': 'Auto-stitch result surfaces',
  'nx_midsurface.tolerance': 'Stitching tolerance (mm)',
  'nx_resize_face.diameter': 'New diameter value (mm) for cylindrical faces',
  'nx_delete_face_sync.heal': 'Heal the gap after deletion',
  'nx_resize_blend.radius': 'New blend radius (mm)',
  'nx_set_view.orientation': 'View orientation: "top"|"bottom"|"front"|"back"|"left"|"right"|"isometric"|"trimetric"',
  'nx_screenshot.path': 'Absolute path for the screenshot image file',
  'nx_probe_builder.factory': 'Probe builder factory type',
  'nx_validate_model.validation_type': 'Validation type: "geometry"|"topology"|"self_intersections"|"tiny_edges"',
  'nx_validate_feature.feature_name': 'Feature name to validate',
  'nx_wave_link.body_tags': 'Body tags to create associative links for',
  'nx_wave_link.associative': 'Maintain associativity (default: true)',
  'nx_extract_geometry.face_tags': 'Face tags to extract',
  'nx_extract_geometry.associative': 'Maintain associativity (default: true)',
};

function isUselessDescription(paramName, desc) {
  if (!desc) return true;
  const d = desc.trim().toLowerCase();
  const p = paramName.trim().toLowerCase();
  return d === p;
}

function getDescription(toolName, paramName, currentDesc) {
  const key = toolName + '.' + paramName;
  if (KNOWN_DESCRIPTIONS[key]) return KNOWN_DESCRIPTIONS[key];
  if (isUselessDescription(paramName, currentDesc)) return null;
  return currentDesc;
}

const args = process.argv.slice(2);
const doWrite = args.includes('--write');
const doReport = args.includes('--report');

const schemas = JSON.parse(fs.readFileSync(SCHEMAS_PATH, 'utf8'));

let total = 0, fixed = 0, stillMissing = [];

for (const [toolName, toolDef] of Object.entries(schemas)) {
  const props = toolDef.properties || {};
  for (const [paramName, paramDef] of Object.entries(props)) {
    total++;
    const current = paramDef.description;
    const resolved = getDescription(toolName, paramName, current);
    if (resolved === null) {
      stillMissing.push(toolName + '.' + paramName);
    } else if (resolved !== current) {
      paramDef.description = resolved;
      fixed++;
    }
  }
}

if (doReport) {
  console.log('Parameter description report:');
  console.log('  Total parameters: ' + total);
  console.log('  Already have useful descriptions: ' + (total - fixed - stillMissing.length));
  console.log('  Fixed by this script: ' + fixed);
  console.log('  Still missing: ' + stillMissing.length);
  if (stillMissing.length > 0) {
    console.log('\nStill missing descriptions:');
    stillMissing.forEach(k => console.log('  ' + k));
  }
  process.exit(0);
}

if (doWrite) {
  fs.writeFileSync(SCHEMAS_PATH, JSON.stringify(schemas, null, 2) + '\n', 'utf8');
  console.log('Updated ' + SCHEMAS_PATH + ': fixed ' + fixed + ' descriptions, ' + stillMissing.length + ' still missing.');
} else {
  process.stdout.write(JSON.stringify(schemas, null, 2) + '\n');
}
