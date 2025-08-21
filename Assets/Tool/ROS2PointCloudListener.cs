// using System;
// using System.Collections.Generic;
// using System.Reflection;  // <- live pose reflection
// using System.Text;
// using UnityEngine;
// using ROS2;
// using sensor_msgs.msg;
// using UnityEngine.InputSystem;

// [RequireComponent(typeof(ROS2UnityComponent))]
// public class Ros2PointCloudListener : MonoBehaviour
// {
//     // ======================== Inspector ========================
//     [Header("ROS2")]
//     public string Topic = "/camera/camera/depth/color/points";

//     [Header("Per-Frame Preview")]
//     public int  MaxPoints = 50_000;      // 프레임당 샘플 상한
//     public bool isColorBGR = true;       // rgb float가 BGR 패킹인지 여부
//     public bool forceRenderRed = false;  // 프리뷰 색상 강제 빨강

//     [Header("Global Map (Chunk mode, same as preview)")]
//     public bool accumulateAsChunksLikePreview = true;  // 프리뷰와 동일 계산으로 누적
//     public int  maxChunks = 300;                       // 보관할 청크 수
//     public int  maxPointsPerChunk = 50_000;            // 청크당 포인트 수

//     [Header("Pose / TF (카메라=link 포즈 사용)")]
//     public DsrPoseListener dsrPose;                    // 카메라(link) 포즈 제공자

//     [Header("Marker / Misc")]
//     public bool drawCameraMarker = true;
//     public float cameraCubeSize = 0.05f;               // m (프리팹 미지정 시 폴백)
//     public bool showGlobalParent = true;
//     public bool logEvery30 = true;
//     public bool logColorValidation = true;

//     [Header("Gripper Marker (snapshot on capture)")]
//     public GameObject gripperPrefab;                   // 캡처용 그리퍼 프리팹(없으면 큐브)
//     public bool reuseSingleGripperMarker = true;       // true: 하나 재사용, false: 캡처마다 생성
//     public Vector3 gripperPosOffset = Vector3.zero;    // 스냅샷 마커 로컬 위치 오프셋
//     public Vector3 gripperRotOffsetEuler = Vector3.zero; // 스냅샷 마커 로컬 회전 오프셋
//     public Vector3 gripperScale = Vector3.one;         // 스냅샷 마커 스케일

//     [Header("Live Gripper (real-time)")]
//     public bool showGripperLive = true;                // 실시간 그리퍼 표시
//     [Tooltip("카메라(link) → 그리퍼 변환(DSR가 그리퍼 포즈를 직접 주지 않을 때 사용)")]
//     public Vector3 camToGripperPos = Vector3.zero;     // m
//     public Vector3 camToGripperRotEuler = Vector3.zero;// deg
//     public Vector3 liveGripperScale = Vector3.one;     // 라이브 마커 스케일

//     [Header("Frames")]
//     [Tooltip("optical→link 고정 회전 후보 토글 (A/B)")]
//     [SerializeField] private bool testAltOpt2Link = false; // 회전 후보 A/B
//     private bool? pcdIsOptical = null;                     // frame_id 캐시

//     // ======================== Internals ========================
//     // ROS
//     private ROS2UnityComponent ros2Unity;
//     private ROS2Node node;
//     private ISubscription<PointCloud2> sub;

//     // Preview buffers
//     private Mesh currentMesh;
//     private Vector3[] frameVerts;
//     private Color[]   frameCols;
//     private Color[]   frameColsRed;
//     private int[]     frameIdx;

//     private struct Frame { public int n; }
//     private Frame? latest;
//     private readonly object latestLock = new object();
//     private ulong recvCount;

//     // Children & materials
//     private GameObject currentGO, globalGO;
//     private Material defaultMat, redMat;

//     // Chunks
//     private readonly Queue<GameObject> chunkPool = new Queue<GameObject>();

//     // Color parsing/field offsets
//     private enum ColorParsingMethod { UNKNOWN, PACKED_FLOAT_RGB, SEPARATE_RGB_UINT8 }
//     private ColorParsingMethod colorMethod = ColorParsingMethod.UNKNOWN;
//     private int offX = -1, offY = -1, offZ = -1, offR = -1, offG = -1, offB = -1, offPackedRGB = -1;
//     private bool structureAnalyzed = false;

//     // Input
//     private InputAction captureAction;

//     // Snapshot gripper instance (reuse mode)
//     private GameObject gripperMarker; // 재사용 모드일 때 인스턴스 캐시

//     // Live gripper instance
//     private GameObject gripperLiveGO;

//     // ======================== Unity Lifecycle ========================
//     void Awake()
//     {
//         ros2Unity = GetComponent<ROS2UnityComponent>();
//         if (dsrPose == null) dsrPose = FindObjectOfType<DsrPoseListener>();

//         // Parents
//         currentGO = new GameObject("CurrentFrame");
//         currentGO.transform.SetParent(transform, false);
//         var mf1 = currentGO.AddComponent<MeshFilter>();
//         var mr1 = currentGO.AddComponent<MeshRenderer>();

//         globalGO = new GameObject("GlobalChunks");
//         globalGO.transform.SetParent(transform, false);

//         // Materials
//         var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
//                  ?? Shader.Find("Particles/Standard Unlit")
//                  ?? Shader.Find("Unlit/Color");
//         defaultMat = new Material(sh) { color = Color.white };
//         redMat     = new Material(sh) { color = Color.red };
//         mr1.sharedMaterial = defaultMat;

//         // Mesh / buffers
//         currentMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
//         mf1.sharedMesh = currentMesh;

//         frameVerts   = new Vector3[MaxPoints];
//         frameCols    = new Color[MaxPoints];
//         frameColsRed = new Color[MaxPoints];
//         frameIdx     = new int[MaxPoints];
//         for (int i = 0; i < MaxPoints; i++)
//         {
//             frameIdx[i] = i;
//             frameCols[i] = Color.white;
//             frameColsRed[i] = Color.red;
//         }

//         // Input: Space → capture
//         captureAction = new InputAction(binding: "<Keyboard>/space");
//         captureAction.performed += _ => CaptureRenderAndMerge();
//         captureAction.Enable();

//         Debug.Log("[ROS2 PointCloud(link-aligned)] Ready. Press <Space> to capture → preview+merge.");
//     }

//     void OnDestroy()
//     {
//         try { captureAction?.Disable(); } catch { }
//         try { sub?.Dispose(); } catch { }
//         node = null;
//     }

//     void Update()
//     {
//         if (node == null && ros2Unity != null && ros2Unity.Ok())
//         {
//             node = ros2Unity.CreateNode("unity_pcd_listener");
//             sub  = node.CreateSubscription<PointCloud2>(Topic, OnPointCloud);
//             Debug.Log($"[ROS2] Subscribed: {Topic}");
//         }

//         if (globalGO) globalGO.SetActive(showGlobalParent);

//         if (showGripperLive) UpdateGripperLive();
//         else if (gripperLiveGO) gripperLiveGO.SetActive(false);
//     }

//     // ======================== ROS Callback ========================
//     private void OnPointCloud(PointCloud2 msg)
//     {
//         if (!structureAnalyzed) AnalyzeStructure(msg);
//         if (offX < 0 || offY < 0 || offZ < 0) return;

//         // 0) frame_id에서 optical 여부 캐시(최초 1회)
//         EnsureFrameIdCached(msg);

//         int step  = (int)msg.Point_step;
//         int total = msg.Data.Length / Math.Max(1, step);
//         int n     = Mathf.Min(total, MaxPoints);
//         var buf   = msg.Data;

//         // optical → link 고정 회전 (Unity 축 기준)
//         Quaternion qOpt2Link = GetOpticalToLinkRotation(testAltOpt2Link);

//         int black=0, white=0, rr=0, gg=0, bb=0, other=0;

//         for (int j = 0; j < n; j++)
//         {
//             int i = Mathf.FloorToInt((float)j * total / n);
//             int baseOfs = i * step;

//             float vx = ReadFloat(buf, baseOfs + offX, msg.Is_bigendian);
//             float vy = ReadFloat(buf, baseOfs + offY, msg.Is_bigendian);
//             float vz = ReadFloat(buf, baseOfs + offZ, msg.Is_bigendian);

//             Vector3 vLocal;

//             if (pcdIsOptical == true)
//             {
//                 // ROS Optical(x-right, y-down, z-fwd) → Unity optical(x-right, y-up, z-fwd)
//                 Vector3 vInUnityOptical = new Vector3(vx, -vy, vz);
//                 // optical → link (Unity 회전)
//                 vLocal = qOpt2Link * vInUnityOptical;
//             }
//             else
//             {
//                 // Link(ROS base) → Unity : (-y, z, x)
//                 vLocal = new Vector3(-vy, vz, vx);
//             }

//             frameVerts[j] = vLocal;

//             // 색상 파싱
//             switch (colorMethod)
//             {
//                 case ColorParsingMethod.PACKED_FLOAT_RGB:
//                     frameCols[j] = ParsePackedFloatColor(buf, baseOfs + offPackedRGB, msg.Is_bigendian, isColorBGR);
//                     break;
//                 case ColorParsingMethod.SEPARATE_RGB_UINT8:
//                     frameCols[j] = ParseSeparateUint8Color(buf, baseOfs, isColorBGR);
//                     break;
//                 default:
//                     frameCols[j] = Color.magenta;
//                     break;
//             }
//             ClassifyColor(frameCols[j], ref black, ref white, ref rr, ref gg, ref bb, ref other);
//         }

//         lock (latestLock) latest = new Frame { n = n };
//         recvCount++;

//         if (logEvery30 && (recvCount % 30 == 0))
//             LogStatus(msg, n, colorMethod != ColorParsingMethod.UNKNOWN, black, white, rr, gg, bb, other);
//     }

//     // ======================== Capture → Preview + Merge ========================
//     private void CaptureRenderAndMerge()
//     {
//         // 0) 최신 포인트 프레임 스냅샷
//         Frame? f;
//         lock (latestLock) f = latest;
//         if (!f.HasValue) { Debug.LogWarning("[Capture] No buffered frame."); return; }
//         int n = f.Value.n;

//         // 1) 카메라(link) 포즈
//         if (dsrPose == null || !dsrPose.TryGetLatestCameraPose(out Vector3 camPos, out Quaternion camRot))
//         {
//             Debug.LogWarning("[Capture] No latest CAMERA(link) pose available; skip.");
//             return;
//         }

//         // 2) 카메라 월드 변환
//         Matrix4x4 T_world = Matrix4x4.TRS(camPos, camRot, Vector3.one);

//         // 3) 프리뷰: 카메라(link) 포즈 적용 (점은 link 로컬)
//         {
//             currentGO.transform.SetPositionAndRotation(camPos, camRot);

//             currentMesh.Clear();
//             int m = Mathf.Min(n, MaxPoints);
//             currentMesh.SetVertices(frameVerts, 0, m);
//             currentMesh.SetColors(forceRenderRed ? frameColsRed : frameCols, 0, m);

//             if (frameIdx.Length < m)
//             {
//                 frameIdx = new int[m];
//                 for (int i = 0; i < m; i++) frameIdx[i] = i;
//             }
//             currentMesh.SetIndices(frameIdx, 0, m, MeshTopology.Points, 0, false);
//             currentMesh.RecalculateBounds();
//         }

//         // 4) 마커(스냅샷): gripperPrefab 있으면 그리퍼, 없으면 빨간 큐브
//         if (drawCameraMarker) SpawnCameraMarker(T_world);

//         // 5) 머지(청크)도 같은 포즈로 (프리뷰와 동일 경로/값)
//         if (accumulateAsChunksLikePreview) CreateChunkFromCurrentFrame(T_world, n);
//         else Debug.LogWarning("[Merge] Only chunk mode is implemented. Set accumulateAsChunksLikePreview=true.");

//         Debug.Log($"[Capture/LinkAligned] CAM pos={V3(camPos)}  rot(eul)={V3(camRot.eulerAngles)}");
//     }

//     // ======================== Chunk Builder ========================
//     private void CreateChunkFromCurrentFrame(Matrix4x4 T_world, int n)
//     {
//         // 1) 용량 관리
//         while (chunkPool.Count >= Mathf.Max(1, maxChunks))
//         {
//             var old = chunkPool.Dequeue();
//             if (old) Destroy(old);
//         }

//         // 2) 새 청크 GO (카메라 포즈 대입)
//         var go = new GameObject($"GlobalChunk_{Time.frameCount}");
//         go.transform.SetParent(globalGO.transform, worldPositionStays:false);

//         Vector3 p = T_world.MultiplyPoint3x4(Vector3.zero);
//         Vector3 fwd = T_world.MultiplyVector(Vector3.forward);
//         Vector3 up  = T_world.MultiplyVector(Vector3.up);
//         Quaternion q = (fwd.sqrMagnitude>1e-8f && up.sqrMagnitude>1e-8f)
//             ? Quaternion.LookRotation(fwd.normalized, up.normalized)
//             : Quaternion.identity;
//         go.transform.SetPositionAndRotation(p, q);

//         var mf = go.AddComponent<MeshFilter>();
//         var mr = go.AddComponent<MeshRenderer>();
//         mr.sharedMaterial = defaultMat;

//         // 3) 프레임 버텍스/컬러 복사 (link 로컬 그대로)
//         int m = Mathf.Min(n, Mathf.Min(MaxPoints, maxPointsPerChunk));
//         var vertsCopy  = new Vector3[m];
//         var colorsCopy = new Color[m];
//         Array.Copy(frameVerts, vertsCopy, m);
//         Array.Copy(frameCols,  colorsCopy, m);

//         var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
//         var idx  = new int[m];
//         for (int i = 0; i < m; i++) idx[i] = i;

//         mesh.SetVertices(vertsCopy);
//         mesh.SetColors(colorsCopy);
//         mesh.SetIndices(idx, MeshTopology.Points, 0, false);
//         mesh.RecalculateBounds();
//         mf.sharedMesh = mesh;

//         chunkPool.Enqueue(go);
//     }

//     // ======================== Snapshot Marker (Gripper or Cube) ========================
//     private void SpawnCameraMarker(Matrix4x4 T)
//     {
//         Vector3 p = T.MultiplyPoint3x4(Vector3.zero);
//         Vector3 fwd = T.MultiplyVector(Vector3.forward);
//         Vector3 up  = T.MultiplyVector(Vector3.up);
//         Quaternion q = (fwd.sqrMagnitude>1e-8f && up.sqrMagnitude>1e-8f)
//             ? Quaternion.LookRotation(fwd.normalized, up.normalized)
//             : Quaternion.identity;

//         if (gripperPrefab != null)
//         {
//             // 로컬 오프셋 적용(스냅샷용)
//             p += (q * gripperPosOffset);
//             q  = q * Quaternion.Euler(gripperRotOffsetEuler);

//             if (reuseSingleGripperMarker)
//             {
//                 if (gripperMarker == null)
//                 {
//                     gripperMarker = Instantiate(gripperPrefab, globalGO.transform);
//                     gripperMarker.name = "GripperMarker_Singleton";
//                     ForceURPMaterialsSafe(gripperMarker);
//                     RemoveAllColliders(gripperMarker);
//                     gripperMarker.transform.localScale = gripperScale;
//                 }
//                 gripperMarker.transform.SetPositionAndRotation(p, q);
//                 gripperMarker.transform.localScale = gripperScale;
//             }
//             else
//             {
//                 var snap = Instantiate(gripperPrefab, globalGO.transform);
//                 snap.name = $"GripperMarker_{Time.frameCount}";
//                 ForceURPMaterialsSafe(snap);
//                 RemoveAllColliders(snap);
//                 snap.transform.SetPositionAndRotation(p, q);
//                 snap.transform.localScale = gripperScale;
//             }
//         }
//         else
//         {
//             // 폴백: 빨간 큐브
//             var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
//             cube.name = $"CameraMarker_{Time.frameCount}";
//             cube.transform.SetParent(globalGO.transform, worldPositionStays:false);
//             cube.transform.SetPositionAndRotation(p, q);
//             cube.transform.localScale = Vector3.one * Mathf.Max(0.001f, cameraCubeSize);

//             var mr = cube.GetComponent<MeshRenderer>();
//             if (mr) mr.sharedMaterial = redMat;
//             var col = cube.GetComponent<Collider>();
//             if (col) Destroy(col);
//         }
//     }

//     // ======================== LIVE Gripper (real-time) ========================
//     private void UpdateGripperLive()
//     {
//         if (dsrPose == null) return;

//         // 1) dsrPose가 "그리퍼/ TCP 포즈"를 직접 주면 그걸 우선 사용
//         Vector3 gPos; Quaternion gRot;
//         if (!TryGetPose("TryGetLatestGripperPose", out gPos, out gRot) &&
//             !TryGetPose("TryGetLatestTcpPose",     out gPos, out gRot) &&
//             !TryGetPose("TryGetLatestPose",        out gPos, out gRot))
//         {
//             // 2) 없으면 "카메라 포즈 + 카메라→그리퍼 오프셋"으로 보정
//             Vector3 camPos; Quaternion camRot;
//             if (!TryGetPose("TryGetLatestCameraPose", out camPos, out camRot)) return;

//             gPos = camPos + camRot * camToGripperPos;
//             gRot = camRot * Quaternion.Euler(camToGripperRotEuler);
//         }

//         // 3) 마커 준비(프리팹 우선, 없으면 큐브)
//         if (gripperLiveGO == null)
//         {
//             if (gripperPrefab != null)
//             {
//                 gripperLiveGO = Instantiate(gripperPrefab, globalGO.transform);
//                 gripperLiveGO.name = "GripperLive";
//                 ForceURPMaterialsSafe(gripperLiveGO);
//                 RemoveAllColliders(gripperLiveGO);
//             }
//             else
//             {
//                 gripperLiveGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
//                 gripperLiveGO.name = "GripperLive_Cube";
//                 gripperLiveGO.transform.SetParent(globalGO.transform, false);
//                 var mr = gripperLiveGO.GetComponent<MeshRenderer>();
//                 if (mr) mr.sharedMaterial = redMat;
//                 var col = gripperLiveGO.GetComponent<Collider>(); if (col) Destroy(col);
//             }
//         }

//         // 4) 위치/회전/스케일 갱신
//         gripperLiveGO.transform.SetPositionAndRotation(gPos, gRot);
//         gripperLiveGO.transform.localScale = liveGripperScale;
//         gripperLiveGO.SetActive(showGlobalParent); // 부모가 꺼지면 같이 안 보이도록
//     }

//     // dsrPose의 다양한 시그니처(있는 경우)에 대응하기 위한 리플렉션 호출
//     private bool TryGetPose(string methodName, out Vector3 pos, out Quaternion rot)
//     {
//         pos = default; rot = default;
//         try
//         {
//             var mi = dsrPose.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
//             if (mi == null) return false;

//             var ps = mi.GetParameters();
//             if (ps.Length != 2) return false; // (out Vector3, out Quaternion) 시그니처

//             object[] args = new object[] { default(Vector3), default(Quaternion) };
//             var ret = mi.Invoke(dsrPose, args);
//             bool ok = (ret is bool b) && b;
//             if (!ok) return false;

//             pos = (Vector3)args[0];
//             rot = (Quaternion)args[1];
//             return true;
//         }
//         catch { return false; }
//     }

//     // ======================== Helpers ========================
//     private void RemoveAllColliders(GameObject go)
//     {
//         var cols = go.GetComponentsInChildren<Collider>(true);
//         foreach (var c in cols) Destroy(c);
//     }

//     private void ForceURPMaterialsSafe(GameObject go)
//     {
//         var urp = Shader.Find("Universal Render Pipeline/Lit")
//                ?? Shader.Find("Universal Render Pipeline/Simple Lit")
//                ?? Shader.Find("Standard");
//         if (urp == null) return;

//         foreach (var r in go.GetComponentsInChildren<Renderer>(true))
//         {
//             var mats = r.sharedMaterials;
//             for (int i = 0; i < mats.Length; i++)
//             {
//                 var m = mats[i];
//                 if (m == null) continue;
//                 if (m.shader != null && m.shader.name.StartsWith("Universal")) continue;
//                 m.shader = urp;
//             }
//         }
//     }

//     private void AnalyzeStructure(PointCloud2 msg)
//     {
//         var sb = new StringBuilder("[ROS2 PointCloud] Fields:\n");
//         foreach (var f in msg.Fields)
//         {
//             sb.AppendLine($"  - {f.Name} @ {f.Offset} (datatype={f.Datatype}, count={f.Count})");
//             if (f.Name == "x") offX = (int)f.Offset;
//             else if (f.Name == "y") offY = (int)f.Offset;
//             else if (f.Name == "z") offZ = (int)f.Offset;
//             else if (f.Name == "rgb" || f.Name == "rgba")
//             {
//                 if (f.Datatype == 7) { offPackedRGB = (int)f.Offset; colorMethod = ColorParsingMethod.PACKED_FLOAT_RGB; }
//             }
//             else if (f.Name == "r") offR = (int)f.Offset;
//             else if (f.Name == "g") offG = (int)f.Offset;
//             else if (f.Name == "b") offB = (int)f.Offset;
//         }
//         if (offR != -1 && offG != -1 && offB != -1) colorMethod = ColorParsingMethod.SEPARATE_RGB_UINT8;
//         sb.AppendLine($"--> ColorMethod: {colorMethod}");
//         Debug.Log(sb.ToString());
//         structureAnalyzed = true;
//     }

//     // PCD frame_id에서 optical 여부를 최초 1회만 판별
//     private void EnsureFrameIdCached(PointCloud2 msg)
//     {
//         if (pcdIsOptical != null) return;

//         string fid = msg.Header != null ? msg.Header.Frame_id ?? "" : "";
//         bool isOpt = !string.IsNullOrEmpty(fid) && fid.ToLower().Contains("optical");
//         pcdIsOptical = isOpt;

//         Debug.Log($"[PCD] frame_id='{fid}', optical={pcdIsOptical}");
//     }

//     // optical → link 고정 회전 (Unity 축 기준), 드라이버/기기차 보정용 후보 A/B 제공
//     private static Quaternion GetOpticalToLinkRotation(bool useAltCandidate)
//     {
//         // 후보 A: Rz(+90°) * Rx(-90°)
//         Quaternion A = Quaternion.AngleAxis( 90f, Vector3.up)
//                       * Quaternion.AngleAxis(-90f, Vector3.right);

//         // 후보 B: Ry(+90°) * Rz(-90°)
//         Quaternion B = Quaternion.AngleAxis( 90f, Vector3.up)
//                       * Quaternion.AngleAxis(-90f, Vector3.forward);

//         return useAltCandidate ? B : A;
//     }

//     private Color ParseSeparateUint8Color(byte[] buffer, int baseOffset, bool isBgr)
//     {
//         byte r = buffer[baseOffset + offR];
//         byte g = buffer[baseOffset + offG];
//         byte b = buffer[baseOffset + offB];
//         return isBgr ? new Color(b/255f, g/255f, r/255f, 1f) : new Color(r/255f, g/255f, b/255f, 1f);
//     }

//     private Color ParsePackedFloatColor(byte[] buffer, int offset, bool isBigEndian, bool isBgr)
//     {
//         float rgbFloat = ReadFloat(buffer, offset, isBigEndian);
//         int rgbBits = BitConverter.SingleToInt32Bits(rgbFloat);
//         byte R, G, B;
//         if (isBgr) { B = (byte)(rgbBits & 0xFF); G = (byte)((rgbBits >> 8) & 0xFF); R = (byte)((rgbBits >> 16) & 0xFF); }
//         else       { R = (byte)((rgbBits >> 16) & 0xFF); G = (byte)((rgbBits >> 8) & 0xFF); B = (byte)(rgbBits & 0xFF); }
//         return new Color(R/255f, G/255f, B/255f, 1f);
//     }

//     private float ReadFloat(byte[] buffer, int offset, bool isBigEndian)
//     {
//         if (!isBigEndian) return BitConverter.ToSingle(buffer, offset);
//         byte[] t = new byte[4];
//         Array.Copy(buffer, offset, t, 0, 4);
//         Array.Reverse(t);
//         return BitConverter.ToSingle(t, 0);
//     }

//     private void ClassifyColor(Color c, ref int black, ref int white, ref int r, ref int g, ref int b, ref int other)
//     {
//         float lum = 0.1f, sat = 0.1f;
//         if (c.r < lum && c.g < lum && c.b < lum) black++;
//         else if (Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b) < sat) white++;
//         else if (c.r > c.g && c.r > c.b) r++;
//         else if (c.g > c.r && c.g > c.b) g++;
//         else if (c.b > c.r && c.b > c.g) b++;
//         else other++;
//     }

//     private void LogStatus(PointCloud2 msg, int n, bool hasColor, int black, int white, int r, int g, int b, int other)
//     {
//         float mb = msg.Data.Length / 1e6f;
//         Debug.Log($"[ROS2] recv #{recvCount} pts={n} raw={mb:F2}MB (preview={(forceRenderRed ? "RED":"Color")})");
//         if (logColorValidation && hasColor && n > 0)
//         {
//             float f = 100f / n;
//             Debug.Log($"[Color] W {white*f:F1}% | R {r*f:F1}% | G {g*f:F1}% | B {b*f:F1}% | K {black*f:F1}% | O {other*f:F1}%");
//         }
//     }

//     // Short vector formatter
//     private static string V3(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";
// }

// Ros2PointCloudListener.cs
// Unity + ROS2 PointCloud2 구독 → 프레임 프리뷰 + 청크 누적 + 스냅샷/라이브 그리퍼 마커
// - PCD frame_id가 optical이면 optical→link 회전 적용, 아니면 ROS link(base)→Unity 축 매핑 적용
// - 카메라 포즈는 DsrPoseListener.TryGetLatestCameraPose() 사용(link 기준 가정)
// - Space: 현재 프레임을 청크로 누적(프리뷰와 동일 좌표계)
// - Snapshot/Live 마커는 cam→gripper 외부정렬을 공유하여 각도 불일치 방지
//
// 요구 패키지:
// - UnityEngine.InputSystem
// - ROS-TCP-Connector(또는 ROS2Unity) + sensor_msgs/PointCloud2 바인딩



using System;
using System.Collections.Generic;
using System.Reflection;  // reflection for dsrPose optional methods
using System.Text;
using UnityEngine;
using ROS2;
using sensor_msgs.msg;
using UnityEngine.InputSystem;

[RequireComponent(typeof(ROS2UnityComponent))]
public class Ros2PointCloudListener : MonoBehaviour
{
    // ======================== Inspector ========================
    [Header("ROS2")]
    public string Topic = "/camera/camera/depth/color/points";

    [Header("Per-Frame Preview")]
    public int  MaxPoints = 50_000;      // 프레임당 샘플 상한
    public bool isColorBGR = true;       // rgb float가 BGR 패킹인지 여부
    public bool forceRenderRed = false;  // 프리뷰 색상 강제 빨강

    [Header("Global Map (Chunk mode, same as preview)")]
    public bool accumulateAsChunksLikePreview = true;  // 프리뷰와 동일 계산으로 누적
    public int  maxChunks = 300;                       // 보관할 청크 수
    public int  maxPointsPerChunk = 50_000;            // 청크당 포인트 수

    [Header("Pose / TF (카메라=link 포즈 사용)")]
    public DsrPoseListener dsrPose;                    // 카메라(link) 포즈 제공자

    [Header("Marker / Misc")]
    public bool drawCameraMarker = true;
    public float cameraCubeSize = 0.05f;               // 프리팹 미지정 시 폴백 큐브 크기
    public bool showGlobalParent = true;
    public bool logEvery30 = true;
    public bool logColorValidation = true;

    [Header("Gripper Prefab")]
    public GameObject gripperPrefab;                   // 프리팹(있으면 사용, 없으면 큐브)

    [Header("cam → gripper (공통 외부정렬)")]
    [Tooltip("카메라(link) 로컬에서 그리퍼로의 위치/회전 오프셋 (두 마커 공통)")]
    public Vector3 camToGripperPos = Vector3.zero;     // meters
    public Vector3 camToGripperRotEuler = Vector3.zero;// degrees

    [Header("Snapshot Gripper (on capture)")]
    public bool reuseSingleSnapshotMarker = true;      // true: 하나 재사용, false: 캡처마다 생성
    public Vector3 snapshotGripperScale = Vector3.one; // 스냅샷 마커 스케일

    [Header("Live Gripper (real-time)")]
    public bool showGripperLive = true;                // 실시간 그리퍼 표시
    public bool preferCameraExtrinsicForLive = false;  // true면 항상 카메라+외부정렬 경로 사용
    public Vector3 liveGripperScale = Vector3.one;     // 라이브 마커 스케일

    [Header("Frames / Debug")]
    [Tooltip("optical→link 고정 회전 후보 토글(A/B)")]
    [SerializeField] private bool testAltOpt2Link = false; // 회전 후보 A/B
    private bool? pcdIsOptical = null;                      // frame_id 캐시

    // ======================== Internals ========================
    // ROS
    private ROS2UnityComponent ros2Unity;
    private ROS2Node node;
    private ISubscription<PointCloud2> sub;

    // Preview buffers
    private Mesh currentMesh;
    private Vector3[] frameVerts;
    private Color[]   frameCols;
    private Color[]   frameColsRed;
    private int[]     frameIdx;

    private struct Frame { public int n; }
    private Frame? latest;
    private readonly object latestLock = new object();
    private ulong recvCount;

    // Children & materials
    private GameObject currentGO, globalGO;
    private Material defaultMat, redMat;

    // Chunks
    private readonly Queue<GameObject> chunkPool = new Queue<GameObject>();

    // Color parsing/field offsets
    private enum ColorParsingMethod { UNKNOWN, PACKED_FLOAT_RGB, SEPARATE_RGB_UINT8 }
    private ColorParsingMethod colorMethod = ColorParsingMethod.UNKNOWN;
    private int offX = -1, offY = -1, offZ = -1, offR = -1, offG = -1, offB = -1, offPackedRGB = -1;
    private bool structureAnalyzed = false;

    // Input
    private InputAction captureAction;

    // Snapshot marker reuse instance
    private GameObject snapshotMarkerSingleton;

    // Live gripper instance
    private GameObject gripperLiveGO;

    // ======================== Unity Lifecycle ========================
    void Awake()
    {
        ros2Unity = GetComponent<ROS2UnityComponent>();
        if (dsrPose == null) dsrPose = FindObjectOfType<DsrPoseListener>();

        // Parents
        currentGO = new GameObject("CurrentFrame");
        currentGO.transform.SetParent(transform, false);
        var mf1 = currentGO.AddComponent<MeshFilter>();
        var mr1 = currentGO.AddComponent<MeshRenderer>();

        globalGO = new GameObject("GlobalChunks");
        globalGO.transform.SetParent(transform, false);

        // Materials
        var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                 ?? Shader.Find("Particles/Standard Unlit")
                 ?? Shader.Find("Unlit/Color");
        defaultMat = new Material(sh) { color = Color.white };
        redMat     = new Material(sh) { color = Color.red };
        mr1.sharedMaterial = defaultMat;

        // Mesh / buffers
        currentMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mf1.sharedMesh = currentMesh;

        frameVerts   = new Vector3[MaxPoints];
        frameCols    = new Color[MaxPoints];
        frameColsRed = new Color[MaxPoints];
        frameIdx     = new int[MaxPoints];
        for (int i = 0; i < MaxPoints; i++)
        {
            frameIdx[i] = i;
            frameCols[i] = Color.white;
            frameColsRed[i] = Color.red;
        }

        // Input: Space → capture
        captureAction = new InputAction(binding: "<Keyboard>/space");
        captureAction.performed += _ => CaptureRenderAndMerge();
        captureAction.Enable();

        Debug.Log("[ROS2 PointCloud(link-aligned)] Ready. Press <Space> to capture → preview+merge.");
    }

    void OnDestroy()
    {
        try { captureAction?.Disable(); } catch { }
        try { sub?.Dispose(); } catch { }
        node = null;
    }

    void Update()
    {
        if (node == null && ros2Unity != null && ros2Unity.Ok())
        {
            node = ros2Unity.CreateNode("unity_pcd_listener");
            sub  = node.CreateSubscription<PointCloud2>(Topic, OnPointCloud);
            Debug.Log($"[ROS2] Subscribed: {Topic}");
        }

        if (globalGO) globalGO.SetActive(showGlobalParent);

        if (showGripperLive) UpdateGripperLive();
        else if (gripperLiveGO) gripperLiveGO.SetActive(false);
    }

    // ======================== ROS Callback ========================
    private void OnPointCloud(PointCloud2 msg)
    {
        if (!structureAnalyzed) AnalyzeStructure(msg);
        if (offX < 0 || offY < 0 || offZ < 0) return;

        // frame_id에서 optical 여부 캐시(최초 1회)
        EnsureFrameIdCached(msg);

        int step  = (int)msg.Point_step;
        int total = msg.Data.Length / Math.Max(1, step);
        int n     = Mathf.Min(total, MaxPoints);
        var buf   = msg.Data;

        // optical → link 고정 회전 (Unity 축 기준)
        Quaternion qOpt2Link = GetOpticalToLinkRotation(testAltOpt2Link);

        int black=0, white=0, rr=0, gg=0, bb=0, other=0;

        for (int j = 0; j < n; j++)
        {
            int i = Mathf.FloorToInt((float)j * total / n);
            int baseOfs = i * step;

            float vx = ReadFloat(buf, baseOfs + offX, msg.Is_bigendian);
            float vy = ReadFloat(buf, baseOfs + offY, msg.Is_bigendian);
            float vz = ReadFloat(buf, baseOfs + offZ, msg.Is_bigendian);

            Vector3 vLocal;

            if (pcdIsOptical == true)
            {
                // ROS Optical(x-right, y-down, z-fwd) → Unity optical(x-right, y-up, z-fwd)
                Vector3 vInUnityOptical = new Vector3(vx, -vy, vz);
                // optical → link (Unity 회전)
                vLocal = qOpt2Link * vInUnityOptical;
            }
            else
            {
                // Link(ROS base) → Unity : (-y, z, x)
                vLocal = new Vector3(-vy, vz, vx);
            }

            frameVerts[j] = vLocal;

            // 색상 파싱
            switch (colorMethod)
            {
                case ColorParsingMethod.PACKED_FLOAT_RGB:
                    frameCols[j] = ParsePackedFloatColor(buf, baseOfs + offPackedRGB, msg.Is_bigendian, isColorBGR);
                    break;
                case ColorParsingMethod.SEPARATE_RGB_UINT8:
                    frameCols[j] = ParseSeparateUint8Color(buf, baseOfs, isColorBGR);
                    break;
                default:
                    frameCols[j] = Color.magenta;
                    break;
            }
            ClassifyColor(frameCols[j], ref black, ref white, ref rr, ref gg, ref bb, ref other);
        }

        lock (latestLock) latest = new Frame { n = n };
        recvCount++;

        if (logEvery30 && (recvCount % 30 == 0))
            LogStatus(msg, n, colorMethod != ColorParsingMethod.UNKNOWN, black, white, rr, gg, bb, other);
    }

    // ======================== Capture → Preview + Merge ========================
    private void CaptureRenderAndMerge()
    {
        // 0) 최신 포인트 프레임 스냅샷
        Frame? f;
        lock (latestLock) f = latest;
        if (!f.HasValue) { Debug.LogWarning("[Capture] No buffered frame."); return; }
        int n = f.Value.n;

        // 1) 카메라(link) 포즈
        if (dsrPose == null || !dsrPose.TryGetLatestCameraPose(out Vector3 camPos, out Quaternion camRot))
        {
            Debug.LogWarning("[Capture] No latest CAMERA(link) pose available; skip.");
            return;
        }

        // 2) 카메라 월드 변환
        Matrix4x4 T_world_cam = Matrix4x4.TRS(camPos, camRot, Vector3.one);

        // 3) 프리뷰: 카메라(link) 포즈 적용 (점은 link 로컬)
        {
            currentGO.transform.SetPositionAndRotation(camPos, camRot);

            currentMesh.Clear();
            int m = Mathf.Min(n, MaxPoints);
            currentMesh.SetVertices(frameVerts, 0, m);
            currentMesh.SetColors(forceRenderRed ? frameColsRed : frameCols, 0, m);

            if (frameIdx.Length < m)
            {
                frameIdx = new int[m];
                for (int i = 0; i < m; i++) frameIdx[i] = i;
            }
            currentMesh.SetIndices(frameIdx, 0, m, MeshTopology.Points, 0, false);
            currentMesh.RecalculateBounds();
        }

        // 4) 스냅샷 마커(그리퍼/큐브)
        if (drawCameraMarker) SpawnSnapshotGripperMarker(T_world_cam);

        // 5) 머지(청크)도 같은 포즈로 (프리뷰와 동일 경로/값)
        if (accumulateAsChunksLikePreview) CreateChunkFromCurrentFrame(T_world_cam, n);
        else Debug.LogWarning("[Merge] Only chunk mode is implemented. Set accumulateAsChunksLikePreview=true.");

        Debug.Log($"[Capture/LinkAligned] CAM pos={V3(camPos)}  rot(eul)={V3(camRot.eulerAngles)}");
    }

    // ======================== Chunk Builder ========================
    private void CreateChunkFromCurrentFrame(Matrix4x4 T_world_cam, int n)
    {
        // 1) 용량 관리
        while (chunkPool.Count >= Mathf.Max(1, maxChunks))
        {
            var old = chunkPool.Dequeue();
            if (old) Destroy(old);
        }

        // 2) 새 청크 GO (카메라 포즈 대입)
        var go = new GameObject($"GlobalChunk_{Time.frameCount}");
        go.transform.SetParent(globalGO.transform, worldPositionStays:false);

        Vector3 p = T_world_cam.MultiplyPoint3x4(Vector3.zero);
        Vector3 fwd = T_world_cam.MultiplyVector(Vector3.forward);
        Vector3 up  = T_world_cam.MultiplyVector(Vector3.up);
        Quaternion q = (fwd.sqrMagnitude>1e-8f && up.sqrMagnitude>1e-8f)
            ? Quaternion.LookRotation(fwd.normalized, up.normalized)
            : Quaternion.identity;
        go.transform.SetPositionAndRotation(p, q);

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = defaultMat;

        // 3) 프레임 버텍스/컬러 복사 (link 로컬 그대로)
        int m = Mathf.Min(n, Mathf.Min(MaxPoints, maxPointsPerChunk));
        var vertsCopy  = new Vector3[m];
        var colorsCopy = new Color[m];
        Array.Copy(frameVerts, vertsCopy, m);
        Array.Copy(frameCols,  colorsCopy, m);

        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        var idx  = new int[m];
        for (int i = 0; i < m; i++) idx[i] = i;

        mesh.SetVertices(vertsCopy);
        mesh.SetColors(colorsCopy);
        mesh.SetIndices(idx, MeshTopology.Points, 0, false);
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;

        chunkPool.Enqueue(go);
    }

    // ======================== Snapshot Marker ========================
    private void SpawnSnapshotGripperMarker(Matrix4x4 T_world_cam)
    {
        // 카메라→그리퍼 외부정렬을 합성한 그리퍼 포즈
        Matrix4x4 T_world_grip = ComposeCamToGripper(T_world_cam, camToGripperPos, camToGripperRotEuler);

        Vector3 p = T_world_grip.MultiplyPoint3x4(Vector3.zero);
        Vector3 f = T_world_grip.MultiplyVector(Vector3.forward);
        Vector3 u = T_world_grip.MultiplyVector(Vector3.up);
        Quaternion q = (f.sqrMagnitude>1e-8f && u.sqrMagnitude>1e-8f)
            ? Quaternion.LookRotation(f.normalized, u.normalized)
            : Quaternion.identity;

        if (gripperPrefab != null)
        {
            if (reuseSingleSnapshotMarker)
            {
                if (snapshotMarkerSingleton == null)
                {
                    snapshotMarkerSingleton = Instantiate(gripperPrefab, globalGO.transform);
                    snapshotMarkerSingleton.name = "GripperSnapshot_Singleton";
                    ForceURPMaterialsSafe(snapshotMarkerSingleton);
                    RemoveAllColliders(snapshotMarkerSingleton);
                }
                snapshotMarkerSingleton.transform.SetPositionAndRotation(p, q);
                snapshotMarkerSingleton.transform.localScale = snapshotGripperScale;
            }
            else
            {
                var snap = Instantiate(gripperPrefab, globalGO.transform);
                snap.name = $"GripperSnapshot_{Time.frameCount}";
                ForceURPMaterialsSafe(snap);
                RemoveAllColliders(snap);
                snap.transform.SetPositionAndRotation(p, q);
                snap.transform.localScale = snapshotGripperScale;
            }
        }
        else
        {
            // 폴백: 빨간 큐브
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"GripperSnapshotCube_{Time.frameCount}";
            cube.transform.SetParent(globalGO.transform, false);
            cube.transform.SetPositionAndRotation(p, q);
            cube.transform.localScale = Vector3.one * Mathf.Max(0.001f, cameraCubeSize);

            var mr = cube.GetComponent<MeshRenderer>();
            if (mr) mr.sharedMaterial = redMat;
            var col = cube.GetComponent<Collider>();
            if (col) Destroy(col);
        }
    }

    // ======================== LIVE Gripper (real-time) ========================
    private void UpdateGripperLive()
    {
        if (dsrPose == null) return;

        Vector3 gPos; Quaternion gRot;
        bool usedDirectGrip = false;

        // direct gripper/tcp pose (world) 우선 사용 (옵션으로 비활성화 가능)
        if (!preferCameraExtrinsicForLive &&
            ( TryGetPose("TryGetLatestGripperPose", out gPos, out gRot) ||
              TryGetPose("TryGetLatestTcpPose",     out gPos, out gRot) ||
              TryGetPose("TryGetLatestPose",        out gPos, out gRot) ))
        {
            usedDirectGrip = true;
        }
        else
        {
            // 카메라 포즈 + 외부정렬
            if (!TryGetPose("TryGetLatestCameraPose", out Vector3 camPos, out Quaternion camRot)) return;

            Matrix4x4 T_world_cam = Matrix4x4.TRS(camPos, camRot, Vector3.one);
            Matrix4x4 T_world_grip = ComposeCamToGripper(T_world_cam, camToGripperPos, camToGripperRotEuler);

            gPos = T_world_grip.MultiplyPoint3x4(Vector3.zero);
            Vector3 f = T_world_grip.MultiplyVector(Vector3.forward);
            Vector3 u = T_world_grip.MultiplyVector(Vector3.up);
            gRot = (f.sqrMagnitude>1e-8f && u.sqrMagnitude>1e-8f)
                 ? Quaternion.LookRotation(f.normalized, u.normalized)
                 : Quaternion.identity;
        }

        // 마커 준비(프리팹 우선, 없으면 큐브)
        if (gripperLiveGO == null)
        {
            if (gripperPrefab != null)
            {
                gripperLiveGO = Instantiate(gripperPrefab, globalGO.transform);
                gripperLiveGO.name = "GripperLive";
                ForceURPMaterialsSafe(gripperLiveGO);
                RemoveAllColliders(gripperLiveGO);
            }
            else
            {
                gripperLiveGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                gripperLiveGO.name = "GripperLive_Cube";
                gripperLiveGO.transform.SetParent(globalGO.transform, false);
                var mr = gripperLiveGO.GetComponent<MeshRenderer>();
                if (mr) mr.sharedMaterial = redMat;
                var col = gripperLiveGO.GetComponent<Collider>(); if (col) Destroy(col);
            }
        }

        gripperLiveGO.transform.SetPositionAndRotation(gPos, gRot);
        gripperLiveGO.transform.localScale = liveGripperScale;
        gripperLiveGO.SetActive(showGlobalParent);

        if (logEvery30 && (recvCount % 30 == 0))
        {
            Debug.Log($"[LiveGrip] source={(usedDirectGrip ? "DIRECT":"CAM+EXTR")} pos={V3(gPos)} rot={V3(gRot.eulerAngles)}");
        }
    }

    // ======================== dsrPose helper (reflection) ========================
    private bool TryGetPose(string methodName, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = default;
        try
        {
            var mi = dsrPose.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            if (mi == null) return false;

            var ps = mi.GetParameters();
            if (ps.Length != 2) return false; // (out Vector3, out Quaternion) 시그니처

            object[] args = new object[] { default(Vector3), default(Quaternion) };
            var ret = mi.Invoke(dsrPose, args);
            bool ok = (ret is bool b) && b;
            if (!ok) return false;

            pos = (Vector3)args[0];
            rot = (Quaternion)args[1];
            return true;
        }
        catch { return false; }
    }

    // ======================== Utils ========================
    private static Matrix4x4 ComposeCamToGripper(Matrix4x4 T_cam, Vector3 posOff, Vector3 rotOffEuler)
    {
        return T_cam * Matrix4x4.TRS(posOff, Quaternion.Euler(rotOffEuler), Vector3.one);
    }

    private void RemoveAllColliders(GameObject go)
    {
        var cols = go.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols) Destroy(c);
    }

    private void ForceURPMaterialsSafe(GameObject go)
    {
        var urp = Shader.Find("Universal Render Pipeline/Lit")
               ?? Shader.Find("Universal Render Pipeline/Simple Lit")
               ?? Shader.Find("Standard");
        if (urp == null) return;

        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.shader != null && m.shader.name.StartsWith("Universal")) continue;
                m.shader = urp;
            }
        }
    }

    private void AnalyzeStructure(PointCloud2 msg)
    {
        var sb = new StringBuilder("[ROS2 PointCloud] Fields:\n");
        foreach (var f in msg.Fields)
        {
            sb.AppendLine($"  - {f.Name} @ {f.Offset} (datatype={f.Datatype}, count={f.Count})");
            if (f.Name == "x") offX = (int)f.Offset;
            else if (f.Name == "y") offY = (int)f.Offset;
            else if (f.Name == "z") offZ = (int)f.Offset;
            else if (f.Name == "rgb" || f.Name == "rgba")
            {
                if (f.Datatype == 7) { offPackedRGB = (int)f.Offset; colorMethod = ColorParsingMethod.PACKED_FLOAT_RGB; }
            }
            else if (f.Name == "r") offR = (int)f.Offset;
            else if (f.Name == "g") offG = (int)f.Offset;
            else if (f.Name == "b") offB = (int)f.Offset;
        }
        if (offR != -1 && offG != -1 && offB != -1) colorMethod = ColorParsingMethod.SEPARATE_RGB_UINT8;
        sb.AppendLine($"--> ColorMethod: {colorMethod}");
        Debug.Log(sb.ToString());
        structureAnalyzed = true;
    }

    // PCD frame_id에서 optical 여부를 최초 1회만 판별
    private void EnsureFrameIdCached(PointCloud2 msg)
    {
        if (pcdIsOptical != null) return;

        string fid = msg.Header != null ? msg.Header.Frame_id ?? "" : "";
        bool isOpt = !string.IsNullOrEmpty(fid) && fid.ToLower().Contains("optical");
        pcdIsOptical = isOpt;

        Debug.Log($"[PCD] frame_id='{fid}', optical={pcdIsOptical}");
    }

    // optical → link 고정 회전 (Unity 축 기준), 드라이버/기기차 보정용 후보 A/B 제공
    private static Quaternion GetOpticalToLinkRotation(bool useAltCandidate)
    {
        // 후보 A: Rz(+90°) * Rx(-90°) in Unity axes terms
        Quaternion A = Quaternion.AngleAxis( 90f, Vector3.up)
                      * Quaternion.AngleAxis(-90f, Vector3.right);

        // 후보 B: Ry(+90°) * Rz(-90°)
        Quaternion B = Quaternion.AngleAxis( 90f, Vector3.up)
                      * Quaternion.AngleAxis(-90f, Vector3.forward);

        return useAltCandidate ? B : A;
    }

    private Color ParseSeparateUint8Color(byte[] buffer, int baseOffset, bool isBgr)
    {
        byte r = buffer[baseOffset + offR];
        byte g = buffer[baseOffset + offG];
        byte b = buffer[baseOffset + offB];
        return isBgr ? new Color(b/255f, g/255f, r/255f, 1f) : new Color(r/255f, g/255f, b/255f, 1f);
    }

    private Color ParsePackedFloatColor(byte[] buffer, int offset, bool isBigEndian, bool isBgr)
    {
        float rgbFloat = ReadFloat(buffer, offset, isBigEndian);
        int rgbBits = BitConverter.SingleToInt32Bits(rgbFloat);
        byte R, G, B;
        if (isBgr) { B = (byte)(rgbBits & 0xFF); G = (byte)((rgbBits >> 8) & 0xFF); R = (byte)((rgbBits >> 16) & 0xFF); }
        else       { R = (byte)((rgbBits >> 16) & 0xFF); G = (byte)((rgbBits >> 8) & 0xFF); B = (byte)(rgbBits & 0xFF); }
        return new Color(R/255f, G/255f, B/255f, 1f);
    }

    private float ReadFloat(byte[] buffer, int offset, bool isBigEndian)
    {
        if (!isBigEndian) return BitConverter.ToSingle(buffer, offset);
        byte[] t = new byte[4];
        Array.Copy(buffer, offset, t, 0, 4);
        Array.Reverse(t);
        return BitConverter.ToSingle(t, 0);
    }

    private void ClassifyColor(Color c, ref int black, ref int white, ref int r, ref int g, ref int b, ref int other)
    {
        float lum = 0.1f, sat = 0.1f;
        if (c.r < lum && c.g < lum && c.b < lum) black++;
        else if (Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b) < sat) white++;
        else if (c.r > c.g && c.r > c.b) r++;
        else if (c.g > c.r && c.g > c.b) g++;
        else if (c.b > c.r && c.b > c.g) b++;
        else other++;
    }

    private void LogStatus(PointCloud2 msg, int n, bool hasColor, int black, int white, int r, int g, int b, int other)
    {
        float mb = msg.Data.Length / 1e6f;
        Debug.Log($"[ROS2] recv #{recvCount} pts={n} raw={mb:F2}MB (preview={(forceRenderRed ? "RED":"Color")})");
        if (logColorValidation && hasColor && n > 0)
        {
            float f = 100f / n;
            Debug.Log($"[Color] W {white*f:F1}% | R {r*f:F1}% | G {g*f:F1}% | B {b*f:F1}% | K {black*f:F1}% | O {other*f:F1}%");
        }
    }

    private static string V3(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";
}
