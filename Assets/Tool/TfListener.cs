using UnityEngine;
using System.Collections.Generic;
using ROS2;
using tf2_msgs.msg;
using System.Threading; // 스레드 관련 클래스 사용을 위해 추가

public class TfListener : MonoBehaviour
{
    private ROS2UnityComponent ros2Unity;
    private ROS2Node ros2Node;
    private ISubscription<TFMessage> tfSubscription;

    // 최신 tf 변환 정보를 저장하는 딕셔너리
    private Dictionary<string, TransformData> latestTfTransforms = new Dictionary<string, TransformData>();
    private readonly object tfLock = new object();

    // 메인 스레드에서 사용할 변환 데이터 구조체
    private struct TransformData
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    void Start()
    {
        ros2Unity = GetComponent<ROS2UnityComponent>();
        if (ros2Unity == null || !ros2Unity.Ok())
        {
            Debug.LogError("ROS2UnityComponent not ready.");
            return;
        }

        if (ros2Node == null)
        {
            ros2Node = ros2Unity.CreateNode("unity_tf_listener");
            tfSubscription = ros2Node.CreateSubscription<TFMessage>("/tf", OnTfMessage);
            Debug.Log("Subscribed to /tf topic.");
        }
    }

    private void OnTfMessage(TFMessage msg)
    {
        if (msg.Transforms == null) return;

        lock (tfLock)
        {
            foreach (var transformStamped in msg.Transforms)
            {
                string key = $"{transformStamped.Header.Frame_id}/{transformStamped.Child_frame_id}";

                // ROS Transform을 Unity TransformData 구조체로 변환
                Vector3 position = new Vector3(
                    (float)transformStamped.Transform.Translation.X,
                    (float)transformStamped.Transform.Translation.Y,
                    (float)transformStamped.Transform.Translation.Z
                );
                Quaternion rotation = new Quaternion(
                    (float)transformStamped.Transform.Rotation.X,
                    (float)transformStamped.Transform.Rotation.Y,
                    (float)transformStamped.Transform.Rotation.Z,
                    (float)transformStamped.Transform.Rotation.W
                );

                // ROS (x-forward, y-left, z-up) -> Unity (x-right, y-up, z-forward)
                Vector3 unityPosition = new Vector3(-position.y, position.z, position.x);
                Quaternion unityRotation = new Quaternion(rotation.x, -rotation.z, -rotation.y, rotation.w);

                // TransformData 구조체에 저장
                TransformData tfData = new TransformData
                {
                    position = unityPosition,
                    rotation = unityRotation
                };

                // 스레드 안전하게 딕셔너리에 저장
                latestTfTransforms[key] = tfData;
            }
        }
    }

    // 메인 스레드에서 호출할 메서드
    public bool TryGetTransform(string parentFrame, string childFrame, out Vector3 position, out Quaternion rotation)
    {
        lock (tfLock)
        {
            string key = $"{parentFrame}/{childFrame}";
            TransformData tfData;
            if (latestTfTransforms.TryGetValue(key, out tfData))
            {
                position = tfData.position;
                rotation = tfData.rotation;
                return true;
            }
        }
        position = Vector3.zero;
        rotation = Quaternion.identity;
        return false;
    }
}