using UnityEngine;
using Photon.Pun;

/// <summary>
/// 타깃(플레이어) 뒤에서 따라오는 3인칭 카메라
/// - 타깃의 Yaw를 따라감(배틀그라운드식 백뷰)
/// - 부드러운 보간 + 벽 충돌 시 카메라 당김
/// - 로컬 소유자일 때만 활성 (PUN)
/// </summary>
public class ThirdPersonFollowCamera : MonoBehaviourPun
{
    [Header("Target")]
    [SerializeField] private Transform target;          // 기본: this.transform

    [Header("Orbit")]
    [SerializeField] private float distance = 4.5f;     // 뒤로 거리
    [SerializeField] private float height = 2.0f;       // 위로 높이
    [SerializeField] private float followLerp = 12f;    // 위치 보간
    [SerializeField] private float yawLerp = 12f;       // 회전 보간
    [SerializeField] private Vector3 lookAtOffset = new Vector3(0, 1.3f, 0);

    [Header("Collision")]
    [SerializeField] private float clipSphereRadius = 0.2f;
    [SerializeField] private LayerMask clipMask = ~0;

    private Transform _cam;
    private float _currentYaw; // 현 카메라 yaw(부드럽게 타깃 yaw에 수렴)

    void Awake()
    {
        if (target == null) target = transform;

        // 로컬 소유자가 아니면 카메라 제어 비활성
        if (photonView && !photonView.IsMine)
        {
            enabled = false;
        }
    }

    void Start()
    {
        _cam = Camera.main ? Camera.main.transform : null;
        if (_cam == null)
        {
            // 씬 카메라가 늦게 생기는 경우, 다음 프레임에 다시 시도
            StartCoroutine(FindCameraNextFrame());
        }

        // 시작 각도는 타깃의 Yaw로
        _currentYaw = target.eulerAngles.y;
        SnapToIdeal();
    }

    System.Collections.IEnumerator FindCameraNextFrame()
    {
        yield return null;
        _cam = Camera.main ? Camera.main.transform : null;
    }

    void LateUpdate()
    {
        if (_cam == null || target == null) return;

        // 타깃의 Yaw로 수렴
        float targetYaw = target.eulerAngles.y;
        _currentYaw = Mathf.LerpAngle(_currentYaw, targetYaw, Time.deltaTime * yawLerp);

        // 이상적 위치(타깃 뒤·위)
        Vector3 idealOffset = Quaternion.Euler(0f, _currentYaw, 0f) * new Vector3(0f, height, -distance);
        Vector3 idealPos = target.position + idealOffset;

        // 충돌 보정: 타깃 머리 위치 -> 이상적 카메라 사이 스피어캐스트
        Vector3 lookAtPos = target.position + lookAtOffset;
        Vector3 camDir = (idealPos - lookAtPos);
        float camDist = camDir.magnitude;
        if (camDist > 0.001f)
        {
            camDir /= camDist;
            if (Physics.SphereCast(lookAtPos, clipSphereRadius, camDir, out var hit, camDist, clipMask, QueryTriggerInteraction.Ignore))
            {
                idealPos = hit.point - camDir * 0.05f; // 살짝 앞당김
            }
        }

        // 부드럽게 이동/시선 고정
        _cam.position = Vector3.Lerp(_cam.position, idealPos, Time.deltaTime * followLerp);
        _cam.rotation = Quaternion.Slerp(_cam.rotation, Quaternion.LookRotation(lookAtPos - _cam.position, Vector3.up), Time.deltaTime * followLerp);
    }

    /// <summary>초기 스냅(씬 진입 시 튀지 않도록)</summary>
    void SnapToIdeal()
    {
        if (_cam == null) return;

        Vector3 idealOffset = Quaternion.Euler(0f, _currentYaw, 0f) * new Vector3(0f, height, -distance);
        Vector3 lookAtPos = target.position + lookAtOffset;
        Vector3 idealPos = target.position + idealOffset;

        _cam.position = idealPos;
        _cam.LookAt(lookAtPos, Vector3.up);
    }
}
