// FlyCameraInput.cs
// Unity Input System (New) 기반 씬뷰 스타일 플라이 카메라
// Controls:
//  - RMB 홀드: 마우스 보기(회전), 커서 잠금
//  - WASD: 전후좌우 이동 (카메라 기준)
//  - Q/E: 상승/하강 (월드 Y 기준)
//  - Mouse Wheel: 기본 속도 가감
//  - Shift: 가속  /  Alt: 감속
//  - 옵션: requireRightMouse=false 면 RMB 없이 항상 이동/회전

using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class FlyCameraInput : MonoBehaviour
{
    [Header("Look")]
    public bool requireRightMouse = true;
    public bool invertY = false;
    [Tooltip("마우스 감도 (deg/pixel 비슷한 체감)")]
    public float lookSensitivity = 0.15f;
    [Tooltip("피치 제한(상하 회전)")]
    public float pitchMin = -89f, pitchMax = 89f;

    [Header("Move")]
    [Tooltip("기본 이동 속도 (m/s)")]
    public float baseSpeed = 5f;
    [Tooltip("휠로 조절되는 배율의 민감도")]
    public float wheelSpeedAccel = 0.05f;
    [Tooltip("가속/감속 배수")]
    public float fastMultiplier = 3f, slowMultiplier = 0.25f;
    [Tooltip("휠로 조절 배율의 범위")]
    public Vector2 speedScalarRange = new Vector2(0.05f, 100f);

    [Header("Quality")]
    [Tooltip("회전 스무딩 (0=즉시)")]
    [Range(0f, 1f)] public float lookSmoothing = 0.0f;
    [Tooltip("이동 스무딩 (0=즉시)")]
    [Range(0f, 1f)] public float moveSmoothing = 0.0f;

    // Input Actions
    private InputAction moveAct;     // WASD -> Vector2
    private InputAction upDownAct;   // Q/E   -> float (-1..+1)
    private InputAction lookAct;     // Mouse delta -> Vector2
    private InputAction rmbAct;      // RMB -> Button
    private InputAction scrollAct;   // Wheel -> Vector2 (y)
    private InputAction boostAct;    // Shift
    private InputAction slowAct;     // Alt

    // State
    private float yaw, pitch;
    private float speedScalar = 1f;
    private Vector3 moveVel; // smoothed
    private Vector2 lookVel; // smoothed

    void Awake()
    {
        // 초기 오일러 저장
        var e = transform.rotation.eulerAngles;
        yaw = e.y; pitch = WrapPitch(e.x);

        // --- Create InputActions in code (에셋 없이 동작) ---
        moveAct   = new InputAction("Move", InputActionType.Value);
        moveAct.AddCompositeBinding("2DVector")
               .With("Up",    "<Keyboard>/w")
               .With("Down",  "<Keyboard>/s")
               .With("Left",  "<Keyboard>/a")
               .With("Right", "<Keyboard>/d");

        upDownAct = new InputAction("UpDown", InputActionType.Value);
        upDownAct.AddCompositeBinding("1DAxis")
                 .With("Negative", "<Keyboard>/q")
                 .With("Positive", "<Keyboard>/e");
        // 추가 키 (원하면 주석 해제)
        // upDownAct.AddCompositeBinding("1DAxis")
        //          .With("Negative", "<Keyboard>/leftCtrl")
        //          .With("Positive", "<Keyboard>/space");

        lookAct   = new InputAction("Look",   InputActionType.Value, "<Mouse>/delta");
        rmbAct    = new InputAction("RMB",    InputActionType.Button, "<Mouse>/rightButton");
        scrollAct = new InputAction("Scroll", InputActionType.Value, "<Mouse>/scroll");

        boostAct  = new InputAction("Boost",  InputActionType.Button, "<Keyboard>/leftShift");
        slowAct   = new InputAction("Slow",   InputActionType.Button, "<Keyboard>/leftAlt");
    }

    void OnEnable()
    {
        moveAct.Enable();
        upDownAct.Enable();
        lookAct.Enable();
        rmbAct.Enable();
        scrollAct.Enable();
        boostAct.Enable();
        slowAct.Enable();
    }

    void OnDisable()
    {
        moveAct.Disable();
        upDownAct.Disable();
        lookAct.Disable();
        rmbAct.Disable();
        scrollAct.Disable();
        boostAct.Disable();
        slowAct.Disable();
        // 커서 복구
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    void Update()
    {
        // === Cursor lock ===
        bool aiming = requireRightMouse ? rmbAct.IsPressed() : true;
        if (aiming)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        // === Speed scalar by wheel ===
        float wheelY = scrollAct.ReadValue<Vector2>().y;
        if (Mathf.Abs(wheelY) > 0.01f)
        {
            // 지수형 가중: 작은 휠에도 부드러운 배율 변화
            speedScalar *= Mathf.Exp(wheelY * wheelSpeedAccel);
            speedScalar = Mathf.Clamp(speedScalar, speedScalarRange.x, speedScalarRange.y);
        }

        // === Look ===
        if (aiming)
        {
            Vector2 raw = lookAct.ReadValue<Vector2>();
            Vector2 target = raw * lookSensitivity * (invertY ? new Vector2(1f, 1f) : new Vector2(1f, -1f));
            lookVel = Vector2.Lerp(lookVel, target, 1f - lookSmoothing);
            yaw   += lookVel.x;
            pitch += lookVel.y;
            pitch  = Mathf.Clamp(pitch, pitchMin, pitchMax);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            lookVel = Vector2.zero;
        }

        // === Move ===
        Vector2 planar = aiming ? moveAct.ReadValue<Vector2>() : Vector2.zero;
        float ud = aiming ? upDownAct.ReadValue<float>() : 0f;

        float speed = baseSpeed * speedScalar;
        if (boostAct.IsPressed()) speed *= fastMultiplier;
        if (slowAct.IsPressed())  speed *= slowMultiplier;

        // 전후/좌우는 카메라 기준, 상승/하강은 월드 Y 기준(씬뷰 스타일)
        Vector3 wish =
            transform.forward * planar.y +
            transform.right   * planar.x +
            Vector3.up        * ud;

        Vector3 targetVel = wish.normalized * speed;
        moveVel = Vector3.Lerp(moveVel, targetVel, 1f - moveSmoothing);
        transform.position += moveVel * Time.unscaledDeltaTime; // 편집 모드/정지 중에도 자연스럽게
    }

    private static float WrapPitch(float x)
    {
        // Unity euler x는 0..360, 씬뷰 감각 위해 -180..180 → clamp
        x = Mathf.Repeat(x + 180f, 360f) - 180f;
        return x;
    }
}
