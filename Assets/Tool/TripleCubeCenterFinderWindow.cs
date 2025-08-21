// ===== File: Assets/TripleCubeCenterFinderWindow.cs =====
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class TripleCubeCenterFinderWindow : EditorWindow
{
    [MenuItem("Tools/Point Cloud/Triple Cube Center Finder")]
    public static void ShowWindow()
    {
        GetWindow<TripleCubeCenterFinderWindow>(true, "Triple Cube Center Finder");
    }

    // =======================
    // User Parameters (GUI)
    // =======================
    [Header("Input")]
    public MeshFilter pointCloudMesh;

    public enum UpAxis { X, Y, Z }
    [Tooltip("위에서 바라보는 기준축 (이 축을 '높이'로 보고, 나머지 2축 평면으로 투영합니다).")]
    public UpAxis upAxis = UpAxis.Z;

    [Header("Grid & Blur")]
    [Tooltip("블록 한 변 s (대략값). 예: 0.025m")]
    public float blockSize = 0.025f;

    [Tooltip("히트맵 셀 크기 (보통 s/4 ~ s/6).")]
    public float cellSize = 0.005f;

    [Tooltip("가우시안 블러 시그마(미터 단위). 보통 s/6 전후.")]
    public float blurSigma = 0.004f;

    [Header("Peak & Scoring")]
    [Tooltip("히트맵에서 뽑을 지역 최대(피크) 후보 수")]
    public int topPeakCount = 6;

    [Tooltip("모양 판정 각도 허용치(도). ㄱ≈90°, ㅡ≈180° 기준에서 허용 오차")]
    public float angleToleranceDeg = 18f;

    [Tooltip("간격 일치 허용률(예: 0.2 = ±20%)")]
    public float spacingToleranceRatio = 0.2f;

    [Tooltip("피크 주변 원형 윈도 반경(미터). 이 안의 3D 포인트로 centroid 계산")]
    public float centroidRadius = 0.0125f;

    [Header("Debug/Output")]
    [Tooltip("선택된 3개 중심을 씬에 마커(빈 오브젝트)로 생성")]
    public bool createMarkersInScene = true;

    [Tooltip("선택된 3개 중심의 월드 좌표를 Console 에 로그")]
    public bool logCenters = true;

    // 내부 상태
    private string status = "Ready.";

    private void OnEnable()
    {
        // 기본값 보정
        if (cellSize <= 0f) cellSize = Mathf.Max(0.001f, blockSize / 5f);
        if (blurSigma <= 0f) blurSigma = Mathf.Max(0.001f, blockSize / 6f);
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
        EditorGUILayout.LabelField("Peak & Scoring", EditorStyles.boldLabel);
        topPeakCount = EditorGUILayout.IntSlider("Top Peak Count", topPeakCount, 3, 12);
        angleToleranceDeg = EditorGUILayout.Slider("Angle Tolerance (deg)", angleToleranceDeg, 5f, 35f);
        spacingToleranceRatio = EditorGUILayout.Slider("Spacing Tolerance Ratio", spacingToleranceRatio, 0.05f, 0.5f);
        centroidRadius = EditorGUILayout.FloatField("Centroid Radius (m)", centroidRadius);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Debug/Output", EditorStyles.boldLabel);
        createMarkersInScene = EditorGUILayout.Toggle("Create Markers in Scene", createMarkersInScene);
        logCenters = EditorGUILayout.Toggle("Log Centers", logCenters);

        EditorGUILayout.Space(10);
        if (GUILayout.Button("Compute 3 Centers"))
        {
            Compute3Centers();
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.HelpBox(status, MessageType.Info);
    }

    // ============= Core Pipeline =============
    private void Compute3Centers()
    {
        try
        {
            if (pointCloudMesh == null || pointCloudMesh.sharedMesh == null)
            {
                status = "MeshFilter가 비어있습니다.";
                return;
            }

            var mesh = pointCloudMesh.sharedMesh;
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0)
            {
                status = "Mesh.vertices 가 비어있습니다.";
                return;
            }

            // 0) 월드 좌표로 변환
            var tf = pointCloudMesh.transform;
            var pointsW = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                pointsW[i] = tf.TransformPoint(verts[i]);

            // 1) UpAxis 기준으로 "위에서 본" 평면으로 투영할 2D 좌표 (p2) 뽑기
            //    upAxis가 Z -> XY 사용 / Y -> XZ 사용 / X -> YZ 사용
            Vector2[] p2;
            Vector3[] p3 = pointsW;
            GetProjected2D(pointsW, upAxis, out p2); // discard height

            // 2) 2D PCA로 주축(u) 계산하고, (u,v) 좌표계로 회전
            Vector2 mean2;
            Matrix2x2 cov;
            ComputeMeanAndCov(p2, out mean2, out cov);
            Vector2 uAxis = FirstEigenVector(cov); // 주성분 방향 (정규화됨)
            Vector2 vAxis = new Vector2(-uAxis.y, uAxis.x); // 직교 축

            // (u,v)로 좌표 변환
            var uv = new Vector2[p2.Length];
            for (int i = 0; i < p2.Length; i++)
            {
                Vector2 d = p2[i] - mean2;
                uv[i] = new Vector2(Vector2.Dot(d, uAxis), Vector2.Dot(d, vAxis));
            }

            // 3) UV 히트맵(그리드) 생성 + 가우시안 블러
            Rect uvBounds = GetBounds(uv);
            if (uvBounds.width <= 0f || uvBounds.height <= 0f)
            {
                status = "UV bounds 가 비정상입니다.";
                return;
            }

            int nx = Mathf.Max(8, Mathf.CeilToInt(uvBounds.width / cellSize));
            int ny = Mathf.Max(8, Mathf.CeilToInt(uvBounds.height / cellSize));
            float[,] hist = new float[nx, ny];

            // 누적
            for (int i = 0; i < uv.Length; i++)
            {
                int ix = Mathf.FloorToInt((uv[i].x - uvBounds.xMin) / cellSize);
                int iy = Mathf.FloorToInt((uv[i].y - uvBounds.yMin) / cellSize);
                if (ix >= 0 && ix < nx && iy >= 0 && iy < ny)
                    hist[ix, iy] += 1f;
            }

            // 블러 (separable Gaussian)
            float sigmaCells = Mathf.Max(0.5f, blurSigma / Mathf.Max(1e-6f, cellSize));
            hist = GaussianBlurSeparable(hist, sigmaCells);

            // 4) 지역 최대(피크) 추출
            var peaks = FindLocalMaxima(hist, topPeakCount);

            if (peaks.Count < 3)
            {
                status = $"피크가 {peaks.Count}개만 검출됨(3 미만). 파라미터(blur/cellSize/UpAxis)를 조정하세요.";
                return;
            }

            // 5) 후보 피크 좌표를 (u,v) 실수 좌표로 변환
            var peakUV = peaks.Select(p => new Vector2(
                uvBounds.xMin + (p.x + 0.5f) * cellSize,
                uvBounds.yMin + (p.y + 0.5f) * cellSize
            )).ToList();

            // 6) 피크들 중 3개 조합을 모두 평가 → 최고 점수 선택
            var best = ChooseBest3(peakUV, peaks.Select(p => p.value).ToList(), blockSize, angleToleranceDeg, spacingToleranceRatio);

            if (best == null)
            {
                status = "적합한 3개 조합을 찾지 못했습니다. 허용치/간격/블러/셀 크기를 조정하세요.";
                return;
            }

            // 선택된 3개의 UV → 3D centroid 계산
            var chosenUV = best.Item1;
            var centers3 = new List<Vector3>();

            // 반경 내 포인트 수집을 위해, 모든 포인트의 (u,v)도 사용
            float r = Mathf.Max(centroidRadius, cellSize * 1.2f);

            for (int k = 0; k < 3; k++)
            {
                Vector2 targetUV = chosenUV[k];
                // targetUV 주위의 포인트를 고르고 3D 평균
                var acc = Vector3.zero;
                int cnt = 0;
                for (int i = 0; i < uv.Length; i++)
                {
                    if ((uv[i] - targetUV).sqrMagnitude <= r * r)
                    {
                        acc += p3[i];
                        cnt++;
                    }
                }
                if (cnt == 0)
                {
                    // 혹시 비었으면 가장 가까운 포인트 하나라도 사용
                    int nearest = NearestIndex(uv, targetUV);
                    acc = p3[nearest];
                    cnt = 1;
                }
                centers3.Add(acc / Mathf.Max(1, cnt));
            }

            // 7) 정렬: u값 기준 좌→우 (ㄱ/ㅡ 판단에 상관 없이 일관성 위해)
            centers3 = centers3
                .Select(c => new { c, val = Vector2.Dot(To2DOnPlane(c, mean2, uAxis, vAxis), Vector2.right) })
                .OrderBy(t => t.val)
                .Select(t => t.c)
                .ToList();

            // 8) 결과 출력
            if (createMarkersInScene)
            {
                var parent = new GameObject("TripleCenters");
                for (int i = 0; i < centers3.Count; i++)
                {
                    var go = new GameObject($"Center_{i+1}");
                    go.transform.position = centers3[i];
                    go.transform.SetParent(parent.transform, true);
                }
            }
            if (logCenters)
            {
                Debug.Log($"[TripleCubeCenterFinder] Centers (world):\n" +
                          string.Join("\n", centers3.Select((c, i) => $"{i+1}: {c:F6}")));
            }

            status = "완료: 3개 중심을 계산했습니다.";
        }
        catch (Exception ex)
        {
            status = "에러: " + ex.Message;
            Debug.LogException(ex);
        }
    }

    // ============= Helpers =============

    private static void GetProjected2D(Vector3[] pointsW, UpAxis up, out Vector2[] p2)
    {
        p2 = new Vector2[pointsW.Length];
        switch (up)
        {
            case UpAxis.Z:
                for (int i = 0; i < pointsW.Length; i++)
                    p2[i] = new Vector2(pointsW[i].x, pointsW[i].y);
                break;
            case UpAxis.Y:
                for (int i = 0; i < pointsW.Length; i++)
                    p2[i] = new Vector2(pointsW[i].x, pointsW[i].z);
                break;
            case UpAxis.X:
                for (int i = 0; i < pointsW.Length; i++)
                    p2[i] = new Vector2(pointsW[i].y, pointsW[i].z);
                break;
        }
    }

    private static void ComputeMeanAndCov(Vector2[] p, out Vector2 mean, out Matrix2x2 cov)
    {
        mean = Vector2.zero;
        int n = p.Length;
        for (int i = 0; i < n; i++) mean += p[i];
        mean /= Mathf.Max(1, n);

        float xx = 0, xy = 0, yy = 0;
        for (int i = 0; i < n; i++)
        {
            var d = p[i] - mean;
            xx += d.x * d.x;
            xy += d.x * d.y;
            yy += d.y * d.y;
        }
        float inv = 1f / Mathf.Max(1, n);
        cov = new Matrix2x2(xx * inv, xy * inv, xy * inv, yy * inv);
    }

    private static Vector2 FirstEigenVector(Matrix2x2 c)
    {
        // 2x2 symmetric covariance: closed-form eigenvectors
        // eigenvalues: λ = (a+d ± sqrt((a-d)^2 + 4b^2))/2
        float a = c.m00, b = c.m01, d = c.m11;
        float T = a + d;
        float D = (a - d) * (a - d) + 4f * b * b;
        float sqrtD = Mathf.Sqrt(Mathf.Max(0f, D));
        float lambda1 = 0.5f * (T + sqrtD); // largest

        // (A - λI)v = 0 -> [(a-λ), b; b, (d-λ)]
        Vector2 v;
        if (Mathf.Abs(b) > 1e-12f)
            v = new Vector2(lambda1 - d, b);
        else
            v = (a >= d) ? new Vector2(1, 0) : new Vector2(0, 1);

        if (v.sqrMagnitude < 1e-20f) v = new Vector2(1, 0);
        v.Normalize();
        return v;
    }

    private static Rect GetBounds(Vector2[] pts)
    {
        if (pts.Length == 0) return new Rect(0, 0, 0, 0);
        float minx = pts[0].x, maxx = pts[0].x, miny = pts[0].y, maxy = pts[0].y;
        for (int i = 1; i < pts.Length; i++)
        {
            var p = pts[i];
            if (p.x < minx) minx = p.x;
            if (p.x > maxx) maxx = p.x;
            if (p.y < miny) miny = p.y;
            if (p.y > maxy) maxy = p.y;
        }
        // 약간의 여유 패딩
        float pad = 1e-6f + 0.5f * Mathf.Max( (maxx-minx), (maxy-miny) ) * 0.01f;
        return new Rect(minx - pad, miny - pad, (maxx - minx) + 2*pad, (maxy - miny) + 2*pad);
    }

    private static float[,] GaussianBlurSeparable(float[,] src, float sigmaCells)
    {
        int nx = src.GetLength(0);
        int ny = src.GetLength(1);
        int radius = Mathf.Clamp(Mathf.CeilToInt(3f * sigmaCells), 1, 64);

        // 1D kernel
        float[] k = new float[2 * radius + 1];
        float s2 = 2f * sigmaCells * sigmaCells;
        float sum = 0f;
        for (int i = -radius; i <= radius; i++)
        {
            float val = Mathf.Exp(-(i * i) / s2);
            k[i + radius] = val;
            sum += val;
        }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;

        // temp x
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

        // temp y -> dst
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

    private struct Peak
    {
        public int x, y;
        public float value;
        public Peak(int x, int y, float v) { this.x = x; this.y = y; this.value = v; }
    }

    private static List<Peak> FindLocalMaxima(float[,] img, int topK)
    {
        int nx = img.GetLength(0);
        int ny = img.GetLength(1);
        var list = new List<Peak>(nx * ny / 8);

        for (int y = 1; y < ny - 1; y++)
        {
            for (int x = 1; x < nx - 1; x++)
            {
                float c = img[x, y];
                bool isMax = true;
                for (int j = -1; j <= 1 && isMax; j++)
                {
                    for (int i = -1; i <= 1; i++)
                    {
                        if (i == 0 && j == 0) continue;
                        if (img[x + i, y + j] > c) { isMax = false; break; }
                    }
                }
                if (isMax && c > 0f) list.Add(new Peak(x, y, c));
            }
        }

        list.Sort((a, b) => b.value.CompareTo(a.value));
        if (list.Count > topK) list.RemoveRange(topK, list.Count - topK);
        return list;
    }

    // 선택 스코어링: ㄱ(≈90°) 또는 ㅡ(≈180°) 형태 + 간격(≈s 또는 2s) + 피크세기
    // 반환: (선택된 3개 UV, 점수)
    private static Tuple<List<Vector2>, float> ChooseBest3(List<Vector2> peakUV, List<float> peakVals,
                                                           float s, float angleTolDeg, float spacingTolRatio)
    {
        int m = peakUV.Count;
        if (m < 3) return null;

        float bestScore = float.NegativeInfinity;
        List<Vector2> bestSet = null;

        for (int i = 0; i < m; i++)
        for (int j = i + 1; j < m; j++)
        for (int k = j + 1; k < m; k++)
        {
            var a = peakUV[i];
            var b = peakUV[j];
            var c = peakUV[k];

            float vab = (a - b).magnitude;
            float vbc = (b - c).magnitude;
            float vac = (a - c).magnitude;

            // 간격 기준 추정: 세 거리의 중앙값을 대표 간격 d 로 사용
            float[] ds = new[] { vab, vbc, vac };
            Array.Sort(ds);
            float d = ds[1]; // median

            // s 또는 2s에 근접한지 점수
            float err1 = Mathf.Abs(d - s) / Mathf.Max(1e-6f, s);
            float err2 = Mathf.Abs(d - 2f * s) / Mathf.Max(1e-6f, 2f * s);
            float spacingErr = Mathf.Min(err1, err2);

            if (spacingErr > spacingTolRatio) // 너무 동떨어지면 컷 (속도 up)
                continue;

            // 각도 점수: 세 점에서 끼인 각 3개 계산 → (≈180°) 또는 (≈90°)가 나오면 좋음
            float angA = AngleAt(a, b, c);
            float angB = AngleAt(b, a, c);
            float angC = AngleAt(c, a, b);

            // ㅡ형 (거의 일직선) 후보: 180° 근접 각이 하나 매우 큼
            float lineCost = Mathf.Min(
                Mathf.Abs(180f - angA),
                Mathf.Abs(180f - angB),
                Mathf.Abs(180f - angC)
            );

            // ㄱ형 후보: 90° 근접 각이 하나
            float elbowCost = Mathf.Min(
                Mathf.Abs(90f - angA),
                Mathf.Abs(90f - angB),
                Mathf.Abs(90f - angC)
            );

            bool lineOK = lineCost <= angleTolDeg;
            bool elbowOK = elbowCost <= angleTolDeg;

            if (!lineOK && !elbowOK) continue;

            // 피크 세기 보상
            float valSum = peakVals[i] + peakVals[j] + peakVals[k];

            // 최종 점수: 간격 가산(작을수록 좋음), 형태 적합(라인/엘보 중 더 좋은 쪽), 피크세기 합
            // 간단 가중치
            float score = (lineOK ? 1.0f : 0f) * Mathf.Max(0f, (angleTolDeg - lineCost))
                        + (elbowOK ? 1.0f : 0f) * Mathf.Max(0f, (angleTolDeg - elbowCost))
                        + Mathf.Max(0f, (spacingTolRatio - spacingErr)) * 10f
                        + Mathf.Log(1f + valSum);

            if (score > bestScore)
            {
                bestScore = score;
                bestSet = new List<Vector2> { a, b, c };
            }
        }

        if (bestSet == null) return null;
        return Tuple.Create(bestSet, bestScore);
    }

    private static float AngleAt(Vector2 vertex, Vector2 p1, Vector2 p2)
    {
        Vector2 v1 = (p1 - vertex).normalized;
        Vector2 v2 = (p2 - vertex).normalized;
        float dot = Mathf.Clamp(Vector2.Dot(v1, v2), -1f, 1f);
        return Mathf.Acos(dot) * Mathf.Rad2Deg;
    }

    private static int NearestIndex(Vector2[] ps, Vector2 q)
    {
        int idx = 0; float best = float.PositiveInfinity;
        for (int i = 0; i < ps.Length; i++)
        {
            float d = (ps[i] - q).sqrMagnitude;
            if (d < best) { best = d; idx = i; }
        }
        return idx;
    }

    private static Vector2 To2DOnPlane(Vector3 worldPoint, Vector2 mean2, Vector2 uAxis, Vector2 vAxis)
    {
        // worldPoint 를 평면상 2D로 보려면, mean2에 대응되는 월드 기준이 필요하지만
        // 여기서는 단순 정렬용으로만 사용 → mean2는 오프셋 상징적
        // (실제 정렬에서는 uv 배열로 처리했으므로, 여기서는 상대 비교만 수행)
        Vector2 p2 = new Vector2(worldPoint.x, worldPoint.y); // UpAxis=Z 기준의 간략화
        Vector2 d = p2 - mean2;
        return new Vector2(Vector2.Dot(d, uAxis), Vector2.Dot(d, vAxis));
    }

    // 간단 2x2 행렬 구조체
    private struct Matrix2x2
    {
        public float m00, m01, m10, m11;
        public Matrix2x2(float m00, float m01, float m10, float m11)
        {
            this.m00 = m00; this.m01 = m01; this.m10 = m10; this.m11 = m11;
        }
    }
}
#endif
