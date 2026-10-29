using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Totem pickup by hand touch - attach this to each totem.
/// When either controller (hand) touches the totem's visual bounds:
///   - fires "On Picked Up" (UnityEvent - wire sounds or extra effects here)
///   - auto-calls CompletePickup() on an ItemPickup component on the same totem,
///     so your existing "Totems collected" HUD counter and destroy pipeline run
///   - if there is no ItemPickup component, the totem is hidden (or destroyed,
///     per the checkbox) instead
/// Hands are found automatically from the XR rig's controller interactors
/// (Near-Far Interactors). Nothing to configure in the Inspector.
///
/// v1.1 — hand detection hardened:
///   • the interactor scan now INCLUDES inactive objects, so a rig that is
///     currently disabled (or appears later) is still found — Update keeps
///     retrying about once per second either way;
///   • new fallback: controller objects ("Left/Right Controller") are searched
///     UNDER any XROrigin in the scene instead of GameObject.Find (which only
///     sees active objects);
///   • "no hands yet" is now a WARNING, not an error — it is a temporary,
///     self-healing state, and desktop mode (E-key pickup) is unaffected.
/// </summary>
public class TotemTouchPickup : MonoBehaviour
{
    [Header("Touch Settings")]
    [Tooltip("Extra meters added around the totem's visual bounds. 0.1 adds about 5 cm on every side.")]
    [SerializeField] private float touchPadding = 0.1f;

    [Tooltip("Fallback touch radius in meters (center of totem to hand), used when the totem has no renderers.")]
    [SerializeField] private float fallbackRadius = 0.25f;

    [Header("Hands (optional - found automatically when left empty)")]
    [Tooltip("Optional. If assigned, these transforms replace the automatic hand detection.")]
    [SerializeField] private Transform leftHandOverride = null;
    [SerializeField] private Transform rightHandOverride = null;

    [Header("Pickup")]
    [Tooltip("Fired once when a hand touches the totem. Wire sounds/extra effects here. " +
             "The HUD counter is updated by your ItemPickup - do not also connect the counter here or it would count twice.")]
    [SerializeField] private UnityEvent onPickedUp = new UnityEvent();

    [Tooltip("If true, also calls CompletePickup() on an ItemPickup component on this totem, so the " +
             "'Totems collected' HUD updates and the totem is destroyed by your existing pipeline.")]
    [SerializeField] private bool triggerItemPickupPipeline = true;

    [Tooltip("Destroy the totem GameObject instead of just hiding it. Keep OFF if a pickup sound on the totem should finish playing.")]
    [SerializeField] private bool destroyAfterPickup = false;

    private Transform[] hands = new Transform[0];
    private Bounds touchBounds;
    private bool hasRenderers;
    private bool collected;
    private bool loggedNoHands;

    private void Awake()
    {
        // Combined world-space bounds of everything visible on this totem.
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        hasRenderers = renderers.Length > 0;
        if (hasRenderers)
        {
            touchBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                touchBounds.Encapsulate(renderers[i].bounds);
            touchBounds.Expand(touchPadding);
        }

        ResolveHands();
    }

    private void Update()
    {
        if (collected)
            return;

        if (!HandsValid())
        {
            // The XR rig may not be ready yet - retry about once per second.
            if (Time.frameCount % 60 == 0)
                ResolveHands();
            if (!HandsValid())
                return;
        }

        for (int i = 0; i < hands.Length; i++)
        {
            Vector3 handPos = hands[i].position;
            bool touching = hasRenderers
                ? touchBounds.Contains(handPos)
                : (handPos - transform.position).sqrMagnitude <= fallbackRadius * fallbackRadius;
            if (touching)
            {
                Collect(hands[i].name);
                return;
            }
        }
    }

    private void ResolveHands()
    {
        // 1. Explicit overrides win if assigned.
        if (leftHandOverride != null || rightHandOverride != null)
        {
            int count = (leftHandOverride != null ? 1 : 0) + (rightHandOverride != null ? 1 : 0);
            Transform[] overrides = new Transform[count];
            int index = 0;
            if (leftHandOverride != null)
            {
                overrides[index] = leftHandOverride;
                index++;
            }
            if (rightHandOverride != null)
            {
                overrides[index] = rightHandOverride;
                index++;
            }
            hands = overrides;
            Debug.Log("[TotemTouchPickup] '" + name + "' using hand overrides (" + count + " hand(s)).", this);
            return;
        }

        // 2. Automatic: every controller interactor in the scene (the Near-Far,
        //    Poke, Ray and Teleport interactors on the XR Origin controllers).
        //    Inactive objects are INCLUDED on purpose: a rig that is currently
        //    disabled - or one that appears later - is still found, and the
        //    Update retry locks onto it the moment it becomes usable.
        XRBaseInputInteractor[] interactors;
#if UNITY_6000_0_OR_NEWER
        interactors = FindObjectsByType<XRBaseInputInteractor>(FindObjectsInactive.Include);
#elif UNITY_2023_1_OR_NEWER
        interactors = FindObjectsByType<XRBaseInputInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        interactors = FindObjectsOfType<XRBaseInputInteractor>(true);
#endif
        if (interactors != null && interactors.Length > 0)
        {
            hands = new Transform[interactors.Length];
            for (int i = 0; i < interactors.Length; i++)
                hands[i] = interactors[i].transform;

            Debug.Log("[TotemTouchPickup] '" + name + "' watching " + hands.Length + " hand(s): " +
                hands[0].name + (hands.Length > 1 ? " and " + hands[1].name : string.Empty) +
                ". Touch a hand to the totem to pick it up.", this);
            return;
        }

        // 3. Fallback: controller GameObjects by name, searched under any XR
        //    Origin in the scene (works even when the rig is disabled, unlike
        //    GameObject.Find which only sees active objects).
        XROrigin xrOrigin;
#if UNITY_2023_1_OR_NEWER
        xrOrigin = FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
#else
        xrOrigin = FindObjectOfType<XROrigin>();
#endif
        if (xrOrigin != null)
        {
            Transform left = FindDescendant(xrOrigin.transform, "Left Controller");
            Transform right = FindDescendant(xrOrigin.transform, "Right Controller");
            int found = (left != null ? 1 : 0) + (right != null ? 1 : 0);
            if (found > 0)
            {
                hands = new Transform[found];
                int index = 0;
                if (left != null)
                {
                    hands[index] = left;
                    index++;
                }
                if (right != null)
                {
                    hands[index] = right;
                    index++;
                }
                Debug.Log("[TotemTouchPickup] '" + name + "' watching " + found + " hand(s) by name.", this);
                return;
            }
        }

        // 4. Nothing found yet. Not fatal - Update retries about once per
        //    second, and desktop mode is unaffected (the E-key pickup on
        //    ItemPickup does not depend on this script at all).
        hands = new Transform[0];
        if (!loggedNoHands)
        {
            loggedNoHands = true;
            Debug.LogWarning("[TotemTouchPickup] No XR hands found yet on '" + name + "' - touch pickup will keep retrying. " +
                "If you are in VR: make sure the XR Origin (with its controller interactors) is active, " +
                "or assign the Left/Right Hand Override fields. " +
                "If you are testing desktop mode (no XR rig): this is expected - the E-key pickup still works.", this);
        }
    }

    /// <summary>Depth-first name search through the hierarchy, inactive children included. Only used on the XR rig (small), so a simple walk is fine.</summary>
    private static Transform FindDescendant(Transform root, string objectName)
    {
        if (root.name == objectName)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindDescendant(root.GetChild(i), objectName);
            if (result != null)
                return result;
        }
        return null;
    }

    private bool HandsValid()
    {
        if (hands.Length == 0)
            return false;

        for (int i = 0; i < hands.Length; i++)
        {
            if (hands[i] == null)
                return false;
        }
        return true;
    }

    private void Collect(string handName)
    {
        collected = true;

        Debug.Log("[TotemTouchPickup] '" + name + "' picked up by " + handName + ".", this);

        if (onPickedUp != null)
            onPickedUp.Invoke();

        // Trigger the existing ItemPickup pipeline (HUD counter + destroy) if present.
        if (triggerItemPickupPipeline)
            SendMessage("CompletePickup", SendMessageOptions.DontRequireReceiver);

        if (destroyAfterPickup)
        {
            Destroy(gameObject);
        }
        else
        {
            // Hide the totem but keep the GameObject alive so pickup sounds can finish.
            foreach (Renderer r in GetComponentsInChildren<Renderer>())
                r.enabled = false;
            foreach (Collider c in GetComponentsInChildren<Collider>())
                c.enabled = false;
            enabled = false;
        }
    }
}