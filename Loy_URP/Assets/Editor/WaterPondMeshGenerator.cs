// 池塘水面网格重建工具（Loy_URP）
//
// 把 PondWater 的低模三角面片（73 verts / 平均边长 6.8m）重建为密度均匀、
// 轮廓精确的平面网格，供 shader 做顶点波浪位移动画。
//
// 方案：均匀网格 + 单元裁剪。
//   1. 提取原网格边界环（外轮廓 + 洞）。
//   2. 在轮廓包围盒上铺 0.4m 均匀网格。
//   3. 每个单元收集"落在单元内的轮廓顶点 / 在单元角点内 / 轮廓边与单元边交点"，
//      按绕质心角度排序后从质心扇形三角化——等价于 cell ∩ 池塘，轮廓交点精确在池塘边上。
//      单遍完成、无迭代循环，顶点数有上限，不会像耳切+细分那样卡死。
//   4. 写入 UV0（包围盒归一化）、UV1.x（到岸距离，世界米）、法线 +Y、切线 +X。
//
// 菜单：Tools -> Loy -> Regenerate Pond Water Mesh
// 产出：Assets/GameRes/Mesh/PondWater_HighRes.asset，并替换 MeshFilter / MeshCollider。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class WaterPondMeshGenerator
{
    const string k_MeshPath = "Assets/GameRes/Mesh/PondWater_HighRes.asset";
    const float k_Spacing = 0.4f;     // 网格单元边长（局部空间米）
    const float k_SurfaceY = -0.22f;  // 水面局部高度，与全局水面反射平面对齐
    const float k_Quant = 0.002f;     // 顶点去重量化粒度
    const int k_MaxCellPts = 20;      // 单单元收集点上限（防病态轮廓）

    [MenuItem("Tools/Loy/Regenerate Pond Water Mesh")]
    public static void Regenerate()
    {
        GameObject pond = GameObject.Find("PondWater");
        if (pond == null) { Debug.LogError("[WaterGen] 场景中找不到 PondWater"); return; }
        MeshFilter mf = pond.GetComponent<MeshFilter>();
        MeshCollider mc = pond.GetComponent<MeshCollider>();
        Mesh src = mf != null ? mf.sharedMesh : null;
        if (src == null) { Debug.LogError("[WaterGen] PondWater 没有 MeshFilter"); return; }
        if (src.vertexCount > 1000)
        {
            Debug.LogError($"[WaterGen] 输入网格已有 {src.vertexCount} 顶点，疑似已生成。请先重载场景恢复原始低模，再运行。");
            return;
        }

        // 1) 边界环：面积最大的环作为外轮廓，其余作为洞（参与内部判定）
        List<List<Vector3>> loops = ExtractBoundaryLoops(src);
        if (loops.Count == 0) { Debug.LogError("[WaterGen] 无法提取边界"); return; }
        List<Vector3> outer = loops[0];
        for (int i = 1; i < loops.Count; i++)
            if (Mathf.Abs(SignedArea2(loops[i])) > Mathf.Abs(SignedArea2(outer))) outer = loops[i];
        var holes = new List<List<Vector3>>();
        for (int i = 0; i < loops.Count; i++)
            if (loops[i] != outer) holes.Add(loops[i]);

        // 2) 包围盒 + 均匀网格
        Bounds b = new Bounds(outer[0], Vector3.zero);
        foreach (Vector3 p in outer) b.Encapsulate(p);
        Vector3 min = b.min - Vector3.one * k_Spacing;
        Vector3 max = b.max + Vector3.one * k_Spacing;

        var verts = new List<Vector3>();
        var tris = new List<int>();
        var uvs = new List<Vector2>();
        var uv1 = new List<Vector2>();
        var vmap = new Dictionary<long, int>();

        int gxCount = Mathf.CeilToInt((max.x - min.x) / k_Spacing);
        int gzCount = Mathf.CeilToInt((max.z - min.z) / k_Spacing);
        for (int gz = 0; gz < gzCount; gz++)
        for (int gx = 0; gx < gxCount; gx++)
        {
            float x0 = min.x + gx * k_Spacing;
            float z0 = min.z + gz * k_Spacing;
            EmitCell(outer, holes, x0, z0, min, max,
                verts, vmap, tris, uvs, uv1);
        }

        if (verts.Count < 4) { Debug.LogError("[WaterGen] 生成的网格太小"); return; }

        // 3) 组装
        Mesh m = new Mesh { name = "PondWater_HighRes" };
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        var normals = new List<Vector3>(verts.Count);
        var tangents = new List<Vector4>(verts.Count);
        for (int i = 0; i < verts.Count; i++) { normals.Add(Vector3.up); tangents.Add(new Vector4(1f, 0f, 0f, 1f)); }
        m.SetNormals(normals);
        m.SetTangents(tangents);
        m.SetUVs(0, uvs);
        m.SetUVs(1, uv1);
        m.RecalculateBounds();

        // 4) 保存并替换
        if (!AssetDatabase.IsValidFolder("Assets/GameRes")) AssetDatabase.CreateFolder("Assets", "GameRes");
        if (!AssetDatabase.IsValidFolder("Assets/GameRes/Mesh")) AssetDatabase.CreateFolder("Assets/GameRes", "Mesh");
        AssetDatabase.DeleteAsset(k_MeshPath);
        AssetDatabase.CreateAsset(m, k_MeshPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Mesh asset = AssetDatabase.LoadAssetAtPath<Mesh>(k_MeshPath);

        mf.sharedMesh = asset;
        if (mc != null) mc.sharedMesh = asset;
        EditorUtility.SetDirty(pond);

        Debug.Log($"[WaterGen] 完成: verts={asset.vertexCount} tris={asset.triangles.Length / 3} bounds={asset.bounds.size} 已替换到 PondWater");
    }

    // ---------- 单元裁剪 ----------

    static void EmitCell(List<Vector3> outer, List<List<Vector3>> holes, float x0, float z0,
        Vector3 min, Vector3 max,
        List<Vector3> verts, Dictionary<long, int> vmap, List<int> tris, List<Vector2> uvs, List<Vector2> uv1)
    {
        Vector3 c00 = new Vector3(x0, 0f, z0);
        Vector3 c10 = new Vector3(x0 + k_Spacing, 0f, z0);
        Vector3 c11 = new Vector3(x0 + k_Spacing, 0f, z0 + k_Spacing);
        Vector3 c01 = new Vector3(x0, 0f, z0 + k_Spacing);
        Vector3[] corners = { c00, c10, c11, c01 };

        // 角点在内判定
        bool[] inCorner = new bool[4];
        int insideCount = 0;
        for (int i = 0; i < 4; i++)
        {
            inCorner[i] = InsideEvenOdd(corners[i], outer, holes);
            if (inCorner[i]) insideCount++;
        }
        // 中心（用于判断单元是否值得处理 / 质心加权）
        bool centerIn = InsideEvenOdd(new Vector3(x0 + k_Spacing * 0.5f, 0f, z0 + k_Spacing * 0.5f), outer, holes);

        // 收集候选点：内的角点 + 轮廓与单元边交点 + 落在单元内的轮廓顶点
        var pts = new List<Vector3>();
        for (int i = 0; i < 4; i++) if (inCorner[i]) pts.Add(corners[i]);

        if (insideCount < 4)
        {
            // 单元边与轮廓边求交（只有非全内单元才需要）
            for (int e = 0; e < 4; e++)
            {
                Vector3 ea = corners[e], eb = corners[(e + 1) % 4];
                for (int j = 0; j < outer.Count && pts.Count < k_MaxCellPts; j++)
                {
                    Vector3 oa = outer[j], ob = outer[(j + 1) % outer.Count];
                    if (SegIntersect2(ea, eb, oa, ob, out Vector3 ip)) pts.Add(ip);
                }
            }
            // 落在单元内的轮廓顶点（凹角）——先用中心粗筛，再逐点判
            if (centerIn || insideCount > 0)
            {
                for (int j = 0; j < outer.Count && pts.Count < k_MaxCellPts; j++)
                {
                    Vector3 p = outer[j];
                    if (p.x >= x0 && p.x <= x0 + k_Spacing && p.z >= z0 && p.z <= z0 + k_Spacing
                        && pts.Count < k_MaxCellPts)
                        pts.Add(p);
                }
            }
        }

        if (pts.Count < 3) return;

        // 去重
        var unique = new List<Vector3>();
        foreach (Vector3 p in pts)
        {
            bool dup = false;
            foreach (Vector3 q in unique)
                if (Mathf.Abs(p.x - q.x) < 1e-4f && Mathf.Abs(p.z - q.z) < 1e-4f) { dup = true; break; }
            if (!dup) unique.Add(p);
        }
        if (unique.Count < 3) return;

        // 质心
        Vector3 centroid = Vector3.zero;
        foreach (Vector3 p in unique) centroid += p;
        centroid /= unique.Count;

        // 绕质心角度排序（单元内区域近似凸，质心扇形三角化安全）
        unique.Sort((p, q) =>
        {
            float ap = Mathf.Atan2(p.z - centroid.z, p.x - centroid.x);
            float aq = Mathf.Atan2(q.z - centroid.z, q.x - centroid.x);
            return ap.CompareTo(aq);
        });

        // 从质心扇形三角化
        int ci = GetOrAdd(verts, vmap, uvs, uv1, centroid, outer, min, max);
        for (int i = 0; i < unique.Count; i++)
        {
            Vector3 a = unique[i], b = unique[(i + 1) % unique.Count];
            int ia = GetOrAdd(verts, vmap, uvs, uv1, a, outer, min, max);
            int ib = GetOrAdd(verts, vmap, uvs, uv1, b, outer, min, max);
            AddTri(tris, verts, ci, ia, ib);
        }
    }

    // ---------- 顶点 / 三角形 ----------

    static void AddTri(List<int> tris, List<Vector3> verts, int a, int b, int c)
    {
        if (Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).y < 0f) { int t = b; b = c; c = t; }
        tris.Add(a); tris.Add(b); tris.Add(c);
    }

    static int GetOrAdd(List<Vector3> verts, Dictionary<long, int> vmap, List<Vector2> uvs, List<Vector2> uv1,
        Vector3 p, List<Vector3> outer, Vector3 min, Vector3 max)
    {
        int ix = Mathf.RoundToInt(p.x / k_Quant);
        int iz = Mathf.RoundToInt(p.z / k_Quant);
        long key = ((long)(uint)ix << 32) | (uint)iz;
        if (vmap.TryGetValue(key, out int idx)) return idx;

        idx = verts.Count;
        vmap[key] = idx;
        verts.Add(new Vector3(p.x, k_SurfaceY, p.z));
        uvs.Add(new Vector2((p.x - min.x) / Mathf.Max(max.x - min.x, 1e-5f),
                            (p.z - min.z) / Mathf.Max(max.z - min.z, 1e-5f)));
        uv1.Add(new Vector2(DistToLoop(p, outer), 0f));
        return idx;
    }

    static float DistToLoop(Vector3 p, List<Vector3> loop)
    {
        float best = float.MaxValue;
        for (int i = 0; i < loop.Count; i++)
            best = Mathf.Min(best, DistToSegment(p, loop[i], loop[(i + 1) % loop.Count]));
        return best;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector2 P = new Vector2(p.x, p.z), A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z);
        Vector2 ab = B - A, ap = P - A;
        float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
        return (P - (A + ab * t)).magnitude;
    }

    // ---------- 2D 线段相交 ----------

    static bool SegIntersect2(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 ip)
    {
        Vector2 A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z);
        Vector2 C = new Vector2(c.x, c.z), D = new Vector2(d.x, d.z);
        Vector2 r = B - A, s = D - C;
        float denom = Cross(r, s);
        float t = Cross(C - A, s) / denom;
        float u = Cross(C - A, r) / denom;
        if (Mathf.Abs(denom) < 1e-9f) { ip = Vector3.zero; return false; }  // 平行/共线
        if (t < 0f || t > 1f || u < 0f || u > 1f) { ip = Vector3.zero; return false; }
        Vector2 q = A + r * t;
        ip = new Vector3(q.x, 0f, q.y);
        return true;
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    // ---------- 边界提取 / 内部判定 ----------

    static List<List<Vector3>> ExtractBoundaryLoops(Mesh m)
    {
        Vector3[] verts = m.vertices;
        int[] tris = m.triangles;

        var edgeUse = new Dictionary<ulong, int>();
        var edgeVerts = new Dictionary<ulong, (int a, int b)>();

        for (int i = 0; i < tris.Length; i += 3)
        {
            AddEdge(tris[i], tris[i + 1]); AddEdge(tris[i + 1], tris[i + 2]); AddEdge(tris[i + 2], tris[i]);
        }

        void AddEdge(int a, int b)
        {
            ulong key = MakeKey(a, b);
            edgeUse.TryGetValue(key, out int u);
            edgeUse[key] = u + 1;
            edgeVerts[key] = (a, b);
        }

        var boundary = new List<(int a, int b)>();
        foreach (KeyValuePair<ulong, int> kv in edgeUse)
            if (kv.Value == 1) boundary.Add(edgeVerts[kv.Key]);

        var fromStart = new Dictionary<int, int>();
        for (int i = 0; i < boundary.Count; i++) fromStart[boundary[i].a] = i;

        var loops = new List<List<Vector3>>();
        var used = new bool[boundary.Count];
        for (int i = 0; i < boundary.Count; i++)
        {
            if (used[i]) continue;
            var loop = new List<Vector3>();
            int cur = i;
            while (!used[cur])
            {
                used[cur] = true;
                (int a, int b) = boundary[cur];
                loop.Add(verts[a]);
                if (!fromStart.TryGetValue(b, out int next) || used[next]) break;
                cur = next;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    static ulong MakeKey(int a, int b) => a < b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;

    // even-odd 射线法（跨所有环，洞自然生效）
    static bool InsideEvenOdd(Vector3 p, List<Vector3> outer, List<List<Vector3>> holes)
    {
        bool inside = false;
        for (int l = 0; l <= holes.Count; l++)
        {
            List<Vector3> loop = l == 0 ? outer : holes[l - 1];
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                Vector3 a = loop[j], b = loop[i];
                if ((a.z > p.z) != (b.z > p.z))
                {
                    float xint = (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x;
                    if (p.x < xint) inside = !inside;
                }
            }
        }
        return inside;
    }

    static float SignedArea2(List<Vector3> poly)
    {
        float area = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
            area += a.x * b.z - b.x * a.z;
        }
        return area * 0.5f;
    }
}
