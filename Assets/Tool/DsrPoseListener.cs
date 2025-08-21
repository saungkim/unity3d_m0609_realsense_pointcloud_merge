
using UnityEngine;
using ROS2;
using std_msgs.msg;
using System;


/// 단순화 버전(완전체): 위치 변환(Ros→Unity) + 회전(ZYZ/XYZ을 ROS축으로 합성하여 Unity 쿼터니언 생성)
/// - 위치: Robotec Transformations 사용 → v_u = (-y, z, x)  (p.Ros2Unity())
/// - 회전: 입력 각도(a,b,c)를 ROS축 기준으로 해석하여 Unity에서 직접 합성 (2중 변환 방지)
/// - 카메라 포즈 제공: TryGetLatestCameraPose()
public class DsrPoseListener : MonoBehaviour
{
    // ===================== Inspector =====================
    [Header("Input & Conventions")]
    [Tooltip("입력 위치가 mm 이면 true (mm→m 변환)")]
    public bool inputInMillimeters = true;

    [Tooltip("TCP 위치에 Robotec 변환(Ros→Unity: -y,z,x) 적용")]
    public bool applyRosPosAxisMapping = true;

    [Tooltip("TCP 각도 a,b,c가 ZYZ(α,β,γ)이면 true, XYZ이면 false")]
    public bool tcpAnglesAreZYZ = true;

    [Header("Camera Extrinsic (relative to TCP)")]
    [Tooltip("TCP→Camera 위치 오프셋 (Unity 좌표, m)")]
    public Vector3 camPosOffset = Vector3.zero;

    [Tooltip("TCP→Camera 회전 오프셋 (deg)")]
    public Vector3 camRotOffsetEuler = Vector3.zero;

    [Tooltip("Extrinsic 각도 순서: true=ZYZ, false=XYZ")]
    public bool extrinsicAnglesAreZYZ = false;

    [Tooltip("Extrinsic 각도를 ROS축 기준으로 해석할지 (기본 false=Unity기준)")]
    public bool extrinsicAnglesInROS = false;

    [Header("Logging")]
    public bool logEveryMsg = true;

    // ===================== Internals =====================
    private ROS2UnityComponent ros2Unity;
    private ROS2Node ros2Node;
    private ISubscription<Float64MultiArray> sub;

    private struct PoseData
    {
        public Vector3 tcpPosU;
        public Quaternion tcpRotU;
        public Vector3 camPosU;
        public Quaternion camRotU;
        public bool hasNew;
    }
    private PoseData latest;
    private readonly object _lock = new object();

    // ROS 축을 Unity 기준으로 표현
    //   x_r → +Z_u,  y_r → -X_u,  z_r → +Y_u
    private static readonly Vector3 ROS_X_inU = Vector3.forward;
    private static readonly Vector3 ROS_Y_inU = -Vector3.right;
    private static readonly Vector3 ROS_Z_inU = Vector3.up;

    // ===================== Unity Lifecycle =====================
    void Start()
    {
        ros2Unity = GetComponent<ROS2UnityComponent>();
        if (ros2Unity == null || !ros2Unity.Ok())
        {
            Debug.LogError("ROS2UnityComponent not ready.");
            return;
        }

        ros2Node = ros2Unity.CreateNode("unity_dsr_pose_listener_simple");
        sub = ros2Node.CreateSubscription<Float64MultiArray>("/dsr01/msg/current_posx", OnMsg);
        Debug.Log("[DSR] Subscribed: /dsr01/msg/current_posx");
    }

    private void OnDestroy()
    {
        try { sub?.Dispose(); } catch { }
        sub = null;
        ros2Node = null;
    }

    // ===================== ROS Callback =====================
    private void OnMsg(Float64MultiArray msg)
    {
        if (msg.Data.Length < 6) return;

        // 1) 위치 (단위 보정)
        float px = (float)msg.Data[0];
        float py = (float)msg.Data[1];
        float pz = (float)msg.Data[2];
        if (inputInMillimeters) { px *= 0.001f; py *= 0.001f; pz *= 0.001f; }

        Vector3 p = new Vector3(px, py, pz);
        if (applyRosPosAxisMapping)
            p = p.Ros2Unity(); // Robotec 변환: (-y, z, x)

        // 2) 회전 (ROS 기준 Euler → Unity Quaternion)
        float a = (float)msg.Data[3];
        float b = (float)msg.Data[4];
        float c = (float)msg.Data[5];
        Quaternion q = tcpAnglesAreZYZ
            ? RosZYZDeg_ToUnityQuat(a, b, c)
            : RosXYZDeg_ToUnityQuat(a, b, c);

        // 3) Extrinsic (TCP→Camera)
        Quaternion qOff;
        if (extrinsicAnglesAreZYZ)
        {
            qOff = extrinsicAnglesInROS
                ? RosZYZDeg_ToUnityQuat(camRotOffsetEuler.x, camRotOffsetEuler.y, camRotOffsetEuler.z)
                : (Quaternion.AngleAxis(camRotOffsetEuler.x, Vector3.forward)
                 * Quaternion.AngleAxis(camRotOffsetEuler.y, Vector3.up)
                 * Quaternion.AngleAxis(camRotOffsetEuler.z, Vector3.forward));
        }
        else
        {
            qOff = extrinsicAnglesInROS
                ? RosXYZDeg_ToUnityQuat(camRotOffsetEuler.x, camRotOffsetEuler.y, camRotOffsetEuler.z)
                : Quaternion.Euler(camRotOffsetEuler);
        }

        Matrix4x4 T_tcp   = Matrix4x4.TRS(p, q, Vector3.one);
        Matrix4x4 T_cam   = Matrix4x4.TRS(camPosOffset, qOff, Vector3.one);
        Matrix4x4 T_world = T_tcp * T_cam;

        Vector3 camPos = T_world.MultiplyPoint3x4(Vector3.zero);
        Quaternion camRot = Quaternion.LookRotation(
            T_world.MultiplyVector(Vector3.forward),
            T_world.MultiplyVector(Vector3.up)
        );

        lock (_lock)
        {
            latest.tcpPosU = p;
            latest.tcpRotU = q;
            latest.camPosU = camPos;
            latest.camRotU = camRot;
            latest.hasNew  = true;
        }

        if (logEveryMsg)
        {
            Debug.Log($"[DSR/Simple] TCP pos={p:F3}, rot(eul)={q.eulerAngles:F1} | CAM pos={camPos:F3}, rot(eul)={camRot.eulerAngles:F1}");
        }
    }

    // ===================== Public Getters =====================
    public bool TryGetLatestPose(out Vector3 position, out Quaternion rotation)
    {
        lock (_lock)
        {
            if (latest.hasNew)
            {
                position = latest.tcpPosU;
                rotation = latest.tcpRotU;
                return true;
            }
        }
        position = Vector3.zero;
        rotation = Quaternion.identity;
        return false;
    }

    public bool TryGetLatestCameraPose(out Vector3 position, out Quaternion rotation)
    {
        lock (_lock)
        {
            if (latest.hasNew)
            {
                position = latest.camPosU;
                rotation = latest.camRotU;
                return true;
            }
        }
        position = Vector3.zero;
        rotation = Quaternion.identity;
        return false;
    }

    // ===================== Helpers (ROS-axis rotations) =====================
    /// ROS ZYZ(α,β,γ)[deg] → Unity Quaternion (Rz(α) * Ry(β) * Rz(γ) w.r.t. ROS axes)
    private static Quaternion RosZYZDeg_ToUnityQuat(float alphaDeg, float betaDeg, float gammaDeg)
    {
        var qa = Quaternion.AngleAxis(alphaDeg, ROS_Z_inU);
        var qb = Quaternion.AngleAxis(betaDeg,  ROS_Y_inU);
        var qc = Quaternion.AngleAxis(gammaDeg, ROS_Z_inU);
        return qa * qb * qc;
    }

    /// ROS XYZ(α,β,γ)[deg] → Unity Quaternion (Rx(α) * Ry(β) * Rz(γ) w.r.t. ROS axes)
    private static Quaternion RosXYZDeg_ToUnityQuat(float alphaDeg, float betaDeg, float gammaDeg)
    {
        var qa = Quaternion.AngleAxis(alphaDeg, ROS_X_inU);
        var qb = Quaternion.AngleAxis(betaDeg,  ROS_Y_inU);
        var qc = Quaternion.AngleAxis(gammaDeg, ROS_Z_inU);
        return qa * qb * qc;
    }
}
