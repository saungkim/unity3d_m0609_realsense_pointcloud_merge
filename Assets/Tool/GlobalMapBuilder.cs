// // using UnityEngine;
// // using System.Collections.Generic;
// // using System.Linq;

// // [RequireComponent(typeof(Ros2PointCloudListener), typeof(DsrPoseListener))] 
// // public class GlobalMapBuilder : MonoBehaviour
// // {
// //     private Ros2PointCloudListener listener;
// //     private DsrPoseListener dsrPoseListener;
// //     private MeshFilter globalMapMeshFilter;
// //     private Mesh globalMapMesh;

// //     private Vector3[] globalVertices;
// //     private Color[] globalColors;
// //     private int currentIndex = 0;
// //     private int currentPointCount = 0;

// //     // ⭐︎ 변경: pointGrid은 인덱스(int)를 저장하도록 수정하여 PointGrid과 globalVertices의 일관성을 유지
// //     private Dictionary<Vector3Int, List<int>> pointGrid = new Dictionary<Vector3Int, List<int>>();
// //     public float cellSize = 0.1f;

// //     private bool isFirstFrame = true;

// //     [Header("Map Building Settings")]
// //     public float mergeDistanceThreshold = 0.05f;
// //     public int MaxTotalPoints = 50000; // ⭐︎ MaxPoints를 5만으로 고정 (Ros2PointCloudListener와 일치)

// //     private readonly object dataLock = new object();
// //     private int[] globalIndices;

// //     void Start()
// //     {
// //         listener = GetComponent<Ros2PointCloudListener>();
// //         dsrPoseListener = GetComponent<DsrPoseListener>();
        
// //         if (listener == null || dsrPoseListener == null)
// //         {
// //             Debug.LogError("Required components not found.");
// //             this.enabled = false;
// //             return;
// //         }
        
// //         GameObject mapObject = new GameObject("GlobalMap");
// //         globalMapMeshFilter = mapObject.AddComponent<MeshFilter>();
// //         MeshRenderer renderer = mapObject.AddComponent<MeshRenderer>();
// //         renderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

// //         globalMapMesh = new Mesh();
// //         globalMapMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
// //         globalMapMeshFilter.mesh = globalMapMesh;

// //         // ⭐︎ 배열 초기화: MaxTotalPoints로 고정된 크기
// //         globalVertices = new Vector3[MaxTotalPoints];
// //         globalColors = new Color[MaxTotalPoints];
// //         globalIndices = new int[MaxTotalPoints];
// //         for (int i = 0; i < MaxTotalPoints; i++)
// //         {
// //             globalIndices[i] = i;
// //         }

// //         listener.OnPointCloudReceived += UpdateGlobalMap;
// //         Debug.Log("Global Map Builder is ready. Max points set to " + MaxTotalPoints);
// //     }
    
// //     private void UpdateGlobalMap(Vector3[] newVertices, Color[] newColors)
// //     {
// //         Vector3 position;
// //         Quaternion rotation;

// //         if (!dsrPoseListener.TryGetLatestPose(out position, out rotation))
// //         {
// //             Debug.LogWarning("No new DSR pose data available. Skipping frame.");
// //             return;
// //         }

// //         Matrix4x4 tfMatrix = Matrix4x4.TRS(position, rotation, Vector3.one);

// //         lock(dataLock)
// //         {
// //             if (isFirstFrame)
// //             {
// //                 int n = Mathf.Min(newVertices.Length, MaxTotalPoints);
// //                 for(int i = 0; i < n; i++)
// //                 {
// //                     Vector3 transformedPoint = tfMatrix.MultiplyPoint3x4(newVertices[i]);
// //                     globalVertices[i] = transformedPoint;
// //                     globalColors[i] = newColors[i];
// //                     AddPointToGrid(transformedPoint, i); // ⭐︎ 인덱스도 함께 전달
// //                 }
// //                 currentIndex = n % MaxTotalPoints;
// //                 currentPointCount = n;
// //                 isFirstFrame = false;
// //                 Debug.Log($"Initial map created with {currentPointCount} points.");
// //             }
// //             else
// //             {
// //                 MergeNewPoints(newVertices, newColors, tfMatrix);

// //                 Debug.Log("머지 뉴 포인트");
// //             }
// //         }
// //     }
    
// //     void Update()
// //     {
// //         lock(dataLock)
// //         {
// //             if (currentPointCount > 0)
// //             {
// //                 // ⭐︎⭐⭐︎ 수정된 부분: Update()에서 새로운 배열을 생성하지 않음
// //                 globalMapMesh.Clear();
// //                 globalMapMesh.SetVertices(globalVertices);
// //                 globalMapMesh.SetColors(globalColors);
                
// //                 // ⭐︎ 인덱스도 고정된 배열 사용
// //                 globalMapMesh.SetIndices(globalIndices, 0, currentPointCount, MeshTopology.Points, 0, false);
                
// //                 globalMapMesh.RecalculateBounds();
// //             }
// //         }
// //     }

// //     private void MergeNewPoints(Vector3[] newVertices, Color[] newColors, Matrix4x4 transform)
// //     {
// //         int mergedCount = 0;
// //         for (int i = 0; i < newVertices.Length; i++)
// //         {
// //             Vector3 transformedPoint = transform.MultiplyPoint3x4(newVertices[i]);
            
// //             int closestIndex = FindClosestPointIndex(transformedPoint);
            
// //             if (closestIndex != -1)
// //             {
// //                 // 중복 포인트가 있으면 업데이트
// //                 globalVertices[closestIndex] = transformedPoint;
// //                 globalColors[closestIndex] = newColors[i];
// //                 UpdatePointInGrid(transformedPoint, closestIndex);
// //             }
// //             else
// //             {
// //                 // ⭐︎ 새 포인트 추가: 순환 버퍼 로직
// //                 // 가장 오래된 포인트의 인덱스
// //                 int oldPointIndex = currentIndex;
                
// //                 // ⭐︎ pointGrid에서 오래된 포인트 정보 삭제
// //                 RemovePointFromGrid(globalVertices[oldPointIndex], oldPointIndex);

// //                 // 새 포인트로 덮어쓰기
// //                 globalVertices[currentIndex] = transformedPoint;
// //                 globalColors[currentIndex] = newColors[i];
                
// //                 // ⭐︎ pointGrid에 새 포인트 정보 추가
// //                 AddPointToGrid(transformedPoint, currentIndex);
                
// //                 currentIndex = (currentIndex + 1) % MaxTotalPoints;
// //                 if (currentPointCount < MaxTotalPoints)
// //                 {
// //                     currentPointCount++;
// //                 }
// //                 mergedCount++;
// //             }
// //         }
        
// //         if (mergedCount > 0)
// //         {
// //             Debug.Log($"Merged {mergedCount} new points. Total points: {currentPointCount}");
// //         }
// //     }

// //     private Vector3Int GetCellIndex(Vector3 point)
// //     {
// //         return new Vector3Int(
// //             Mathf.FloorToInt(point.x / cellSize),
// //             Mathf.FloorToInt(point.y / cellSize),
// //             Mathf.FloorToInt(point.z / cellSize)
// //         );
// //     }
    
// //     private void AddPointToGrid(Vector3 point, int index)
// //     {
// //         Vector3Int cellIndex = GetCellIndex(point);
// //         if (!pointGrid.ContainsKey(cellIndex))
// //         {
// //             pointGrid[cellIndex] = new List<int>();
// //         }
// //         pointGrid[cellIndex].Add(index);
// //     }
    
// //     private void RemovePointFromGrid(Vector3 point, int index)
// //     {
// //         Vector3Int cellIndex = GetCellIndex(point);
// //         if (pointGrid.ContainsKey(cellIndex))
// //         {
// //             pointGrid[cellIndex].Remove(index);
// //             if (pointGrid[cellIndex].Count == 0)
// //             {
// //                 pointGrid.Remove(cellIndex);
// //             }
// //         }
// //     }

// //     private void UpdatePointInGrid(Vector3 point, int index)
// //     {
// //         Vector3Int cellIndex = GetCellIndex(point);
        
// //         // 이전 포인트의 위치를 정확히 알 수 없으므로,
// //         // 해당 인덱스를 가진 모든 포인트 제거 후 다시 추가하는 방어적 로직
// //         // (원래는 인덱스가 중복되지 않으므로 한 번만 제거)
// //         if (pointGrid.ContainsKey(cellIndex))
// //         {
// //             pointGrid[cellIndex].Remove(index);
// //         }
// //         AddPointToGrid(point, index);
// //     }
    
// //     private int FindClosestPointIndex(Vector3 point)
// //     {
// //         Vector3Int cellIndex = GetCellIndex(point);
// //         float minDistanceSqr = mergeDistanceThreshold * mergeDistanceThreshold;
// //         int closestIndex = -1;

// //         for (int x = -1; x <= 1; x++)
// //         {
// //             for (int y = -1; y <= 1; y++)
// //             {
// //                 for (int z = -1; z <= 1; z++)
// //                 {
// //                     Vector3Int neighborCellIndex = cellIndex + new Vector3Int(x, y, z);
                    
// //                     if (pointGrid.ContainsKey(neighborCellIndex))
// //                     {
// //                         foreach (var existingIndex in pointGrid[neighborCellIndex])
// //                         {
// //                             float distSqr = Vector3.SqrMagnitude(point - globalVertices[existingIndex]);
// //                             if (distSqr < minDistanceSqr)
// //                             {
// //                                 minDistanceSqr = distSqr;
// //                                 closestIndex = existingIndex;
// //                             }
// //                         }
// //                     }
// //                 }
// //             }
// //         }
// //         return closestIndex;
// //     }
// // }

// using UnityEngine;
// using System.Collections.Generic;
// using System.Linq;
// using System.Diagnostics;
// using System.Threading.Tasks;

// [RequireComponent(typeof(Ros2PointCloudListener), typeof(DsrPoseListener))] 
// public class GlobalMapBuilder : MonoBehaviour
// {
//     private Ros2PointCloudListener listener;
//     private DsrPoseListener dsrPoseListener;
//     private MeshFilter globalMapMeshFilter;
//     private Mesh globalMapMesh;

//     private Vector3[] globalVertices;
//     private Color[] globalColors;
//     private int currentIndex = 0;
//     private int currentPointCount = 0;

//     private Dictionary<Vector3Int, List<int>> pointGrid = new Dictionary<Vector3Int, List<int>>();
//     public float cellSize = 0.1f;

//     private bool isFirstFrame = true;

//     [Header("Map Building Settings")]
//     public float mergeDistanceThreshold = 0.05f;
//     public int MaxTotalPoints = 50000;

//     private readonly object dataLock = new object();
//     private int[] globalIndices;
    
//     private Queue<FrameData> frameQueue = new Queue<FrameData>();
//     private bool isMerging = false;

//     private struct FrameData
//     {
//         public Vector3[] vertices;
//         public Color[] colors;
//         public Matrix4x4 transform;
//     }

//     void Start()
//     {
//         listener = GetComponent<Ros2PointCloudListener>();
//         dsrPoseListener = GetComponent<DsrPoseListener>();
        
//         if (listener == null || dsrPoseListener == null)
//         {
//             UnityEngine.Debug.LogError("Required components not found.");
//             this.enabled = false;
//             return;
//         }
        
//         GameObject mapObject = new GameObject("GlobalMap");
//         globalMapMeshFilter = mapObject.AddComponent<MeshFilter>();
//         MeshRenderer renderer = mapObject.AddComponent<MeshRenderer>();
//         renderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

//         globalMapMesh = new Mesh();
//         globalMapMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
//         globalMapMeshFilter.mesh = globalMapMesh;

//         globalVertices = new Vector3[MaxTotalPoints];
//         globalColors = new Color[MaxTotalPoints];
//         globalIndices = new int[MaxTotalPoints];
//         for (int i = 0; i < MaxTotalPoints; i++)
//         {
//             globalIndices[i] = i;
//         }

//         listener.OnPointCloudReceived += EnqueueNewPoints;
//         UnityEngine.Debug.Log("Global Map Builder is ready. Max points set to " + MaxTotalPoints);
//     }

//     private void EnqueueNewPoints(Vector3[] newVertices, Color[] newColors)
//     {
//         Vector3 position;
//         Quaternion rotation;

//         if (dsrPoseListener.TryGetLatestPose(out position, out rotation))
//         {
//             lock(dataLock)
//             {
//                 frameQueue.Enqueue(new FrameData
//                 {
//                     vertices = newVertices,
//                     colors = newColors,
//                     transform = Matrix4x4.TRS(position, rotation, Vector3.one)
//                 });
//             }
//             if (!isMerging)
//             {
//                 _ = ProcessQueueAsync();
//             }
//         }
//     }

//     private async Task ProcessQueueAsync()
//     {
//         isMerging = true;
//         await Task.Run(() =>
//         {
//             while (true)
//             {
//                 FrameData frame;
//                 lock(dataLock)
//                 {
//                     if (frameQueue.Count == 0) break;
//                     frame = frameQueue.Dequeue();
//                 }
                
//                 if (isFirstFrame)
//                 {
//                     InitializeMap(frame.vertices, frame.colors, frame.transform);
//                 }
//                 else
//                 {
//                     MergeNewPoints(frame.vertices, frame.colors, frame.transform);
//                 }
//             }
//         });
//         isMerging = false;
//     }
    
//     void Update()
//     {
//         lock(dataLock)
//         {
//             if (currentPointCount > 0)
//             {
//                 // ⭐⭐⭐ 추가된 부분: 현재 렌더링 중인 포인트 수 디버그
//                 UnityEngine.Debug.Log($"[Rendering] Total points to render: {currentPointCount}");

//                 globalMapMesh.Clear();
//                 globalMapMesh.SetVertices(globalVertices);
//                 globalMapMesh.SetColors(globalColors);
                
//                 globalMapMesh.SetIndices(globalIndices, 0, currentPointCount, MeshTopology.Points, 0, false);
                
//                 globalMapMesh.RecalculateBounds();
//             }
//         }
//     }

//     private void InitializeMap(Vector3[] newVertices, Color[] newColors, Matrix4x4 transform)
//     {
//         lock(dataLock)
//         {
//             int n = Mathf.Min(newVertices.Length, MaxTotalPoints);
//             for(int i = 0; i < n; i++)
//             {
//                 Vector3 transformedPoint = transform.MultiplyPoint3x4(newVertices[i]);
//                 globalVertices[i] = transformedPoint;
//                 globalColors[i] = newColors[i];
//                 AddPointToGrid(transformedPoint, i);
//             }
//             currentIndex = n % MaxTotalPoints;
//             currentPointCount = n;
//             isFirstFrame = false;
//             UnityEngine.Debug.Log($"Initial map created with {currentPointCount} points.");
//         }
//     }

//     private void MergeNewPoints(Vector3[] newVertices, Color[] newColors, Matrix4x4 transform)
//     {
//         lock(dataLock)
//         {
//             int mergedCount = 0;
//             for (int i = 0; i < newVertices.Length; i++)
//             {
//                 Vector3 transformedPoint = transform.MultiplyPoint3x4(newVertices[i]);
//                 int closestIndex = FindClosestPointIndex(transformedPoint);
                
//                 if (closestIndex != -1)
//                 {
//                     globalVertices[closestIndex] = transformedPoint;
//                     globalColors[closestIndex] = newColors[i];
//                     UpdatePointInGrid(transformedPoint, closestIndex);
//                 }
//                 else
//                 {
//                     if (currentPointCount == MaxTotalPoints)
//                     {
//                         int oldPointIndex = currentIndex;
//                         Vector3 oldPoint = globalVertices[oldPointIndex];
//                         RemovePointFromGrid(oldPoint, oldPointIndex);
//                     }

//                     globalVertices[currentIndex] = transformedPoint;
//                     globalColors[currentIndex] = newColors[i];
                    
//                     AddPointToGrid(transformedPoint, currentIndex);
                    
//                     currentIndex = (currentIndex + 1) % MaxTotalPoints;
//                     if (currentPointCount < MaxTotalPoints)
//                     {
//                         currentPointCount++;
//                     }
//                     mergedCount++;
//                 }
//             }
//             // ⭐⭐⭐ 추가된 부분: 병합 완료 시 디버그
//             UnityEngine.Debug.Log($"[Merging] Merged {mergedCount} new points. Total points: {currentPointCount}");
//         }
//     }
    
//     // ... (이하 다른 헬퍼 메서드들은 동일)
//     private Vector3Int GetCellIndex(Vector3 point)
//     {
//         return new Vector3Int(
//             Mathf.FloorToInt(point.x / cellSize),
//             Mathf.FloorToInt(point.y / cellSize),
//             Mathf.FloorToInt(point.z / cellSize)
//         );
//     }
    
//     private void AddPointToGrid(Vector3 point, int index)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         if (!pointGrid.ContainsKey(cellIndex))
//         {
//             pointGrid[cellIndex] = new List<int>();
//         }
//         pointGrid[cellIndex].Add(index);
//     }
    
//     private void RemovePointFromGrid(Vector3 point, int index)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         if (pointGrid.ContainsKey(cellIndex))
//         {
//             pointGrid[cellIndex].Remove(index);
//             if (pointGrid[cellIndex].Count == 0)
//             {
//                 pointGrid.Remove(cellIndex);
//             }
//         }
//     }

//     private void UpdatePointInGrid(Vector3 point, int index)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         if (pointGrid.ContainsKey(cellIndex))
//         {
//             pointGrid[cellIndex].Remove(index);
//         }
//         AddPointToGrid(point, index);
//     }
    
//     private int FindClosestPointIndex(Vector3 point)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         float minDistanceSqr = mergeDistanceThreshold * mergeDistanceThreshold;
//         int closestIndex = -1;

//         for (int x = -1; x <= 1; x++)
//         {
//             for (int y = -1; y <= 1; y++)
//             {
//                 for (int z = -1; z <= 1; z++)
//                 {
//                     Vector3Int neighborCellIndex = cellIndex + new Vector3Int(x, y, z);
                    
//                     if (pointGrid.ContainsKey(neighborCellIndex))
//                     {
//                         foreach (var existingIndex in pointGrid[neighborCellIndex])
//                         {
//                             float distSqr = Vector3.SqrMagnitude(point - globalVertices[existingIndex]);
//                             if (distSqr < minDistanceSqr)
//                             {
//                                 minDistanceSqr = distSqr;
//                                 closestIndex = existingIndex;
//                             }
//                         }
//                     }
//                 }
//             }
//         }
//         return closestIndex;
//     }
// // }
// using UnityEngine;
// using System.Collections.Generic;
// using System.Linq;
// using System.Diagnostics;
// using System.Threading.Tasks;

// [RequireComponent(typeof(Ros2PointCloudListener), typeof(DsrPoseListener))] 
// public class GlobalMapBuilder : MonoBehaviour
// {
//     private Ros2PointCloudListener listener;
//     private DsrPoseListener dsrPoseListener;
//     private MeshFilter globalMapMeshFilter;
//     private Mesh globalMapMesh;

//     private Vector3[] globalVertices;
//     private Color[] globalColors;
//     private int currentIndex = 0;
//     private int currentPointCount = 0;

//     private Dictionary<Vector3Int, List<int>> pointGrid = new Dictionary<Vector3Int, List<int>>();
//     public float cellSize = 0.1f;

//     private bool isFirstFrame = true;

//     [Header("Map Building Settings")]
//     public float mergeDistanceThreshold = 0.05f;
//     public int MaxTotalPoints = 50000;

//     // ⭐⭐ 추가된 부분: 타이머 기반 병합을 위한 변수들
//     [Header("Merging Frequency")]
//     public float mergeInterval = 3.0f; // 3초에 한 번씩 병합
//     private float lastMergeTime;
    
//     // ⭐⭐ 추가된 변수: 최신 프레임 데이터만 임시로 저장
//     private FrameData? latestFrame = null;

//     private struct FrameData
//     {
//         public Vector3[] vertices;
//         public Color[] colors;
//         public Matrix4x4 transform;
//     }

//     private readonly object dataLock = new object();
//     private int[] globalIndices;

//     void Start()
//     {
//         listener = GetComponent<Ros2PointCloudListener>();
//         dsrPoseListener = GetComponent<DsrPoseListener>();
        
//         if (listener == null || dsrPoseListener == null)
//         {
//             UnityEngine.Debug.LogError("Required components not found.");
//             this.enabled = false;
//             return;
//         }
        
//         GameObject mapObject = new GameObject("GlobalMap");
//         globalMapMeshFilter = mapObject.AddComponent<MeshFilter>();
//         MeshRenderer renderer = mapObject.AddComponent<MeshRenderer>();
//         renderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

//         globalMapMesh = new Mesh();
//         globalMapMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
//         globalMapMeshFilter.mesh = globalMapMesh;

//         globalVertices = new Vector3[MaxTotalPoints];
//         globalColors = new Color[MaxTotalPoints];
//         globalIndices = new int[MaxTotalPoints];
//         for (int i = 0; i < MaxTotalPoints; i++)
//         {
//             globalIndices[i] = i;
//         }

//         listener.OnPointCloudReceived += StoreLatestFrame;
//         lastMergeTime = Time.time;
//         UnityEngine.Debug.Log("Global Map Builder is ready. Max points set to " + MaxTotalPoints);
//     }
    
//     // ⭐⭐ 수정된 메서드: 큐에 넣는 대신 최신 프레임만 저장
//     private void StoreLatestFrame(Vector3[] newVertices, Color[] newColors)
//     {
//         Vector3 position;
//         Quaternion rotation;

//         if (dsrPoseListener.TryGetLatestPose(out position, out rotation))
//         {
//             lock(dataLock)
//             {
//                 latestFrame = new FrameData
//                 {
//                     vertices = newVertices,
//                     colors = newColors,
//                     transform = Matrix4x4.TRS(position, rotation, Vector3.one)
//                 };
//             }
//         }
//     }

//     // ⭐⭐⭐ 수정된 부분: Update()에서 3초마다 최신 프레임만 병합
//     void Update()
//     {
//         // 렌더링은 매 프레임마다 계속 수행
//         if (currentPointCount > 0)
//         {
//             var stopwatch = Stopwatch.StartNew();
//             globalMapMesh.Clear();
//             globalMapMesh.SetVertices(globalVertices);
//             globalMapMesh.SetColors(globalColors);
//             globalMapMesh.SetIndices(globalIndices, 0, currentPointCount, MeshTopology.Points, 0, false);
//             globalMapMesh.RecalculateBounds();
//             stopwatch.Stop();
//             UnityEngine.Debug.Log($"[Profile] GlobalMapBuilder.Update (렌더링) took {stopwatch.Elapsed.TotalMilliseconds:F2} ms. Rendered points: {currentPointCount}");
//         }

//         // ⭐⭐⭐ 3초가 지났고 최신 프레임 데이터가 있으면 병합 시작
//         if (Time.time - lastMergeTime >= mergeInterval && latestFrame.HasValue)
//         {
//             var stopwatch = Stopwatch.StartNew();
//             FrameData frameToMerge;
            
//             lock(dataLock)
//             {
//                 frameToMerge = latestFrame.Value;
//                 latestFrame = null; // 최신 프레임을 사용했으므로 비움
//             }
            
//             if (isFirstFrame)
//             {
//                 InitializeMap(frameToMerge.vertices, frameToMerge.colors, frameToMerge.transform);
//                 isFirstFrame = false;
//             }
//             else
//             {
//                 MergeNewPoints(frameToMerge.vertices, frameToMerge.colors, frameToMerge.transform);
//             }
            
//             lastMergeTime = Time.time;
//             stopwatch.Stop();
//             UnityEngine.Debug.Log($"[Profile] GlobalMapBuilder.ProcessLatestFrame took {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
//         }
//     }

//     // ... (InitializeMap, MergeNewPoints, GetCellIndex 등 기존 메서드는 동일)
//     private void InitializeMap(Vector3[] newVertices, Color[] newColors, Matrix4x4 transform)
//     {
//         lock(dataLock)
//         {
//             int n = Mathf.Min(newVertices.Length, MaxTotalPoints);
//             for(int i = 0; i < n; i++)
//             {
//                 Vector3 transformedPoint = transform.MultiplyPoint3x4(newVertices[i]);
//                 globalVertices[i] = transformedPoint;
//                 globalColors[i] = newColors[i];
//                 AddPointToGrid(transformedPoint, i);
//             }
//             currentIndex = n % MaxTotalPoints;
//             currentPointCount = n;
//             isFirstFrame = false;
//             UnityEngine.Debug.Log($"Initial map created with {currentPointCount} points.");
//         }
//     }

//     // GlobalMapBuilder.cs
// private void MergeNewPoints(Vector3[] newVertices, Color[] newColors, Matrix4x4 transform)
// {
//     // ⭐⭐ 이 메서드 내부에서 mergedCount를 선언하고 사용
//     int mergedCount = 0;
//     for (int i = 0; i < newVertices.Length; i++)
//     {
//         Vector3 transformedPoint = transform.MultiplyPoint3x4(newVertices[i]);
        
//         int closestIndex = FindClosestPointIndex(transformedPoint);
        
//         if (closestIndex != -1)
//         {
//             // 중복 포인트가 있으면 업데이트
//             globalVertices[closestIndex] = transformedPoint;
//             globalColors[closestIndex] = newColors[i];
//             UpdatePointInGrid(transformedPoint, closestIndex);
//         }
//         else
//         {
//             if (currentPointCount == MaxTotalPoints)
//             {
//                 int oldPointIndex = currentIndex;
//                 Vector3 oldPoint = globalVertices[oldPointIndex];
//                 RemovePointFromGrid(oldPoint, oldPointIndex);
//             }

//             globalVertices[currentIndex] = transformedPoint;
//             globalColors[currentIndex] = newColors[i];
            
//             AddPointToGrid(transformedPoint, currentIndex);
            
//             currentIndex = (currentIndex + 1) % MaxTotalPoints;
//             if (currentPointCount < MaxTotalPoints)
//             {
//                 currentPointCount++;
//             }
//             mergedCount++;
//         }
//     }
    
//     // ⭐⭐ mergedCount 변수가 유효한 이 메서드 내에서 로그를 출력
//     UnityEngine.Debug.Log($"[Merging] Merged {mergedCount} new points. Total points: {currentPointCount}");
// }
    
//     // ... (이하 모든 헬퍼 메서드들은 동일)
//     private Vector3Int GetCellIndex(Vector3 point)
//     {
//         return new Vector3Int(
//             Mathf.FloorToInt(point.x / cellSize),
//             Mathf.FloorToInt(point.y / cellSize),
//             Mathf.FloorToInt(point.z / cellSize)
//         );
//     }
    
//     private void AddPointToGrid(Vector3 point, int index)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         if (!pointGrid.ContainsKey(cellIndex))
//         {
//             pointGrid[cellIndex] = new List<int>();
//         }
//         pointGrid[cellIndex].Add(index);
//     }
    
//     private void RemovePointFromGrid(Vector3 point, int index)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         if (pointGrid.ContainsKey(cellIndex))
//         {
//             pointGrid[cellIndex].Remove(index);
//             if (pointGrid[cellIndex].Count == 0)
//             {
//                 pointGrid.Remove(cellIndex);
//             }
//         }
//     }

//     private void UpdatePointInGrid(Vector3 point, int index)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         if (pointGrid.ContainsKey(cellIndex))
//         {
//             pointGrid[cellIndex].Remove(index);
//         }
//         AddPointToGrid(point, index);
//     }
    
//     private int FindClosestPointIndex(Vector3 point)
//     {
//         Vector3Int cellIndex = GetCellIndex(point);
//         float minDistanceSqr = mergeDistanceThreshold * mergeDistanceThreshold;
//         int closestIndex = -1;

//         for (int x = -1; x <= 1; x++)
//         {
//             for (int y = -1; y <= 1; y++)
//             {
//                 for (int z = -1; z <= 1; z++)
//                 {
//                     Vector3Int neighborCellIndex = cellIndex + new Vector3Int(x, y, z);
                    
//                     if (pointGrid.ContainsKey(neighborCellIndex))
//                     {
//                         foreach (var existingIndex in pointGrid[neighborCellIndex])
//                         {
//                             float distSqr = Vector3.SqrMagnitude(point - globalVertices[existingIndex]);
//                             if (distSqr < minDistanceSqr)
//                             {
//                                 minDistanceSqr = distSqr;
//                                 closestIndex = existingIndex;
//                             }
//                         }
//                     }
//                 }
//             }
//         }
//         return closestIndex;
//     }
// }