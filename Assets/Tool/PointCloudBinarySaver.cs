// PointCloudBinarySaver.cs
// - Source 선택: CurrentFrame / GlobalChunksMerged / GlobalChunksSplit
// - B 키로 저장(trigger). saveWorldSpace, includeVertexColors 적용
// - 경로: Application.persistentDataPath/pcd_*.bin
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PointCloudBinarySaver : MonoBehaviour
{
    public enum SourceKind { CurrentFrame, GlobalChunksMerged, GlobalChunksSplit }

    [Header("Source")]
    [Tooltip("소스를 찾을 루트(비우면 이 컴포넌트의 transform 아래에서 검색)")]
    public Transform rootForSearch;

    [Tooltip("저장 소스 선택")]
    public SourceKind sourceKind = SourceKind.CurrentFrame;

    [Tooltip("Ros2PointCloudListener 기본 미리보기 오브젝트명")]
    public string currentFrameName = "CurrentFrame";

    [Tooltip("Ros2PointCloudListener 머지 청크 부모 오브젝트명")]
    public string globalParentName = "GlobalChunks";

    [Tooltip("비활성화된 청크도 포함할지 (GlobalChunks에서만)")]
    public bool includeInactiveChunks = true;

    [Header("Save Options")]
    [Tooltip("true=월드좌표, false=로컬좌표(소스 기준)로 저장")]
    public bool saveWorldSpace = false;

    [Tooltip("정점 색상을 함께 저장 (없으면 흰색으로 채움)")]
    public bool includeVertexColors = true;

    [Tooltip("파일 이름 접두어")]
    public string filePrefix = "pcd";

    [Tooltip("키(B)를 눌러 저장")]
    public Key saveKey = Key.B;

    // input action
    private InputAction saveAction;

    void Awake()
    {
        // ex) <Keyboard>/b
        string keyName = saveKey.ToString().ToLower();
        saveAction = new InputAction("SavePCDBin", binding: $"<Keyboard>/{keyName}");
        saveAction.performed += _ => TrySave();
        saveAction.Enable();
    }

    void OnDisable()
    {
        try { saveAction?.Disable(); } catch { }
    }

    // 수동 호출용
    public void TrySave()
    {
        try
        {
            switch (sourceKind)
            {
                case SourceKind.CurrentFrame:
                    SaveCurrentFrame();
                    break;
                case SourceKind.GlobalChunksMerged:
                    SaveGlobalChunksMerged();
                    break;
                case SourceKind.GlobalChunksSplit:
                    SaveGlobalChunksSplit();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PCD Save] Failed: {ex}");
        }
    }

    // =============== CurrentFrame ===============
    private void SaveCurrentFrame()
    {
        Transform root = (rootForSearch != null) ? rootForSearch : transform;
        Transform t = FindDeep(root, currentFrameName);
        if (t == null)
        {
            Debug.LogWarning($"[PCD Save] '{currentFrameName}' not found.");
            return;
        }

        var mf = t.GetComponent<MeshFilter>();
        var mesh = mf ? mf.sharedMesh : null;
        if (!mesh || mesh.vertexCount == 0)
        {
            Debug.LogWarning("[PCD Save] CurrentFrame has no mesh/verts.");
            return;
        }

        Vector3[] vtx = mesh.vertices;             // local (CurrentFrame)
        Color32[] col = MeshColorsOrWhite(mesh);   // same length, filled with white if missing

        Matrix4x4 T_world = t.localToWorldMatrix;

        // transform points according to saveWorldSpace
        Vector3[] outV = vtx;
        if (saveWorldSpace)
        {
            outV = new Vector3[vtx.Length];
            for (int i = 0; i < vtx.Length; i++)
                outV[i] = T_world.MultiplyPoint3x4(vtx[i]);
        }

        string path = MakePath($"{filePrefix}_current_{NowTS()}.bin");
        WriteUPCD(path, outV, col, saveWorldSpace ? 1 : 0, includeVertexColors, T_world);
        Debug.Log($"[PCD Save] CurrentFrame → {path}  (N={outV.Length}, world={saveWorldSpace})");
    }

    // =============== GlobalChunks (Merged) ===============
    private void SaveGlobalChunksMerged()
    {
        Transform root = (rootForSearch != null) ? rootForSearch : transform;
        Transform parent = FindDeep(root, globalParentName);
        if (parent == null)
        {
            Debug.LogWarning($"[PCD Save] '{globalParentName}' not found.");
            return;
        }

        var mfs = parent.GetComponentsInChildren<MeshFilter>(includeInactiveChunks);
        if (mfs == null || mfs.Length == 0)
        {
            Debug.LogWarning("[PCD Save] No MeshFilter found under GlobalChunks.");
            return;
        }

        var vertsList = new List<Vector3>(65536);
        var colsList  = new List<Color32>(65536);

        Matrix4x4 toParentLocal = parent.worldToLocalMatrix;

        foreach (var mf in mfs)
        {
            if (!mf || !mf.sharedMesh) continue;
            var mesh = mf.sharedMesh;
            var v = mesh.vertices;
            if (v == null || v.Length == 0) continue;

            var cc = MeshColorsOrWhite(mesh);
            Matrix4x4 toWorld = mf.transform.localToWorldMatrix;

            for (int i = 0; i < v.Length; i++)
            {
                Vector3 worldP = toWorld.MultiplyPoint3x4(v[i]);
                Vector3 outP   = saveWorldSpace ? worldP
                                                : toParentLocal.MultiplyPoint3x4(worldP); // parent local
                vertsList.Add(outP);
                colsList.Add(cc[i]);
            }
        }

        if (vertsList.Count == 0)
        {
            Debug.LogWarning("[PCD Save] GlobalChunks has 0 verts.");
            return;
        }

        Vector3[] outV = vertsList.ToArray();
        Color32[] outC = colsList.ToArray();

        Matrix4x4 T_worldHeader = parent.localToWorldMatrix; // header용
        string path = MakePath($"{filePrefix}_merged_{NowTS()}.bin");
        WriteUPCD(path, outV, outC, saveWorldSpace ? 1 : 0, includeVertexColors, T_worldHeader);
        Debug.Log($"[PCD Save] GlobalChunks(MERGED) → {path} (N={outV.Length}, world={saveWorldSpace})");
    }

    // =============== GlobalChunks (Split each chunk) ===============
    private void SaveGlobalChunksSplit()
    {
        Transform root = (rootForSearch != null) ? rootForSearch : transform;
        Transform parent = FindDeep(root, globalParentName);
        if (parent == null)
        {
            Debug.LogWarning($"[PCD Save] '{globalParentName}' not found.");
            return;
        }

        var mfs = parent.GetComponentsInChildren<MeshFilter>(includeInactiveChunks);
        if (mfs == null || mfs.Length == 0)
        {
            Debug.LogWarning("[PCD Save] No MeshFilter found under GlobalChunks.");
            return;
        }

        int saved = 0;
        foreach (var mf in mfs)
        {
            if (!mf || !mf.sharedMesh) continue;
            var mesh = mf.sharedMesh;
            var v = mesh.vertices;
            if (v == null || v.Length == 0) continue;

            var cc = MeshColorsOrWhite(mesh);

            Matrix4x4 T_world = mf.transform.localToWorldMatrix;

            Vector3[] outV = new Vector3[v.Length];
            if (saveWorldSpace)
            {
                for (int i = 0; i < v.Length; i++)
                    outV[i] = T_world.MultiplyPoint3x4(v[i]); // world
            }
            else
            {
                // 로컬 = 이 청크 오브젝트의 로컬
                for (int i = 0; i < v.Length; i++)
                    outV[i] = v[i]; // already local
            }

            // 파일명: pcd_chunk_<chunkName>_<time>.bin
            string safeName = SafeFileName(mf.name);
            string path = MakePath($"{filePrefix}_chunk_{safeName}_{NowTS()}.bin");
            WriteUPCD(path, outV, cc, saveWorldSpace ? 1 : 0, includeVertexColors, T_world);
            saved++;
            Debug.Log($"[PCD Save] Chunk '{mf.name}' → {path} (N={outV.Length}, world={saveWorldSpace})");
        }

        Debug.Log($"[PCD Save] GlobalChunks(SPLIT): {saved} files saved.");
    }

    // =============== UPCD I/O ===============
    // 포맷:
    // magic 'UPCD' (4)
    // version uint16 (1)
    // coordSpace uint8 (0=local,1=world)
    // hasColor  uint8 (0/1)  -- header가 1이면 모든 점에 r,g,b 바이트를 씀(없던 점은 흰색으로 채움)
    // pointCount uint32
    // T_world float32[16] (row-major)
    // records:
    //   if hasColor: (float32 x,y,z, uint8 r,g,b) * N
    //   else:        (float32 x,y,z) * N
    private void WriteUPCD(string path, Vector3[] pts, Color32[] colors,
                           int coordSpaceFlag, bool includeColors, Matrix4x4 T_world)
    {
        bool hasColor = includeColors && colors != null && colors.Length == pts.Length;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs); // little-endian

        // header
        bw.Write(new byte[] { (byte)'U', (byte)'P', (byte)'C', (byte)'D' });
        bw.Write((ushort)1);
        bw.Write((byte)coordSpaceFlag);
        bw.Write((byte)(hasColor ? 1 : 0));
        bw.Write((uint)pts.Length);
        WriteMatrix4x4(bw, T_world);

        // records
        if (hasColor)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 v = pts[i];
                bw.Write(v.x); bw.Write(v.y); bw.Write(v.z);
                Color32 c = colors[i];
                bw.Write(c.r); bw.Write(c.g); bw.Write(c.b);
            }
        }
        else
        {
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 v = pts[i];
                bw.Write(v.x); bw.Write(v.y); bw.Write(v.z);
            }
        }
    }

    private static Color32[] MeshColorsOrWhite(Mesh mesh)
    {
        var c32 = mesh.colors32;
        int n = mesh.vertexCount;
        if (c32 == null || c32.Length != n)
        {
            c32 = new Color32[n];
            for (int i = 0; i < n; i++) c32[i] = new Color32(255, 255, 255, 255);
        }
        return c32;
    }

    private static void WriteMatrix4x4(BinaryWriter bw, Matrix4x4 m)
    {
        // row-major dump (m00..m03, m10..m13, m20..m23, m30..m33)
        bw.Write(m.m00); bw.Write(m.m01); bw.Write(m.m02); bw.Write(m.m03);
        bw.Write(m.m10); bw.Write(m.m11); bw.Write(m.m12); bw.Write(m.m13);
        bw.Write(m.m20); bw.Write(m.m21); bw.Write(m.m22); bw.Write(m.m23);
        bw.Write(m.m30); bw.Write(m.m31); bw.Write(m.m32); bw.Write(m.m33);
    }

    // =============== Utils ===============
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

    private static string NowTS() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

    private static string MakePath(string file)
    {
        string dir = Application.persistentDataPath;
        return Path.Combine(dir, file);
    }

    private static string SafeFileName(string s)
    {
        foreach (char ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s;
    }
}
