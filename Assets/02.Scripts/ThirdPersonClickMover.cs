using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using Photon.Pun;

[RequireComponent(typeof(NavMeshAgent))]
public class ThirdPersonClickMover : MonoBehaviourPun
{
    [Header("Raycast")]
    [SerializeField] private LayerMask groundMask;              // 바닥 레이어만 체크
    [SerializeField] private float raycastMaxDistance = 200f;

    [Header("Movement")]
    [SerializeField] private float stoppingDistance = 0.3f;     // 목적지 근접 판정
    [SerializeField] private float arrivalTolerance = 0.1f;     // 도착 여유값
    [SerializeField] private bool faceMoveDirection = true;     // 이동 방향 바라보기(Agent 회전)

    [Header("NavMesh Safety")]
    [SerializeField] private bool autoSnapOnStart = true;       // 시작 시 NavMesh로 스냅
    [SerializeField] private float snapSearchRadius = 6f;       // 시작 스냅 탐색 반경

    [Header("Animation (Optional)")]
    [SerializeField] private Animator animator;
    [SerializeField] private string speedParam = "Speed";       // Animator 파라미터명
    [SerializeField] private bool disableRootMotionOnStart = true; // 루트모션 자동 OFF

    [Header("Physics (Optional)")]
    [SerializeField] private bool forceKinematicOnRigidbody = true; // Rigidbody가 있으면 Kinematic+NoGravity

    private Camera _cam;
    private NavMeshAgent _agent;
    private Rigidbody _rb;

    // 도착 후 미세 드리프트 방지용
    private bool _isArrived;
    private Vector3 _lastStablePos;

    void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _rb = GetComponent<Rigidbody>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        // 네트워크 소유자가 아니면 조작 비활성
        if (photonView && !photonView.IsMine)
        {
            enabled = false;
            return;
        }

        // Rigidbody가 있다면 물리로 미끄러지지 않도록 제어
        if (_rb && forceKinematicOnRigidbody)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _rb.interpolation = RigidbodyInterpolation.None;
        }

        // 애니메이터 루트모션 끄기(에이전트 이동과 충돌 방지)
        if (animator && disableRootMotionOnStart)
            animator.applyRootMotion = false;
    }

    void Start()
    {
        _cam = Camera.main;

        // Agent 기본 동작값
        _agent.updateRotation = faceMoveDirection;
        _agent.stoppingDistance = stoppingDistance;
        _agent.autoBraking = true;    // 목적지에서 감속
        _agent.autoRepath   = false;  // 불필요한 재탐색 방지

        if (autoSnapOnStart) EnsureAgentOnNavMesh();

        _lastStablePos = transform.position;
    }

    void Update()
    {
        if (GetTapDown(out Vector2 screenPos))
        {
            TrySetDestination(screenPos);
        }

        // 도착 판정 → 강제 멈춤 처리
        if (_agent.enabled)
        {
            if (_agent.hasPath && !_agent.pathPending)
            {
                float stopEdge = Mathf.Max(0.01f, _agent.stoppingDistance + arrivalTolerance);

                if (_agent.remainingDistance != Mathf.Infinity &&
                    _agent.pathStatus == NavMeshPathStatus.PathComplete &&
                    _agent.remainingDistance <= stopEdge)
                {
                    HardStopAtArrival();
                }
            }
            else if (_isArrived)
            {
                // 도착 상태 유지 중 미세 드리프트 억제(안정 위치로 고정)
                // 에이전트가 위치를 관리하므로 nextPosition과 동기화
                _agent.nextPosition = _lastStablePos;
                transform.position = _lastStablePos;
                _agent.velocity = Vector3.zero;
            }
        }

        // 애니메이터 Speed 갱신(정규화)
        if (animator != null && !string.IsNullOrEmpty(speedParam))
        {
            float speed01 = _agent.velocity.magnitude / Mathf.Max(0.01f, _agent.speed);
            animator.SetFloat(speedParam, speed01);
        }
    }

    bool GetTapDown(out Vector2 screenPos)
    {
        // Mouse (좌클릭)
        if (Input.GetMouseButtonDown(0))
        {
            // UI 위 클릭 무시
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                screenPos = default;
                return false;
            }
            screenPos = Input.mousePosition;
            return true;
        }

        // Touch (첫 손가락만)
        if (Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Began)
            {
                // UI 위 터치 무시 (터치용 오버로드)
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(t.fingerId))
                {
                    screenPos = default;
                    return false;
                }
                screenPos = t.position;
                return true;
            }
        }

        screenPos = default;
        return false;
    }

    void TrySetDestination(Vector2 screenPos)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        // 에이전트가 NavMesh 위에 없으면 먼저 스냅 시도
        if (!_agent.isOnNavMesh && !EnsureAgentOnNavMesh()) return;

        Ray ray = _cam.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out var hit, raycastMaxDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 dst = hit.point;

            // 클릭 지점을 NavMesh로 스냅 후 목적지 지정
            if (NavMesh.SamplePosition(dst, out var navHit, 1.5f, NavMesh.AllAreas))
                dst = navHit.position;

            // 새 목적지 시작
            _isArrived = false;
            _agent.isStopped = false;
            _agent.ResetPath();
            _agent.SetDestination(dst);
        }
    }

    /// <summary>
    /// 현재 위치 근처의 NavMesh를 찾아 에이전트를 워프시킵니다.
    /// </summary>
    bool EnsureAgentOnNavMesh()
    {
        if (_agent.isOnNavMesh) return true;

        if (NavMesh.SamplePosition(transform.position, out var hit, snapSearchRadius, NavMesh.AllAreas))
        {
            _agent.Warp(hit.position);
            return true;
        }
        else
        {
            Debug.LogWarning($"[{name}] 근처 {snapSearchRadius}m 내에 NavMesh가 없습니다. NavMesh Bake/스폰 위치 확인 필요.");
            return false;
        }
    }

    /// <summary>
    /// 도착 시 강제 정지 + 위치 스냅(미끄러짐 방지).
    /// </summary>
    void HardStopAtArrival()
    {
        _agent.isStopped = true;
        _agent.velocity = Vector3.zero;
        _agent.ResetPath();

        // 에이전트 기준 좌표로 스냅(수직 흔들림/미세 드리프트 방지)
        Vector3 snap = _agent.nextPosition;
        // 주변 NavMesh에 다시 한 번 스냅(경계면에서 떨림 방지)
        if (NavMesh.SamplePosition(snap, out var hit, 0.8f, NavMesh.AllAreas))
            snap = hit.position;

        transform.position = snap;
        _agent.nextPosition = snap;

        _lastStablePos = snap;
        _isArrived = true;
    }
}
