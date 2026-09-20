using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NXOpen;
using NXOpen.UF;

namespace NxMcpPlugin.Tools.Inspect
{
    /// <summary>
    /// Inspection helpers - type maps, geometry extraction, topology building.
    /// </summary>
    internal static class InspectHelpers
    {
        /// <summary>Write diagnostic log to the same file as ManagedPlugin.</summary>
        internal static void LogToFile(string msg)
        {
            try
            {
                var path = NxPaths.StartupFile("tcp_server.log");
                System.IO.File.AppendAllText(path,
                    string.Format("[{0:HH:mm:ss.fff}] [Inspect] {1}\n", DateTime.Now, msg));
            }
            catch { }
        }

        /// <summary>NX2412: FaceFaceType integer to human-readable name.</summary>
        public static readonly Dictionary<int, string> FaceTypeIntMap = new Dictionary<int, string>
        {
            {1, "PLANAR"}, {2, "CYLINDRICAL"}, {3, "CONICAL"},
            {4, "SPHERICAL"}, {5, "TOROIDAL"}, {6, "B_SURFACE"},
            {7, "OFFSET"}, {8, "BLENDED"}, {9, "EXTRUDED"},
            {10, "SWEPT"}, {11, "RUBBER"}, {12, "CONVERGENT"},
        };

        /// <summary>NX2412: EdgeEdgeType integer to human-readable name.</summary>
        public static readonly Dictionary<int, string> EdgeTypeIntMap = new Dictionary<int, string>
        {
            {1, "LINEAR"}, {2, "CIRCULAR"}, {3, "ELLIPTICAL"},
            {4, "INTERSECTION"}, {5, "SP_CURVE"}, {6, "B_CURVE"},
            {7, "FOREIGN"},
        };

        /// <summary>Parasolid V35 node type index to human-readable name.</summary>
        public static readonly Dictionary<int, string> NodeTypeNames = new Dictionary<int, string>
        {
            {10, "ASSEMBLY"}, {11, "INSTANCE"}, {12, "BODY"}, {13, "SHELL"},
            {14, "FACE"}, {15, "LOOP"}, {16, "EDGE"}, {17, "HALFEDGE"},
            {18, "VERTEX"}, {19, "REGION"},
            {29, "POINT"}, {30, "LINE"}, {31, "CIRCLE"}, {32, "ELLIPSE"},
            {38, "INTERSECTION"},
            {50, "PLANE"}, {51, "CYLINDER"}, {52, "CONE"}, {53, "SPHERE"},
            {54, "TORUS"}, {56, "BLENDED_EDGE"}, {59, "BLEND_BOUND"},
            {60, "OFFSET_SURF"}, {67, "SWEPT_SURF"}, {68, "SPUN_SURF"},
            {70, "LIST"}, {74, "POINTER_LIS_BLOCK"},
            {79, "ATT_DEF_ID"}, {80, "ATTRIB_DEF"}, {81, "ATTRIBUTE"},
            {82, "INT_VALUES"}, {83, "REAL_VALUES"}, {84, "CHAR_VALUES"},
            {85, "POINT_VALUES"}, {86, "VECTOR_VALUES"}, {87, "AXIS_VALUES"},
            {88, "TAG_VALUES"}, {89, "DIRECTION_VALUES"}, {98, "UNICODE_VALUES"},
            {99, "FIELD_NAMES"},
            {90, "FEATURE"}, {91, "MEMBER_OF_FEATURE"},
            {100, "TRANSFORM"}, {101, "WORLD"}, {102, "KEY"},
            {120, "PE_SURF"}, {124, "B_SURFACE"}, {125, "SURFACE_DATA"},
            {126, "NURBS_SURF"}, {127, "KNOT_MULT"}, {128, "KNOT_SET"},
            {130, "PE_CURVE"}, {133, "TRIMMED_CURVE"}, {134, "B_CURVE"},
            {135, "CURVE_DATA"}, {136, "NURBS_CURVE"}, {137, "SP_CURVE"},
            {141, "GEOMETRIC_OWNER"},
            {176, "PART_XMT_BLOCK"}, {185, "POLYLINE_DATA"},
            {189, "PSM_MESH"}, {200, "POLYLINE"}, {201, "MESH"},
            {204, "INTERSECTION_DATA"}, {222, "LATTICE"},
            {229, "TRANSFORM_PRECISION"},
        };

        /// <summary>Variable-length XT node types (from V35 schema).</summary>
        public static readonly HashSet<int> VarLenTypes = new HashSet<int>
        {
            70, 74, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 98, 99, 127, 128, 135, 136, 185, 207
        };

        /// <summary>Safely convert a value to double, returning null on failure.</summary>
        public static double? SafeFloat(object val)
        {
            try { return Math.Round(Convert.ToDouble(val), 6); }
            catch { return null; }
        }

        /// <summary>Safely extract [x, y, z] as JArray from a Point3d-like object.</summary>
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

        /// <summary>Get human-readable surface type for a face (NX2412 compatible).</summary>
        public static string GetSurfaceType(dynamic face)
        {
            try
            {
                // NX2412: SolidFaceType returns FaceFaceTypeMemberType with .value int
                object st = null;
                try { st = face.SolidFaceType; } catch { }
                if (st == null)
                {
                    try { st = face.FaceType; } catch { }
                }
                if (st == null) return "UNKNOWN";

                // Try integer value first (NX2412)
                try
                {
                    int intVal = Convert.ToInt32(((dynamic)st).value);
                    if (FaceTypeIntMap.ContainsKey(intVal))
                        return FaceTypeIntMap[intVal];
                    return "TYPE_" + intVal;
                }
                catch { /* fallback to string */ }

                string name = st.ToString();
                if (name.Contains("Planar") || name.Contains("Plane")) return "PLANAR";
                if (name.Contains("Cylind")) return "CYLINDRICAL";
                if (name.Contains("Con")) return "CONICAL";
                if (name.Contains("Spher")) return "SPHERICAL";
                if (name.Contains("Tor")) return "TOROIDAL";
                if (name.Contains("BSurface") || name.Contains("B_Surface")) return "B_SURFACE";
                if (name.Contains("Offset")) return "OFFSET";
                if (name.Contains("Blend")) return "BLENDED";
                if (name.Contains("Extrud")) return "EXTRUDED";
                if (name.Contains("Swept")) return "SWEPT";
                return name.Split('.').Last().ToUpperInvariant();
            }
            catch { return "UNKNOWN"; }
        }

        /// <summary>
        /// W8 (2026-09-03, A8 修复): face 面积用 UF_EVALSF 参数面数值积分 (中点法则)。
        /// 旧实现把面 bbox 三面均值当面积 → 系统性恒定比值误差 ~0.4247 (实测错 60%: 磁盘 r21 报 588 实 1385)。
        /// 解析面中平面/圆柱/圆锥 |Su×Sv| 在参数域内线性或恒定 → 中点法精确;
        /// 球/环/B 面 40×40 网格误差 &lt;0.1%。edge length/邻接不受影响。
        /// </summary>
        public static double? GetFaceArea(dynamic face, UFSession ufs)
        {
            if (ufs == null) return null;

            // W8 (2026-09-03): PLANAR 面参数域 = 包围方域 (含修剪区外) — UV 积分会把
            // 域外平面也算进来 (磁盘 r13 报 692 vs 真 531, 比值 = 方域/圆盘)。
            // 平面面边界只含直线/圆弧 → 用边界采样 + shoelace 精确闭合区域。
            string surfaceType = GetSurfaceType(face);
            if (surfaceType == "PLANAR")
                return PlanarFaceAreaByLoop(face, ufs);

            IntPtr evalHandle = IntPtr.Zero;
            try
            {
                evalHandle = CreateFaceEvaluator(ufs, face);
                if (evalHandle == IntPtr.Zero) return null;

                double[] limits = GetFaceLimits(ufs, evalHandle);
                if (limits == null || limits.Length < 4) return null;
                double uSpan = limits[1] - limits[0];
                double vSpan = limits[3] - limits[2];
                if (!(uSpan > 0.0) || !(vSpan > 0.0)) return null; // 退化参数域 — 不报假面积

                const int NU = 40, NV = 40;
                double du = uSpan / NU, dv = vSpan / NV;
                double total = 0.0;
                double[] uv = new double[2];
                for (int i = 0; i < NU; i++)
                {
                    uv[0] = limits[0] + (i + 0.5) * du;
                    for (int j = 0; j < NV; j++)
                    {
                        uv[1] = limits[2] + (j + 0.5) * dv;
                        ModlSrfValue res;
                        ((dynamic)ufs.Evalsf).Evaluate(evalHandle, 1, uv, out res);
                        double[] dU = res.srf_du, dV = res.srf_dv;
                        if (dU == null || dV == null) continue;
                        // |Su × Sv| — 参数面积元素
                        double jx = dU[1] * dV[2] - dU[2] * dV[1];
                        double jy = dU[2] * dV[0] - dU[0] * dV[2];
                        double jz = dU[0] * dV[1] - dU[1] * dV[0];
                        total += Math.Sqrt(jx * jx + jy * jy + jz * jz);
                    }
                }
                return Math.Round(total * du * dv, 6);
            }
            catch (Exception ex)
            {
                LogToFile("[GetFaceArea] numeric integration failed: " + ex.Message);
            }
            finally
            {
                if (evalHandle != IntPtr.Zero)
                    FreeFaceEvaluator(ufs, evalHandle);
            }
            return null;
        }

        /// <summary>
        /// W8 (2026-09-03): PLANAR 面面积 — 边界采样 shoelace。
        /// 平面解析面参数域是包围矩形 (含修剪区外), UV 数值积分不可用;
        /// 平面修剪边界只含 LINE/CIRCULAR 边 → UFEval 每边采样 64 点,
        /// 按端点连通分组环 (盘=1 环, 环面=外环+孔), 符号 shoelace 相抵。
        /// 固体外环视向 +法向 为 CCW(+), 孔为 CW(−), 取 |净和|。
        /// </summary>
        private static double? PlanarFaceAreaByLoop(dynamic face, UFSession ufs)
        {
            try
            {
                // 平面解析几何: 原点 + 法向 (UF_MODL_ask_face_data)
                int ftype;
                double[] pt = new double[3], dir = new double[3], box = new double[6];
                double radius, radData;
                int normDir;
                ufs.Modl.AskFaceData(face.Tag, out ftype, pt, dir, box, out radius, out radData, out normDir);
                double ox = pt[0], oy = pt[1], oz = pt[2];
                double nx = dir[0], ny = dir[1], nz = dir[2];
                double nLen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nLen < 1e-12) return null;
                nx /= nLen; ny /= nLen; nz /= nLen;

                // 构造正交基 e1,e2 (e1×e2 = n)
                double rx = 0, ry = 0, rz = 1;
                if (Math.Abs(nz) > 0.9) { rx = 1; ry = 0; rz = 0; }
                double e1x = ny * rz - nz * ry, e1y = nz * rx - nx * rz, e1z = nx * ry - ny * rx;
                double e1Len = Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z);
                if (e1Len < 1e-12) return null;
                e1x /= e1Len; e1y /= e1Len; e1z /= e1Len;
                double e2x = ny * e1z - nz * e1y, e2y = nz * e1x - nx * e1z, e2z = nx * e1y - ny * e1x;

                // ── 每条边采样 (UFEval 通用曲线求值 — 直线/圆弧/样条通吃) ──
                // 🔴 2026-09-03 实测坑: UFEval param 域对圆边是弧度 [0,2π] (非 [0,1])!
                // 必须先 ufs.Eval.AskLimits(handle,out tmin,out tmax) 拿真实参数范围再采样,
                // 否则只采到 57.3° 扇区 (diag journal 实证: 面积 13.4 vs 真 530.9)。
                // SEG=360: 圆弧内接多边形误差 ≈ (2π/360)²/6 ≈ 0.0025% (r21 盘 → 1385.4 ✓)
                const int SEG = 360;
                dynamic edges = face.GetEdges();
                if (edges == null) return null;
                var edgeChains = new List<double[][]>();  // 每条边一条采样链
                foreach (dynamic edge in edges)
                {
                    if (edge == null) continue;
                    IntPtr eh = IntPtr.Zero;
                    double[][] chain = null;
                    try
                    {
                        NXOpen.Tag etag = (NXOpen.Tag)edge.Tag;
                        ufs.Eval.Initialize2(etag, out eh);
                        if (eh == IntPtr.Zero) continue;
                        double tmin, tmax;
                        try
                        {
                            // 实测: UFEval.AskLimits(IntPtr evaluator, double[] limits) — 数组风格
                            double[] limits2 = new double[2];
                            ufs.Eval.AskLimits(eh, limits2);
                            tmin = limits2[0]; tmax = limits2[1];
                        }
                        catch { tmin = 0.0; tmax = 1.0; }
                        if (!(tmax > tmin)) { tmin = 0.0; tmax = 1.0; }
                        var evalMethod = ufs.Eval.GetType().GetMethod("Evaluate",
                            new[] { typeof(IntPtr), typeof(int), typeof(double), typeof(double[]), typeof(double[]) });
                        chain = new double[SEG][];
                        for (int k = 0; k < SEG; k++)
                        {
                            double t = tmin + (tmax - tmin) * ((double)k / SEG);
                            double[] pnt = new double[3], derivs = new double[6];
                            if (evalMethod != null)
                                evalMethod.Invoke(ufs.Eval, new object[] { eh, 0, t, pnt, derivs });
                            else
                                ((dynamic)ufs.Eval).Evaluate(eh, 0, t, pnt, derivs);
                            chain[k] = pnt;
                        }
                    }
                    catch { chain = null; }
                    finally { if (eh != IntPtr.Zero) { try { ufs.Eval.Free(eh); } catch { } } }
                    if (chain != null) edgeChains.Add(chain);
                }
                if (edgeChains.Count == 0) return null;

                // ── 环组装: 边链首尾相接成闭环 (共享顶点精确重合; 单边圆环自闭合) ──
                const double TOL2 = 1e-6;  // 端点重合容差平方 (mm², UFEval 同顶点一致)
                var usedE = new bool[edgeChains.Count];
                var loopSignedAreas = new List<double>();
                for (int start = 0; start < edgeChains.Count; start++)
                {
                    if (usedE[start]) continue;
                    var loop = new List<double[]>();
                    int cur = start;
                    usedE[cur] = true;
                    // 头尾点 (3D, 已投影前统一收集用投影坐标比较也行 — 直接比较 3D)
                    while (true)
                    {
                        double[][] ch = edgeChains[cur];
                        for (int k = 0; k < SEG; k++)
                        {
                            double dx = ch[k][0] - ox, dy = ch[k][1] - oy, dz = ch[k][2] - oz;
                            loop.Add(new double[] { dx * e1x + dy * e1y + dz * e1z, dx * e2x + dy * e2y + dz * e2z });
                        }
                        double[] tail = ch[SEG - 1];
                        // 找下一边: 其头 ≈ 当前尾
                        int next = -1;
                        for (int j = 0; j < edgeChains.Count; j++)
                        {
                            if (usedE[j]) continue;
                            double[] head = edgeChains[j][0];
                            double ddx = head[0] - tail[0], ddy = head[1] - tail[1], ddz = head[2] - tail[2];
                            if (ddx * ddx + ddy * ddy + ddz * ddz < TOL2) { next = j; break; }
                        }
                        if (next < 0)
                        {
                            // 环闭合 (回到 start 的链头) 或单边自闭合 — 检查闭环
                            double[] chainHead = edgeChains[start][0];
                            double ddx = chainHead[0] - tail[0], ddy = chainHead[1] - tail[1], ddz = chainHead[2] - tail[2];
                            if (ddx * ddx + ddy * ddy + ddz * ddz < TOL2)
                            { /* 闭环 — 链尾即链头 */ }
                            break;
                        }
                        cur = next;
                        usedE[cur] = true;
                    }
                    // shoelace (每边链首尾自动衔接; 缺闭合点则补)
                    int m = loop.Count;
                    if (m >= 3)
                    {
                        double s = 0;
                        for (int i = 0; i < m - 1; i++)
                            s += loop[i][0] * loop[i + 1][1] - loop[i + 1][0] * loop[i][1];
                        s += loop[m - 1][0] * loop[0][1] - loop[0][0] * loop[m - 1][1];
                        loopSignedAreas.Add(s / 2.0);
                    }
                }
                // 组合环面积 (2026-09-03 实测: NX 孔环可能与外环同向, 直接求和会把孔加回 —
                // 顶环 845.05 = 530.9+314.1 而非 π(169−100)=216.8)。
                // 通用式: 面积 = |最大环| − Σ|其余环| (外环必最大, 方向无关)。
                double maxAbs = 0, sumOthers = 0;
                foreach (double a in loopSignedAreas)
                {
                    double absA = Math.Abs(a);
                    if (absA > maxAbs)
                    {
                        sumOthers += maxAbs;   // 旧最大降级为"其余"
                        maxAbs = absA;
                    }
                    else
                        sumOthers += absA;
                }
                return Math.Round(maxAbs - sumOthers, 6);
            }
            catch { }
            return null;
        }

        /// <summary>Build a JSON summary dict for a single face.</summary>
        public static JObject BuildFaceSummary(dynamic face, string parentFeature = "", UFSession ufs = null)
        {
            double? faceTag = SafeFloat(face.Tag);
            var summary = new JObject();
            summary["face_id"] = faceTag != null ? (JToken)(int)faceTag.Value : (JToken)null;
            summary["xt_node_index"] = null;
            summary["surface_type"] = GetSurfaceType(face);

            if (!string.IsNullOrEmpty(parentFeature))
                summary["parent_feature"] = parentFeature;

            double? area = GetFaceArea(face, ufs);
            if (area != null)
                summary["area"] = area.Value;

            // NX2412: face.GetLoops() doesn't exist. Use face.GetEdges() instead.
            try
            {
                var edges = face.GetEdges();
                int edgeCount = 0;
                foreach (var e in edges) edgeCount++;
                summary["edge_count"] = edgeCount;
                summary["loop_count"] = 1; // Default: assume 1 loop (outer boundary)
            }
            catch { /* GetEdges may fail */ }

            return summary;
        }

        /// <summary>
        /// Enrich a face summary JSON with geometric properties via UF_EVALSF.
        /// Adds: radius, axis, normal, mid_point, gaussian_curvature.
        /// Uses principal curvature analysis at face UV midpoint.
        /// </summary>
        public static void EnrichFaceGeometry(dynamic face, JObject summary, UFSession ufs)
        {
            string surfaceType = "";
            JToken st = summary["surface_type"];
            if (st != null && st.Type != JTokenType.Null)
                surfaceType = st.Value<string>() ?? "";
            IntPtr evalHandle = IntPtr.Zero;
            try
            {
                evalHandle = CreateFaceEvaluator(ufs, face);
                if (evalHandle == IntPtr.Zero) return;

                double[] limits = GetFaceLimits(ufs, evalHandle);
                if (limits == null || limits.Length < 4) return;
                double uMid = (limits[0] + limits[1]) / 2.0;
                double vMid = (limits[2] + limits[3]) / 2.0;

                // Normal at midpoint
                var vecResult = EvaluateFaceVectors(ufs, evalHandle, uMid, vMid);
                if (vecResult != null && vecResult["normal"] != null)
                    summary["normal"] = vecResult["normal"];

                // 3D midpoint
                double[] midPoint = EvaluateFacePoint(ufs, evalHandle, uMid, vMid);
                if (midPoint != null && midPoint.Length >= 3)
                    summary["mid_point"] = new JArray(
                        Math.Round(midPoint[0], 6),
                        Math.Round(midPoint[1], 6),
                        Math.Round(midPoint[2], 6));

                // Curvature analysis for radius
                if (surfaceType == "CYLINDRICAL" || surfaceType == "CONICAL" ||
                    surfaceType == "SPHERICAL" || surfaceType == "SURFACEOFREVOLUTION" ||
                    surfaceType == "BLENDED")
                {
                    try
                    {
                        double[] uvParams = new double[2] { uMid, vMid };
                        ModlSrfValue result2;
                        ((dynamic)ufs.Evalsf).Evaluate(evalHandle, 2, uvParams, out result2);

                        // NX2412: second derivatives accessed via reflection (field names vary)
                        double[] du = result2.srf_du, dv = result2.srf_dv;
                        double[] duu = null, dvv = null, duv = null;
                        var r2Type = result2.GetType();
                        try { var f = r2Type.GetField("srf_duu"); if (f != null) duu = f.GetValue(result2) as double[]; } catch { }
                        try { var f = r2Type.GetField("srf_dvv"); if (f != null) dvv = f.GetValue(result2) as double[]; } catch { }
                        try { var f = r2Type.GetField("srf_duv"); if (f != null) duv = f.GetValue(result2) as double[]; } catch { }
                        // Try alternate field names
                        if (duu == null) try { var f = r2Type.GetField("duu"); if (f != null) duu = f.GetValue(result2) as double[]; } catch { }
                        if (dvv == null) try { var f = r2Type.GetField("dvv"); if (f != null) dvv = f.GetValue(result2) as double[]; } catch { }
                        if (duv == null) try { var f = r2Type.GetField("duv"); if (f != null) duv = f.GetValue(result2) as double[]; } catch { }

                        if (du != null && dv != null && duu != null && dvv != null && duv != null)
                        {
                            double nx = du[1]*dv[2] - du[2]*dv[1];
                            double ny = du[2]*dv[0] - du[0]*dv[2];
                            double nz = du[0]*dv[1] - du[1]*dv[0];
                            double nLen = Math.Sqrt(nx*nx + ny*ny + nz*nz);
                            if (nLen > 1e-12) { nx/=nLen; ny/=nLen; nz/=nLen; }

                            double E = du[0]*du[0] + du[1]*du[1] + du[2]*du[2];
                            double F = du[0]*dv[0] + du[1]*dv[1] + du[2]*dv[2];
                            double G = dv[0]*dv[0] + dv[1]*dv[1] + dv[2]*dv[2];
                            double L = duu[0]*nx + duu[1]*ny + duu[2]*nz;
                            double M = duv[0]*nx + duv[1]*ny + duv[2]*nz;
                            double N = dvv[0]*nx + dvv[1]*ny + dvv[2]*nz;

                            double denom = 2.0 * (E*G - F*F);
                            if (Math.Abs(denom) > 1e-15)
                            {
                                double H = (E*N - 2.0*F*M + G*L) / denom;
                                if (Math.Abs(H) > 1e-12)
                                    summary["radius"] = Math.Round(1.0 / Math.Abs(H), 4);
                            }
                            double K = (L*N - M*M) / (E*G - F*F);
                            summary["gaussian_curvature"] = Math.Round(K, 12);
                        }
                    }
                    catch { /* curvature best-effort, level 2 may fail */ }
                }

                // Axis for cylindrical/conical: evaluate along v-direction
                if (surfaceType == "CYLINDRICAL" || surfaceType == "CONICAL")
                {
                    try
                    {
                        double[] p1 = EvaluateFacePoint(ufs, evalHandle, uMid, vMid);
                        double[] p2 = EvaluateFacePoint(ufs, evalHandle, uMid,
                            Math.Min(vMid + (limits[3] - limits[2]) * 0.1, limits[3]));
                        if (p1 != null && p2 != null)
                        {
                            double ax = p2[0]-p1[0], ay = p2[1]-p1[1], az = p2[2]-p1[2];
                            double aLen = Math.Sqrt(ax*ax + ay*ay + az*az);
                            if (aLen > 1e-12)
                                summary["axis"] = new JArray(
                                    Math.Round(ax/aLen, 6), Math.Round(ay/aLen, 6), Math.Round(az/aLen, 6));
                        }
                    }
                    catch { /* axis best-effort */ }
                }
            }
            catch (Exception ex)
            {
                LogToFile(string.Format("[EnrichFaceGeometry] {0}: {1}", surfaceType, ex.Message));
            }
            finally
            {
                if (evalHandle != IntPtr.Zero)
                    FreeFaceEvaluator(ufs, evalHandle);
            }
        }

        /// <summary>Build the full topology tree for a single body.</summary>
        public static JObject BuildBodyTopology(dynamic body, string detailLevel = "summary", bool includeConvexity = false)
        {
            double? bodyTag = SafeFloat(body.Tag);
            string bodyName = "";
            try { bodyName = body.Name.ToString(); } catch { }

            var bodyInfo = new JObject();
            bodyInfo["body_id"] = bodyTag != null ? (JToken)(int)bodyTag.Value : (JToken)null;
            bodyInfo["xt_node_index"] = null;
            bodyInfo["name"] = bodyName;
            bodyInfo["type"] = (bool)body.IsSolidBody ? "solid" : "sheet";

            var faceList = body.GetFaces();
            var edgeList = body.GetEdges();
            int faceCount = 0, edgeCount = 0;
            foreach (var f in faceList) faceCount++;
            foreach (var e in edgeList) edgeCount++;

            bodyInfo["face_count"] = faceCount;
            bodyInfo["edge_count"] = edgeCount;

            // NX2412: body.GetVertices() doesn't exist. Estimate from edge endpoints.
            try
            {
                var vertexSet = new HashSet<int>();
                foreach (dynamic edge in body.GetEdges())
                {
                    try
                    {
                        var verts = edge.GetVertices();
                        foreach (dynamic v in verts)
                        {
                            try
                            {
                                double? vTag = SafeFloat(v.Tag);
                                if (vTag != null) vertexSet.Add((int)vTag.Value);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                bodyInfo["vertex_count"] = vertexSet.Count;
            }
            catch
            {
                bodyInfo["vertex_count"] = 0;
            }

            if (detailLevel == "summary")
                return bodyInfo;

            // Get UF session for face/edge operations
            UFSession ufs = null;
            try { ufs = UFSession.GetUFSession(); } catch { }

            // "full" or "1-ring" - include face-level detail
            // Bounding box computation
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            var faceArray = new JArray();
            foreach (dynamic face in body.GetFaces())
            {
                var faceSummary = BuildFaceSummary(face, ufs: ufs);
                // Enrich with radius, axis, normal for curved faces
                if (ufs != null)
                {
                    try { EnrichFaceGeometry(face, faceSummary, ufs); }
                    catch { /* best-effort enrichment */ }
                }
                faceArray.Add(faceSummary);

                // W8 (2026-09-03, A8 修复): bbox 取每面 UF FaceAskBoundingBox 的并集 —
                // 旧面中点法对回转体塌缩 (面中点全落旋转轴平面 → size_y=0)。
                // faceSummary 的 mid_point 仍保留 (供 normal/半径分析), 只是不再参与 bbox。
                if (ufs != null)
                {
                    try
                    {
                        double[] bb = new double[6];
                        ufs.Sf.FaceAskBoundingBox(face.Tag, bb);
                        if (bb[0] < minX) minX = bb[0];
                        if (bb[1] < minY) minY = bb[1];
                        if (bb[2] < minZ) minZ = bb[2];
                        if (bb[3] > maxX) maxX = bb[3];
                        if (bb[4] > maxY) maxY = bb[4];
                        if (bb[5] > maxZ) maxZ = bb[5];
                    }
                    catch { }
                }
            }
            bodyInfo["faces"] = faceArray;

            // Add bounding box (union of per-face UF bounding boxes) — 无面或全部失败则省略
            if (minX != double.MaxValue)
            {
                var bbox = new JObject();
                bbox["min"] = new JArray(Math.Round(minX, 4), Math.Round(minY, 4), Math.Round(minZ, 4));
                bbox["max"] = new JArray(Math.Round(maxX, 4), Math.Round(maxY, 4), Math.Round(maxZ, 4));
                bbox["size_x"] = Math.Round(maxX - minX, 4);
                bbox["size_y"] = Math.Round(maxY - minY, 4);
                bbox["size_z"] = Math.Round(maxZ - minZ, 4);
                bodyInfo["bbox"] = bbox;
            }

            // Edge summaries + Face Adjacency Graph (Phase 0.1) for "full"
            if (detailLevel == "full")
            {
                var edgesArray = new JArray();
                var faceAdjacency = new JArray();
                var faceEdgesMap = new Dictionary<int, List<int>>();

                foreach (dynamic edge in body.GetEdges())
                {
                    int edgeTag = 0;
                    double? et = SafeFloat(edge.Tag);
                    if (et != null) edgeTag = (int)et.Value;

                    var edgeData = new JObject();
                    edgeData["edge_id"] = et;

                    // NX2412: SolidEdgeType returns EdgeEdgeTypeMemberType with .value int
                    try
                    {
                        int intVal = Convert.ToInt32(edge.SolidEdgeType.value);
                        edgeData["curve_type"] = EdgeTypeIntMap.ContainsKey(intVal)
                            ? EdgeTypeIntMap[intVal]
                            : "TYPE_" + intVal;
                    }
                    catch
                    {
                        try
                        {
                            string ct = edge.SolidEdgeType.ToString();
                            edgeData["curve_type"] = ct.Split('.').Last().ToUpperInvariant();
                        }
                        catch { edgeData["curve_type"] = "UNKNOWN"; }
                    }

                    try
                    {
                        double length = Convert.ToDouble(edge.GetLength());
                        edgeData["length"] = Math.Round(length, 6);
                    }
                    catch { /* GetLength may fail */ }

                    // Phase 0.1: Build face adjacency graph via edge.GetFaces()
                    try
                    {
                        var adjFaces = edge.GetFaces();
                        var faceTags = new List<int>();
                        var faceObjs = new List<dynamic>();
                        foreach (dynamic f in adjFaces)
                        {
                            faceObjs.Add(f);
                            double? ft = SafeFloat(f.Tag);
                            if (ft != null) faceTags.Add((int)ft.Value);
                        }

                        if (faceTags.Count >= 1)
                            edgeData["face_a"] = faceTags[0];
                        if (faceTags.Count >= 2)
                            edgeData["face_b"] = faceTags[1];

                        // Build face->edges reverse mapping
                        foreach (int ft in faceTags)
                        {
                            if (!faceEdgesMap.ContainsKey(ft))
                                faceEdgesMap[ft] = new List<int>();
                            faceEdgesMap[ft].Add(edgeTag);
                        }

                        // Phase 0.2: Compute convexity if requested
                        if (includeConvexity && faceTags.Count == 2 && ufs != null)
                        {
                            try
                            {
                                string convexity = ClassifyEdgeConvexity(ufs, edge, faceObjs[0], faceObjs[1]);
                                edgeData["convexity"] = convexity;
                                faceAdjacency.Add(new JArray(faceTags[0], faceTags[1], edgeTag, convexity));
                            }
                            catch
                            {
                                faceAdjacency.Add(new JArray(faceTags[0], faceTags[1], edgeTag, "UNKNOWN"));
                            }
                        }
                        else if (faceTags.Count == 2)
                        {
                            faceAdjacency.Add(new JArray(faceTags[0], faceTags[1], edgeTag));
                        }
                    }
                    catch { /* GetFaces may fail on some edge types */ }

                    edgesArray.Add(edgeData);
                }

                // Enrich face entries with their edge lists (Phase 0.1)
                foreach (JObject faceEntry in faceArray)
                {
                    int faceId = (int)faceEntry["face_id"];
                    if (faceEdgesMap.ContainsKey(faceId))
                    {
                        faceEntry["edges"] = new JArray(faceEdgesMap[faceId]);
                    }
                }

                bodyInfo["edges"] = edgesArray;
                bodyInfo["face_adjacency"] = faceAdjacency;
            }

            return bodyInfo;
        }

        /// <summary>Count items in a dynamic collection.</summary>
        public static int CountCollection(dynamic collection)
        {
            int count = 0;
            foreach (var item in collection) count++;
            return count;
        }

        // ========================================================================
        // UFEvalsf Bridge — Phase 0.2 edge convexity infrastructure
        // Uses UF_EVALSF (surface evaluation), NOT UFEval (curve evaluation).
        // See cookbook/patterns/edge-convexity-nx2412-pitfalls.md
        // ========================================================================

        /// <summary>Create a face evaluator handle for a face tag using UF_EVALSF.</summary>
        public static IntPtr CreateFaceEvaluator(UFSession ufs, dynamic face)
        {
            IntPtr handle;
            int faceTag = (int)((NXOpen.Tag)face.Tag);
            ufs.Evalsf.Initialize2((NXOpen.Tag)face.Tag, out handle);
            LogToFile("[CreateFaceEvaluator] face=" + faceTag + " handle=" + handle.ToString());
            return handle;
        }

        /// <summary>Create an edge evaluator handle for an edge tag using UF_EVAL.</summary>
        public static IntPtr CreateEdgeEvaluator(UFSession ufs, dynamic edge)
        {
            IntPtr handle;
            ufs.Eval.Initialize2((NXOpen.Tag)edge.Tag, out handle);
            return handle;
        }

        /// <summary>Free an edge (UFEval) evaluator handle.</summary>
        public static void FreeEvaluator(UFSession ufs, IntPtr evalHandle)
        {
            if (evalHandle != IntPtr.Zero)
                ((dynamic)ufs.Eval).Free(evalHandle);
        }

        /// <summary>
        /// Free a face (UFEvalsf) evaluator handle.
        /// W8 (2026-09-03): NX2412 实测签名 UFEvalsf.Free(out IntPtr) — 旧代码直传值
        /// 触发 RuntimeBinderException "与 Free(out System.IntPtr) 最匹配的重载方法具有一些无效参数",
        /// 使 EnrichFaceGeometry 的 finally 必炸 → radius/axis enrichment 静默死亡。
        /// out 版优先, 失败再退回直传 (兼容其他版本)。
        /// </summary>
        public static void FreeFaceEvaluator(UFSession ufs, IntPtr evalHandle)
        {
            if (evalHandle == IntPtr.Zero) return;
            try
            {
                IntPtr h = evalHandle;
                ((dynamic)ufs.Evalsf).Free(out h);
                return;
            }
            catch { }
            try
            {
                ((dynamic)ufs.Evalsf).Free(evalHandle);
            }
            catch { }
        }

        /// <summary>Evaluate 3D point on a face at parametric (u,v) using UF_EVALSF.</summary>
        public static double[] EvaluateFacePoint(UFSession ufs, IntPtr evalHandle, double u, double v)
        {
            double[] uvParams = new double[2] { u, v };
            ModlSrfValue result;
            ((dynamic)ufs.Evalsf).Evaluate(evalHandle, 0, uvParams, out result);
            return new double[] { result.srf_pos[0], result.srf_pos[1], result.srf_pos[2] };
        }

        /// <summary>Evaluate face normal + tangent vectors at parametric (u,v) using UF_EVALSF.</summary>
        public static JObject EvaluateFaceVectors(UFSession ufs, IntPtr evalHandle, double u, double v)
        {
            double[] uvParams = new double[2] { u, v };
            ModlSrfValue result;
            ((dynamic)ufs.Evalsf).Evaluate(evalHandle, 1, uvParams, out result);

            // Normal = cross product of srf_du × srf_dv (first derivatives)
            double[] du = result.srf_du;
            double[] dv = result.srf_dv;
            double nx = du[1] * dv[2] - du[2] * dv[1];
            double ny = du[2] * dv[0] - du[0] * dv[2];
            double nz = du[0] * dv[1] - du[1] * dv[0];
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }

            var resultObj = new JObject();
            resultObj["normal"] = new JArray(Math.Round(nx, 9), Math.Round(ny, 9), Math.Round(nz, 9));
            resultObj["u_tangent"] = new JArray(Math.Round(du[0], 9), Math.Round(du[1], 9), Math.Round(du[2], 9));
            resultObj["v_tangent"] = new JArray(Math.Round(dv[0], 9), Math.Round(dv[1], 9), Math.Round(dv[2], 9));
            return resultObj;
        }

        /// <summary>Find closest (u,v) on a face to a 3D probe point using UF_EVALSF.
        /// Signature: FindClosestPoint(IntPtr handle, double[] point, out Pos3 result)
        /// Pos3 has fields: uv[2], closest_point[3], etc.
        /// </summary>
        public static double[] ClosestUV(UFSession ufs, IntPtr evalHandle, double[] point3d)
        {
            var pos3Type = ufs.Evalsf.GetType().Assembly.GetType("NXOpen.UF.UFEvalsf+Pos3");
            if (pos3Type == null) { LogToFile("[ClosestUV] Pos3 type not found"); return null; }
            var method = ufs.Evalsf.GetType().GetMethod("FindClosestPoint",
                new[] { typeof(IntPtr), typeof(double[]), pos3Type.MakeByRefType() });
            if (method == null) { LogToFile("[ClosestUV] FindClosestPoint(IntPtr,double[],Pos3@) not found"); return null; }
            var pos3 = Activator.CreateInstance(pos3Type);
            var args = new object[] { evalHandle, point3d, pos3 };
            method.Invoke(ufs.Evalsf, args);
            pos3 = args[2]; // out param written back
            // Extract UV from Pos3 — try common field names
            double[] uv = ReadStructPoint3d(pos3, "uv");
            if (uv == null) uv = ReadStructPoint3d(pos3, "param");
            if (uv != null) return new double[] { uv[0], uv[1] };
            // Fallback: try reading as double[2] field
            var uvField = pos3Type.GetField("uv") ?? pos3Type.GetField("param") ?? pos3Type.GetField("UV");
            if (uvField != null)
            {
                var uvVal = uvField.GetValue(pos3);
                if (uvVal is double[])
                {
                    var uvArr = (double[])uvVal;
                    if (uvArr.Length >= 2) return new double[] { uvArr[0], uvArr[1] };
                }
            }
            LogToFile("[ClosestUV] Could not extract UV from Pos3. Fields: " +
                string.Join(", ", Array.ConvertAll(pos3Type.GetFields(), f => f.Name)));
            return null;
        }

        /// <summary>Get parametric UV limits of a face using UF_EVALSF.</summary>
        public static double[] GetFaceLimits(UFSession ufs, IntPtr evalHandle)
        {
            double[] limits = new double[4];
            ((dynamic)ufs.Evalsf).AskFaceUvMinmax(evalHandle, limits);
            return limits;
        }

        /// <summary>
        /// Compute the midpoint of an edge as a 3D point [x, y, z].
        ///
        /// Strategy (ordered by reliability):
        ///   1. UF_CURVE_ask_line_data  — LINEAR edges (type 1), direct start/end
        ///   2. UF_CURVE_ask_arc_data   — CIRCULAR edges (type 2), center+radius+angles
        ///   3. GetVertices() averaging  — any edge with accessible vertices
        ///   4. UFEval at param 0.5     — generic curve evaluation (last resort)
        /// </summary>
        public static double[] GetEdgeMidpoint(dynamic edge)
        {
            UFSession ufs = null;
            try { ufs = UFSession.GetUFSession(); }
            catch { /* will be created on demand */ }

            NXOpen.Tag edgeTag = (NXOpen.Tag)edge.Tag;
            int edgeType = -1;
            try { edgeType = (int)edge.SolidEdgeType; }
            catch (Exception exType) { LogToFile("[GetEdgeMidpoint] SolidEdgeType failed: " + exType.Message); }

            // ── Approach 1: NXOpen.Edge.GetVertices(out Point3d, out Point3d) ──
            // Correct signature: GetVertices(Point3d@ vertex1, Point3d@ vertex2)
            // Uses reflection because dynamic+out fails overload resolution.
            try
            {
                var edgeTypeObj = ((object)edge).GetType();
                // Find the 2-param overload (out Point3d, out Point3d)
                var getVtxMethod = edgeTypeObj.GetMethod("GetVertices",
                    new[] { typeof(NXOpen.Point3d).MakeByRefType(), typeof(NXOpen.Point3d).MakeByRefType() });
                if (getVtxMethod != null)
                {
                    var vtxArgs = new object[] { new NXOpen.Point3d(), new NXOpen.Point3d() };
                    getVtxMethod.Invoke((object)edge, vtxArgs);
                    var v1 = (NXOpen.Point3d)vtxArgs[0];
                    var v2 = (NXOpen.Point3d)vtxArgs[1];
                    double mx = (v1.X + v2.X) / 2.0;
                    double my = (v1.Y + v2.Y) / 2.0;
                    double mz = (v1.Z + v2.Z) / 2.0;
                    LogToFile("[GetEdgeMidpoint] NXOpen.GetVertices OK: [" + v1.X+","+v1.Y+","+v1.Z + "] → [" + v2.X+","+v2.Y+","+v2.Z + "] mid=[" + mx+","+my+","+mz + "]");
                    return new double[] { mx, my, mz };
                }
                else { LogToFile("[GetEdgeMidpoint] GetVertices(Point3d,Point3d) not found on " + edgeTypeObj.FullName); }
            }
            catch (Exception ex1) { LogToFile("[GetEdgeMidpoint] NXOpen.GetVertices failed: " + ex1.Message); }

            // ── Approach 2: UF_CURVE_ask_line_data (LINEAR edges) ──
            if (edgeType == 1)
            {
                try
                {
                    if (ufs == null) ufs = UFSession.GetUFSession();
                    var askLineMethod = ufs.Curve.GetType().GetMethod("AskLineData",
                        new[] { typeof(NXOpen.Tag), typeof(NXOpen.UF.UFCurve.Line).MakeByRefType() });
                    if (askLineMethod != null)
                    {
                        var lineArgs = new object[] { edgeTag, new NXOpen.UF.UFCurve.Line() };
                        askLineMethod.Invoke(ufs.Curve, lineArgs);
                        var lineData = lineArgs[1]; // out param written back
                        double[] sp = ReadStructPoint3d(lineData, "start_point");
                        double[] ep = ReadStructPoint3d(lineData, "end_point");
                        if (sp != null && ep != null)
                        {
                            LogToFile("[GetEdgeMidpoint] UF_CURVE line: [" + sp[0]+","+sp[1]+","+sp[2] + "] → [" + ep[0]+","+ep[1]+","+ep[2] + "]");
                            return new double[] { (sp[0]+ep[0])/2.0, (sp[1]+ep[1])/2.0, (sp[2]+ep[2])/2.0 };
                        }
                    }
                    else { LogToFile("[GetEdgeMidpoint] AskLineData method not found"); }
                }
                catch (Exception exLine) { LogToFile("[GetEdgeMidpoint] UF_CURVE line failed: " + exLine.Message); }
            }

            // ── Approach 3: UF_CURVE_ask_arc_data (CIRCULAR edges) ──
            if (edgeType == 2)
            {
                try
                {
                    if (ufs == null) ufs = UFSession.GetUFSession();
                    var askArcMethod = ufs.Curve.GetType().GetMethod("AskArcData",
                        new[] { typeof(NXOpen.Tag), typeof(NXOpen.UF.UFCurve.Arc).MakeByRefType() });
                    if (askArcMethod != null)
                    {
                        var arcArgs = new object[] { edgeTag, new NXOpen.UF.UFCurve.Arc() };
                        askArcMethod.Invoke(ufs.Curve, arcArgs);
                        var arcData = arcArgs[1];
                        double[] center = ReadStructPoint3d(arcData, "arc_center");
                        double radius = ReadStructDouble(arcData, "radius");
                        double startAngle = ReadStructDouble(arcData, "start_angle");
                        double endAngle = ReadStructDouble(arcData, "end_angle");
                        if (center != null && !double.IsNaN(radius))
                        {
                            double midAngle = (startAngle + endAngle) / 2.0;
                            double[] pt = new double[] {
                                center[0] + radius * Math.Cos(midAngle),
                                center[1] + radius * Math.Sin(midAngle),
                                center[2]
                            };
                            LogToFile("[GetEdgeMidpoint] UF_CURVE arc: center=[" + center[0]+","+center[1]+","+center[2] + "] r=" + radius + " midAngle=" + midAngle);
                            return pt;
                        }
                    }
                    else { LogToFile("[GetEdgeMidpoint] AskArcData method not found"); }
                }
                catch (Exception exArc) { LogToFile("[GetEdgeMidpoint] UF_CURVE arc failed: " + exArc.Message); }
            }

            // ── Approach 4: UFEval.Evaluate(IntPtr, int, double, double[], double[]) ──
            // Correct signature: Evaluate(handle, derivative_order, param, point[3], derivs[])
            try
            {
                if (ufs == null) ufs = UFSession.GetUFSession();
                IntPtr evalHandle;
                ufs.Eval.Initialize2(edgeTag, out evalHandle);
                if (evalHandle != IntPtr.Zero)
                {
                    try
                    {
                        double[] point = new double[3];
                        double[] derivs = new double[6]; // unused but required by signature
                        // Use reflection for exact 5-param overload
                        var evalMethod = ufs.Eval.GetType().GetMethod("Evaluate",
                            new[] { typeof(IntPtr), typeof(int), typeof(double), typeof(double[]), typeof(double[]) });
                        if (evalMethod != null)
                        {
                            evalMethod.Invoke(ufs.Eval, new object[] { evalHandle, 0, 0.5, point, derivs });
                        }
                        else
                        {
                            // Fallback: try dynamic (may fail if DLR can't resolve)
                            ((dynamic)ufs.Eval).Evaluate(evalHandle, 0, 0.5, point, derivs);
                        }
                        LogToFile("[GetEdgeMidpoint] UFEval point: [" + point[0] + "," + point[1] + "," + point[2] + "]");
                        if (point[0] != 0 || point[1] != 0 || point[2] != 0)
                            return new double[] { point[0], point[1], point[2] };
                    }
                    finally { try { ufs.Eval.Free(evalHandle); } catch { } }
                }
                else { LogToFile("[GetEdgeMidpoint] UFEval Initialize2 returned null handle (type=" + edgeType + ")"); }
            }
            catch (Exception ex2) { LogToFile("[GetEdgeMidpoint] UFEval failed: " + ex2.Message); }

            LogToFile("[GetEdgeMidpoint] ALL approaches failed for edge tag " + (int)edgeTag + " type=" + edgeType);
            return null;
        }

        /// <summary>
        /// Read a 3D point [x,y,z] from a dynamic struct by field name.
        /// Tries snake_case first (e.g. start_point), then PascalCase (e.g. StartPoint).
        /// </summary>
        private static double[] ReadStructPoint3d(dynamic structObj, string snakeName)
        {
            try
            {
                // Try field access via reflection (handles both naming conventions)
                var type = ((object)structObj).GetType();
                // snake_case (e.g. start_point)
                var field = type.GetField(snakeName);
                if (field == null)
                {
                    // PascalCase (e.g. StartPoint)
                    string pascal = SnakeToPascal(snakeName);
                    field = type.GetField(pascal);
                }
                if (field != null)
                {
                    var val = field.GetValue((object)structObj);
                    if (val is double[])
                    {
                        var arr = (double[])val;
                        if (arr.Length >= 3)
                            return new double[] { arr[0], arr[1], arr[2] };
                    }
                    // Could also be Array type
                    if (val is Array)
                    {
                        var arr2 = (Array)val;
                        if (arr2.Length >= 3)
                            return new double[] { (double)arr2.GetValue(0), (double)arr2.GetValue(1), (double)arr2.GetValue(2) };
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Read a double from a dynamic struct by field name.</summary>
        private static double ReadStructDouble(dynamic structObj, string snakeName)
        {
            try
            {
                var type = ((object)structObj).GetType();
                var field = type.GetField(snakeName);
                if (field == null) field = type.GetField(SnakeToPascal(snakeName));
                if (field != null)
                {
                    var val = field.GetValue((object)structObj);
                    return Convert.ToDouble(val);
                }
            }
            catch { }
            return double.NaN;
        }

        /// <summary>Convert snake_case to PascalCase (e.g. start_point → StartPoint).</summary>
        private static string SnakeToPascal(string snake)
        {
            if (string.IsNullOrEmpty(snake)) return snake;
            var parts = snake.Split('_');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
            }
            return string.Join("", parts);
        }

        /// <summary>Extract [x,y,z] from a Vertex object (NX2412 compatible).</summary>
        private static double[] GetVertexCoords(dynamic vertex)
        {
            // Method 0: Try Position property (NXOpen.Vertex specific)
            try
            {
                dynamic pos = vertex.Position;
                if (pos != null)
                {
                    double px = (double)pos.X;
                    double py = (double)pos.Y;
                    double pz = (double)pos.Z;
                    if (!double.IsNaN(px) && !double.IsNaN(py) && !double.IsNaN(pz))
                        return new double[] { px, py, pz };
                }
            }
            catch (Exception ex0) { LogToFile("[GetVertexCoords] .Position fail: " + ex0.Message); }

            try
            {
                // Method 1: Direct .X .Y .Z properties on Vertex (NXOpen.Point base class)
                double x = (double)vertex.X;
                double y = (double)vertex.Y;
                double z = (double)vertex.Z;
                if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z))
                    return new double[] { x, y, z };
            }
            catch (Exception ex1) { LogToFile("[GetVertexCoords] .X fail: " + ex1.Message); }
            try
            {
                // Method 2: .Coordinates (NXOpen.Point3d) property
                dynamic coord = vertex.Coordinates;
                double x = (double)coord.X;
                double y = (double)coord.Y;
                double z = (double)coord.Z;
                if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z))
                    return new double[] { x, y, z };
            }
            catch (Exception ex2) { LogToFile("[GetVertexCoords] .Coordinates fail: " + ex2.Message); }
            try
            {
                // Method 3: UF_SF_ask_vertex_data via tag
                UFSession ufs = UFSession.GetUFSession();
                double[] result = new double[3];
                ((dynamic)ufs.Sf).AskVertexData((NXOpen.Tag)vertex.Tag, result);
                if (result != null && result.Length >= 3)
                    return new double[] { result[0], result[1], result[2] };
            }
            catch (Exception ex3) { LogToFile("[GetVertexCoords] UF ask fail: " + ex3.Message); }

            // Method 4: Try reflection on the Vertex to find ANY property/field containing coordinates
            try
            {
                var vType = ((object)vertex).GetType();
                foreach (var prop in vType.GetProperties())
                {
                    if (prop.Name.IndexOf("oint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.Name.IndexOf("coord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.Name.IndexOf("position", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            var val = prop.GetValue((object)vertex, null);
                            if (val != null)
                            {
                                dynamic dval = val;
                                try
                                {
                                    double vx = (double)dval.X;
                                    double vy = (double)dval.Y;
                                    double vz = (double)dval.Z;
                                    if (!double.IsNaN(vx) && !double.IsNaN(vy) && !double.IsNaN(vz))
                                        return new double[] { vx, vy, vz };
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex4) { LogToFile("[GetVertexCoords] reflection fail: " + ex4.Message); }

            return null;
        }

        /// <summary>
        /// Evaluate the unit normal on a face at the closest point to a 3D query point.
        /// Returns [nx, ny, nz] or null.
        /// </summary>
        public static double[] GetFaceNormalAtPoint(UFSession ufs, dynamic face, double[] point3d)
        {
            IntPtr evalHandle = IntPtr.Zero;
            try
            {
                int faceTag = (int)((NXOpen.Tag)face.Tag);
                evalHandle = CreateFaceEvaluator(ufs, face);
                if (evalHandle == IntPtr.Zero) { LogToFile("[GetFaceNormal] CreateFaceEvaluator returned NULL for face " + faceTag); return null; }
                double[] uv = ClosestUV(ufs, evalHandle, point3d);
                if (uv == null) { LogToFile("[GetFaceNormal] ClosestUV returned NULL for face " + faceTag); return null; }
                JObject vecs = EvaluateFaceVectors(ufs, evalHandle, uv[0], uv[1]);
                if (vecs == null) { LogToFile("[GetFaceNormal] EvaluateFaceVectors returned NULL for face " + faceTag + " uv=[" + uv[0]+","+uv[1] + "]"); return null; }
                var n = vecs["normal"] as JArray;
                if (n == null) { LogToFile("[GetFaceNormal] normal key missing in result for face " + faceTag); return null; }
                return new double[] { (double)n[0], (double)n[1], (double)n[2] };
            }
            catch (Exception ex) { LogToFile("[GetFaceNormal] EXCEPTION: " + ex.Message); return null; }
            finally
            {
                if (evalHandle != IntPtr.Zero)
                {
                    try { FreeFaceEvaluator(ufs, evalHandle); } catch { }
                }
            }
        }

        /// <summary>
        /// Classify edge convexity by comparing face normals at the edge midpoint.
        /// Returns "CONVEX", "CONCAVE", "SMOOTH", or "UNKNOWN".
        /// Accepts pre-fetched face objects to avoid double-enumeration of edge.GetFaces().
        /// </summary>
        public static string ClassifyEdgeConvexity(UFSession ufs, dynamic edge, dynamic faceA = null, dynamic faceB = null)
        {
            try
            {
                // Step 1: Get adjacent faces (use provided faces to avoid double-enumeration)
                if (faceA == null || faceB == null)
                {
                    var faces = edge.GetFaces();
                    int fcount = 0;
                    foreach (dynamic f in faces)
                    {
                        if (fcount == 0) faceA = f;
                        else if (fcount == 1) { faceB = f; break; }
                        fcount++;
                    }
                    if (fcount != 2) return fcount < 2 ? "FREE_EDGE" : "NON_MANIFOLD";
                }

                // Step 2: Compute edge midpoint
                double[] midpoint = GetEdgeMidpoint(edge);
                if (midpoint == null) return "UNKNOWN";

                // Step 3: Get normals at closest point on each face
                double[] normalA = GetFaceNormalAtPoint(ufs, faceA, midpoint);
                double[] normalB = GetFaceNormalAtPoint(ufs, faceB, midpoint);
                if (normalA == null || normalB == null) return "UNKNOWN";

                // Step 4: Dot product classification
                double dot = normalA[0] * normalB[0]
                           + normalA[1] * normalB[1]
                           + normalA[2] * normalB[2];

                double absDot = Math.Abs(dot);
                if (absDot > 0.9999) return "SMOOTH";   // ~0° or ~180° between normals
                if (dot < 0) return "CONCAVE";           // normals converge → internal
                return "CONVEX";                         // normals diverge → external
            }
            catch { return "UNKNOWN"; }
        }
    }

    // ========================================================================
    // 1. nx_inspect_topology
    // ========================================================================

    /// <summary>
    /// Inspect the B-rep topology of bodies in the work part.
    /// Returns body->face->edge hierarchy with surface types, face adjacency graph (Phase 0.1),
    /// and optional edge convexity (Phase 0.2) via UFEval.
    /// detail_level: 'summary' (counts only), 'full' (faces+edges+FAG), '1-ring' (faces only).
    /// include_convexity: true to compute CONVEX/CONCAVE/SMOOTH per edge.
    /// </summary>
    /// Parameters:
    ///   body_id (string, optional) - body_id parameter.
    ///
    public class InspectTopologyTool : IToolHandler
    {
        public string Name { get { return "nx_inspect_topology"; } }
        public string Description { get { return "Inspect the B-rep topology of bodies in the work part. Returns body->face->edge hierarchy with face adjacency graph and optional edge convexity."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var bodies = workPart.Bodies;
                bool hasBodies = false;
                foreach (var b in bodies) { hasBodies = true; break; }
                if (!hasBodies)
                {
                    var skipData = new JObject();
                    skipData["bodies"] = new JArray();
                    skipData["count"] = 0;
                    return new ToolResult { Success = true, Message = "No bodies found in work part.", Data = skipData }.ToJson();
                }

                string level = "summary";
                string levelParam = parameters.Value<string>("detail_level");
                if (!string.IsNullOrEmpty(levelParam))
                    level = levelParam.Trim().ToLowerInvariant();

                if (level != "summary" && level != "full" && level != "1-ring")
                {
                    return ToolResult.Fail("Invalid detail_level '" + levelParam + "'. Use: summary, full, or 1-ring.").ToJson();
                }

                int? bodyIdFilter = null;
                string bodyIdString = null;
                if (parameters["body_id"] != null)
                {
                    string raw = parameters["body_id"].ToString().Trim();
                    int parsedInt;
                    if (int.TryParse(raw, out parsedInt))
                        bodyIdFilter = parsedInt;
                    else
                        bodyIdString = raw;
                }

                bool includeConvexity = parameters.Value<bool?>("include_convexity") ?? false;

                var resultBodies = new JArray();
                foreach (dynamic body in bodies)
                {
                    if (bodyIdFilter != null || bodyIdString != null)
                    {
                        if (bodyIdFilter != null)
                        {
                            double? tag = InspectHelpers.SafeFloat(body.Tag);
                            if (tag == null || (int)tag.Value != bodyIdFilter.Value)
                            {
                                if (bodyIdString == null) continue;
                                string bName = (string)body.Name;
                                if (bName != bodyIdString) continue;
                            }
                        }
                        else
                        {
                            string bName = (string)body.Name;
                            if (bName != bodyIdString) continue;
                        }
                    }
                    resultBodies.Add(InspectHelpers.BuildBodyTopology(body, level, includeConvexity));
                }

                // M-006 (2026-09-03): 过滤器无命中时静默返回空数组 → 改为明确报错 + 可用体清单
                if (resultBodies.Count == 0 && (bodyIdFilter != null || bodyIdString != null))
                {
                    string wanted = bodyIdString ?? bodyIdFilter.ToString();
                    var avail = new JArray();
                    foreach (dynamic b in bodies)
                    {
                        var entry = new JObject();
                        entry["tag"] = InspectHelpers.SafeFloat(b.Tag);
                        string bName = "";
                        try { bName = (string)b.Name ?? ""; } catch { }
                        entry["name"] = bName;
                        string bJid = "";
                        try { bJid = (string)b.JournalIdentifier ?? ""; } catch { }
                        entry["journal_id"] = bJid;
                        avail.Add(entry);
                    }
                    return ToolResult.Fail(
                        "No body matches filter '" + wanted + "'. " +
                        "Available bodies (tag / name / journal_id): " + avail.ToString(Newtonsoft.Json.Formatting.None)).ToJson();
                }

                var data = new JObject();
                data["bodies"] = resultBodies;
                data["count"] = resultBodies.Count;
                return new ToolResult
                {
                    Success = true,
                    Message = "Inspected " + resultBodies.Count + " body(ies) at '" + level + "' level.",
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_inspect_topology failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 1b. nx_evaluate_face — UFEval bridge test tool
    // ========================================================================

    /// <summary>
    /// Evaluate a face's normal vector at a given (u,v) parameter or at the closest
    /// point to a probe point. Uses UF.UFEval (Phase 0.2).
    /// Modes: 'parametric' — provide (u, v); 'closest_point' — provide (px, py, pz).
    /// </summary>
    public class EvaluateFaceTool : IToolHandler
    {
        public string Name { get { return "nx_evaluate_face"; } }
        public string Description { get { return "Evaluate face normal and tangent vectors at a (u,v) parameter or closest point to a probe."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                int faceId = parameters.Value<int>("face_id");
                string mode = parameters.Value<string>("mode") ?? "closest_point";

                // Find face by tag
                dynamic targetFace = null;
                foreach (dynamic body in workPart.Bodies)
                {
                    foreach (dynamic face in body.GetFaces())
                    {
                        double? tag = InspectHelpers.SafeFloat(face.Tag);
                        if (tag != null && (int)tag.Value == faceId)
                        {
                            targetFace = face;
                            break;
                        }
                    }
                    if (targetFace != null) break;
                }

                if (targetFace == null)
                    return ToolResult.Fail("Face with tag " + faceId + " not found.").ToJson();

                UFSession ufs = null;
                try { ufs = UFSession.GetUFSession(); } catch { }
                if (ufs == null)
                    return ToolResult.Fail("UFSession not available.").ToJson();

                IntPtr evalHandle = IntPtr.Zero;
                try
                {
                    evalHandle = InspectHelpers.CreateFaceEvaluator(ufs, targetFace);
                    if (evalHandle == IntPtr.Zero)
                        return ToolResult.Fail("Failed to create UFEval for face " + faceId).ToJson();

                    double u = 0, v = 0;
                    if (mode == "parametric")
                    {
                        u = parameters.Value<double>("u");
                        v = parameters.Value<double>("v");
                    }
                    else
                    {
                        double px = parameters.Value<double>("px");
                        double py = parameters.Value<double>("py");
                        double pz = parameters.Value<double>("pz");
                        double[] uv = InspectHelpers.ClosestUV(ufs, evalHandle, new double[] { px, py, pz });
                        u = uv[0]; v = uv[1];
                    }

                    double[] point = InspectHelpers.EvaluateFacePoint(ufs, evalHandle, u, v);
                    JObject vectors = InspectHelpers.EvaluateFaceVectors(ufs, evalHandle, u, v);

                    var data = new JObject();
                    data["face_id"] = faceId;
                    data["surface_type"] = InspectHelpers.GetSurfaceType(targetFace);
                    data["mode"] = mode;
                    data["u"] = Math.Round(u, 9);
                    data["v"] = Math.Round(v, 9);
                    data["point"] = new JArray(Math.Round(point[0], 9), Math.Round(point[1], 9), Math.Round(point[2], 9));
                    data["normal"] = vectors["normal"];
                    data["u_tangent"] = vectors["u_tangent"];
                    data["v_tangent"] = vectors["v_tangent"];

                    return new ToolResult
                    {
                        Success = true,
                        Message = string.Format("Face {0} evaluated at u={1:F6}, v={2:F6}.", faceId, u, v),
                        Data = data
                    }.ToJson();
                }
                finally
                {
                    if (evalHandle != IntPtr.Zero)
                    {
                        try { InspectHelpers.FreeFaceEvaluator(ufs, evalHandle); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_evaluate_face failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 2. nx_inspect_geometry
    // ========================================================================

    /// <summary>
    /// Extract detailed geometric definition of a face, edge, or vertex.
    /// Returns surface/curve parameters, control points, normals, tolerances.
    /// Set include_xt=true to include Parasolid XT node reference for deep inspection.
    /// </summary>
    public class InspectGeometryTool : IToolHandler
    {
        public string Name { get { return "nx_inspect_geometry"; } }
        public string Description { get { return "Extract geometric definition of a face, edge, or vertex: face type/radius/direction/point/bbox; edge type/length/endpoints/midpoint/tangent; vertex coords."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string entityType = parameters.Value<string>("entity_type");
                if (string.IsNullOrEmpty(entityType))
                    return ToolResult.Fail("Parameter 'entity_type' is required.").ToJson();

                int entityId = parameters.Value<int>("entity_id");
                bool includeXt = parameters.Value<bool?>("include_xt") ?? false;

                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string etype = entityType.Trim().ToLowerInvariant();

                if (etype == "face")
                    return InspectFaceGeometry(workPart, entityId, includeXt);
                else if (etype == "edge")
                    return InspectEdgeGeometry(workPart, entityId, includeXt);
                else if (etype == "vertex")
                    return InspectVertexGeometry(workPart, entityId);
                else
                    return ToolResult.Fail("Invalid entity_type '" + entityType + "'. Use: face, edge, or vertex.").ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_inspect_geometry failed: " + ex.Message).ToJson();
            }
        }

        private JObject InspectFaceGeometry(dynamic workPart, int faceId, bool includeXt)
        {
            // Find face by tag across all bodies
            dynamic target = null;
            foreach (dynamic body in workPart.Bodies)
            {
                foreach (dynamic face in body.GetFaces())
                {
                    double? tag = InspectHelpers.SafeFloat(face.Tag);
                    if (tag != null && (int)tag.Value == faceId)
                    {
                        target = face;
                        break;
                    }
                }
                if (target != null) break;
            }

            if (target == null)
            {
                return ToolResult.Fail("Face with tag " + faceId + " not found. Use nx_inspect_topology (detail_level=full) to list face tags.").ToJson();
            }

            var data = new JObject();
            data["face_id"] = faceId;
            data["surface_type"] = InspectHelpers.GetSurfaceType(target);

            // NX2412: face.GetSurface() doesn't exist. Provide surface info from face metadata.
            var surfaceInfo = new JObject();
            surfaceInfo["type"] = data["surface_type"].ToString();

            try
            {
                var edges = target.GetEdges();
                int edgeCount = 0;
                foreach (var e in edges) edgeCount++;
                surfaceInfo["edge_count"] = edgeCount;
            }
            catch { }

            data["surface"] = surfaceInfo;

            // Face geometry via UF.Modl.AskFaceData (point/dir/radius/bbox) — M-004 (2026-09-03):
            // 旧实现只有 type+bbox; 面半径/轴向缺失, 下游只能从 bbox 反推(10-2a 逆向教训)。
            // 同签名先例: InspectSelectionTool (line ~2242) 实测可用。
            try
            {
                UFSession uf = UFSession.GetUFSession();
                int ftype;
                double[] pt = new double[3], dir = new double[3], box = new double[6];
                double radius, rad_data;
                int norm_dir;
                uf.Modl.AskFaceData(target.Tag, out ftype, pt, dir, box, out radius, out rad_data, out norm_dir);
                surfaceInfo["face_type_int"] = ftype;
                surfaceInfo["point"] = new JArray(
                    Math.Round((double)pt[0], 6), Math.Round((double)pt[1], 6), Math.Round((double)pt[2], 6));
                surfaceInfo["direction"] = new JArray(
                    Math.Round((double)dir[0], 6), Math.Round((double)dir[1], 6), Math.Round((double)dir[2], 6));
                surfaceInfo["radius"] = Math.Round(radius, 6);
                surfaceInfo["rad_data"] = Math.Round(rad_data, 6);
                surfaceInfo["norm_dir_sign"] = norm_dir;
                if (box != null && box.Length >= 6)
                {
                    var minObj = new JObject();
                    minObj["x"] = Math.Round((double)box[0], 6);
                    minObj["y"] = Math.Round((double)box[1], 6);
                    minObj["z"] = Math.Round((double)box[2], 6);
                    var maxObj = new JObject();
                    maxObj["x"] = Math.Round((double)box[3], 6);
                    maxObj["y"] = Math.Round((double)box[4], 6);
                    maxObj["z"] = Math.Round((double)box[5], 6);
                    data["bounding_box"] = new JObject() { { "min", minObj }, { "max", maxObj } };
                }
            }
            catch { /* AskFaceData may fail on some face types */ }

            // XT node reference (dual-track protocol)
            if (includeXt)
            {
                var xtRef = new JObject();
                xtRef["node_id"] = faceId;
                xtRef["node_type"] = 14; // FACE in V35
                xtRef["node_name"] = "FACE";
                xtRef["note"] = "Export XT with nx_export_xt, then query by node_id with nx_inspect_xt_node";
                data["xt_reference"] = xtRef;
            }

            return new ToolResult
            {
                Success = true,
                Message = "Face " + faceId + " geometry (" + data["surface_type"] + ").",
                Data = data
            }.ToJson();
        }

        private JObject InspectEdgeGeometry(dynamic workPart, int edgeId, bool includeXt)
        {
            // Find edge by tag across all bodies
            dynamic target = null;
            foreach (dynamic body in workPart.Bodies)
            {
                foreach (dynamic edge in body.GetEdges())
                {
                    double? tag = InspectHelpers.SafeFloat(edge.Tag);
                    if (tag != null && (int)tag.Value == edgeId)
                    {
                        target = edge;
                        break;
                    }
                }
                if (target != null) break;
            }

            if (target == null)
            {
                return ToolResult.Fail("Edge with tag " + edgeId + " not found. Use nx_inspect_topology (detail_level=full) to list edge tags.").ToJson();
            }

            var data = new JObject();
            data["edge_id"] = edgeId;

            // Curve type (NX2412: SolidEdgeType returns EdgeEdgeTypeMemberType with .value int)
            try
            {
                int intVal = Convert.ToInt32(target.SolidEdgeType.value);
                data["curve_type"] = InspectHelpers.EdgeTypeIntMap.ContainsKey(intVal)
                    ? InspectHelpers.EdgeTypeIntMap[intVal]
                    : "TYPE_" + intVal;
            }
            catch
            {
                try
                {
                    string ct = target.SolidEdgeType.ToString();
                    data["curve_type"] = ct.Split('.').Last().ToUpperInvariant();
                }
                catch { data["curve_type"] = "UNKNOWN"; }
            }

            // Length
            try
            {
                data["length"] = Math.Round(Convert.ToDouble(target.GetLength()), 6);
            }
            catch { }

            // NX2412: edge.Tolerance doesn't exist. Skip tolerance.

            // NX2412: edge.GetCurve() doesn't exist. Use edge metadata for curve info.
            var curveInfo = new JObject();
            curveInfo["type"] = data["curve_type"] != null ? data["curve_type"].ToString() : null;

            try
            {
                // Edge endpoints via edge.GetVertices()
                var vertices = target.GetVertices();
                int vertCount = 0;
                foreach (var v in vertices) vertCount++;

                if (vertCount >= 2)
                {
                    var vertArray = new JArray();
                    foreach (dynamic v in vertices) vertArray.Add(v);

                    try
                    {
                        curveInfo["start"] = InspectHelpers.SafeXyz(vertArray[0]);
                        curveInfo["end"] = InspectHelpers.SafeXyz(vertArray[1]);
                    }
                    catch { }
                }

                // For arcs, try EdgeEvaluateParamLocation at midpoint
                string ct = data["curve_type"] != null ? data["curve_type"].ToString() : null;
                if (ct == "CIRCLE" || ct == "ELLIPSE")
                {
                    try
                    {
                        UFSession ufs = UFSession.GetUFSession();
                        double[] r = new double[8];
                        ufs.Sf.EdgeEvaluateParamLocation(target.Tag, 0.5, r);
                        if (r != null && r.Length >= 6)
                        {
                            curveInfo["mid_point"] = new JArray(
                                Math.Round((double)r[2], 6),
                                Math.Round((double)r[3], 6),
                                Math.Round((double)r[4], 6)
                            );
                            if (r.Length >= 8)
                            {
                                curveInfo["tangent"] = new JArray(
                                    Math.Round((double)r[5], 6),
                                    Math.Round((double)r[6], 6),
                                    Math.Round((double)r[7], 6)
                                );
                            }
                        }
                    }
                    catch { /* EdgeEvaluateParamLocation may fail */ }
                }
            }
            catch { }

            data["curve"] = curveInfo;

            // Vertex endpoint IDs
            try
            {
                var vertices = target.GetVertices();
                var vertexIds = new JArray();
                foreach (dynamic v in vertices)
                {
                    vertexIds.Add(InspectHelpers.SafeFloat(v.Tag));
                }
                if (vertexIds.Count > 0)
                    data["vertex_ids"] = vertexIds;
            }
            catch { }

            // XT node reference
            if (includeXt)
            {
                var xtRef = new JObject();
                xtRef["node_id"] = edgeId;
                xtRef["node_type"] = 16; // EDGE in V35
                xtRef["node_name"] = "EDGE";
                xtRef["note"] = "Export XT with nx_export_xt, then query by node_id with nx_inspect_xt_node";
                data["xt_reference"] = xtRef;
            }

            return new ToolResult
            {
                Success = true,
                Message = "Edge " + edgeId + " geometry (" + data["curve_type"] + ").",
                Data = data
            }.ToJson();
        }

        private JObject InspectVertexGeometry(dynamic workPart, int vertexId)
        {
            // NX2412: body.GetVertices() doesn't exist. Search vertices from edge.GetVertices().
            dynamic target = null;
            foreach (dynamic body in workPart.Bodies)
            {
                foreach (dynamic edge in body.GetEdges())
                {
                    try
                    {
                        foreach (dynamic vertex in edge.GetVertices())
                        {
                            double? tag = InspectHelpers.SafeFloat(vertex.Tag);
                            if (tag != null && (int)tag.Value == vertexId)
                            {
                                target = vertex;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (target != null) break;
                }
                if (target != null) break;
            }

            if (target == null)
            {
                return ToolResult.Fail(
                    "Vertex with tag " + vertexId + " not found. " +
                    "Vertices are discovered from edge endpoints. Use nx_inspect_topology (detail_level=full) to see edges."
                ).ToJson();
            }

            var data = new JObject();
            data["vertex_id"] = vertexId;
            try
            {
                data["position"] = InspectHelpers.SafeXyz(target.Position);
            }
            catch { }

            return new ToolResult
            {
                Success = true,
                Message = "Vertex " + vertexId + " position.",
                Data = data
            }.ToJson();
        }
    }

    // ========================================================================
    // 3. nx_inspect_feature_tree
    // ========================================================================

    /// <summary>
    /// Inspect the feature tree with design intent extraction.
    /// Returns feature parameters, output faces, parent sketches, and status.
    /// Extends nx_list_features + nx_get_feature_info with structured intent data.
    ///
    /// Parameters:
    ///   feature_name (string, optional) -- Filter to one feature (default: all).
    ///
    /// Returns:
    ///   features (array)  -- [{ journal_id, type, timestamp, status, ... }]
    ///   count    (number) -- Number of features.
    /// </summary>
    public class InspectFeatureTreeTool : IToolHandler
    {
        public string Name { get { return "nx_inspect_feature_tree"; } }
        public string Description { get { return "Inspect the feature tree with design intent extraction. Returns feature parameters, output faces, and status."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                var features = workPart.Features;
                bool hasFeatures = false;
                foreach (var f in features) { hasFeatures = true; break; }

                if (!hasFeatures)
                {
                    var noFeatData = new JObject();
                    noFeatData["features"] = new JArray();
                    noFeatData["count"] = 0;
                    return new ToolResult { Success = true, Message = "No features found.", Data = noFeatData }.ToJson();
                }

                string filterName = parameters.Value<string>("feature_name");

                var resultFeatures = new JArray();

                foreach (dynamic feat in features)
                {
                    if (!string.IsNullOrEmpty(filterName) &&
                        !string.Equals(feat.Name.ToString(), filterName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var featData = new JObject();
                    featData["journal_id"] = feat.FeatureType + "(" + feat.Timestamp + ")";
                    featData["name"] = feat.Name.ToString();
                    featData["type"] = feat.FeatureType.ToString();
                    featData["timestamp"] = feat.Timestamp.ToString();

                    // Extract expressions (parameters)
                    var parametersDict = new JObject();
                    try
                    {
                        foreach (dynamic expr in feat.GetExpressions())
                        {
                            try
                            {
                                double? val = InspectHelpers.SafeFloat(expr.Value);
                                parametersDict[expr.Name.ToString()] = val != null
                                    ? (JToken)val.Value
                                    : expr.RightHandSide.ToString();
                            }
                            catch
                            {
                                try { parametersDict[expr.Name.ToString()] = expr.RightHandSide.ToString(); }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    if (parametersDict.Count > 0)
                        featData["parameters"] = parametersDict;

                    // Get output faces (faces created by the feature via Feature.GetFaces().
                    // NX2412 verified 2026-08-15: GetBodies() returns the whole united body
                    // (same 20 faces for every extrude), GetFaces() returns per-feature faces.
                    try
                    {
                        var outputFaces = new JArray();
                        var faces = feat.GetFaces();
                        foreach (dynamic face in faces)
                        {
                            double? faceTag = InspectHelpers.SafeFloat(face.Tag);
                            if (faceTag != null)
                            {
                                outputFaces.Add((int)faceTag.Value);
                                if (outputFaces.Count >= 50) break; // cap at 50
                            }
                        }
                        if (outputFaces.Count > 0)
                            featData["output_faces"] = outputFaces;
                    }
                    catch (Exception exGetFaces)
                    {
                        InspectHelpers.LogToFile("[FeatureTree] GetFaces failed for " + featData["journal_id"] + ": " + exGetFaces.Message);
                    }

                    // Status (Feature.Suppressed — Feature.IsSuppressed does NOT exist in
                    // NX2412, verified by journal compile error + runtime reflection 2026-08-15)
                    try
                    {
                        featData["status"] = (bool)feat.Suppressed ? "SUPPRESSED" : "OK";
                    }
                    catch (Exception exSupp)
                    {
                        InspectHelpers.LogToFile("[FeatureTree] Suppressed failed for " + featData["journal_id"] + ": " + exSupp.Message);
                        featData["status"] = "UNKNOWN";
                    }

                    resultFeatures.Add(featData);
                }

                var data = new JObject();
                data["features"] = resultFeatures;
                data["count"] = resultFeatures.Count;
                return new ToolResult
                {
                    Success = true,
                    Message = "Inspected " + resultFeatures.Count + " feature(s) with intent data.",
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_inspect_feature_tree failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 4. nx_export_xt
    // ========================================================================

    /// <summary>
    /// Export the current work part as a Parasolid XT file.
    /// Returns the file path, schema version, and node count.
    /// NX2412: Part.ExportParasolid doesn't exist. Use Session.DexManager.CreateParasolidExporter.
    /// </summary>
    public class ExportXtTool : IToolHandler
    {
        public string Name { get { return "nx_export_xt"; } }
        public string Description { get { return "Export the current work part as a Parasolid XT file."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                dynamic workPart = session.Parts.Work;
                if (workPart == null)
                    return ToolResult.Fail("No work part is open.").ToJson();

                string format = parameters.Value<string>("format") ?? "text";
                format = format.Trim().ToLowerInvariant();
                bool isText = format != "binary";

                string filename = parameters.Value<string>("filename");
                if (string.IsNullOrEmpty(filename))
                {
                    string partName = "export";
                    try { partName = Path.GetFileNameWithoutExtension(workPart.Leaf.ToString()); } catch { }
                    string ext = isText ? ".x_t" : ".x_b";
                    filename = Path.Combine(Path.GetTempPath(), partName + ext);
                }

                // Ensure parent directory exists
                string parent = Path.GetDirectoryName(filename);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                // Export using NXOpen DexManager (NX2412: Part.ExportParasolid doesn't exist)
                Session nxSession = Session.GetSession();
                var psExporter = nxSession.DexManager.CreateParasolidExporter();
                psExporter.OutputFile = filename;
                // Verified 2026-08-04: sheet bodies need ObjectTypes.Surfaces (no "Sheets" property exists)
                psExporter.ObjectTypes.Solids = true;
                psExporter.ObjectTypes.Surfaces = true;
                psExporter.ExportFrom = ParasolidExporter.ExportFromOption.DisplayedPart;
                psExporter.Commit();
                psExporter.Destroy();

                // Count lines/nodes for text format
                int nodeCount = 0;
                if (isText && File.Exists(filename))
                {
                    foreach (string line in File.ReadLines(filename))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            nodeCount++;
                    }
                }

                // Try to read schema version from header
                string schemaVersion = "unknown";
                if (File.Exists(filename))
                {
                    try
                    {
                        string header;
                        using (var sr = new StreamReader(filename))
                        {
                            char[] buf = new char[500];
                            int read = sr.Read(buf, 0, 500);
                            header = new string(buf, 0, read);
                        }
                        var match = Regex.Match(header, @"SCH_(\d+)_(\d+)");
                        if (match.Success)
                            schemaVersion = match.Value;
                    }
                    catch { }
                }

                var data = new JObject();
                data["xt_path"] = filename;
                data["schema"] = schemaVersion;
                data["node_count"] = nodeCount;
                data["format"] = format;
                return new ToolResult
                {
                    Success = true,
                    Message = "Exported XT to " + filename + " (" + nodeCount + " nodes, schema: " + schemaVersion + ").",
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_export_xt failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 5. nx_inspect_xt_node
    // ========================================================================

    /// <summary>
    /// Parasolid XT text file parser - V35 compliant.
    /// Each node is one line of space-separated tokens.
    /// Variable-length nodes have an extra count field after the type number.
    /// </summary>
    internal class XtParser
    {
        private readonly string _xtPath;
        private readonly List<JObject> _nodes = new List<JObject>();
        private readonly Dictionary<int, int> _nodeIdToIndex = new Dictionary<int, int>();
        private bool _parsed = false;

        public XtParser(string xtPath) { _xtPath = xtPath; }

        private void EnsureParsed()
        {
            if (_parsed) return;
            if (!File.Exists(_xtPath))
                throw new FileNotFoundException("XT file not found: " + _xtPath);

            string[] lines = File.ReadAllLines(_xtPath);
            int nodeIndex = 0;

            foreach (string line in lines)
            {
                string stripped = line.Trim();
                if (string.IsNullOrEmpty(stripped)) continue;
                if (stripped.StartsWith("**") || stripped.StartsWith("T")) continue;

                string[] tokens = stripped.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                int nodeType;
                if (!int.TryParse(tokens[0], out nodeType)) continue;

                // Terminator
                if (nodeType == 1)
                {
                    if (tokens.Length > 1 && tokens[1] == "0") break;
                    continue;
                }

                nodeIndex++;
                string nodeName = InspectHelpers.NodeTypeNames.ContainsKey(nodeType)
                    ? InspectHelpers.NodeTypeNames[nodeType]
                    : "TYPE_" + nodeType;

                string[] fieldTokens;
                int nodeId = 0;

                if (InspectHelpers.VarLenTypes.Contains(nodeType))
                {
                    // Format: type var_length fields...
                    fieldTokens = tokens.Length > 2
                        ? SubArray(tokens, 2)
                        : new string[0];
                    if (fieldTokens.Length > 0) int.TryParse(fieldTokens[0], out nodeId);
                }
                else
                {
                    fieldTokens = tokens.Length > 1
                        ? SubArray(tokens, 1)
                        : new string[0];
                    if (fieldTokens.Length > 0) int.TryParse(fieldTokens[0], out nodeId);
                }

                var node = new JObject();
                node["node_index"] = nodeIndex;
                node["node_type"] = nodeType;
                node["node_id"] = nodeId;
                node["node_name"] = nodeName;
                node["fields"] = new JArray(fieldTokens);
                node["raw"] = stripped;
                _nodes.Add(node);

                if (nodeId > 0)
                    _nodeIdToIndex[nodeId] = nodeIndex;
            }
            _parsed = true;
        }

        public int GetNodeCount()
        {
            EnsureParsed();
            return _nodes.Count;
        }

        public JObject GetNode(int index)
        {
            EnsureParsed();
            if (index >= 1 && index <= _nodes.Count)
                return _nodes[index - 1];
            return null;
        }

        public int? GetIndexByNodeId(int nodeId)
        {
            EnsureParsed();
            if (_nodeIdToIndex.ContainsKey(nodeId))
                return _nodeIdToIndex[nodeId];
            return null;
        }

        private static string[] SubArray(string[] arr, int start)
        {
            string[] result = new string[arr.Length - start];
            Array.Copy(arr, start, result, 0, result.Length);
            return result;
        }
    }

    /// <summary>
    /// Query a single node from a Parasolid XT file by 1-based index.
    /// Returns the node type, node_id, name, and all parsed fields.
    /// Supports V35 schema with node-id to index cross-referencing.
    /// Use after nx_export_xt to do deep geometry inspection.
    /// </summary>
    public class InspectXtNodeTool : IToolHandler
    {
        public string Name { get { return "nx_inspect_xt_node"; } }
        public string Description { get { return "Query a single node from a Parasolid XT file by 1-based index."; } }

        /// <summary>Cached XT parsers by file path.</summary>
        private static readonly Dictionary<string, XtParser> _xtCache = new Dictionary<string, XtParser>();

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                string xtPath = parameters.Value<string>("xt_path");
                if (string.IsNullOrEmpty(xtPath))
                    return ToolResult.Fail("Parameter 'xt_path' is required.").ToJson();

                int nodeIndex = parameters.Value<int>("node_index");
                if (nodeIndex < 1)
                    return ToolResult.Fail("Parameter 'node_index' must be >= 1.").ToJson();

                if (!File.Exists(xtPath))
                    return ToolResult.Fail("XT file not found: " + xtPath + ". Run nx_export_xt first to generate the file.").ToJson();

                // Use cached parser or create new one
                XtParser parser;
                if (!_xtCache.ContainsKey(xtPath))
                {
                    parser = new XtParser(xtPath);
                    _xtCache[xtPath] = parser;
                }
                else
                {
                    parser = _xtCache[xtPath];
                }

                JObject node = parser.GetNode(nodeIndex);
                if (node == null)
                {
                    int total = parser.GetNodeCount();
                    return ToolResult.Fail(
                        "Node index " + nodeIndex + " not found (total nodes: " + total + "). Use a 1-based index between 1 and " + total + "."
                    ).ToJson();
                }

                return new ToolResult
                {
                    Success = true,
                    Message = "XT node " + nodeIndex + ": " + node["node_name"] + " (type " + node["node_type"] + ").",
                    Data = node
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_inspect_xt_node failed: " + ex.Message).ToJson();
            }
        }
    }

    // ========================================================================
    // 2. nx_inspect_selection
    // ========================================================================

    /// <summary>
    /// Inspect currently selected objects in NX (faces/edges/bodies/components/drafting objects).
    /// Uses UI.SelectionManager to read the live selection, then describes each object:
    ///   Face  → SolidFaceType + AskFaceData (pt/dir/radius/bbox) + area
    ///   Edge  → SolidEdgeType + length
    ///   Body  → face/edge counts
    ///   Component → name/part/transform
    ///   DraftingView/DraftingCurve → type info
    ///   Feature → feature type
    /// </summary>
    public class InspectSelectionTool : IToolHandler
    {
        public string Name { get { return "nx_inspect_selection"; } }
        public string Description { get { return "Inspect currently selected objects (faces/edges/bodies/components/drafting). Returns type, tag, and geometric data per object."; } }

        public JObject Execute(dynamic session, JObject parameters)
        {
            try
            {
                var ui = UI.GetUI();
                var sel = ui.SelectionManager;
                int count = sel.GetNumSelectedObjects();
                if (count == 0)
                    return ToolResult.Fail("No objects selected. Select faces/edges/bodies/components in NX first.").ToJson();

                var uf = UFSession.GetUFSession();
                var items = new JArray();

                for (int i = 0; i < count; i++)
                {
                    var o = sel.GetSelectedObject(i);
                    var item = new JObject();
                    item["index"] = i;
                    item["tag"] = (int)o.Tag;
                    item["type"] = o.GetType().Name;

                    if (o is Face)
                    {
                        var f = (Face)o;
                        item["face_type"] = f.SolidFaceType.ToString();
                        try
                        {
                            int ftype;
                            double[] pt = new double[3], dir = new double[3], box = new double[6];
                            double radius, rad_data;
                            int norm_dir;
                            uf.Modl.AskFaceData(f.Tag, out ftype, pt, dir, box, out radius, out rad_data, out norm_dir);
                            item["data_type"] = ftype;
                            item["point"] = new JArray(pt);
                            item["direction"] = new JArray(dir);
                            item["radius"] = radius;
                            item["rad_data"] = rad_data;
                            item["bbox"] = new JArray(box);
                            item["edge_count"] = f.GetEdges().Length;
                        }
                        catch (Exception ex) { item["face_data_error"] = ex.Message; }
                    }
                    else if (o is Edge)
                    {
                        var e = (Edge)o;
                        item["edge_type"] = e.SolidEdgeType.ToString();
                        try { item["length"] = e.GetLength(); } catch { }
                        try
                        {
                            var locs = e.GetLocations();
                            var pts = new JArray();
                            if (locs != null)
                                foreach (var l in locs)
                                    pts.Add(new JArray(new double[] { l.Location.X, l.Location.Y, l.Location.Z }));
                            item["locations"] = pts;
                        }
                        catch { }
                    }
                    else if (o is Body)
                    {
                        var b = (Body)o;
                        try { item["face_count"] = b.GetFaces().Length; } catch { }
                        try { item["edge_count"] = b.GetEdges().Length; } catch { }
                        try
                        {
                            int ftype;
                            double[] pt = new double[3], dir = new double[3], box = new double[6];
                            double radius, rad_data;
                            int norm_dir;
                            uf.Modl.AskFaceData(b.GetFaces()[0].Tag, out ftype, pt, dir, box, out radius, out rad_data, out norm_dir);
                            item["bbox"] = new JArray(box);
                        }
                        catch { }
                    }
                    else if (o is NXOpen.Assemblies.Component)
                    {
                        var c = (NXOpen.Assemblies.Component)o;
                        try { item["component_name"] = c.Name; } catch { }
                        try { item["display_name"] = c.DisplayName; } catch { }
                        try { item["is_occurrence"] = c.IsOccurrence; } catch { }
                    }
                    else if (o is NXOpen.Drawings.DraftingView)
                    {
                        var dv = (NXOpen.Drawings.DraftingView)o;
                        try { item["view_name"] = dv.Name; } catch { }
                        try
                        {
                            item["origin"] = new JArray(new double[] { dv.Origin.X, dv.Origin.Y });
                            item["scale"] = dv.Scale;
                        }
                        catch { }
                    }
                    else if (o is NXOpen.Drawings.DraftingCurve)
                    {
                        var dc = (NXOpen.Drawings.DraftingCurve)o;
                        try
                        {
                            var info = dc.GetDraftingCurveInfo();
                            item["curve_type"] = info.CurveType.ToString();
                        }
                        catch { }
                        try { item["length"] = dc.GetLength(); } catch { }
                    }
                    else if (o is NXOpen.Features.Feature)
                    {
                        var feat = (NXOpen.Features.Feature)o;
                        try { item["feature_name"] = feat.GetFeatureName(); } catch { }
                        try { item["feature_type"] = feat.FeatureType.ToString(); } catch { }
                    }
                    else if (o is NXOpen.Drawings.DrawingSheet)
                    {
                        var s = (NXOpen.Drawings.DrawingSheet)o;
                        try { item["sheet_name"] = s.Name; } catch { }
                    }

                    // ── 附加测量（NX 内置引擎，勿用坐标推算）──
                    try
                    {
                        var wp2 = (Part)session.Parts.Work;
                        var mm = wp2.MeasureManager;
                        var unitMM = wp2.UnitCollection.FindObject("MilliMeter");

                        // 面: 面积+周长 (NewFaceProperties)
                        if (o is Face)
                        {
                            var f2 = (Face)o;
                            var unitMM2 = wp2.UnitCollection.FindObject("MilliMeter");
                            try
                            {
                                dynamic fp = mm.NewFaceProperties(unitMM2, unitMM2, 0.9, new IParameterizedSurface[] { f2 });
                                item["area_mm2"] = fp.Area;
                                item["perimeter_mm"] = fp.Perimeter;
                            }
                            catch { }
                        }
                        // 体: 体积+质量 (NewMassProperties)
                        else if (o is Body)
                        {
                            var b2 = (Body)o;
                            try
                            {
                                dynamic mp = mm.NewMassProperties(new Unit[] { unitMM, unitMM, unitMM }, 0.9, new IBody[] { b2 });
                                item["volume_mm3"] = mp.Volume;
                                item["surface_area_mm2"] = mp.SurfaceArea;
                            }
                            catch { }
                        }
                        // 边: 长度 (NewLength)
                        else if (o is Edge)
                        {
                            var e2 = (Edge)o;
                            try
                            {
                                dynamic len = mm.NewLength(unitMM, new DisplayableObject[] { e2 });
                                item["length_mm"] = len.Length;
                            }
                            catch { }
                        }
                    }
                    catch { }

                    items.Add(item);
                }

                // ── 两两测量: 距离 + 角度 ──
                var pairwise = new JArray();
                if (count >= 2)
                {
                    try
                    {
                        var wp2 = (Part)session.Parts.Work;
                        var mm = wp2.MeasureManager;
                        var unitMM = wp2.UnitCollection.FindObject("MilliMeter");
                        for (int a = 0; a < count && a < 8; a++)
                        {
                            for (int b = a + 1; b < count && b < 8; b++)
                            {
                                var oa = sel.GetSelectedObject(a);
                                var ob = sel.GetSelectedObject(b);
                                var pair = new JObject();
                                pair["obj1"] = oa.GetType().Name + ":" + (int)oa.Tag;
                                pair["obj2"] = ob.GetType().Name + ":" + (int)ob.Tag;
                                try
                                {
                                    dynamic dist = mm.NewDistance(unitMM, oa, ob);
                                    pair["distance_mm"] = dist.Value;
                                }
                                catch (Exception ex) { pair["distance_error"] = ex.Message; }
                                try
                                {
                                    dynamic ang = mm.NewAngle(unitMM, (DisplayableObject)oa, NXOpen.MeasureManager.EndpointType.StartPoint, (DisplayableObject)ob, NXOpen.MeasureManager.EndpointType.StartPoint, true);
                                    pair["angle_deg"] = ang.Value;
                                }
                                catch { }
                                pairwise.Add(pair);
                            }
                        }
                    }
                    catch { }
                }

                var data = new JObject();
                data["count"] = count;
                data["pairwise"] = pairwise;
                data["objects"] = items;
                return new ToolResult
                {
                    Success = true,
                    Message = "Inspected " + count + " selected object(s).",
                    Data = data
                }.ToJson();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("nx_inspect_selection failed: " + ex.Message).ToJson();
            }
        }
    }
}
