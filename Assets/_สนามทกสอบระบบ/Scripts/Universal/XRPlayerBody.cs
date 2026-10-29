using Unity.XR.CoreUtils;
using UnityEngine;

/// <summary>
/// Keeps the XR Origin (VR rig) root centered under the player's HEAD.
///
/// WHY THIS EXISTS (the Goal 1 fix)
/// Every gameplay system in this project (enemies, spawners, fog, audio,
/// waypoints...) finds the player through the object tagged "Player" — which,
/// in the VR scene, is the XR Origin ROOT. But that root only sits under the
/// player's head while the head is at the center of the play space:
/// room-scale walking, or moving the head with the XR Interaction Simulator,
/// moves the HEAD away while the root stays behind — so enemies would run
/// toward the empty rig position instead of toward the player.
/// (Moving the tag to the Main Camera instead breaks everything else, because
/// the root is the object that carries the capsule body and the triggers.)
///
/// WHAT IT DOES (every LateUpdate)
/// 1. Measures how far the head camera has drifted from the rig center (XZ only).
/// 2. If the drift is beyond "Recenter Threshold" meters, moves the rig root by
///    that amount (through the CharacterController when present, so walls still
///    block the body), then shifts the Camera Floor Offset back by the same
///    amount so the view does not jump. Net result: the rig root — and with it
///    the "Player" tag, the capsule and every trigger — is always directly
///    under the player's head.
///
/// HOW TO USE
/// Add this component to the XR Origin root GameObject (the one with the
/// XROrigin component — prefab "XR Origin (XR Rig)" in this project).
/// Recommended: add it to the PREFAB itself so every VR scene inherits it.
/// No other setup. Everything else keeps working unchanged.
///
/// NOTES
/// • Desktop scenes: no XROrigin on the player there — the component simply
///   never gets added / does nothing.
/// • Teleport, smooth locomotion and snap turn keep working: they move the rig
///   root, this script only recenters the play space under the head afterwards.
/// • Climbing locomotion (ClimbLocomotionProvider) also drives the camera
///   offset — if climbing gameplay is ever added, untick "Recenter Enabled"
///   while climbing (or call .recenterEnabled from code).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(XROrigin))]
[AddComponentMenu("Gameplay/XR Player Body (keeps the rig under the head)")]
public class XRPlayerBody : MonoBehaviour
{
    [Header("Recentering")]

    [Tooltip("How far (meters) the head may drift from the rig center before the rig recenters under it. Smaller = tighter tracking, more micro-corrections.")]
    [SerializeField, Min(0.01f)] private float recenterThreshold = 0.05f;

    [Tooltip("Master switch. Untick to freeze the rig center in place (e.g. while climbing).")]
    [SerializeField] private bool recenterEnabled = true;

    private XROrigin origin;
    private CharacterController body;
    private Transform head;
    private Transform floorOffset;

    /// <summary>The XRPlayerBody of the active VR rig, or null in desktop scenes. Handy for future systems (menus anchored to the player, etc.).</summary>
    public static XRPlayerBody Instance { get; private set; }

    /// <summary>The head (HMD) camera transform, once resolved. Null in desktop scenes.</summary>
    public Transform HeadTransform => head;

    private void Awake()
    {
        origin = GetComponent<XROrigin>();
        body = GetComponent<CharacterController>();
        Instance = this;
    }

    private void OnEnable()
    {
        ResolveReferences();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>Finds the head camera and the camera floor offset from the XROrigin (they can be null early, so this is re-run lazily).</summary>
    private void ResolveReferences()
    {
        Camera cam = origin != null ? origin.Camera : null;
        if (cam == null)
            cam = Camera.main;
        head = cam != null ? cam.transform : null;

        floorOffset = origin != null && origin.CameraFloorOffsetObject != null
            ? origin.CameraFloorOffsetObject.transform
            : null;
    }

    private void LateUpdate()
    {
        if (!recenterEnabled || origin == null)
            return;

        if (head == null || floorOffset == null)
        {
            ResolveReferences();
            if (head == null || floorOffset == null)
                return;
        }

        // How far has the head drifted from the rig's center? (XZ only —
        // height changes from jumping, crouching or stairs are the job of
        // the locomotion providers, not this script.)
        Vector3 headWorld = head.position;
        Vector3 rootWorld = transform.position;
        Vector3 drift = new Vector3(headWorld.x - rootWorld.x, 0f, headWorld.z - rootWorld.z);

        if (drift.sqrMagnitude < recenterThreshold * recenterThreshold)
            return; // already (nearly) centered — nothing to do

        // 1. Move the rig root under the head. Through the CharacterController
        //    when present, so walls and props still block the body (the head can
        //    still lean a little past it — same as every room-scale VR title;
        //    a blocked move simply pulls the head back by the blocked amount).
        if (body != null)
            body.Move(drift);
        else
            transform.position += drift;

        // 2. Shift the camera floor offset back by the same delta so the view
        //    does not jump — only the play space's center point moved.
        //    Head and controllers keep their exact world positions.
        floorOffset.position -= drift;
    }
}