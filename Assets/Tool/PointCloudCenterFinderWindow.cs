#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 위에서 본 포인트클라우드에서 블록(큐브)들의 "중점들"을 자동 검출하고
/// 씬/게임뷰 모두에서 시각화(마커 + 라벨 + 가이드 선)까지 해주는 에디터 유틸.
/// - 줄(ㅡ) 배치와 ㄱ(엘보) 배치 모두 대응 (축 자동 정렬 + 밀도 피크 기반)
/// - 정확히 3개만 고정이 아니라, Target Count로 원하는 개수만큼 탐지 가능
/// - [Require Right Angle] 옵션으로 3점을 ㄱ(90°) 형태로 강제 선택 가능
/// 사용법:
/// 1) 이 파일을 Assets/ 에 저장 → Tools/Point Cloud/Center Finder 열기
/// 2) MeshFilter(포인트클라우드 Mesh) 지정 → Compute Centers
/// 3) 씬/게임뷰에 Centers/Center_# 마커가 보이고, LineRenderer로 연결됨
/// </summary>
public class PointCloudCenterFinderWindow : EditorWindow
{
    [MenuItem("Tools/Point Cloud/Center Finder")] 
    public static void ShowWindow() => GetWindow<PointCloudCenterFinderWindow>(true, "Point Cloud Center Finder");

    // ======================= GUI Params =======================
    [Header("Input")]
    public MeshFilter pointCloudMesh;

    public enum UpAxis { X, Y, Z }
    [Tooltip("위에서 바라보는 기준축 (이 축을 높이로 보고 나머지 2축 평면으로 투영)")]
    public UpAxis upAxis = UpAxis.Z;

    [Header("Grid & Blur")]
    [Tooltip("블록 한 변 s(대략값). 예: 0.025m")] public float blockSize = 0.025f;
    [Tooltip("히트맵 셀 크기 (보통 s/4~s/6)")] public float cellSize = 0.005f;
    [Tooltip("가우시안 블러 시그마(미터). 보통 s/6 전후")] public float blurSigma = 0.004f;

    [Header("Detection")]
    [Tooltip("찾고 싶은 중점 개수(정수). 0이면 자동(피크 기반 최적 조합)")]
    public int targetCount = 3;

    [Tooltip("히트맵에서 후보 피크 개수(상위 N)")]
    public int topPeakCount = 12;

    [Tooltip("피크 NMS 최소 거리(미터). 가까운 피크를 하나로 묶음")]
    public float nmsMinDist = 0.012f; // ≈ s/2

    [Tooltip("간격/형태 스코어에 사용할 허용 비율(±). s 또는 2s와의 상대오차")]
    public float spacingToleranceRatio = 0.30f;

    [Tooltip("형태 판정 각도 허용치(도). ㅡ≈180°, ㄱ≈90° 근처 허용")] 
    public float angleToleranceDeg = 25f;

    [Tooltip("피크 주변 반경(미터) 내의 원래 3D 포인트로 centroid 계산")]
    public float centroidRadius = 0.015f;

    [Header("Right-Angle Constraint")]
    [Tooltip("체크 시, 정확히 3개의 중점을 선택하고 그 중 하나에서 90°가 되도록 강제")] 
    public bool requireRightAngle = false;

    [Tooltip("90° 허용 오차 (deg)")] public float rightAngleToleranceDeg = 12f;

    [Tooltip("90° 선택 후, ㄱ 형상을 실제로 보정(직교 + 간격 스냅)")]
    public bool refineRightAngle = true;

    [Tooltip("보정 반복 횟수")] public int refineIterations = 2;

    [Tooltip("ㄱ의 각 다리 폭(uv 단위, 보통 s*0.6)")]
    public float legHalfWidth = 0.015f;

    [Tooltip("다리 길이 스냅: s 또는 2s 근처로 맞춤")]
    public bool snapSpacingToBlock = true;

    [Header("Visualization")]
    [Tooltip("씬/게임뷰에 마커(스피어) 생성")]
    public bool createMarkersInScene = true;

    [Tooltip("마커(스피어) 월드 스케일")] public float markerScale = 0.01f;

    [Tooltip("검출된 센터들을 선으로 연결(게임뷰 포함)")]
    public bool drawPolyline = true;

    [Tooltip("Console에 좌표 로그")] public bool logCenters = true;

    // 상태
    private string status = "Ready.";

    // ======= Defaults for reset =======
    private const UpAxis DEFAULT_UP = UpAxis.Z;
    private const float DEFAULT_S = 0.025f;
    private const float DEFAULT_CELL = 0.005f;
    private const float DEFAULT_SIGMA = 0.004f;
    private const int   DEFAULT_TARGET = 3;
    private const int   DEFAULT_TOPK = 12;
    private const float DEFAULT_NMS = 0.012f;
    private const float DEFAULT_SPACING_TOL = 0.30f;
    private const float DEFAULT_ANGLE_TOL = 25f;
    private const float DEFAULT_RADIUS = 0.015f;
    private const bool  DEFAULT_CREATE = true;
    private const float DEFAULT_MARKER = 0.01f;
    private const bool  DEFAULT_LINE = true;
    private const bool  DEFAULT_LOG = true;
    private const bool  DEFAULT_REQUIRE_RIGHT = false;
    private const float DEFAULT_RIGHT_TOL = 12f;
    private const bool  DEFAULT_REFINE_RIGHT = true;
    private const int   DEFAULT_REFINE_ITERS = 2;
    private const float DEFAULT_LEG_HALF_WIDTH = 0.015f;
    private const bool  DEFAULT_SNAP_SPACING = true;

    private void ResetParams()
    {
        upAxis = DEFAULT_UP;
        blockSize = DEFAULT_S;
        cellSize = DEFAULT_CELL;
        blurSigma = DEFAULT_SIGMA;
        targetCount = DEFAULT_TARGET;
        topPeakCount = DEFAULT_TOPK;
        nmsMinDist = DEFAULT_NMS;
        spacingToleranceRatio = DEFAULT_SPACING_TOL;
        angleToleranceDeg = DEFAULT_ANGLE_TOL;
        centroidRadius = DEFAULT_RADIUS;
        createMarkersInScene = DEFAULT_CREATE;
        markerScale = DEFAULT_MARKER;
        drawPolyline = DEFAULT_LINE;
        logCenters = DEFAULT_LOG;
        requireRightAngle = DEFAULT_REQUIRE_RIGHT;
        rightAngleToleranceDeg = DEFAULT_RIGHT_TOL;
        refineRightAngle = DEFAULT_REFINE_RIGHT;
        refineIterations = DEFAULT_REFINE_ITERS;
        legHalfWidth = DEFAULT_LEG_HALF_WIDTH;
        snapSpacingToBlock = DEFAULT_SNAP_SPACING;
        status = "Parameters reset to defaults.";
    }

    private void OnEnable()
    {
        // 값이 비정상일 때만 보정 (세션 유지 방해하지 않음)
        if (cellSize <= 0f) cellSize = Mathf.Max(0.001f, blockSize / 5f);
        if (blurSigma <= 0f) blurSigma = Mathf.Max(0.001f, blockSize / 6f);
        if (nmsMinDist <= 0f) nmsMinDist = Mathf.Max(0.0025f, blockSize * 0.4f);
        if (centroidRadius <= 0f) centroidRadius = Mathf.Max(0.005f, blockSize * 0.5f);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
        pointCloudMesh = (MeshFilter)EditorGUILayout.ObjectField("Point Cloud (MeshFilter)", pointCloudMesh, typeof(MeshFilter), true);
        upAxis = (UpAxis)EditorGUILayout.EnumPopup("Up Axis (Top-Down)", upAxis);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Grid & Blur", EditorStyles.boldLabel);
        blockSize = EditorGUILayout.FloatField("Block Size s (m)", blockSize);
        cellSize = EditorGUILayout.FloatField("Cell Size (m)", cellSize);
        blurSigma = EditorGUILayout.FloatField("Gaussian Sigma (m)", blurSigma);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Detection", EditorStyles.boldLabel);
        targetCount = EditorGUILayout.IntSlider("Target Count", targetCount, 0, 32);
        topPeakCount = EditorGUILayout.IntSlider("Top Peak Count", topPeakCount, 3, 64);
        nmsMinDist = EditorGUILayout.FloatField("NMS Min Dist (m)", nmsMinDist);
        spacingToleranceRatio = EditorGUILayout.Slider("Spacing Tolerance Ratio", spacingToleranceRatio, 0.05f, 0.6f);
        angleToleranceDeg = EditorGUILayout.Slider("Angle Tolerance (deg)", angleToleranceDeg, 5f, 45f);
        centroidRadius = EditorGUILayout.FloatField("Centroid Radius (m)", centroidRadius);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Right-Angle Constraint", EditorStyles.boldLabel);
        requireRightAngle = EditorGUILayout.Toggle("Require Right Angle", requireRightAngle);
        using (new EditorGUI.DisabledScope(!requireRightAngle))
        {
            rightAngleToleranceDeg = EditorGUILayout.Slider("Right Angle Tolerance (deg)", rightAngleToleranceDeg, 1f, 30f);
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Visualization", EditorStyles.boldLabel);
        createMarkersInScene = EditorGUILayout.Toggle("Create Markers in Scene", createMarkersInScene);
        markerScale = EditorGUILayout.FloatField("Marker Scale", markerScale);
        drawPolyline = EditorGUILayout.Toggle("Draw Polyline", drawPolyline);
        logCenters = EditorGUILayout.Toggle("Log Centers", logCenters);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Right-Angle Refinement", EditorStyles.boldLabel);
        refineRightAngle = EditorGUILayout.Toggle("Refine Right Angle", refineRightAngle);
        using (new EditorGUI.DisabledScope(!requireRightAngle || !refineRightAngle))
        {
            refineIterations = EditorGUILayout.IntSlider("Refine Iterations", Mathf.Max(1, refineIterations), 1, 5);
            legHalfWidth = EditorGUILayout.FloatField("Leg Half Width (uv)", legHalfWidth);
            snapSpacingToBlock = EditorGUILayout.Toggle("Snap Length to s/2s", snapSpacingToBlock);
        }

        EditorGUILayout.Space(10);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Compute Centers", GUILayout.Height(24))) ComputeCenters();
            if (GUILayout.Button("Reset Parameters", GUILayout.Height(24))) ResetParams();
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.HelpBox(status, MessageType.Info);
    }

    // ======================= Core =======================
    private void ComputeCenters()
    {
        try
        {
            if (pointCloudMesh == null || pointCloudMesh.sharedMesh == null)
            { status = "MeshFilter가 비어있습니다."; return; }

            var mesh = pointCloudMesh.sharedMesh;
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0)
            { status = "Mesh.vertices 가 비어있습니다."; return; }

            // 월드 포인트로 변환
            var tf = pointCloudMesh.transform;
            var pointsW = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) pointsW[i] = tf.TransformPoint(verts[i]);

            // 1) 선택 UpAxis 기준 2D 투영
            Vector2[] p2; GetProjected2D(pointsW, upAxis, out p2);

            // 2) 2D PCA로 주축(u) → (u,v) 좌표계로 회전
            Vector2 mean2; Matrix2x2 cov; ComputeMeanAndCov(p2, out mean2, out cov);
            Vector2 uAxis = FirstEigenVector(cov); Vector2 vAxis = new Vector2(-uAxis.y, uAxis.x);
            var uv = new Vector2[p2.Length];
            for (int i = 0; i < p2.Length; i++) { var d = p2[i] - mean2; uv[i] = new Vector2(Vector2.Dot(d, uAxis), Vector2.Dot(d, vAxis)); }

            // 3) UV 히트맵 생성 + 가우시안 블러
            Rect uvBounds = GetBounds(uv);
            int nx = Mathf.Max(8, Mathf.CeilToInt(uvBounds.width / cellSize));
            int ny = Mathf.Max(8, Mathf.CeilToInt(uvBounds.height / cellSize));
            float[,] hist = new float[nx, ny];
            for (int i = 0; i < uv.Length; i++)
            {
                int ix = Mathf.FloorToInt((uv[i].x - uvBounds.xMin) / cellSize);
                int iy = Mathf.FloorToInt((uv[i].y - uvBounds.yMin) / cellSize);
                if ((uint)ix < (uint)nx && (uint)iy < (uint)ny) hist[ix, iy] += 1f;
            }
            float sigmaCells = Mathf.Max(0.5f, blurSigma / Mathf.Max(1e-6f, cellSize));
            hist = GaussianBlurSeparable(hist, sigmaCells);

            // 4) 피크 추출 → NMS → 후보 좌표
            var peaks = FindLocalMaxima(hist, topPeakCount);
            if (peaks.Count == 0) { status = "피크가 0개입니다. cellSize/blurSigma 조정 필요"; return; }

            var peakUV = peaks.Select(p => new Vector2(
                uvBounds.xMin + (p.x + 0.5f) * cellSize,
                uvBounds.yMin + (p.y + 0.5f) * cellSize
            )).ToList();
            var peakVals = peaks.Select(p => p.value).ToList();

            // 기본 NMS로 과밀 피크 정리
            var basePicked = NmsTopK(peakUV, peakVals, Math.Min(Math.Max(targetCount, 6), peakUV.Count), nmsMinDist);
            if (basePicked == null || basePicked.Count == 0) { status = "NMS 결과가 비었습니다. nmsMinDist를 줄여보세요."; return; }

            List<Vector2> picked;
            if (requireRightAngle)
            {
                // 90° 강제: 항상 3개 선택 (ㄱ 모양)
                var triple = ChooseTripleRightAngle(basePicked, peakUV, peakVals, blockSize, rightAngleToleranceDeg, spacingToleranceRatio);
                if (triple == null)
                { status = "90° 조건을 만족하는 3개 조합을 찾지 못했습니다. RightAngleTolerance/SpacingTol 조정"; return; }
                picked = triple;
            }
            else
            {
                picked = (targetCount == 0 && basePicked.Count >= 3)
                    ? AutoSelectBestCenters(peakUV, peakVals, basePicked, blockSize, spacingToleranceRatio, angleToleranceDeg)
                    : basePicked.Take(Math.Max(targetCount, 1)).ToList();
            }

            // 5) 각 선택 UV 근방에서 3D centroid 계산 → (필요 시) ㄱ 보정
            var centers = new List<Vector3>();
            float r = Mathf.Max(centroidRadius, cellSize * 1.2f);
            foreach (var targetUV in picked)
                centers.Add(RobustCentroid(pointsW, uv, targetUV, r));

            if (requireRightAngle && refineRightAngle && centers.Count == 3)
            {
                for (int it = 0; it < refineIterations; it++)
                {
                    RefineRightAngleTriplet(pointsW, uv, ref centers, blockSize, legHalfWidth, r, snapSpacingToBlock);
                }
            }

            // 6) 시각화: 마커 생성 + 폴리라인(LineRenderer)
            if (createMarkersInScene)
            {
                var parent = GameObject.Find("Centers");
                if (!parent) parent = new GameObject("Centers");

                // 기존 Center_ 오브젝트 청소
                for (int i = parent.transform.childCount - 1; i >= 0; --i)
                {
                    var ch = parent.transform.GetChild(i);
                    if (ch.name.StartsWith("Center_")) DestroyImmediate(ch.gameObject);
                }

                // 마커 생성 (게임뷰에서도 보이는 실제 Sphere)
                for (int i = 0; i < centers.Count; i++)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = $"Center_{i + 1}";
                    go.transform.position = centers[i];
                    go.transform.localScale = Vector3.one * Mathf.Max(0.001f, markerScale);
                    go.transform.SetParent(parent.transform, true);
                    var col = go.GetComponent<Collider>(); if (col) DestroyImmediate(col);
                    var mr = go.GetComponent<MeshRenderer>(); if (mr)
                    {
                        mr.sharedMaterial = GetOrCreateMarkerMaterial();
                    }

                    var giz = go.GetComponent<CenterMarkerGizmo>();
                    if (!giz) giz = go.AddComponent<CenterMarkerGizmo>();
                    giz.radius = r; giz.label = (i + 1).ToString(); giz.color = new Color(0.1f, 0.8f, 0.3f, 1f);
                }

                // 폴리라인 갱신(게임뷰 렌더)
                var line = parent.GetComponent<LineRenderer>();
                if (!line)
                {
                    line = parent.AddComponent<LineRenderer>();
                    line.material = new Material(Shader.Find("Sprites/Default"));
                    line.widthMultiplier = Mathf.Max(0.0005f, markerScale * 0.2f);
                    line.positionCount = 0;
                    line.loop = false;
                    line.useWorldSpace = true;
                    line.numCapVertices = 4;
                    line.numCornerVertices = 2;
                    line.startColor = new Color(0.2f, 0.6f, 1f, 1f);
                    line.endColor   = new Color(0.2f, 0.6f, 1f, 1f);
                }

                line.enabled = drawPolyline && centers.Count >= 2;
                if (line.enabled)
                {
                    // 정렬: u축 방향으로 연결
                    var sorted = centers
                        .Select(c => new { c, u = Vector2.Dot(new Vector2(c.x, c.y) - mean2, uAxis) })
                        .OrderBy(t => t.u)
                        .Select(t => t.c)
                        .ToList();
                    line.positionCount = sorted.Count;
                    for (int i = 0; i < sorted.Count; i++) line.SetPosition(i, sorted[i]);
                }
            }

            if (logCenters)
            {
            
            }

            status = $"완료: {centers.Count}개 중점을 계산하고 시각화했습니다.";
        }
        catch (Exception ex)
        {
            status = "에러: " + ex.Message; Debug.LogException(ex);
        }
    }

    // ======================= Helpers =======================
    private static void GetProjected2D(Vector3[] pointsW, UpAxis up, out Vector2[] p2)
    {
        p2 = new Vector2[pointsW.Length];
        switch (up)
        {
            case UpAxis.Z: for (int i = 0; i < pointsW.Length; i++) p2[i] = new Vector2(pointsW[i].x, pointsW[i].y); break;
            case UpAxis.Y: for (int i = 0; i < pointsW.Length; i++) p2[i] = new Vector2(pointsW[i].x, pointsW[i].z); break;
            case UpAxis.X: for (int i = 0; i < pointsW.Length; i++) p2[i] = new Vector2(pointsW[i].y, pointsW[i].z); break;
        }
    }

    private static void ComputeMeanAndCov(Vector2[] p, out Vector2 mean, out Matrix2x2 cov)
    {
        mean = Vector2.zero; int n = p.Length; for (int i = 0; i < n; i++) mean += p[i]; mean /= Mathf.Max(1, n);
        float xx = 0, xy = 0, yy = 0;
        for (int i = 0; i < n; i++) { var d = p[i] - mean; xx += d.x * d.x; xy += d.x * d.y; yy += d.y * d.y; }
        float inv = 1f / Mathf.Max(1, n); cov = new Matrix2x2(xx * inv, xy * inv, xy * inv, yy * inv);
    }

    private static Vector2 FirstEigenVector(Matrix2x2 c)
    {
        // 2x2 대칭 공분산의 주고유벡터(큰 고유값)
        float a = c.m00, b = c.m01, d = c.m11; float T = a + d; float D = (a - d) * (a - d) + 4f * b * b;
        float sqrtD = Mathf.Sqrt(Mathf.Max(0f, D)); float lambda1 = 0.5f * (T + sqrtD);
        Vector2 v; if (Mathf.Abs(b) > 1e-12f) v = new Vector2(lambda1 - d, b); else v = (a >= d) ? new Vector2(1, 0) : new Vector2(0, 1);
        if (v.sqrMagnitude < 1e-20f) v = new Vector2(1, 0); v.Normalize(); return v;
    }

    private static Rect GetBounds(Vector2[] pts)
    {
        if (pts.Length == 0) return new Rect(0, 0, 0, 0);
        float minx = pts[0].x, maxx = pts[0].x, miny = pts[0].y, maxy = pts[0].y;
        for (int i = 1; i < pts.Length; i++)
        { var p = pts[i]; if (p.x < minx) minx = p.x; if (p.x > maxx) maxx = p.x; if (p.y < miny) miny = p.y; if (p.y > maxy) maxy = p.y; }
        float pad = 1e-6f + 0.5f * Mathf.Max((maxx - minx), (maxy - miny)) * 0.01f;
        return new Rect(minx - pad, miny - pad, (maxx - minx) + 2 * pad, (maxy - miny) + 2 * pad);
    }

    private struct Peak { public int x, y; public float value; public Peak(int x, int y, float v) { this.x = x; this.y = y; this.value = v; } }

    private static List<Peak> FindLocalMaxima(float[,] img, int topK)
    {
        int nx = img.GetLength(0), ny = img.GetLength(1); var list = new List<Peak>(nx * ny / 8);
        for (int y = 1; y < ny - 1; y++)
        for (int x = 1; x < nx - 1; x++)
        {
            float c = img[x, y]; if (c <= 0f) continue; bool isMax = true;
            for (int j = -1; j <= 1 && isMax; j++)
                for (int i = -1; i <= 1; i++)
                { if (i == 0 && j == 0) continue; if (img[x + i, y + j] > c) { isMax = false; break; } }
            if (isMax) list.Add(new Peak(x, y, c));
        }
        list.Sort((a, b) => b.value.CompareTo(a.value));
        if (list.Count > topK) list.RemoveRange(topK, list.Count - topK);
        return list;
    }

    private static List<Vector2> NmsTopK(List<Vector2> pts, List<float> vals, int k, float minDist)
    {
        var order = Enumerable.Range(0, pts.Count).OrderByDescending(i => vals[i]).ToList();
        var picked = new List<Vector2>();
        foreach (var idx in order)
        {
            var p = pts[idx]; bool ok = true; foreach (var q in picked) if ((p - q).sqrMagnitude < minDist * minDist) { ok = false; break; }
            if (ok) { picked.Add(p); if (k > 0 && picked.Count >= k) break; }
        }
        return picked;
    }

    // === Histogram Gaussian Blur (separable) ===
    private static float[,] GaussianBlurSeparable(float[,] src, float sigmaCells)
    {
        int nx = src.GetLength(0), ny = src.GetLength(1);
        int radius = Mathf.Clamp(Mathf.CeilToInt(3f * sigmaCells), 1, 64);

        // 1D kernel
        float[] k = new float[2 * radius + 1];
        float s2 = 2f * sigmaCells * sigmaCells;
        float sum = 0f;
        for (int i = -radius; i <= radius; i++)
        {
            float val = Mathf.Exp(-(i * i) / s2);
            k[i + radius] = val; sum += val;
        }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;

        // temp x-pass
        float[,] tmp = new float[nx, ny];
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                float acc = 0f;
                for (int t = -radius; t <= radius; t++)
                {
                    int xx = Mathf.Clamp(x + t, 0, nx - 1);
                    acc += src[xx, y] * k[t + radius];
                }
                tmp[x, y] = acc;
            }
        }

        // temp y-pass
        float[,] dst = new float[nx, ny];
        for (int x = 0; x < nx; x++)
        {
            for (int y = 0; y < ny; y++)
            {
                float acc = 0f;
                for (int t = -radius; t <= radius; t++)
                {
                    int yy = Mathf.Clamp(y + t, 0, ny - 1);
                    acc += tmp[x, yy] * k[t + radius];
                }
                dst[x, y] = acc;
            }
        }
        return dst;
    }

    private static int NearestIndex(Vector2[] ps, Vector2 q)
    { int idx = 0; float best = float.PositiveInfinity; for (int i = 0; i < ps.Length; i++) { float d = (ps[i] - q).sqrMagnitude; if (d < best) { best = d; idx = i; } } return idx; }

    private static Vector3 RobustCentroid(Vector3[] pointsW, Vector2[] uv, Vector2 targetUV, float radius)
    {
        // 가우시안 가중 평균(uv 거리 기반)으로 치우침 완화
        float r2 = radius * radius;
        Vector3 acc = Vector3.zero; float wsum = 0f;
        for (int i = 0; i < uv.Length; i++)
        {
            var d2 = (uv[i] - targetUV).sqrMagnitude; if (d2 > r2) continue;
            float w = Mathf.Exp(-d2 / Mathf.Max(1e-8f, 2f * r2 * 0.25f)); // sigma≈radius/√2
            acc += pointsW[i] * w; wsum += w;
        }
        if (wsum <= 1e-6f)
        {
            int ni = NearestIndex(uv, targetUV); return pointsW[ni];
        }
        return acc / wsum;
    }

    private static void RefineRightAngleTriplet(Vector3[] pointsW, Vector2[] uv, ref List<Vector3> centers, float s, float halfWidth, float radius, bool snapLen)
    {
        // UV 좌표에서 세 점 투영
        Vector2[] cuv = new Vector2[3];
        for (int i = 0; i < 3; i++) cuv[i] = new Vector2(centers[i].x, centers[i].y);

        // ㄱ의 코너(90°에 가장 가까운 점) 찾기
        float a0 = AngleAt(cuv[0], cuv[1], cuv[2]);
        float a1 = AngleAt(cuv[1], cuv[0], cuv[2]);
        float a2 = AngleAt(cuv[2], cuv[0], cuv[1]);
        int elbow = 0; float best = Mathf.Abs(90f - a0);
        if (Mathf.Abs(90f - a1) < best) { elbow = 1; best = Mathf.Abs(90f - a1); }
        if (Mathf.Abs(90f - a2) < best) { elbow = 2; }

        int iA = elbow; int iB = (elbow + 1) % 3; int iC = (elbow + 2) % 3;
        Vector2 E = cuv[iA]; Vector2 B = cuv[iB]; Vector2 C = cuv[iC];

        Vector2 d1 = (B - E).normalized; if (d1.sqrMagnitude < 1e-8f) d1 = new Vector2(1, 0);
        // d2는 d1에 직교하도록 강제, 원래 C-E에 가장 가까운 방향으로 사인 선택
        Vector2 cand = (C - E); Vector2 d2 = new Vector2(-d1.y, d1.x);
        if (Vector2.Dot(d2, cand) < 0) d2 = -d2;

        // 각 다리에 대해, E를 원점으로 보고 1D 밀도 피크로 길이 추정 → s 또는 2s로 스냅(옵션)
        float t1 = FindRayPeakLength(uv, E, d1, halfWidth, s);
        float t2 = FindRayPeakLength(uv, E, d2, halfWidth, s);
        if (snapLen)
        {
            t1 = SnapToSOr2S(t1, s);
            t2 = SnapToSOr2S(t2, s);
        }
        Vector2 targetB = E + d1 * Mathf.Max(0.3f * s, t1);
        Vector2 targetC = E + d2 * Mathf.Max(0.3f * s, t2);

        // E는 두 직선(각 다리 스트립) 교점 근사로 업데이트: 그대로 E 사용
        // 3D 재-centroid (가우시안 가중)
        Vector3 newE = RobustCentroid(pointsW, uv, E, radius * 0.8f);
        Vector3 newB = RobustCentroid(pointsW, uv, targetB, radius);
        Vector3 newC = RobustCentroid(pointsW, uv, targetC, radius);

        centers[iA] = newE; centers[iB] = newB; centers[iC] = newC;
    }

    private static float SnapToSOr2S(float t, float s)
    {
        // s 또는 2s에 더 가까운 곳으로 스냅 (너무 작으면 그대로)
        float cand1 = s, cand2 = 2f * s;
        float d1 = Mathf.Abs(t - cand1), d2 = Mathf.Abs(t - cand2);
        float best = (d1 < d2) ? cand1 : cand2;
        // t가 그 후보의 40% 이내로 가까울 때만 스냅
        return (Mathf.Abs(t - best) <= 0.4f * s) ? best : t;
    }

    private static float FindRayPeakLength(Vector2[] uv, Vector2 origin, Vector2 dir, float halfWidth, float s)
    {
        // 레이 방향으로 투영한 값들 중, |cross|<=halfWidth 인 포인트만 모아 1D 히스토그램 피크를 찾는다
        List<float> ts = new List<float>(256);
        Vector2 n = new Vector2(-dir.y, dir.x);
        for (int i = 0; i < uv.Length; i++)
        {
            Vector2 d = uv[i] - origin;
            float cross = Mathf.Abs(Vector2.Dot(d, n));
            if (cross > halfWidth) continue;
            float t = Vector2.Dot(d, dir);
            if (t >= 0f && t <= 3f * s) ts.Add(t);
        }
        if (ts.Count == 0) return s;
        ts.Sort();
        // 간단 커널밀도: 고정 밴드폭으로 누적
        int bins = Mathf.Clamp(Mathf.CeilToInt(3f * s / Mathf.Max(1e-4f, s / 20f)), 20, 200);
        float bin = (3f * s) / bins;
        float[] h = new float[bins];
        float sigma = Math.Max(1, 0.8f * (s / 6f) / Mathf.Max(bin, 1e-6f)); // 대략 s/6 정도 스무딩
        int rad = Mathf.Clamp(Mathf.CeilToInt(3f * sigma), 1, 50);
        for (int i = 0; i < ts.Count; i++)
        {
            int k = Mathf.Clamp(Mathf.FloorToInt(ts[i] / bin), 0, bins - 1);
            h[k] += 1f;
        }
        // 1D 가우시안 블러
        float[] g = new float[2 * rad + 1]; float s2 = 2f * sigma * sigma; float norm = 0f;
        for (int i = -rad; i <= rad; i++) { float v = Mathf.Exp(-(i * i) / s2); g[i + rad] = v; norm += v; }
        for (int i = 0; i < g.Length; i++) g[i] /= norm;
        float[] hh = new float[bins];
        for (int i = 0; i < bins; i++)
        {
            float acc = 0f;
            for (int t = -rad; t <= rad; t++)
            {
                int j = Mathf.Clamp(i + t, 0, bins - 1);
                acc += h[j] * g[t + rad];
            }
            hh[i] = acc;
        }
        int imax = 0; float vmax = hh[0];
        for (int i = 1; i < bins; i++) if (hh[i] > vmax) { vmax = hh[i]; imax = i; }
        return (imax + 0.5f) * bin;
    }
    // 자동 선택: 후보들 중에서 간격/각도 스코어가 좋은 subset 선택 (기본 3~5개 범위 탐색)
    private static List<Vector2> AutoSelectBestCenters(List<Vector2> allPeaks, List<float> peakVals, List<Vector2> pickedByNms,
                                                       float s, float spacingTol, float angTolDeg)
    {
        int minN = Math.Min(3, pickedByNms.Count);
        int maxN = Math.Min(5, pickedByNms.Count);
        if (maxN < 3) return pickedByNms;

        List<Vector2> best = null; float bestScore = float.NegativeInfinity;
        var idxs = Enumerable.Range(0, pickedByNms.Count).ToList();
        for (int n = minN; n <= maxN; n++)
        {
            foreach (var comb in Combinations(idxs, n))
            {
                var set = comb.Select(i => pickedByNms[i]).ToList();
                float sc = ScoreSet(set, peakVals, allPeaks, s, spacingTol, angTolDeg);
                if (sc > bestScore) { bestScore = sc; best = set; }
            }
        }
        return best ?? pickedByNms;
    }

    private static float ScoreSet(List<Vector2> set, List<float> peakVals, List<Vector2> allPeaks,
                                  float s, float spacingTol, float angTolDeg)
    {
        // (1) 간격 점수: 모든 쌍 거리의 중앙값이 s 또는 2s에 근접하면 가산
        var dists = new List<float>();
        for (int i = 0; i < set.Count; i++) for (int j = i + 1; j < set.Count; j++) dists.Add((set[i] - set[j]).magnitude);
        dists.Sort(); float med = dists[dists.Count / 2];
        float err1 = Mathf.Abs(med - s) / Mathf.Max(1e-6f, s);
        float err2 = Mathf.Abs(med - 2f * s) / Mathf.Max(1e-6f, 2f * s);
        float spacingErr = Mathf.Min(err1, err2);
        float spacingScore = Mathf.Max(0f, (spacingTol - spacingErr)) * 10f;

        // (2) 각도 점수: ㅡ(180) 또는 ㄱ(90) 형태가 일부라도 성립하면 가산
        float angleScore = 0f; // 최대값 사용
        if (set.Count >= 3)
        {
            for (int a = 0; a < set.Count; a++)
            for (int b = 0; b < set.Count; b++) if (b != a)
            for (int c = 0; c < set.Count; c++) if (c != a && c != b)
            {
                float ang = AngleAt(set[b], set[a], set[c]);
                float lineCost = Mathf.Abs(180f - ang);
                float elbowCost = Mathf.Abs(90f - ang);
                float sc = 0f;
                if (lineCost <= angTolDeg) sc = Mathf.Max(sc, (angTolDeg - lineCost));
                if (elbowCost <= angTolDeg) sc = Mathf.Max(sc, (angTolDeg - elbowCost));
                if (sc > angleScore) angleScore = sc;
            }
        }

        // (3) 밀도 합 점수
        float valSum = 0f;
        foreach (var p in set)
        {
            int idx = 0; float best = float.PositiveInfinity;
            for (int i = 0; i < allPeaks.Count; i++)
            {
                float d = (allPeaks[i] - p).sqrMagnitude; if (d < best) { best = d; idx = i; }
            }
            if (idx >= 0 && idx < peakVals.Count) valSum += peakVals[idx];
        }
        float densityScore = Mathf.Log(1f + valSum);

        return spacingScore + angleScore + densityScore;
    }

    private static float AngleAt(Vector2 vertex, Vector2 p1, Vector2 p2)
    {
        Vector2 v1 = (p1 - vertex).normalized; Vector2 v2 = (p2 - vertex).normalized; 
        float dot = Mathf.Clamp(Vector2.Dot(v1, v2), -1f, 1f); return Mathf.Acos(dot) * Mathf.Rad2Deg;
    }

    private static IEnumerable<List<int>> Combinations(List<int> arr, int k)
    {
        int n = arr.Count; if (k > n) yield break;
        int[] idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx.Select(i => arr[i]).ToList();
            int i; for (i = k - 1; i >= 0 && idx[i] == i + n - k; i--) ;
            if (i < 0) break; idx[i]++;
            for (int j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    private static Material GetOrCreateMarkerMaterial()
    {
        // Unlit 단색 머티리얼
        var mat = new Material(Shader.Find("Unlit/Color"));
        mat.color = new Color(0.1f, 0.8f, 0.3f, 1f);
        return mat;
    }

    // === 90° 조합 선택 ===
    private static List<Vector2> ChooseTripleRightAngle(List<Vector2> candidates, List<Vector2> allPeaks, List<float> peakVals,
                                                        float s, float rightTolDeg, float spacingTol)
    {
        if (candidates.Count < 3) return null;
        float bestScore = float.NegativeInfinity; List<Vector2> best = null;
        for (int i = 0; i < candidates.Count; i++)
        for (int j = i + 1; j < candidates.Count; j++)
        for (int k = j + 1; k < candidates.Count; k++)
        {
            var a = candidates[i]; var b = candidates[j]; var c = candidates[k];
            // 세 각 중 90°에 가장 가까운 것을 사용
            float angA = AngleAt(a, b, c);
            float angB = AngleAt(b, a, c);
            float angC = AngleAt(c, a, b);
            float elbowErr = Mathf.Min(Mathf.Abs(90f - angA), Mathf.Abs(90f - angB), Mathf.Abs(90f - angC));
            if (elbowErr > rightTolDeg) continue;

            // 간격(중앙값) s 또는 2s에 근접도
            float dab = (a - b).magnitude, dbc = (b - c).magnitude, dac = (a - c).magnitude;
            var ds = new List<float> { dab, dbc, dac }; ds.Sort(); float med = ds[1];
            float e1 = Mathf.Abs(med - s) / Mathf.Max(1e-6f, s);
            float e2 = Mathf.Abs(med - 2f * s) / Mathf.Max(1e-6f, 2f * s);
            float spacingErr = Mathf.Min(e1, e2);
            if (spacingErr > spacingTol) continue;

            // 밀도 합(가중)
            float valSum = SumPeakVals(new List<Vector2>{a,b,c}, allPeaks, peakVals);

            float score = (rightTolDeg - elbowErr) * 5f + (spacingTol - spacingErr) * 10f + Mathf.Log(1f + valSum);
            if (score > bestScore) { bestScore = score; best = new List<Vector2>{a,b,c}; }
        }
        return best;
    }

    private static float SumPeakVals(List<Vector2> set, List<Vector2> allPeaks, List<float> peakVals)
    {
        float sum = 0f;
        foreach (var p in set)
        {
            int idx = 0; float best = float.PositiveInfinity;
            for (int i = 0; i < allPeaks.Count; i++)
            {
                float d = (allPeaks[i] - p).sqrMagnitude; if (d < best) { best = d; idx = i; }
            }
            if (idx >= 0 && idx < peakVals.Count) sum += peakVals[idx];
        }
        return sum;
    }

    // 간단 2x2 행렬
    private struct Matrix2x2 { public float m00, m01, m10, m11; public Matrix2x2(float a,float b,float c,float d){m00=a;m01=b;m10=c;m11=d;} }
}
#endif

// =============== Scene/Game View Visualization Helpers ===============
// 씬과 게임뷰 모두에서 보이도록 실제 Sphere 프리미티브를 생성하고, 보조 라벨/원은 Gizmos로 표시합니다.
public class CenterMarkerGizmo : MonoBehaviour
{
    public float radius = 0.02f;
    public Color color = Color.green;
    public string label = "";

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = color;
        Gizmos.DrawWireSphere(transform.position, radius);
        Handles.color = color;
        Handles.Label(transform.position + Vector3.up * radius * 0.5f, label);
    }
#endif
}
