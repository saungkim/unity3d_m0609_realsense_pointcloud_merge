// UPCDColorClusterViewer.cs (faces visible + slack + fixed 0.025 side + debug toggles)
// - Load UPCD .bin → color split → spatial clusters
// - Strict 90° axes via PCA→world-axis snap (+ optional global lock)
// - Grid fit (u, offsets) → accept cells → build cell gizmos (spheres/edges)
// - ★ Build visible faces (quads) on boundary cells only
// - ★ Faces always drawn with fixed side length = faceSize (default 0.025)
// - ★ HasFaceSupportTiltAware with coverage slack to avoid disappearing faces
// - Debug: draw-all-faces (bypass evidence), and log face counts
//
// Controls (New Input System):
//   O: Load file
//   C: Build clusters
//   A: Show all clusters
//   H: Toggle original cloud rendering
//   M: Toggle color mode (FixedPalette/KMeans)
//   L: Toggle Lab for KMeans
//   N/P: Next/Prev cluster
//   1..9: Select index
//   E: Estimate (strict 90° grid → cells + faces)
//   K: Legacy PCA axes gizmo (kept but default OFF)
//   G: Toggle gizmos (cells/edges/faces visibility)
//   R: Reset global axis lock
//
// Notes:
// - Requires Unity Input System package.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public class UPCDColorClusterViewer : MonoBehaviour
{
    public enum ColorClusterMode { FixedPalette, KMeans }

    [Header("Load Settings")]
    public string filePath = "";
    public bool allowGlob = true;
    public bool applyTWorld = true;
    [Min(1)] public int strideLoad = 1;

    [Header("Color Split")]
    public ColorClusterMode colorMode = ColorClusterMode.FixedPalette;
    [Range(0f,1f)] public float minSaturation = 0.20f;
    [Range(0f,1f)] public float minValue = 0.20f;
    public bool includeUnclassified = false;

    [Range(2, 12)] public int kColors = 7;
    public bool useLab = true;
    [Range(1, 50)] public int kmeansIters = 10;

    [Header("Fixed Palette Hue Windows (deg)")]
    public HueBin[] hueBins = new HueBin[] {
        new HueBin("Red",         350f,   8f,  new Color(1f,0f,0f)),
        new HueBin("Orange",       18f,  38f,  new Color(1f,0.5f,0f)),
        new HueBin("Yellow",       45f,  70f,  new Color(1f,1f,0f)),
        new HueBin("YellowGreen",  82f, 104f,  new Color(0.6f,1f,0.2f)),
        new HueBin("Green",       122f, 160f,  new Color(0f,1f,0f)),
        new HueBin("SkyBlue",     195f, 205f,  new Color(0.00f,0.75f,1f)),
        new HueBin("Navy",        206f, 280f,  new Color(0.00f,0.10f,0.50f)),
    };

    [Header("Spatial Clustering (Voxel BFS)")]
    public float voxelSizeScale = 1.5f;
    public float defaultVoxelSize = 0.01f;
    public int   minPointsPerCluster = 200;
    public bool  connectivity26 = true;

    [Header("Soma Piece Filter (size/volume)")]
    public bool  enableSomaFilter = true;
    public float pieceMinLongest = 0.03f;
    public float pieceMaxLongest = 0.12f;
    public float pieceMinVolume  = 1e-5f;
    public float pieceMaxVolume  = 1.2e-3f;
    public float maxAspectRatio  = 5.0f;
    public int   maxVoxelCount   = 4000;
    public float minPointsPerVoxel = 3.0f;

    [Header("View")]
    public Material clusterMaterial;
    public bool showOriginal = true;

    [Header("Gizmos Visibility")]
    public bool showPoseGizmos = false; // default off (axes), user asked to hide axes
    public bool showCellGizmos = true;
    public bool showFaceGizmos = true;
    public float axisLength = 0.05f;
    public float axisWidth = 0.004f;

    // ==== Strict 90° grid ====
    [Header("Grid Fit (strict 90°)")]
    public float unitGuess = 0.03f;
    public bool  autoUnit = true;
    public float uMin = 0.026f, uMax = 0.034f;
    [Range(6,64)] public int unitSteps = 12;
    [Range(32,512)] public int offsetBins = 128;
    public int   sampleForFit = 2000;

    [Header("Voting (cells)")]
    [Range(0.00f, 0.5f)] public float boundaryBandFracX = 0.15f;
    [Range(0.00f, 0.5f)] public float boundaryBandFracY = 0.12f;
    [Range(0.00f, 0.5f)] public float boundaryBandFracZ = 0.15f;
    public int  cellMinPts = 14;
    [Range(0.50f, 0.95f)] public float boxHalfFrac = 0.75f;
    public int  minBoxPts = 10;

    [Header("Layer (Up=v1) selection")]
    public float layerKeepTopFrac = 0.25f;
    public int   layerMinAbs = 120;  // lowered a bit
    public float topLayerRelax = 0.8f;
    public int   layerMergeGapUnits = 0;
    public int   maxLayersToUse = 2; // prefer 1 or 2 layers

    [Header("Cell acceptance thresholds")]
    public float voteKeepTopFrac = 0.25f;
    public int   voteMinAbs = 50;
    public float voteKeepTopFracLayer = 0.25f;
    public int   voteMinAbsLayer = 30;
    public int   maxCellsPerCluster = 128;

    [Header("Strict Orthogonality Options")]
    public bool strictOrthogonalCenters = true;
    public bool lockAxesToFirstCluster = true;
    private bool axesLocked = false;
    private Quaternion globalSnap = Quaternion.identity;

    // ==== Face building (boundary quads) ====
    [Header("Face Visualization")]
    public bool drawFaces = true;
    [Tooltip("Fixed face side length (m)")] public float faceSize = 0.025f; // requested
    [Tooltip("Evidence slab half-thickness along face normal")]
    public float faceHalfThickness = 0.005f; // ±5mm
    [Tooltip("In-plane slack multiplier (coverage window = half * slack)")]
    public float faceInplaneSlack = 1.2f; // widen coverage box a bit
    [Tooltip("Max |distance to plane| to count (additional)")]
    public float planeDistanceTol = 0.006f;
    public int   coverageGrid = 6;
    public float coverageMinFrac = 0.25f;
    public int   coverageMinCells = 6;
    public bool  coverageRequireQuadrants = false;
    public float tiltSlabMultiplier = 2.0f; // be generous if tilted

    [Header("Debug")]
    public bool debugDrawAllBoundaryFaces = false;  // bypass evidence → draw all boundary faces
    public bool debugLogFaceCounts = true;

    [Header("Cell Gizmo")]
    public float centerSphereScale = 0.22f;
    public float edgeLineWidth = 0.006f;

    // internals
    private GameObject sourceGO;
    private Mesh sourceMesh;
    private GameObject clustersRoot;
    private readonly List<GameObject> clusterGOs = new();
    private readonly List<int[]> clusterIndices = new();
    private int selectedIndex = -1;

    private readonly Dictionary<int, GameObject> poseGizmoByCluster = new();
    private readonly Dictionary<int, GameObject> cellGizmoByCluster = new();
    private readonly Dictionary<int, GameObject> faceGizmoByCluster = new();

    // Input
    private InputAction actLoad, actBuild, actShowAll, actToggleOrig, actToggleLab, actNext, actPrev, actMode, actEstimate, actLegacy, actToggleGizmo, actResetAxes;
    private InputAction[] actDigits;

    [Serializable]
    public struct HueBin
    {
        public string name;
        [Range(0f,360f)] public float minDeg;
        [Range(0f,360f)] public float maxDeg;
        public Color displayColor;
        public HueBin(string n, float minD, float maxD, Color c){ name=n; minDeg=minD; maxDeg=maxD; displayColor=c; }
        public bool Contains(float h)
        {
            if (Mathf.Approximately(minDeg, maxDeg)) return Mathf.Approximately(h, minDeg);
            if (minDeg <= maxDeg) return (h >= minDeg && h <= maxDeg);
            return (h >= minDeg && h <= 360f) || (h >= 0f && h <= maxDeg);
        }
        public float Center()
        {
            if (minDeg <= maxDeg) return (minDeg + maxDeg) * 0.5f;
            float span = (360f - minDeg) + maxDeg;
            float c = minDeg + span * 0.5f;
            if (c >= 360f) c -= 360f;
            return c;
        }
    }

    void Awake()
    {
        actLoad      = new InputAction("Load",        InputActionType.Button, "<Keyboard>/o");
        actBuild     = new InputAction("Build",       InputActionType.Button, "<Keyboard>/c");
        actShowAll   = new InputAction("ShowAll",     InputActionType.Button, "<Keyboard>/a");
        actToggleOrig= new InputAction("ToggleOrig",  InputActionType.Button, "<Keyboard>/h");
        actToggleLab = new InputAction("ToggleLab",   InputActionType.Button, "<Keyboard>/l");
        actNext      = new InputAction("Next",        InputActionType.Button, "<Keyboard>/n");
        actPrev      = new InputAction("Prev",        InputActionType.Button, "<Keyboard>/p");
        actMode      = new InputAction("ToggleMode",  InputActionType.Button, "<Keyboard>/m");
        actEstimate  = new InputAction("Estimate",    InputActionType.Button, "<Keyboard>/e");
        actLegacy    = new InputAction("Legacy",      InputActionType.Button, "<Keyboard>/k");
        actToggleGizmo=new InputAction("ToggleGizmo", InputActionType.Button, "<Keyboard>/g");
        actResetAxes = new InputAction("ResetAxes",   InputActionType.Button, "<Keyboard>/r");

        actDigits = new InputAction[9];
        for (int i = 0; i < 9; i++)
            actDigits[i] = new InputAction($"Select{i+1}", InputActionType.Button, "<Keyboard>/" + (i+1).ToString());

        actLoad.performed      += _ => LoadFile();
        actBuild.performed     += _ => BuildClusters();
        actShowAll.performed   += _ => { selectedIndex = -1; ApplyVisibility(); };
        actToggleOrig.performed+= _ => { showOriginal = !showOriginal; ApplyVisibility(); };
        actToggleLab.performed += _ => { useLab = !useLab; Debug.Log($"[Cluster] (KMeans) useLab={useLab} (press C)"); };
        actMode.performed      += _ => { colorMode = (colorMode == ColorClusterMode.FixedPalette) ? ColorClusterMode.KMeans : ColorClusterMode.FixedPalette; Debug.Log($"[Cluster] colorMode={colorMode} (press C)"); };
        actNext.performed      += _ => SelectNext(+1);
        actPrev.performed      += _ => SelectNext(-1);
        actEstimate.performed  += _ => EstimateSomaStrict90();
        actLegacy.performed    += _ => EstimatePoseLegacyPCA();
        actToggleGizmo.performed += _ => {
            bool on = !(showCellGizmos || showFaceGizmos);
            showCellGizmos = on; showFaceGizmos = on;
            UpdateAllGizmoVisibility();
        };
        actResetAxes.performed += _ => { axesLocked=false; globalSnap=Quaternion.identity; Debug.Log("[Soma90] Global axis lock reset."); };

        for (int i = 0; i < 9; i++) { int idx = i; actDigits[i].performed += _ => SelectIndex(idx); }
    }

    void OnEnable()
    {
        actLoad.Enable(); actBuild.Enable(); actShowAll.Enable(); actToggleOrig.Enable();
        actToggleLab.Enable(); actNext.Enable(); actPrev.Enable(); actMode.Enable();
        actEstimate.Enable(); actLegacy.Enable(); actToggleGizmo.Enable(); actResetAxes.Enable();
        foreach (var a in actDigits) a.Enable();
    }
    void OnDisable()
    {
        foreach (var a in actDigits) a.Disable();
        actResetAxes.Disable(); actToggleGizmo.Disable(); actLegacy.Disable(); actEstimate.Disable(); actMode.Disable();
        actPrev.Disable(); actNext.Disable(); actToggleLab.Disable();
        actToggleOrig.Disable(); actShowAll.Disable(); actBuild.Disable(); actLoad.Disable();
    }

    // ======================== Load UPCD ========================
    public void LoadFile()
    {
        string path = filePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            string dir = Application.persistentDataPath;
            var files = Directory.GetFiles(dir, "pcd_*.bin");
            if (files.Length == 0) { Debug.LogWarning($"[UPCD] No pcd_*.bin under {dir}. Set filePath manually."); return; }
            Array.Sort(files, StringComparer.Ordinal);
            path = files[files.Length - 1];
            Debug.Log($"[UPCD] Auto-selected latest: {path}");
        }

        Vector3[] xyz; Color32[] rgb;
        Matrix4x4 Tworld; int coordSpace;
        if (allowGlob && (path.Contains("*") || path.Contains("?")))
        { MergeLoadGlob(path, out xyz, out rgb); Tworld = Matrix4x4.identity; coordSpace = 1; }
        else
        { ReadUPCD(path, applyTWorld, out xyz, out rgb, out Tworld, out coordSpace); }

        if (strideLoad > 1 && xyz.Length > 0)
        {
            xyz = Stride(xyz, strideLoad);
            if (rgb != null && rgb.Length > 0) rgb = Stride(rgb, strideLoad);
        }

        if (!sourceGO)
        {
            sourceGO = new GameObject("LoadedPointCloud");
            var mf = sourceGO.AddComponent<MeshFilter>();
            var mr = sourceGO.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")
                                   ?? Shader.Find("Unlit/Color")
                                   ?? Shader.Find("Sprites/Default"));
            mat.color = Color.white;
            mr.sharedMaterial = mat;
        }
        sourceGO.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        sourceGO.transform.localScale = Vector3.one;

        sourceMesh = BuildPointsMesh(xyz, rgb);
        sourceGO.GetComponent<MeshFilter>().sharedMesh = sourceMesh;

        ClearClusters();
        Debug.Log($"[UPCD] Loaded {xyz.Length} pts (colors={(rgb!=null)}, coord={(coordSpace==1?"world":"local")})");
    }

    private void ReadUPCD(string path, bool applyT, out Vector3[] xyz, out Color32[] rgb, out Matrix4x4 Tworld, out int coordSpace)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'U' || magic[1] != (byte)'P' || magic[2] != (byte)'C' || magic[3] != (byte)'D')
            throw new Exception("Invalid UPCD magic");

        ushort version = br.ReadUInt16();
        byte coord = br.ReadByte();
        byte hasColor = br.ReadByte();
        uint N = br.ReadUInt32();

        float[] tf = new float[16];
        for (int i = 0; i < 16; i++) tf[i] = br.ReadSingle();
        Tworld = RowMajorToMatrix(tf);

        xyz = new Vector3[N];
        rgb = hasColor==1 ? new Color32[N] : null;

        if (hasColor==1)
        {
            for (int i = 0; i < N; i++)
            {
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float z = br.ReadSingle();
                byte r = br.ReadByte();
                byte g = br.ReadByte();
                byte b = br.ReadByte();
                xyz[i] = new Vector3(x, y, z);
                rgb[i] = new Color32(r,g,b,255);
            }
        }
        else
        {
            for (int i = 0; i < N; i++)
            {
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float z = br.ReadSingle();
                xyz[i] = new Vector3(x, y, z);
            }
        }

        coordSpace = coord;

        if (applyT && coord == 0)
        {
            Matrix4x4 R = Tworld;
            for (int i = 0; i < xyz.Length; i++)
                xyz[i] = R.MultiplyPoint3x4(xyz[i]);
            coordSpace = 1;
        }
    }

    private void MergeLoadGlob(string pattern, out Vector3[] xyz, out Color32[] rgb)
    {
        string dir = Path.GetDirectoryName(pattern);
        if (string.IsNullOrEmpty(dir)) dir = Application.persistentDataPath;
        string file = Path.GetFileName(pattern);
        var files = Directory.GetFiles(dir, file);
        Array.Sort(files, StringComparer.Ordinal);

        var allPts = new List<Vector3>(1<<20);
        var allRgb = new List<Color32>(1<<20);
        bool allHaveColor = true;

        foreach (var p in files)
        {
            ReadUPCD(p, applyTWorld, out var pts, out var cols, out var Tw, out var cs);
            allPts.AddRange(pts);
            if (cols != null) allRgb.AddRange(cols);
            else allHaveColor = false;
        }

        xyz = allPts.ToArray();
        rgb = allHaveColor ? allRgb.ToArray() : null;
    }

    private static Matrix4x4 RowMajorToMatrix(float[] f16)
    {
        Matrix4x4 m = new Matrix4x4();
        m.m00=f16[0];  m.m01=f16[1];  m.m02=f16[2];  m.m03=f16[3];
        m.m10=f16[4];  m.m11=f16[5];  m.m12=f16[6];  m.m13=f16[7];
        m.m20=f16[8];  m.m21=f16[9];  m.m22=f16[10]; m.m23=f16[11];
        m.m30=f16[12]; m.m31=f16[13]; m.m32=f16[14]; m.m33=f16[15];
        return m;
    }

    private static Vector3[] Stride(Vector3[] a, int s)
    {
        if (s<=1 || a==null || a.Length==0) return a;
        int n = (a.Length + s - 1) / s;
        var o = new Vector3[n];
        for (int i=0,j=0;i<n;i++,j+=s) o[i]=a[j];
        return o;
    }
    private static Color32[] Stride(Color32[] a, int s)
    {
        if (s<=1 || a==null || a.Length==0) return a;
        int n = (a.Length + s - 1) / s;
        var o = new Color32[n];
        for (int i=0,j=0;i<n;i++,j+=s) o[i]=a[j];
        return o;
    }

    // ======================== Build Mesh & Clusters ========================
    private Mesh BuildPointsMesh(Vector3[] verts, Color32[] cols32)
    {
        int n = verts.Length;
        var mesh = new Mesh
        {
            indexFormat = (n > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32
                                     : UnityEngine.Rendering.IndexFormat.UInt16)
        };
        mesh.SetVertices(verts);
        if (cols32 != null && cols32.Length == n) mesh.SetColors(cols32);
        else
        {
            var whites = Enumerable.Repeat(new Color32(255,255,255,255), n).ToArray();
            mesh.SetColors(whites);
        }
        int[] idx = Enumerable.Range(0, n).ToArray();
        mesh.SetIndices(idx, MeshTopology.Points, 0, false);
        mesh.RecalculateBounds();
        return mesh;
    }

    public void BuildClusters()
    {
        if (sourceMesh == null || sourceMesh.vertexCount == 0)
        { Debug.LogWarning("[Cluster] No source mesh. Press O to load a file first."); return; }

        var verts = sourceMesh.vertices;
        var cols  = sourceMesh.colors32;
        int n = verts.Length;

        // 1) 색 라벨링
        int[] colorLabel;
        if (colorMode == ColorClusterMode.FixedPalette)
        {
            EnsureBins();
            colorLabel = new int[n];
            for (int i = 0; i < n; i++)
                colorLabel[i] = LabelByHueWindows(cols[i], minSaturation, minValue);
        }
        else
        {
            var R=new byte[n]; var G=new byte[n]; var B=new byte[n];
            for (int i=0;i<n;i++){ R[i]=cols[i].r; G[i]=cols[i].g; B[i]=cols[i].b; }
            int K = Mathf.Clamp(kColors, 2, 12);
            colorLabel = KMeansColor(R,G,B,K,Mathf.Max(1,kmeansIters),useLab);
        }

        // 2) 보xel 크기 추정
        float voxel = EstimateVoxelSizeFromDensity(verts);
        if (!(voxel > 0f)) voxel = defaultVoxelSize;
        voxel *= Mathf.Max(0.001f, voxelSizeScale);

        // 3) 색별 공간 군집
        var preClusters = new List<(string cname, Color visColor, int[] indices)>();
        if (colorMode == ColorClusterMode.FixedPalette)
        {
            for (int c = 0; c < hueBins.Length; c++)
            {
                var idx = IndicesWhere(colorLabel, c);
                if (idx.Count < minPointsPerCluster) continue;
                var comps = SpatialClustersVoxelBFS(verts, idx, voxel, minPointsPerCluster, connectivity26);
                foreach (var cc in comps) preClusters.Add((hueBins[c].name, hueBins[c].displayColor, cc));
            }
            if (includeUnclassified)
            {
                var idxU = IndicesWhere(colorLabel, -1);
                if (idxU.Count >= minPointsPerCluster)
                {
                    var compsU = SpatialClustersVoxelBFS(verts, idxU, voxel, minPointsPerCluster, connectivity26);
                    foreach (var cc in compsU) preClusters.Add(("Unclassified", Color.white, cc));
                }
            }
        }
        else
        {
            int K = colorLabel.Max()+1;
            for (int c = 0; c < K; c++)
            {
                var idx = IndicesWhere(colorLabel, c);
                if (idx.Count < minPointsPerCluster) continue;
                var comps = SpatialClustersVoxelBFS(verts, idx, voxel, minPointsPerCluster, connectivity26);
                Color meanCol = MeanColor(cols, idx);
                foreach (var cc in comps) preClusters.Add(($"K{c}", meanCol, cc));
            }
        }

        // 4) Soma piece filter
        var allClusters = new List<(string cname, Color visColor, int[] indices)>();
        int dropped = 0;
        foreach (var pc in preClusters)
        {
            var stats = ComputeClusterStats(verts, pc.indices, voxel);
            if (!enableSomaFilter || PassSomaFilter(stats))
                allClusters.Add(pc);
            else
            {
                dropped++;
                Debug.Log($"[Filter] drop '{pc.cname}' | N={pc.indices.Length} | Lmax={stats.longest:F3}m, Vol={stats.volumeAABB:E3} m^3, Aspect={stats.aspect:F2}, Vox={stats.voxelCount}, pts/vox={stats.pointsPerVoxel:F2}");
            }
        }

        // 5) 메쉬 생성
        BuildClusterMeshes(verts, cols, allClusters);
        selectedIndex = -1;
        ApplyVisibility();

        Debug.Log($"[Cluster] Built {clusterGOs.Count} clusters (filtered {dropped})");
    }

    private void BuildClusterMeshes(Vector3[] verts, Color32[] cols32,
        List<(string cname, Color visColor, int[] indices)> clusters)
    {
        if (!clustersRoot)
            clustersRoot = new GameObject("Clusters");

        clustersRoot.transform.SetParent(sourceGO != null ? sourceGO.transform : transform, worldPositionStays:false);
        clustersRoot.transform.localPosition = Vector3.zero;
        clustersRoot.transform.localRotation = Quaternion.identity;
        clustersRoot.transform.localScale    = Vector3.one;

        ClearClusters();

        var baseMat = clusterMaterial ?? new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")
                                                      ?? Shader.Find("Unlit/Color")
                                                      ?? Shader.Find("Sprites/Default"));

        for (int ci = 0; ci < clusters.Count; ci++)
        {
            var (cname, visColor, idx) = clusters[ci];
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

            var go = new GameObject($"Cluster_{ci:00}_{cname}");
            go.transform.SetParent(clustersRoot.transform, worldPositionStays:false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;

            var mat = new Material(baseMat); mat.color = visColor;
            mr.sharedMaterial = mat;

            clusterGOs.Add(go);
            clusterIndices.Add(idx);
        }
    }

    private void ClearClusters()
    {
        foreach (var kv in poseGizmoByCluster) if (kv.Value) Destroy(kv.Value);
        poseGizmoByCluster.Clear();
        foreach (var kv in cellGizmoByCluster) if (kv.Value) Destroy(kv.Value);
        cellGizmoByCluster.Clear();
        foreach (var kv in faceGizmoByCluster) if (kv.Value) Destroy(kv.Value);
        faceGizmoByCluster.Clear();

        if (clusterGOs.Count > 0)
        {
            foreach (var go in clusterGOs) if (go) Destroy(go);
            clusterGOs.Clear();
            clusterIndices.Clear();
        }
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (sourceGO)
        {
            var mr = sourceGO.GetComponent<MeshRenderer>();
            if (mr) mr.enabled = (showOriginal && selectedIndex < 0);
        }

        for (int i = 0; i < clusterGOs.Count; i++)
        {
            var go = clusterGOs[i]; if (!go) continue;
            var mr = go.GetComponent<MeshRenderer>(); if (!mr) continue;
            bool show = (selectedIndex < 0 || selectedIndex == i);
            mr.enabled = show;

            if (cellGizmoByCluster.TryGetValue(i, out var cellsGO) && cellsGO)
                cellsGO.SetActive(show && showCellGizmos);
            if (faceGizmoByCluster.TryGetValue(i, out var facesGO) && facesGO)
                facesGO.SetActive(show && showFaceGizmos);
            if (poseGizmoByCluster.TryGetValue(i, out var axesGO) && axesGO)
                axesGO.SetActive(show && showPoseGizmos);
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
    private void UpdateAllGizmoVisibility() => ApplyVisibility();

    // ======================== Palette / KMeans Helpers ========================
    private void EnsureBins()
    {
        if (hueBins == null || hueBins.Length == 0)
        {
            hueBins = new HueBin[] {
                new HueBin("Red",         350f,   8f,  new Color(1f,0f,0f)),
                new HueBin("Orange",       18f,  38f,  new Color(1f,0.5f,0f)),
                new HueBin("Yellow",       45f,  70f,  new Color(1f,1f,0f)),
                new HueBin("YellowGreen",  82f, 104f,  new Color(0.6f,1f,0.2f)),
                new HueBin("Green",       122f, 160f,  new Color(0f,1f,0f)),
                new HueBin("SkyBlue",     195f, 205f,  new Color(0.00f,0.75f,1f)),
                new HueBin("Navy",        206f, 280f,  new Color(0.00f,0.10f,0.50f)),
            };
        }
    }

    private int LabelByHueWindows(Color32 c32, float sMin, float vMin)
    {
        Color c = c32;
        Color.RGBToHSV(c, out float h, out float s, out float v);
        if (s < sMin || v < vMin) return -1;
        float hDeg = h * 360f;
        EnsureBins();

        int best = -1; float bestDist = float.PositiveInfinity;
        for (int i = 0; i < hueBins.Length; i++)
        {
            if (!hueBins[i].Contains(hDeg)) continue;
            float center = hueBins[i].Center();
            float d = HueCircularDistanceDeg(hDeg, center);
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

    private int[] KMeansColor(byte[] R, byte[] G, byte[] B, int K, int iters, bool lab)
    {
        int n = R.Length;
        var data = new Vector3[n];
        if (lab) { for (int i = 0; i < n; i++) data[i] = RGB_to_Lab(new Color(R[i]/255f, G[i]/255f, B[i]/255f, 1f)); }
        else     { for (int i = 0; i < n; i++) data[i] = new Vector3(R[i], G[i], B[i]); }

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
            for (int i = 0; i < n; i++)
            {
                int best = ClosestCenter(data[i], centers, K);
                if (labels[i] != best) { labels[i] = best; changed = true; }
            }
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

    private List<int[]> SpatialClustersVoxelBFS(Vector3[] pts, List<int> indices, float voxel, int minPts, bool conn26)
    {
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

        var results = new List<int[]>();
        var visited = new HashSet<VKey>();
        var q = new Queue<VKey>();

        var neigh = new List<VKey>();
        for (int dx=-1; dx<=1; dx++)
        for (int dy=-1; dy<=1; dy++)
        for (int dz=-1; dz<=1; dz++)
        {
            if (dx==0 && dy==0 && dz==0) continue;
            if (!conn26 && Math.Abs(dx)+Math.Abs(dy)+Math.Abs(dz) != 1) continue;
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

    private struct ClusterStats
    {
        public Vector3 min, max, size;
        public float longest, shortest, volumeAABB, aspect;
        public int voxelCount;
        public float pointsPerVoxel;
    }

    private ClusterStats ComputeClusterStats(Vector3[] pts, int[] idx, float voxel)
    {
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int i = 0; i < idx.Length; i++)
        {
            var p = pts[idx[i]];
            if (p.x < min.x) min.x = p.x; if (p.y < min.y) min.y = p.y; if (p.z < min.z) min.z = p.z;
            if (p.x > max.x) max.x = p.x; if (p.y > max.y) max.y = p.y; if (p.z > max.z) max.z = p.z;
        }
        Vector3 size = max - min;
        float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float shortest = Mathf.Max(1e-9f, Mathf.Min(size.x, Mathf.Min(size.y, size.z)));
        float volumeAABB = Mathf.Max(size.x*size.y*size.z, 1e-12f);
        float aspect = longest / shortest;

        int vcount = 0;
        if (voxel > 0f)
        {
            float inv = 1f / voxel;
            var set = new HashSet<(int,int,int)>();
            for (int i=0;i<idx.Length;i++)
            {
                var p = pts[idx[i]];
                int vx = Mathf.FloorToInt(p.x * inv);
                int vy = Mathf.FloorToInt(p.y * inv);
                int vz = Mathf.FloorToInt(p.z * inv);
                set.Add((vx,vy,vz));
            }
            vcount = set.Count;
        }
        float ppv = (vcount>0) ? (idx.Length / (float)vcount) : float.PositiveInfinity;

        return new ClusterStats {
            min=min, max=max, size=size, longest=longest, shortest=shortest,
            volumeAABB=volumeAABB, aspect=aspect, voxelCount=vcount, pointsPerVoxel=ppv
        };
    }

    private bool PassSomaFilter(ClusterStats s)
    {
        if (s.longest < pieceMinLongest) return false;
        if (s.longest > pieceMaxLongest) return false;
        if (s.volumeAABB < pieceMinVolume) return false;
        if (s.volumeAABB > pieceMaxVolume) return false;
        if (maxAspectRatio > 0f && s.aspect > maxAspectRatio) return false;
        if (maxVoxelCount > 0 && s.voxelCount > maxVoxelCount) return false;
        if (minPointsPerVoxel > 0f && s.pointsPerVoxel < minPointsPerVoxel) return false;
        return true;
    }

    // ======================== LEGACY PCA SNAP (K) ========================
    private void EstimatePoseLegacyPCA()
    {
        if (clusterGOs.Count == 0) { Debug.LogWarning("[Legacy] No clusters."); return; }
        if (selectedIndex >= 0) PCAAndAxes(selectedIndex, out _, true);
        else for (int i=0;i<clusterGOs.Count;i++) PCAAndAxes(i, out _, true);
        UpdateAllGizmoVisibility();
    }

    // ======================== STRICT 90° MULTI BOX (E) ========================
    private void EstimateSomaStrict90()
    {
        if (clusterGOs.Count == 0) { Debug.LogWarning("[Soma90] No clusters. Build first (C)."); return; }
        if (selectedIndex >= 0) SomaStrict90_ForCluster(selectedIndex);
        else for (int i=0;i<clusterGOs.Count;i++) SomaStrict90_ForCluster(i);
        UpdateAllGizmoVisibility();
    }

    private void SomaStrict90_ForCluster(int ci)
    {
        if (ci < 0 || ci >= clusterGOs.Count) return;
        var verts = sourceMesh.vertices;
        var idx = clusterIndices[ci];
        if (idx == null || idx.Length < 60) { Debug.LogWarning($"[Soma90] Cluster {ci} too small."); return; }

        // 1) PCA → nearest world axes; optional global lock
        PCAAndAxes(ci, out (Vector3 mu, Vector3 v0, Vector3 v1, Vector3 v2) lf, true);

        // 2) Local coords
        var local = new Vector3[idx.Length];
        for (int i=0;i<idx.Length;i++)
        {
            Vector3 p = verts[idx[i]] - lf.mu;
            local[i] = new Vector3(Vector3.Dot(p, lf.v0), Vector3.Dot(p, lf.v1), Vector3.Dot(p, lf.v2));
        }

        // 3) Unit size & offsets (Y-stabilized)
        float u = unitGuess;
        Vector3 o = Vector3.zero;
        bool ok = true;

        if (autoUnit) ok = TryFitGrid(local, out u, out _); // u only
        if (!ok) u = Mathf.Max(1e-4f, unitGuess);

        o.x = BestOffset1D(local.Select(p => p.x).ToArray(), u, offsetBins, out _, out _);
        o.z = BestOffset1D(local.Select(p => p.z).ToArray(), u, offsetBins, out _, out _);
        if (local.Length > 0)
        {
            float y_mean = 0f; for (int i=0;i<local.Length;i++) y_mean += local[i].y; y_mean /= local.Length;
            float n_float = (y_mean / u) - 0.5f;
            o.y = y_mean - (Mathf.Round(n_float) + 0.5f) * u;
        }

        // 4) Votes / per-cell counts / layer histogram
        var votes = new Dictionary<(int,int,int), int>();
        var perCellCount = new Dictionary<(int,int,int), int>();
        var layerCount = new Dictionary<int, int>();

        float bandX = Mathf.Clamp01(boundaryBandFracX) * u;
        float bandY = Mathf.Clamp01(boundaryBandFracY) * u;
        float bandZ = Mathf.Clamp01(boundaryBandFracZ) * u;

        for (int i = 0; i < local.Length; i++)
        {
            Vector3 l = local[i];

            int ix = Mathf.FloorToInt((l.x - o.x) / u);
            int iy = Mathf.FloorToInt((l.y - o.y) / u);
            int iz = Mathf.FloorToInt((l.z - o.z) / u);

            float fx = l.x - (o.x + ix * u);
            float fy = l.y - (o.y + iy * u);
            float fz = l.z - (o.z + iz * u);

            fx -= u * Mathf.Floor(fx / u); if (fx < 0) fx += u;
            fy -= u * Mathf.Floor(fy / u); if (fy < 0) fy += u;
            fz -= u * Mathf.Floor(fz / u); if (fz < 0) fz += u;

            var c0 = (ix, iy, iz);
            AddVote(votes, c0, 2);
            AddCount(perCellCount, c0);

            if (fx < bandX) { var k=(ix-1,iy,iz); AddVote(votes,k,1); AddCount(perCellCount,k); }
            else if (fx > u - bandX) { var k=(ix+1,iy,iz); AddVote(votes,k,1); AddCount(perCellCount,k); }
            if (fy < bandY) { var k=(ix,iy-1,iz); AddVote(votes,k,1); AddCount(perCellCount,k); }
            else if (fy > u - bandY) { var k=(ix,iy+1,iz); AddVote(votes,k,1); AddCount(perCellCount,k); }
            if (fz < bandZ) { var k=(ix,iy,iz-1); AddVote(votes,k,1); AddCount(perCellCount,k); }
            else if (fz > u - bandZ) { var k=(ix,iy,iz+1); AddVote(votes,k,1); AddCount(perCellCount,k); }

            layerCount.TryGetValue(iy, out int lc); layerCount[iy] = lc + 1;
        }

        if (votes.Count == 0) { Debug.LogWarning($"[Soma90] no votes."); return; }

        // 5) Layer selection (peak only)
        var allowedLayers = SelectLayers(layerCount);

        // 6) Cell acceptance
        int vmax = 0; foreach (var kv in votes) if (kv.Value>vmax) vmax=kv.Value;
        int globalThr = Mathf.Max(voteMinAbs, Mathf.CeilToInt(vmax * Mathf.Clamp01(voteKeepTopFrac)));

        var layerMax = new Dictionary<int, int>();
        foreach (var kv in votes)
        { int y = kv.Key.Item2; if (!layerMax.TryGetValue(y, out int m) || kv.Value > m) layerMax[y] = kv.Value; }

        var cells = new List<((int,int,int) ijk, int v)>();
        foreach (var kv in votes)
        {
            int iy = kv.Key.Item2;
            if (!allowedLayers.Contains(iy)) continue;

            int layerThr = Mathf.Max(voteMinAbsLayer, Mathf.CeilToInt(layerMax[iy]*Mathf.Clamp01(voteKeepTopFracLayer)));
            if (IsTopLayer(iy, allowedLayers)) layerThr = Mathf.CeilToInt(layerThr * Mathf.Clamp(topLayerRelax, 0.5f, 1.0f));

            int thr = Math.Max(globalThr, layerThr);
            if (kv.Value < thr) continue;

            if (!perCellCount.TryGetValue(kv.Key, out int cnt) || cnt < cellMinPts) continue;
            if (!HasLocalBoxSupport(kv.Key, local, u, o)) continue;

            cells.Add((kv.Key, kv.Value));
            if (cells.Count >= maxCellsPerCluster) break;
        }

        if (cells.Count == 0)
        {
            Debug.LogWarning("[Soma90] No accepted cells (try thresholds).");
            return;
        }

        // 7) Gizmos: cells (spheres/edges)
        CreateOrUpdateCellGizmos_MultiStrict(ci, cells, lf, u, o);

        // 8) Faces: build boundary quads (visible)
        if (drawFaces) CreateOrUpdateCubeFaceGizmos(ci, cells.Select(e=>e.ijk).ToList(), lf, u, o, local);

        var layerText = string.Join(",", allowedLayers.OrderBy(x=>x));
        Debug.Log($"[Soma90] Cluster {ci} | u={u:F4}m, cells={cells.Count}, layers[{allowedLayers.Count}]={layerText}");
    }

    private static void AddVote(Dictionary<(int,int,int),int> dict, (int,int,int) k, int w)
    { dict.TryGetValue(k, out int vv); dict[k] = vv + w; }

    private static void AddCount(Dictionary<(int,int,int),int> dict, (int,int,int) k)
    { if (!dict.ContainsKey(k)) dict[k]=0; dict[k]++; }

    // Peak-only layer selection
    private HashSet<int> SelectLayers(Dictionary<int, int> layerCount)
    {
        var finalLayers = new HashSet<int>();
        if (layerCount == null || layerCount.Count == 0) return finalLayers;

        int peakLayerY = 0;
        int maxPointsInLayer = -1;
        foreach (var kvp in layerCount)
        {
            if (kvp.Value > maxPointsInLayer)
            {
                maxPointsInLayer = kvp.Value;
                peakLayerY = kvp.Key;
            }
        }
        if (maxPointsInLayer >= layerMinAbs) finalLayers.Add(peakLayerY);
        return finalLayers;
    }

    private static bool IsTopLayer(int iy, HashSet<int> allowed)
    { if (allowed.Count == 0) return false; int max= int.MinValue; foreach(var a in allowed) if (a>max) max=a; return iy==max; }

    private bool HasLocalBoxSupport((int,int,int) cell, Vector3[] local, float u, Vector3 o)
    {
        float hx = u * boxHalfFrac;
        float hy = u * boxHalfFrac;
        float hz = u * boxHalfFrac;
        Vector3 c = CellCenterLocal(cell, u, o);

        int cnt = 0;
        for (int t=0; t<local.Length; t++)
        {
            Vector3 d = local[t] - c;
            if (Mathf.Abs(d.x) <= hx && Mathf.Abs(d.y) <= hy && Mathf.Abs(d.z) <= hz)
            {
                cnt++;
                if (cnt >= minBoxPts) return true;
            }
        }
        return false;
    }

    // === Linear algebra & axes (strict) ===
    private void PCAAndAxes(int ci, out (Vector3 mu, Vector3 v0, Vector3 v1, Vector3 v2) lf, bool _unused)
    {
        var verts = sourceMesh.vertices;
        var idx = clusterIndices[ci];

        Vector3 mu = Vector3.zero;
        for (int i=0;i<idx.Length;i++) mu += verts[idx[i]];
        mu /= Mathf.Max(1, idx.Length);

        // covariance
        float sxx=0, sxy=0, sxz=0, syy=0, syz=0, szz=0;
        for (int i=0;i<idx.Length;i++)
        {
            var d = verts[idx[i]] - mu;
            sxx += d.x*d.x; sxy += d.x*d.y; sxz += d.x*d.z;
            syy += d.y*d.y; syz += d.y*d.z; szz += d.z*d.z;
        }
        float invN = 1f / Mathf.Max(1, idx.Length);
        sxx*=invN; sxy*=invN; sxz*=invN; syy*=invN; syz*=invN; szz*=invN;

        Vector3 v0 = PowerIter3(sxx,sxy,sxz,syy,syz,szz, new Vector3(1,0.2f,0.1f), 16);
        Vector3 v1s = new Vector3(0.3f,1,0.2f);
        for (int it=0; it<2; it++) v1s = (v1s - Vector3.Dot(v1s, v0)*v0).normalized;
        Vector3 v1 = PowerIter3(sxx,sxy,sxz,syy,syz,szz, v1s, 16);
        v1 = (v1 - Vector3.Dot(v1, v0)*v0).normalized;
        Vector3 v2 = Vector3.Cross(v0, v1).normalized;
        if (Vector3.Dot(Vector3.Cross(v0, v1), v2) < 0f) v2 = -v2;

        // Snap to world axes; lock optionally
        Quaternion qSnap = FindNearestAxisAlignedRotation(v0, v1, v2);
        if (lockAxesToFirstCluster)
        {
            if (!axesLocked) { globalSnap = qSnap; axesLocked = true; }
            qSnap = globalSnap; // same axes for all clusters
        }
        v0 = qSnap * Vector3.right;
        v1 = qSnap * Vector3.up;
        v2 = qSnap * Vector3.forward;

        lf = (mu, v0, v1, v2);

        if (showPoseGizmos) CreateOrUpdateAxes(ci, mu, v0, v1, v2);
    }

    private static Vector3 PowerIter3(float sxx,float sxy,float sxz,float syy,float syz,float szz, Vector3 init, int iters)
    {
        Vector3 v = init.normalized;
        for (int i=0;i<iters;i++)
        {
            Vector3 Av = new Vector3(
                sxx*v.x + sxy*v.y + sxz*v.z,
                sxy*v.x + syy*v.y + syz*v.z,
                sxz*v.x + syz*v.y + szz*v.z
            );
            float n = Av.magnitude;
            if (n < 1e-12f) break;
            v = Av / Mathf.Max(n, 1e-12f);
        }
        return v.normalized;
    }

    private static Quaternion FindNearestAxisAlignedRotation(Vector3 v0, Vector3 v1, Vector3 v2)
    {
        Vector3[] E = { Vector3.right, Vector3.up, Vector3.forward };
        int[][] perms = { new[]{0,1,2}, new[]{0,2,1}, new[]{1,0,2}, new[]{1,2,0}, new[]{2,0,1}, new[]{2,1,0} };
        float best = -1e9f; Vector3 b0=Vector3.right, b1=Vector3.up, b2=Vector3.forward;
        foreach (var p in perms)
        for (int s0=-1; s0<=1; s0+=2)
        for (int s1=-1; s1<=1; s1+=2)
        for (int s2=-1; s2<=1; s2+=2)
        {
            Vector3 c0 = s0 * E[p[0]], c1 = s1 * E[p[1]], c2 = s2 * E[p[2]];
            if (Vector3.Dot(Vector3.Cross(c0,c1), c2) < 0) continue; // proper
            float score = Mathf.Abs(Vector3.Dot(v0,c0)) + Mathf.Abs(Vector3.Dot(v1,c1)) + Mathf.Abs(Vector3.Dot(v2,c2));
            if (score > best) { best=score; b0=c0; b1=c1; b2=c2; }
        }
        return Quaternion.LookRotation(b2, b1);
    }

    private bool TryFitGrid(Vector3[] local, out float u, out Vector3 o)
    {
        u = unitGuess; o = Vector3.zero;
        if (local == null || local.Length == 0) return false;

        int N = Mathf.Min(sampleForFit, local.Length);
        var rng = new System.Random(12345);
        var xs = new float[N];
        var ys = new float[N];
        var zs = new float[N];
        for (int i=0;i<N;i++)
        {
            var p = local[rng.Next(local.Length)];
            xs[i] = p.x; ys[i] = p.y; zs[i] = p.z;
        }

        int U = Mathf.Max(6, unitSteps);
        int B = Mathf.Clamp(offsetBins, 32, 512);

        float bestScore = float.PositiveInfinity;
        float bestU = unitGuess;
        Vector3 bestO = Vector3.zero;

        for (int ui=0; ui<U; ui++)
        {
            float candU = Mathf.Lerp(uMin, uMax, (ui+0.5f)/U);
            if (!(candU > 1e-6f)) continue;

            float ox = BestOffset1D(xs, candU, B, out _, out float scoreX);
            float oy = BestOffset1D(ys, candU, B, out _, out float scoreY);
            float oz = BestOffset1D(zs, candU, B, out _, out float scoreZ);

            float score = scoreX + scoreY + scoreZ;
            if (score < bestScore)
            {
                bestScore = score;
                bestU = candU;
                bestO = new Vector3(ox, oy, oz);
            }
        }

        u = bestU; o = bestO;
        return float.IsFinite(bestScore);
    }

    private static float BestOffset1D(float[] t, float u, int bins, out int peakCount, out float score)
    {
        int B = bins;
        int[] hist = new int[B];
        float invu = 1f / u;

        for (int i=0;i<t.Length;i++)
        {
            float y = t[i] * invu;
            y -= Mathf.Floor(y);
            int b = (int)Mathf.Clamp(Mathf.Floor(y * B), 0, B-1);
            hist[b]++;
        }

        int maxB = 0, maxC = 0;
        for (int b=0;b<B;b++) if (hist[b] > maxC) { maxC = hist[b]; maxB = b; }

        float binCenter = (maxB + 0.5f) / B;
        float delta = 0.5f - binCenter;
        float o = (delta - Mathf.Floor(delta)) * u;

        peakCount = maxC;
        score = 1f - (maxC / Mathf.Max(1f, (float)t.Length));
        return o;
    }

    private static (int,int,int) NearestCellIndex(Vector3 l, float u, Vector3 o)
    {
        int ix = Mathf.RoundToInt((l.x - o.x)/u - 0.5f);
        int iy = Mathf.RoundToInt((l.y - o.y)/u - 0.5f);
        int iz = Mathf.RoundToInt((l.z - o.z)/u - 0.5f);
        return (ix,iy,iz);
    }

    private static Vector3 CellCenterLocal((int,int,int) idx, float u, Vector3 o)
    {
        return new Vector3(o.x + (idx.Item1 + 0.5f)*u,
                           o.y + (idx.Item2 + 0.5f)*u,
                           o.z + (idx.Item3 + 0.5f)*u);
    }

    // === Axes Gizmo (optional) ===
    private void CreateOrUpdateAxes(int ci, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z)
    {
        GameObject root;
        if (!poseGizmoByCluster.TryGetValue(ci, out root) || !root)
        {
            root = new GameObject($"PoseAxes_{ci:00}");
            root.transform.SetParent(clusterGOs[ci].transform, worldPositionStays:false);
            poseGizmoByCluster[ci] = root;
            CreateAxisLR(root, Color.red,   "X");
            CreateAxisLR(root, Color.green, "Y");
            CreateAxisLR(root, Color.blue,  "Z");
        }
        var x = root.transform.Find("Axis_X").GetComponent<LineRenderer>();
        var y = root.transform.Find("Axis_Y").GetComponent<LineRenderer>();
        var z = root.transform.Find("Axis_Z").GetComponent<LineRenderer>();

        SetLR(x, origin, origin + X.normalized*axisLength);
        SetLR(y, origin, origin + Y.normalized*axisLength);
        SetLR(z, origin, origin + Z.normalized*axisLength);
    }
    private void CreateAxisLR(GameObject parent, Color col, string name)
    {
        var go = new GameObject($"Axis_{name}");
        go.transform.SetParent(parent.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.startWidth = axisWidth; lr.endWidth = axisWidth;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = col; lr.endColor = col;
        lr.numCornerVertices = 4; lr.numCapVertices = 4;
    }
    private void SetLR(LineRenderer lr, Vector3 a, Vector3 b){ lr.SetPosition(0,a); lr.SetPosition(1,b); }

    // === Cell gizmo (spheres + 6-neighbor edges) ===
    private void CreateOrUpdateCellGizmos_MultiStrict(
        int ci,
        List<((int,int,int) ijk, int v)> cells,
        (Vector3 mu, Vector3 v0, Vector3 v1, Vector3 v2) lf,
        float u, Vector3 o
    )
    {
        GameObject root;
        if (!cellGizmoByCluster.TryGetValue(ci, out root) || !root)
        {
            root = new GameObject($"SomaCells_{ci:00}");
            root.transform.SetParent(clusterGOs[ci].transform, worldPositionStays:false);
            cellGizmoByCluster[ci] = root;
        }
        for (int i=root.transform.childCount-1; i>=0; i--) Destroy(root.transform.GetChild(i).gameObject);

        if (!showCellGizmos) { root.SetActive(false); return; }
        root.SetActive(true);

        float r = Mathf.Max(0.001f, u * Mathf.Clamp(centerSphereScale, 0.1f, 0.5f));
        var worldCenters = new Dictionary<(int,int,int), Vector3>(cells.Count);

        foreach (var c in cells)
        {
            Vector3 lc = CellCenterLocal(c.ijk, u, o);
            Vector3 wc = lf.mu + lf.v0*lc.x + lf.v1*lc.y + lf.v2*lc.z;
            worldCenters[c.ijk] = wc;

            var sgo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sgo.name = $"Cell_{c.ijk.Item1}_{c.ijk.Item2}_{c.ijk.Item3}_{c.v}";
            sgo.transform.SetParent(root.transform, false);
            sgo.transform.position = wc;
            sgo.transform.localScale = Vector3.one * (2f*r);
            var mr = sgo.GetComponent<MeshRenderer>();
            if (mr) { mr.sharedMaterial = new Material(Shader.Find("Standard")); mr.sharedMaterial.color = new Color(1f,0.75f,0.1f,1f); }
            var coll = sgo.GetComponent<Collider>(); if (coll) Destroy(coll);
        }

        foreach (var c in cells)
        {
            var neighbors = new (int,int,int)[]{
                (c.ijk.Item1+1,c.ijk.Item2,  c.ijk.Item3),
                (c.ijk.Item1-1,c.ijk.Item2,  c.ijk.Item3),
                (c.ijk.Item1,  c.ijk.Item2+1,c.ijk.Item3),
                (c.ijk.Item1,  c.ijk.Item2-1,c.ijk.Item3),
                (c.ijk.Item1,  c.ijk.Item2,  c.ijk.Item3+1),
                (c.ijk.Item1,  c.ijk.Item2,  c.ijk.Item3-1),
            };
            foreach (var nb in neighbors)
            {
                if (!worldCenters.ContainsKey(nb)) continue;
                Vector3 w0 = worldCenters[c.ijk];
                Vector3 w1 = worldCenters[nb];

                var go = new GameObject($"Edge_{c.ijk}_{nb}");
                go.transform.SetParent(root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startWidth = edgeLineWidth; lr.endWidth = edgeLineWidth;
                lr.startColor = Color.yellow; lr.endColor = Color.yellow;
                lr.SetPosition(0, w0); lr.SetPosition(1, w1);
            }
        }
    }

    // === FACE BUILDING ===
    private void CreateOrUpdateCubeFaceGizmos(
        int ci,
        List<(int,int,int)> cellSet,
        (Vector3 mu, Vector3 v0, Vector3 v1, Vector3 v2) lf,
        float u, Vector3 o,
        Vector3[] local // local points of selected cluster
    )
    {
        GameObject root;
        if (!faceGizmoByCluster.TryGetValue(ci, out root) || !root)
        {
            root = new GameObject($"SomaFaces_{ci:00}");
            root.transform.SetParent(clusterGOs[ci].transform, worldPositionStays:false);
            faceGizmoByCluster[ci] = root;
        }
        for (int i=root.transform.childCount-1; i>=0; i--) Destroy(root.transform.GetChild(i).gameObject);

        if (!showFaceGizmos || !drawFaces) { root.SetActive(false); return; }
        root.SetActive(true);

        // Build a hash set for quick neighbor test
        var set = new HashSet<(int,int,int)>(cellSet);

        float halfFace = faceSize * 0.5f;
        float thick = Mathf.Max(1e-4f, faceHalfThickness);
        float slack = Mathf.Max(1.0f, faceInplaneSlack);

        // 6 directions
        var dirs = new (Vector3 nLocal, int axis, Color col, string name, Vector3 t1, Vector3 t2)[]
        {
            ( new Vector3(+1,0,0), 0, new Color(1,0,0,0.5f), "+X", lf.v1, lf.v2 ),
            ( new Vector3(-1,0,0), 0, new Color(1,0,0,0.5f), "-X", lf.v1, lf.v2 ),
            ( new Vector3(0,+1,0), 1, new Color(0,1,0,0.5f), "+Y", lf.v0, lf.v2 ),
            ( new Vector3(0,-1,0), 1, new Color(0,1,0,0.5f), "-Y", lf.v0, lf.v2 ),
            ( new Vector3(0,0,+1), 2, new Color(0,0,1,0.5f), "+Z", lf.v0, lf.v1 ),
            ( new Vector3(0,0,-1), 2, new Color(0,0,1,0.5f), "-Z", lf.v0, lf.v1 ),
        };

        int facesMade = 0;

        foreach (var cell in cellSet)
        {
            // neighbor check
            var neigh = new (int,int,int)[]{
                (cell.Item1+1, cell.Item2,   cell.Item3),
                (cell.Item1-1, cell.Item2,   cell.Item3),
                (cell.Item1,   cell.Item2+1, cell.Item3),
                (cell.Item1,   cell.Item2-1, cell.Item3),
                (cell.Item1,   cell.Item2,   cell.Item3+1),
                (cell.Item1,   cell.Item2,   cell.Item3-1),
            };

            // cell center in local/world
            Vector3 lc = CellCenterLocal(cell, u, o);
            Vector3 wc = lf.mu + lf.v0*lc.x + lf.v1*lc.y + lf.v2*lc.z;

            // 6 faces
            for (int f = 0; f < 6; f++)
            {
                bool boundary = !set.Contains(neigh[f]);
                if (!boundary) continue;

                var d = dirs[f];
                // face center local = cell center + 0.5*u * normalAxis
                Vector3 fcl = lc + d.nLocal * (0.5f * u);
                Vector3 wCenter = lf.mu + lf.v0*fcl.x + lf.v1*fcl.y + lf.v2*fcl.z;

                bool ok = debugDrawAllBoundaryFaces || HasFaceSupportTiltAware(local, fcl, halfFace, thick, slack, d.axis);
                if (!ok) continue;

                // Build a quad with fixed size (faceSize), oriented by tangents t1, t2
                BuildQuad(root.transform, wCenter, d.t1, d.t2, halfFace, halfFace, d.col, $"Face_{cell}_{d.name}");
                facesMade++;
            }
        }

        if (debugLogFaceCounts)
            Debug.Log($"[Soma90/Faces] Cluster {ci} faces made = {facesMade}");
    }

    // Evidence test for a face centered at fcl (local), axisId: 0=X,1=Y,2=Z
    private bool HasFaceSupportTiltAware(
        Vector3[] local,
        Vector3 fcl,
        float half,           // half side length (fixed face)
        float thickHalf,      // half thickness along normal
        float slackMul,       // in-plane slack multiplier
        int axisId
    )
    {
        if (local == null || local.Length == 0) return false;

        // Normal axis
        int ax = Mathf.Clamp(axisId, 0, 2);
        // in-plane axes: choose two remaining axes
        int a1 = (ax == 0) ? 1 : 0;
        int a2 = (ax == 2) ? 1 : 2;
        if (ax == 1) a2 = 2; // (a1=0, a2=2) when ax=1

        // Accept points within slab along normal (thickness), and within in-plane coverage window
        float coverHalf = half * Mathf.Max(1f, slackMul);
        float tol = Mathf.Max(thickHalf, planeDistanceTol);
        int G = Mathf.Clamp(coverageGrid, 3, 16);
        bool[,] occ = new bool[G,G];
        int occCnt = 0;
        float cell = (2f*coverHalf) / G;

        // 4 quadrants occupancy
        bool q11=false,q1m1=false,qm11=false,qm1m1=false;

        int totalHit = 0;

        for (int i=0;i<local.Length;i++)
        {
            Vector3 p = local[i];
            float dn = 0f;
            if (ax == 0) dn = p.x - fcl.x;
            else if (ax == 1) dn = p.y - fcl.y;
            else dn = p.z - fcl.z;

            if (Mathf.Abs(dn) > tol * Mathf.Max(1f, tiltSlabMultiplier)) continue;

            // in-plane
            float u = 0f, v = 0f;
            if (a1 == 0) u = p.x - fcl.x; else if (a1 == 1) u = p.y - fcl.y; else u = p.z - fcl.z;
            if (a2 == 0) v = p.x - fcl.x; else if (a2 == 1) v = p.y - fcl.y; else v = p.z - fcl.z;

            if (Mathf.Abs(u) > coverHalf || Mathf.Abs(v) > coverHalf) continue;

            totalHit++;

            int iu = Mathf.Clamp(Mathf.FloorToInt((u + coverHalf)/cell), 0, G-1);
            int iv = Mathf.Clamp(Mathf.FloorToInt((v + coverHalf)/cell), 0, G-1);
            if (!occ[iu,iv]) { occ[iu,iv]=true; occCnt++; }

            if (u >= 0 && v >= 0) q11 = true;
            else if (u >= 0 && v < 0) q1m1 = true;
            else if (u < 0 && v >= 0) qm11 = true;
            else qm1m1 = true;
        }

        if (totalHit < Mathf.Max(6, minBoxPts/2)) return false; // very sparse

        float occFrac = occCnt / (float)(G*G);
        if (occFrac < Mathf.Clamp01(coverageMinFrac)) return false;
        if (occCnt < Mathf.Max(1, coverageMinCells)) return false;

        if (coverageRequireQuadrants)
        {
            int quads = (q11?1:0) + (q1m1?1:0) + (qm11?1:0) + (qm1m1?1:0);
            if (quads < 3) return false; // encourage spread
        }
        return true;
    }

    // === Quad builder (double-sided, visible with Sprites/Default) ===
    private void BuildQuad(Transform parent, Vector3 center, Vector3 aDir, Vector3 bDir, float ha, float hb, Color col, string name)
    {
        aDir = aDir.normalized; bDir = bDir.normalized;
        Vector3 v0 = center + aDir*ha + bDir*hb;
        Vector3 v1 = center - aDir*ha + bDir*hb;
        Vector3 v2 = center - aDir*ha - bDir*hb;
        Vector3 v3 = center + aDir*ha - bDir*hb;

        var mesh = new Mesh();
        mesh.SetVertices(new List<Vector3>{ v0, v1, v2, v3 });

        // double-sided triangles
        mesh.SetTriangles(new int[]{ 0,1,2, 0,2,3,  0,2,1, 0,3,2 }, 0);

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();

        var m = new Material(Shader.Find("Sprites/Default"));
        col.a = Mathf.Clamp01(col.a > 0f ? col.a : 0.5f);
        m.color = col;
        mr.sharedMaterial = m;
    }
}
