// ColorSpatialClusterTool.cs
// - Source: Ros2PointCloudListener의 "CurrentFrame" 또는 "GlobalChunks"
// - Color split: FixedPalette(빨강/주황/노랑/연두/초록/하늘색/남색) or KMeans
// - Spatial split: 색별로 보xel BFS(6/26-연결)로 근접 군집
// - New Input System 전용

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public class ColorSpatialClusterTool : MonoBehaviour
{
    public enum SourceKind { CurrentFrame, GlobalChunks }
    public enum ColorClusterMode { FixedPalette, KMeans }

    [Header("Source Root (optional)")]
    [Tooltip("소스를 찾을 루트(비우면 this.transform에서 검색)")]
    public Transform rootForSearch = null;

    [Header("Source Selection")]
    public SourceKind sourceKind = SourceKind.CurrentFrame;
    [Tooltip("CurrentFrame 오브젝트 이름")]
    public string currentFrameName = "CurrentFrame";
    [Tooltip("머지된 청크들의 부모 오브젝트 이름")]
    public string globalParentName = "GlobalChunks";
    [Tooltip("비활성화된 청크도 포함할지")]
    public bool includeInactiveChunks = true;

    [Header("Color Split")]
    public ColorClusterMode colorMode = ColorClusterMode.FixedPalette;

    [Tooltip("팔레트 분류에서 채도/명도 최소값 (이보다 낮으면 'Unclassified')")]
    [Range(0f,1f)] public float minSaturation = 0.20f;
    [Range(0f,1f)] public float minValue = 0.20f;
    [Tooltip("'Unclassified'도 클러스터링에 포함할지")]
    public bool includeUnclassified = false;

    [Tooltip("KMeans: 색상 KMeans 클러스터 개수")]
    [Range(2, 12)] public int kColors = 7;
    [Tooltip("KMeans: 색공간 true=Lab, false=RGB")]
    public bool useLab = true;
    [Range(1, 50)] public int kmeansIters = 10;

    [Header("Spatial Clustering (Voxel BFS)")]
    [Tooltip("보xel 크기 자동 추정 배율 (↑크면 더 느슨하게 뭉침)")]
    public float voxelSizeScale = 1.5f;
    [Tooltip("자동 추정 실패시 기본 보xel 크기 (m)")]
    public float defaultVoxelSize = 0.01f;
    [Tooltip("클러스터 최소 점 개수")]
    public int minPointsPerCluster = 200;
    [Tooltip("26-연결 사용 (대각 포함)")]
    public bool connectivity26 = true;

    [Header("Build / View")]
    [Tooltip("클러스터 메쉬 머티리얼 (비우면 Unlit/Color 계열 자동)")]
    public Material clusterMaterial;
    [Tooltip("원본 소스 표시")]
    public bool showOriginal = true;

    // ------- Fixed palette (Hue centers in degrees) -------
    // 빨강(0), 주황(30), 노랑(60), 연두(90), 초록(120), 하늘색(200), 남색(240)
    private static readonly string[] PALETTE_NAMES = {
        "Red","Orange","Yellow","YellowGreen","Green","SkyBlue","Navy"
    };
    private static readonly float[]  PALETTE_HUES = { 0f, 30f, 60f, 90f, 120f, 200f, 240f };
    private static readonly Color[]  PALETTE_SHOW = {
        new Color(1f,0f,0f),            // Red
        new Color(1f,0.5f,0f),          // Orange
        new Color(1f,1f,0f),            // Yellow
        new Color(0.6f,1f,0.2f),        // YellowGreen(연두)
        new Color(0f,1f,0f),            // Green
        new Color(0.0f,0.75f,1f),       // SkyBlue
        new Color(0.0f,0.1f,0.5f)       // Navy
    };
    private const int UNCLASSIFIED = -1;

    // --------- Internals ---------
    private Transform sourceRoot;            // CurrentFrame 또는 GlobalChunks
    private Mesh sourceMesh;                 // CurrentFrame 모드일 때만 사용
    private GameObject clustersRoot;         // 생성된 클러스터 부모
    private readonly List<GameObject> clusterGOs = new();
    private int selectedIndex = -1;          // -1 = 전체 표시

    // InputActions (New Input System)
    private InputAction actBuild, actShowAll, actToggleOrig, actToggleLab, actNext, actPrev, actMode;
    private InputAction[] actDigits;

    void Awake()
    {
        // Input actions (code-based, no asset)
        actBuild      = new InputAction("Build",       InputActionType.Button, "<Keyboard>/c");
        actShowAll    = new InputAction("ShowAll",     InputActionType.Button, "<Keyboard>/a");
        actToggleOrig = new InputAction("ToggleOrig",  InputActionType.Button, "<Keyboard>/h");
        actToggleLab  = new InputAction("ToggleLab",   InputActionType.Button, "<Keyboard>/l");
        actNext       = new InputAction("Next",        InputActionType.Button, "<Keyboard>/n");
        actPrev       = new InputAction("Prev",        InputActionType.Button, "<Keyboard>/p");
        actMode       = new InputAction("ToggleMode",  InputActionType.Button, "<Keyboard>/m");

        actDigits = new InputAction[9];
        for (int i = 0; i < 9; i++)
            actDigits[i] = new InputAction($"Select{i+1}", InputActionType.Button, "<Keyboard>/" + (i+1).ToString());

        actBuild.performed      += _ => BuildClusters();
        actShowAll.performed    += _ => { selectedIndex = -1; ApplyVisibility(); };
        actToggleOrig.performed += _ => { showOriginal = !showOriginal; ApplyVisibility(); };
        actToggleLab.performed  += _ => { useLab = !useLab; Debug.Log($"[Cluster] (KMeans) useLab={useLab} (press C to rebuild)"); };
        actMode.performed       += _ => {
            colorMode = (colorMode == ColorClusterMode.FixedPalette) ? ColorClusterMode.KMeans : ColorClusterMode.FixedPalette;
            Debug.Log($"[Cluster] colorMode={colorMode} (press C to rebuild)");
        };
        actNext.performed       += _ => SelectNext(+1);
        actPrev.performed       += _ => SelectNext(-1);
        for (int i = 0; i < 9; i++) { int idx = i; actDigits[i].performed += _ => SelectIndex(idx); }
    }

    void OnEnable()
    {
        actBuild.Enable(); actShowAll.Enable(); actToggleOrig.Enable();
        actToggleLab.Enable(); actNext.Enable(); actPrev.Enable(); actMode.Enable();
        foreach (var a in actDigits) a.Enable();
    }
    void OnDisable()
    {
        foreach (var a in actDigits) a.Disable();
        actMode.Disable(); actPrev.Disable(); actNext.Disable(); actToggleLab.Disable();
        actToggleOrig.Disable(); actShowAll.Disable(); actBuild.Disable();
    }

    // ======================== Main Entry ========================
    public void BuildClusters()
    {
        if (!TryGatherSource(out Vector3[] pts, out Color32[] cols, out string srcLabel))
        {
            Debug.LogWarning("[Cluster] Source not ready. Capture or merge first.");
            return;
        }

        int n = pts.Length;
        if (n == 0) { Debug.LogWarning("[Cluster] No vertices."); return; }
        if (cols == null || cols.Length != n)
        {
            Debug.LogWarning("[Cluster] Source has no vertex colors; color-based split requires colors.");
            return;
        }

        // 1) Color grouping → label[]
        int[] colorLabel;
        int paletteCount = PALETTE_HUES.Length;

        if (colorMode == ColorClusterMode.FixedPalette)
        {
            colorLabel = new int[n];
            for (int i = 0; i < n; i++)
            {
                colorLabel[i] = LabelByPalette(cols[i], minSaturation, minValue);
            }
        }
        else
        {
            // KMeans fallback
            var R = new byte[n]; var G = new byte[n]; var B = new byte[n];
            for (int i=0;i<n;i++){ R[i]=cols[i].r; G[i]=cols[i].g; B[i]=cols[i].b; }
            int K = Mathf.Clamp(kColors, 2, 12);
            colorLabel = KMeansColor(R, G, B, K, Mathf.Max(1,kmeansIters), useLab);
            paletteCount = K; // 아래 루프에서 c < paletteCount 로 사용
        }

        // 2) Voxel size estimate
        float voxel = EstimateVoxelSizeFromDensity(pts);
        if (!(voxel > 0f)) voxel = defaultVoxelSize;
        voxel *= Mathf.Max(0.001f, voxelSizeScale);

        // 3) Spatial clustering (per color) via voxel-BFS
        var allClusters = new List<(int colorK, string cname, Color visColor, int[] indices)>();
        // 팔레트 모드: c == -1(Unclassified) 처리 옵션
        if (colorMode == ColorClusterMode.FixedPalette)
        {
            // 정규 팔레트 색들
            for (int c = 0; c < PALETTE_HUES.Length; c++)
            {
                var idx = IndicesWhere(colorLabel, c);
                if (idx.Count < minPointsPerCluster) continue;
                var clusters = SpatialClustersVoxelBFS(pts, idx, voxel, minPointsPerCluster, connectivity26);
                foreach (var cc in clusters) allClusters.Add((c, PALETTE_NAMES[c], PALETTE_SHOW[c], cc));
            }
            // Unclassified
            if (includeUnclassified)
            {
                var idxU = IndicesWhere(colorLabel, UNCLASSIFIED);
                if (idxU.Count >= minPointsPerCluster)
                {
                    var clustersU = SpatialClustersVoxelBFS(pts, idxU, voxel, minPointsPerCluster, connectivity26);
                    foreach (var cc in clustersU) allClusters.Add((-1, "Unclassified", Color.white, cc));
                }
            }
        }
        else
        {
            // KMeans 결과를 c=0..K-1로 처리
            for (int c = 0; c < paletteCount; c++)
            {
                var idx = IndicesWhere(colorLabel, c);
                if (idx.Count < minPointsPerCluster) continue;
                var clusters = SpatialClustersVoxelBFS(pts, idx, voxel, minPointsPerCluster, connectivity26);
                // 시각 색은 평균색으로 설정
                Color meanCol = MeanColor(cols, idx);
                foreach (var cc in clusters) allClusters.Add((c, $"K{c}", meanCol, cc));
            }
        }

        // 4) Build meshes
        BuildClusterMeshes(pts, cols, allClusters, srcLabel);
        selectedIndex = -1;
        ApplyVisibility();

        Debug.Log($"[Cluster] Built {clusterGOs.Count} clusters (voxel={voxel:F4} m, mode={colorMode}, src={srcLabel})");
    }

    // ======================== Source Gather ========================
    private bool TryGatherSource(out Vector3[] verts, out Color32[] cols32, out string label)
    {
        verts = null; cols32 = null; label = "";
        sourceRoot = null; sourceMesh = null;

        if (sourceKind == SourceKind.CurrentFrame)
        {
            Transform root = (rootForSearch != null) ? rootForSearch : transform;
            Transform cf = FindDeep(root, currentFrameName);
            if (cf == null) return false;

            var mf = cf.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;

            sourceRoot = cf;
            sourceMesh = mf.sharedMesh;

            verts = sourceMesh.vertices; // cf 로컬 좌표
            var c = sourceMesh.colors32;
            if (c != null && c.Length == verts.Length) cols32 = c;
            else
            {
                cols32 = new Color32[verts.Length];
                for (int i = 0; i < cols32.Length; i++) cols32[i] = new Color32(255, 255, 255, 255);
            }

            label = "CurrentFrame";
            return true;
        }
        else // GlobalChunks
        {
            Transform root = (rootForSearch != null) ? rootForSearch : transform;
            Transform parent = FindDeep(root, globalParentName);
            if (parent == null) return false;

            sourceRoot = parent;

            var mfs = parent.GetComponentsInChildren<MeshFilter>(includeInactiveChunks);
            if (mfs == null || mfs.Length == 0) return false;

            var vertsList = new List<Vector3>(65536);
            var colsList  = new List<Color32>(65536);
            Matrix4x4 toParentLocal = parent.worldToLocalMatrix;

            foreach (var mf in mfs)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                var v = mesh.vertices;
                if (v == null || v.Length == 0) continue;

                Matrix4x4 toWorld = mf.transform.localToWorldMatrix;

                var cc = mesh.colors32;
                bool hasColor = (cc != null && cc.Length == v.Length);

                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 w  = toWorld.MultiplyPoint3x4(v[i]);       // child local → world
                    Vector3 pl = toParentLocal.MultiplyPoint3x4(w);    // world → GlobalChunks 로컬
                    vertsList.Add(pl);
                    colsList.Add(hasColor ? cc[i] : new Color32(255, 255, 255, 255));
                }
            }

            if (vertsList.Count == 0) return false;

            verts  = vertsList.ToArray();     // GlobalChunks 로컬 좌표
            cols32 = colsList.ToArray();
            label = "GlobalChunks";
            return true;
        }
    }

    // ======================== Build ========================
    private void BuildClusterMeshes(
        Vector3[] verts, Color32[] cols32,
        List<(int colorK, string cname, Color visColor, int[] indices)> clusters,
        string srcLabel)
    {
        if (!clustersRoot)
            clustersRoot = new GameObject("Clusters");

        // 부모/변환 = sourceRoot로 설정 (동일 로컬 공간)
        clustersRoot.transform.SetParent(sourceRoot, worldPositionStays:false);
        clustersRoot.transform.localPosition = Vector3.zero;
        clustersRoot.transform.localRotation = Quaternion.identity;
        clustersRoot.transform.localScale    = Vector3.one;

        // 기존 삭제
        foreach (var go in clusterGOs) if (go) Destroy(go);
        clusterGOs.Clear();

        var baseMat = clusterMaterial ?? new Material(
            Shader.Find("Universal Render Pipeline/Particles/Unlit")
            ?? Shader.Find("Particles/Standard Unlit")
            ?? Shader.Find("Unlit/Color"));

        for (int ci = 0; ci < clusters.Count; ci++)
        {
            var (ck, cname, visColor, idx) = clusters[ci];
            int m = idx.Length;

            var v = new Vector3[m];
            var c = new Color32[m];
            for (int i = 0; i < m; i++)
            {
                int k = idx[i];
                v[i] = verts[k];
                c[i] = cols32[k];
            }

            var mesh = new Mesh
            {
                indexFormat = (m > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32
                                         : UnityEngine.Rendering.IndexFormat.UInt16)
            };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            var indices = Enumerable.Range(0, m).ToArray();
            mesh.SetIndices(indices, MeshTopology.Points, 0, false);
            mesh.RecalculateBounds();

            var go = new GameObject($"Cluster_{ci:00}_{cname}_{srcLabel}");
            go.transform.SetParent(clustersRoot.transform, worldPositionStays:false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;

            var mat = new Material(baseMat);
            mat.color = visColor;            // 클러스터 표시색(팔레트 or 평균색)
            mr.sharedMaterial = mat;

            clusterGOs.Add(go);
        }
    }

    private void ApplyVisibility()
    {
        // 원본 표시/숨김
        if (sourceRoot) sourceRoot.gameObject.SetActive(showOriginal && selectedIndex < 0);

        // 클러스터들 표시/숨김
        for (int i = 0; i < clusterGOs.Count; i++)
        {
            if (!clusterGOs[i]) continue;
            clusterGOs[i].SetActive(selectedIndex < 0 || selectedIndex == i);
        }
        if (selectedIndex >= 0 && selectedIndex < clusterGOs.Count)
            Debug.Log($"[Cluster] Selected {selectedIndex}/{clusterGOs.Count-1}: {clusterGOs[selectedIndex].name}");
    }

    public void SelectNext(int delta)
    {
        if (clusterGOs.Count == 0) return;
        if (selectedIndex < 0) selectedIndex = 0;
        else selectedIndex = (selectedIndex + delta + clusterGOs.Count) % clusterGOs.Count;
        ApplyVisibility();
    }
    public void SelectIndex(int idx)
    {
        if (idx < 0 || idx >= clusterGOs.Count) return;
        selectedIndex = idx;
        ApplyVisibility();
    }

    // ======================== Fixed Palette Mapping ========================
    private static int LabelByPalette(Color32 c32, float sMin, float vMin)
    {
        // Unity HSV: H[0..1), S[0..1], V[0..1]
        Color.RGBToHSV((Color)c32, out float h, out float s, out float v);
        if (s < sMin || v < vMin) return UNCLASSIFIED;

        float hDeg = h * 360f;
        int best = 0;
        float bestDist = 999f;
        for (int i = 0; i < PALETTE_HUES.Length; i++)
        {
            float d = HueCircularDistanceDeg(hDeg, PALETTE_HUES[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }
    private static float HueCircularDistanceDeg(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return (d > 180f) ? 360f - d : d;
    }

    private static Color MeanColor(Color32[] cols, List<int> idx)
    {
        if (idx.Count == 0) return Color.white;
        double r=0,g=0,b=0;
        foreach (int i in idx) { r+=cols[i].r; g+=cols[i].g; b+=cols[i].b; }
        float inv = 1f/idx.Count;
        return new Color((float)(r*inv/255.0), (float)(g*inv/255.0), (float)(b*inv/255.0));
    }

    // ======================== KMeans (optional) ========================
    private int[] KMeansColor(byte[] R, byte[] G, byte[] B, int K, int iters, bool lab)
    {
        int n = R.Length;
        var data = new Vector3[n];
        if (lab) { for (int i = 0; i < n; i++) data[i] = RGB_to_Lab(new Color(R[i]/255f, G[i]/255f, B[i]/255f, 1f)); }
        else     { for (int i = 0; i < n; i++) data[i] = new Vector3(R[i], G[i], B[i]); }

        // k-means++ 초기화
        var rng = new System.Random(0xC0FFEE);
        var centers = new Vector3[K];
        centers[0] = data[rng.Next(n)];
        var dist = new float[n];
        for (int k = 1; k < K; k++)
        {
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                int ci = ClosestCenter(data[i], centers, k);
                float d = (data[i] - centers[ci]).sqrMagnitude;
                dist[i] = d; sum += d;
            }
            float r = (float)rng.NextDouble() * sum;
            float acc = 0f; int pick = 0;
            for (int i = 0; i < n; i++) { acc += dist[i]; if (acc >= r) { pick = i; break; } }
            centers[k] = data[pick];
        }

        var labels = new int[n];
        for (int it = 0; it < iters; it++)
        {
            bool changed = false;
            // Assign
            for (int i = 0; i < n; i++)
            {
                int best = ClosestCenter(data[i], centers, K);
                if (labels[i] != best) { labels[i] = best; changed = true; }
            }
            // Update
            var sumV = new Vector3[K];
            var cnt = new int[K];
            for (int i = 0; i < n; i++) { sumV[labels[i]] += data[i]; cnt[labels[i]]++; }
            for (int k = 0; k < K; k++) if (cnt[k] > 0) centers[k] = sumV[k] / cnt[k];
            if (!changed) break;
        }
        return labels;
    }
    private int ClosestCenter(Vector3 x, Vector3[] centers, int validK)
    {
        int best = 0; float bd = float.PositiveInfinity;
        for (int k = 0; k < validK; k++)
        {
            float d = (x - centers[k]).sqrMagnitude;
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }

    // sRGB -> Lab (간결 구현)
    private static Vector3 RGB_to_Lab(Color srgb)
    {
        float lin(float u) => (u <= 0.04045f) ? (u/12.92f) : Mathf.Pow((u+0.055f)/1.055f, 2.4f);
        float R = lin(srgb.r), G = lin(srgb.g), B = lin(srgb.b);
        float X = 0.4124564f*R + 0.3575761f*G + 0.1804375f*B;
        float Y = 0.2126729f*R + 0.7151522f*G + 0.0721750f*B;
        float Z = 0.0193339f*R + 0.1191920f*G + 0.9503041f*B;
        float Xn=0.95047f, Yn=1f, Zn=1.08883f;
        float f(float t) => (t>0.008856f) ? Mathf.Pow(t, 1f/3f) : (7.787f*t + 16f/116f);
        float fx=f(X/Xn), fy=f(Y/Yn), fz=f(Z/Zn);
        float L = 116f*fy - 16f;
        float a = 500f*(fx - fy);
        float b = 200f*(fy - fz);
        return new Vector3(L,a,b);
    }

    // ======================== Spatial: voxel BFS ========================
    private struct VKey : IEquatable<VKey>
    {
        public int x,y,z;
        public VKey(int x,int y,int z){ this.x=x; this.y=y; this.z=z; }
        public bool Equals(VKey o) => x==o.x && y==o.y && z==o.z;
        public override bool Equals(object o) => o is VKey v && Equals(v);
        public override int GetHashCode() => unchecked(x*73856093 ^ y*19349663 ^ z*83492791);
    }

    private List<int[]> SpatialClustersVoxelBFS(
        Vector3[] pts, List<int> indices, float voxel, int minPts, bool conn26)
    {
        // 1) voxelize (index → voxel key)
        var map = new Dictionary<VKey, List<int>>(indices.Count/4+8);
        float inv = 1f / Mathf.Max(voxel, 1e-6f);
        foreach (int i in indices)
        {
            Vector3 p = pts[i];
            int vx = Mathf.FloorToInt(p.x * inv);
            int vy = Mathf.FloorToInt(p.y * inv);
            int vz = Mathf.FloorToInt(p.z * inv);
            var key = new VKey(vx,vy,vz);
            if (!map.TryGetValue(key, out var list)) { list = new List<int>(8); map[key] = list; }
            list.Add(i);
        }

        // 2) BFS over voxels -> connected components
        var results = new List<int[]>();
        var visited = new HashSet<VKey>();
        var q = new Queue<VKey>();

        // neighbor offsets
        var neigh = new List<VKey>();
        for (int dx=-1; dx<=1; dx++)
        for (int dy=-1; dy<=1; dy++)
        for (int dz=-1; dz<=1; dz++)
        {
            if (dx==0 && dy==0 && dz==0) continue;
            if (!conn26 && Math.Abs(dx)+Math.Abs(dy)+Math.Abs(dz) != 1) continue; // 6-연결
            neigh.Add(new VKey(dx,dy,dz));
        }

        foreach (var kv in map)
        {
            var seed = kv.Key;
            if (visited.Contains(seed)) continue;

            var voxels = new List<VKey>(64);
            visited.Add(seed);
            q.Enqueue(seed);
            while (q.Count>0)
            {
                var v = q.Dequeue();
                voxels.Add(v);
                foreach (var d in neigh)
                {
                    var nkey = new VKey(v.x+d.x, v.y+d.y, v.z+d.z);
                    if (visited.Contains(nkey)) continue;
                    if (!map.ContainsKey(nkey)) continue;
                    visited.Add(nkey);
                    q.Enqueue(nkey);
                }
            }

            // gather point indices
            int count = 0;
            foreach (var vk in voxels) count += map[vk].Count;
            if (count >= minPts)
            {
                var buf = new int[count];
                int t=0;
                foreach (var vk in voxels)
                {
                    var list = map[vk];
                    list.CopyTo(0, buf, t, list.Count);
                    t += list.Count;
                }
                results.Add(buf);
            }
        }
        return results;
    }

    private static List<int> IndicesWhere(int[] labels, int value)
    {
        var list = new List<int>();
        for (int i = 0; i < labels.Length; i++) if (labels[i] == value) list.Add(i);
        return list;
    }

    // 간단 추정: 밀도 기반 s ≈ (Volume / N)^(1/3)
    private static float EstimateVoxelSizeFromDensity(Vector3[] pts)
    {
        if (pts == null || pts.Length == 0) return 0f;
        Vector3 min = pts[0], max = pts[0];
        for (int i=1;i<pts.Length;i++)
        {
            var p = pts[i];
            if (p.x<min.x) min.x=p.x; if (p.y<min.y) min.y=p.y; if (p.z<min.z) min.z=p.z;
            if (p.x>max.x) max.x=p.x; if (p.y>max.y) max.y=p.y; if (p.z>max.z) max.z=p.z;
        }
        Vector3 size = max - min;
        float vol = Mathf.Max(size.x*size.y*size.z, 1e-12f);
        float n = Mathf.Max(pts.Length, 1);
        float s = Mathf.Pow(vol / n, 1f/3f);
        return Mathf.Max(s, 1e-6f);
    }

    // ======================== Transform/Find helpers ========================
    private static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        foreach (Transform c in root)
        {
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }
}
