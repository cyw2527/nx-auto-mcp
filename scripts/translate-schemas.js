#!/usr/bin/env node
/**
 * translate-schemas.js — One-shot: translate all Chinese in tool-schemas.json to English.
 *
 * Usage:
 *   node scripts/translate-schemas.js --report   # show Chinese occurrences
 *   node scripts/translate-schemas.js --write     # overwrite with English
 */

const fs = require('fs');
const path = require('path');

const SCHEMAS_PATH = path.join(__dirname, '..', 'mcp', 'tool-schemas.json');
const schemas = JSON.parse(fs.readFileSync(SCHEMAS_PATH, 'utf8'));

const TRANSLATIONS = {
  'nx_mate_component.__description__': 'Apply an assembly constraint (11 types: Touch/Concentric/Fix/Distance/Parallel/Perpendicular/AlignLock/Angle/Center12/Bond/Fit) between components (Positioner direct mode. ClearNetwork→Begin→Establish→CreateConstraint(true)→type/alignment→refs→FixHint→Solve→ClearNetwork→End)',
  'nx_mate_component.component': 'Component name to be constrained (moved), e.g. STAND',
  'nx_mate_component.mate_type': 'Touch|Concentric|Fix|Distance|Parallel|Perpendicular|AlignLock|Angle|Center12|Bond|Fit (11 types; AlignLock/Angle use geometry axis with usesAxis=true; Bond uses component-level reference with no geometry; Center12 requires to2_component/to2_geom)',
  'nx_mate_component.alignment': 'InferAlign (default)|CoAlign|ContraAlign',
  'nx_mate_component.to_component': 'Reference component name (not needed for Fix type)',
  'nx_mate_component.to_geom': 'Reference geometry journal id (face format: FACE 120 {(0,0,0) EXTRUDE(1)}; Features|... composite strings are not resolvable)',
  'nx_mate_component.from_geom': 'Moving component geometry journal id',
  'nx_mate_component.offset': 'Distance constraint value (mm) / Angle value (°); 0=unset',
  'nx_mate_component.to2_component': 'Center12 second reference component name (required, Center12 only)',
  'nx_mate_component.to2_geom': 'Center12 second reference geometry journal id (required, Center12 only; format same as to_geom)',
  'nx_explode_assembly.__description__': 'Create or modify an explode view of the assembly. layout=none (empty explode) | linear (linear extrapolation per component, verified via UF ExplodeComponent) | nx_auto (NX automatic). Options: per-component offset, scale factor, selective components. Default: all components, linear along Z.',
  'nx_explode_assembly.explode': 'true=create/update explode, false=restore and delete all explodes (default true)',
  'nx_explode_assembly.layout': 'none (empty explode) | linear (per-component linear extrapolation, verified via UF ExplodeComponent) | nx_auto (NX automatic)',
  'nx_explode_assembly.axis': 'Linear layout axis X/Y/Z (default Z)',
  'nx_explode_assembly.spacing': 'Linear layout spacing mm (default = assembly bbox max dimension × 0.5 / component count, minimum 20)',
  'nx_explode_assembly.scale': 'Spacing multiplier (formerly a dead parameter, now fixed to actually take effect)',
  'nx_explode_assembly.components': 'Only lay out specified component names (default = full tree)',
  'nx_close_part.__description__': 'Close the currently active (work) part. Contract (M-008): can only close the current work part; cannot close a specific named part. Unsaved changes are discarded without prompting.',
  'nx_extrude.__description__': 'Extrude a section or sketch by a given distance. seed_point/seed_points provide region selection (verified). curve_indices/curve_names constrain region boundary to a subset of sketch curves.',
  'nx_extrude.end_condition': 'Warning: currently NOT applied (echo only, not applied to builder). Only supports Limits value semantics.',
  'nx_extrude.target': 'Boolean target body: body name / journal_id / tag. Must be explicitly specified when multiple bodies exist, otherwise error.',
  'nx_extrude.seed_point': 'Region selection (verified in 96.6% of human-authored journals): "x,y,z" seed point to isolate one region from the curve soup. Uses ScRuleFactory.CreateRuleFaceFeatures with FaceHoleSeedPoint.',
  'nx_extrude.seed_points': 'Multi-region union — semicolon-separated seeds "x,y;z;x,y,z". Each seed creates a separate region rule, added sequentially with Boolean.Add.',
  'nx_extrude.curve_indices': 'Constrain region boundary to a 1-based curve subset of the sketch, e.g. "1,3,5" (by sketch GetAllGeometry order). Human region arrays are often the true boundary of the .prt curves.',
  'nx_extrude.curve_names': 'Same as above but select by curve name (takes priority over curve_indices). Names are more stable than indices: .prt preserves NX auto-names (Arc1, Line2...).',
  'nx_revolve.__description__': 'Revolve a section or sketch around an axis. seed_point/seed_points provide region selection (verified). curve_indices/curve_names constrain region boundary.',
  'nx_revolve.axis': 'Axis of revolution: X, Y, Z (UPPERCASE only; default Z). Verified across all committed revolve cases.',
  'nx_revolve.target': 'Boolean target body: body name / journal_id / tag. Must be explicitly specified when multiple bodies exist.',
  'nx_revolve.seed_point': 'Region selection: "x,y,z" seed point to isolate one region from the curve soup, then revolve it. Verified: 35 committed Revolve cases use this approach.',
  'nx_revolve.seed_points': 'Multi-region union, semicolon-separated "x,y;z;x,y,z". Same as nx_extrude.',
  'nx_revolve.curve_indices': 'Constrain region boundary to a 1-based curve subset of the sketch (e.g. to exclude isolated lines). Human region arrays are often the true boundary of .prt curves.',
  'nx_revolve.curve_names': 'Same as above, select by curve name (takes priority over curve_indices). Verified: ASSEMBLY_14_PART_4 uses curve_names successfully.',
  'nx_unite_all.__description__': 'Unite ALL solid bodies in the work part into one (unite everything). No parameters. Used when feature output produces multiple bodies (e.g. multiple closed loops in sketch).',
  'nx_sweep.__description__': 'Sweep a single section along a guide curve ("Sweep Along Guide" in NX). Verified via committed journal cases.',
  'nx_sweep.target': 'Boolean target body: body name / journal_id / tag. Must be explicitly specified when multiple bodies exist.',
  'nx_hole.__description__': 'Create a hole feature at the specified location. NX2412 uses HolePackageBuilder (verified). Supports simple hole, countersink, counterbore; depth, diameter, tip angle all configurable.',
  'nx_pattern.__description__': 'Create a feature pattern of any supported type. Pattern types: rectangular | circular | along path | mirror | polygon | helix | spiral | fill. Each type has its own parameter set; unspecified parameters use NX defaults.',
  'nx_pattern.features': 'Target feature name (matched by Name/journal_id)',
  'nx_boolean.targets': 'Target bodies: body name / tag number / feature journal_id (first item = Target, rest are also targets; NX allows multiple targets in one Boolean).',
  'nx_boolean.tools': 'Tool bodies: body name / tag number / feature journal_id',
  'nx_shell.__description__': 'Shell (hollow out) a body by removing faces and offsetting the remaining walls to a uniform thickness.',
  'nx_shell.body': 'Target body: body name / journal_id / tag (must be explicitly specified when multiple bodies exist).',
  'nx_shell.thickness_flip': 'Flip shell direction (default true = inward, verified on NX2412 2026-09-01)',
  'nx_helix.__description__': 'Create a helix curve. Output curve/feature auto-named HELIX{N}. Parameters: number of turns, pitch, radius, right/left hand, taper angle, limits.',
  'nx_extend_surface.__description__': 'Extend Surface — extend sheet body edges. Source: verified on NX2412. Supports extend to face, by distance, or to plane.',
  'nx_activate_view.__description__': 'Activate (set as WorkView) a modeling view by name, or list all available views when no name is given.',
  'nx_create_component.__description__': 'Create new component(s) (.prt) from work part bodies and assemble them into the current assembly. Each body becomes a separate component in its own .prt file.',
  'nx_create_component.bodies': 'Body name / journal id list (with matching); default = all solid bodies in the work part',
  'nx_create_component.names': 'Per-body component names (default = body name with brackets removed, uppercased)',
  'nx_create_component.dir': 'Directory for new .prt files (default = work part directory)',
  'nx_create_component.keep_originals': 'true=copy and keep original bodies; false=move (NX default, originals deleted)',
  'nx_create_component.fix': 'Fix component after creation (default true, asm-06 Positioner Fix recipe)',
  'nx_find_same_bodies.__description__': 'Find bodies with identical geometry (19.1 algorithm: centroid + surface sampling distance sequence, relative tolerance). Returns groups of matching body names.',
  'nx_find_same_bodies.bodies': 'Body name list (default = all solid bodies)',
  'nx_find_same_bodies.tolerance': 'Relative tolerance (default 0.001 = 0.1%)',
  'nx_bodies_to_assembly.__description__': 'Convert bodies of a multi-body part into an assembly (19.1 related). Each body becomes a component in a new .prt file, then assembled into the work part.',
  'nx_bodies_to_assembly.bodies': 'Body name list (default = all solid bodies)',
  'nx_bodies_to_assembly.dedupe': 'Merge identical bodies (default true)',
  'nx_bodies_to_assembly.tolerance': '19.1 relative tolerance (default 0.001)',
  'nx_bodies_to_assembly.dir': 'Directory for new .prt files (default = work part directory)',
  'nx_bodies_to_assembly.fix': 'Fix all components after creation (default true)',
  'nx_bom_extract.__description__': 'Extract BOM data (part name × quantity × level) from the full assembly tree. Returns flat list and optionally nested tree structure.',
  'nx_bom_extract.tree': 'Also output nested level tree (default false)',
  'nx_offset_curve.__description__': 'Offset Curve — offset curves by distance in a plane. Verified on NX2412. Supports inward/outward offset with optional trim/extend.',
};

let changed = 0;

function applyTranslation(obj, key, translation) {
  if (obj[key] !== undefined && obj[key] !== translation) {
    obj[key] = translation;
    changed++;
  }
}

for (const [toolName, toolDef] of Object.entries(schemas)) {
  const descKey = toolName + '.__description__';
  if (TRANSLATIONS[descKey]) {
    applyTranslation(toolDef, 'description', TRANSLATIONS[descKey]);
  }

  const props = toolDef.properties || {};
  for (const [paramName, paramDef] of Object.entries(props)) {
    const paramKey = toolName + '.' + paramName;
    if (TRANSLATIONS[paramKey]) {
      applyTranslation(paramDef, 'description', TRANSLATIONS[paramKey]);
    }
  }
}

const args = process.argv.slice(2);
if (args.includes('--report')) {
  let remaining = 0;
  for (const [toolName, toolDef] of Object.entries(schemas)) {
    if (/[一-鿿]/.test(toolDef.description || '')) {
      remaining++;
      console.log('  ' + toolName + '.__description__: ' + toolDef.description.slice(0, 80));
    }
    for (const [p, def] of Object.entries(toolDef.properties || {})) {
      if (/[一-鿿]/.test(def.description || '')) {
        remaining++;
        console.log('  ' + toolName + '.' + p + ': ' + def.description.slice(0, 80));
      }
    }
  }
  console.log('\nTranslated: ' + changed + ', Remaining Chinese: ' + remaining);
} else if (args.includes('--write')) {
  fs.writeFileSync(SCHEMAS_PATH, JSON.stringify(schemas, null, 2) + '\n', 'utf8');
  console.log('Updated ' + SCHEMAS_PATH + ': translated ' + changed + ' descriptions.');
} else {
  console.log('Usage: node scripts/translate-schemas.js --report | --write');
}
