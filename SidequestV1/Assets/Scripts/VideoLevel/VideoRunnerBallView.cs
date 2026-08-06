using UnityEngine;

/// <summary>
/// Draws the character as a ball sitting in the filmed world.
/// </summary>
/// <remarks>
/// Presentation only - it decides nothing about gameplay, and reads a pose the
/// character hands it. Split out because the tricks that sell "this object is in
/// the scene" are numerous and none of them belong in the gameplay logic.
///
/// Four cues do the work, in rough order of how much they matter:
///
/// * **The contact shadow.** Without one the ball reads as a sticker on the
///   video. Its size and opacity track height, which is what tells the eye the
///   ball left the ground rather than merely got bigger.
/// * **Rolling.** A ball that translates without rotating looks dragged. The
///   roll rate comes from the level's measured screen speed, so it matches the
///   world sliding past instead of being guessed.
/// * **Perspective scale.** The ground line rises and falls as the road nears
///   and recedes; the ball scales with it, or it appears to swim.
/// * **Squash and stretch.** Stretch on the way up and down, squash on landing.
///   This is the part that makes a jump read as effort rather than a slide up.
/// </remarks>
public sealed class VideoRunnerBallView : MonoBehaviour
{
    /// <summary>Everything the view needs for one frame.</summary>
    public struct Pose
    {
        public Vector3 GroundPosition;
        public float AirHeight;
        public float Diameter;

        /// <summary>Screen speed in frame widths per second, for the roll rate.</summary>
        public float ScreenSpeed;

        /// <summary>Vertical speed as a fraction of this jump's take-off speed, 0..1.</summary>
        /// <remarks>
        /// Normalized rather than absolute so the stretch means the same thing
        /// whatever the jump height. An absolute value sat permanently at the
        /// clamp once cue heights came from the tracked marker, leaving the ball
        /// egg-shaped for the whole arc.
        /// </remarks>
        public float VerticalSpeed01;
        public float JumpHeight;
        public int Heading;
        public bool Hidden;

        /// <summary>Surface being run on, which changes how the ball reads.</summary>
        public string Surface;
    }

    [SerializeField] Transform ball;
    [SerializeField] Renderer ballRenderer;
    [SerializeField] Transform blobShadow;
    [SerializeField] Renderer blobShadowRenderer;

    [Header("Shadow")]
    [Tooltip("Shadow width at ground contact, as a multiple of ball diameter.")]
    [SerializeField] float shadowContactScale = 1.8f;

    [Tooltip("Shadow width at full jump height, as a multiple of ball diameter.")]
    [SerializeField] float shadowApexScale = 0.75f;

    [SerializeField] float shadowContactOpacity = 0.62f;
    [SerializeField] float shadowApexOpacity = 0.18f;

    [Tooltip("Shadow offset along the light, as a multiple of ball diameter.")]
    // The sun in this footage is low and to the left, so the shadow lies well
    // to the right of the ball rather than directly beneath it.
    [SerializeField] Vector2 shadowOffset = new Vector2(0.45f, 0.02f);

    [Tooltip("Vertical squash of the blob, since the ground is seen at a glancing angle.")]
    [SerializeField] float shadowFlatten = 0.34f;

    [Header("Squash and stretch")]
    [SerializeField] float maxStretch = 0.17f;
    [SerializeField] float landingSquash = 0.22f;
    [SerializeField] float landingRecovery = 5f;

    [Header("Roll")]
    [Tooltip("Extra roll beyond rolling without slipping. 1 = physically exact.")]
    [SerializeField] float rollScale = 1f;

    [Header("Surface feel")]
    [Tooltip("Roll rate multiplier on a rail, which should read as fast and precise.")]
    [SerializeField] float railRollBoost = 1.35f;

    [Tooltip("Shadow width multiplier on a rail: a narrow surface, so a tight contact.")]
    [SerializeField] float railShadowNarrowing = 0.45f;

    float rollAngle;
    float squash;
    bool wasAirborne;
    float shadowOpacity = -1f;

    // Reused rather than allocated per frame: a new MaterialPropertyBlock every
    // frame is pure garbage for the collector to chase.
    MaterialPropertyBlock shadowProperties;
    static readonly int OpacityId = Shader.PropertyToID("_Opacity");

    public void Apply(in Pose pose, float deltaTime)
    {
        EnsureBuilt();
        if (ball == null)
        {
            return;
        }

        SetVisible(!pose.Hidden);
        if (pose.Hidden)
        {
            return;
        }

        float radius = pose.Diameter * 0.5f;
        bool airborne = pose.AirHeight > 0.0001f;

        // Landing squash, triggered on the transition rather than on height, so a
        // long fall and a short hop both land with the same punch.
        if (wasAirborne && !airborne)
        {
            squash = landingSquash;
        }

        wasAirborne = airborne;
        squash = Mathf.MoveTowards(squash, 0f, landingRecovery * deltaTime * Mathf.Max(squash, 0.1f));

        UpdateBall(pose, radius, deltaTime);
        UpdateShadow(pose);
    }

    /// <summary>
    /// Create the ball and its shadow if the scene has not supplied them.
    /// </summary>
    /// <remarks>
    /// A saved scene is a snapshot: adding objects to the editor builder does
    /// nothing for a scene saved before that change, and the failure is silent -
    /// the character simply keeps whatever it was last saved with. Building the
    /// visual here means any scene, however old, gets the current character.
    /// RearCameraBackground already self-installs for the same reason.
    /// </remarks>
    void EnsureBuilt()
    {
        if (ball != null)
        {
            return;
        }

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Ball";
        Destroy(sphere.GetComponent<Collider>());
        sphere.transform.SetParent(transform, false);
        ball = sphere.transform;
        ballRenderer = sphere.GetComponent<MeshRenderer>();
        ballRenderer.sharedMaterial = LoadMaterial("Sidequest/BallLit");
        ballRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ballRenderer.receiveShadows = false;

        GameObject blob = GameObject.CreatePrimitive(PrimitiveType.Quad);
        blob.name = "ContactShadow";
        Destroy(blob.GetComponent<Collider>());
        blob.transform.SetParent(transform, false);
        blobShadow = blob.transform;
        blobShadowRenderer = blob.GetComponent<MeshRenderer>();
        blobShadowRenderer.sharedMaterial = LoadMaterial("Sidequest/BlobShadow");
        blobShadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        blobShadowRenderer.receiveShadows = false;

        // Any sprite left over from an older scene would draw through the ball.
        foreach (SpriteRenderer stale in GetComponentsInChildren<SpriteRenderer>())
        {
            stale.enabled = false;
        }
    }

    static Material LoadMaterial(string shaderName)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError($"VideoRunnerBallView: shader '{shaderName}' not found");
            return null;
        }

        return new Material(shader);
    }

    void UpdateBall(in Pose pose, float radius, float deltaTime)
    {
        ball.position = pose.GroundPosition + Vector3.up * (radius + pose.AirHeight);

        // Rolling without slipping: angular rate is speed / radius. Screen speed
        // is in frame widths per second and the radius in world units, so it is
        // converted through the ball's own diameter rather than a magic constant.
        float circumference = Mathf.Max(pose.Diameter * Mathf.PI, 0.0001f);
        float surfaceRoll = pose.Surface == "rail" ? railRollBoost : 1f;
        float degreesPerSecond =
            pose.ScreenSpeed / circumference * 360f * rollScale * surfaceRoll;
        // Heading is negated because rolling leftward is a positive rotation
        // about the axis pointing out of the screen.
        rollAngle += degreesPerSecond * deltaTime * -pose.Heading;

        // Fastest at take-off and landing, round at the apex.
        float stretch = Mathf.Clamp01(pose.VerticalSpeed01) * maxStretch;
        float verticalScale = 1f + stretch - squash;
        // Volume-preserving, so the ball never looks like it changed mass.
        float horizontalScale = 1f / Mathf.Max(verticalScale, 0.01f);

        ball.localRotation = Quaternion.Euler(0f, 0f, rollAngle);
        ball.localScale = new Vector3(
            pose.Diameter * horizontalScale,
            pose.Diameter * verticalScale,
            pose.Diameter);
    }

    void UpdateShadow(in Pose pose)
    {
        if (blobShadow == null)
        {
            return;
        }

        // Height as a fraction of a full jump, so the shadow responds the same
        // way whether the jump is tuned high or low.
        float altitude = pose.JumpHeight > 0.0001f
            ? Mathf.Clamp01(pose.AirHeight / pose.JumpHeight)
            : 0f;

        // A rail is narrow, so the contact patch is too - which is most of what
        // makes running a rail read differently from running open ground.
        float narrowing = pose.Surface == "rail" ? railShadowNarrowing : 1f;
        float scale =
            Mathf.Lerp(shadowContactScale, shadowApexScale, altitude) * pose.Diameter * narrowing;
        float opacity = Mathf.Lerp(shadowContactOpacity, shadowApexOpacity, altitude);

        // The shadow slides away from the ball as it rises, along the light.
        Vector3 offset = new Vector3(
            shadowOffset.x * pose.Diameter * pose.Heading * -1f,
            shadowOffset.y * pose.Diameter,
            0f) * (0.3f + altitude);

        // Just *behind* the ball in depth, so the ball occludes the near half of
        // the blob instead of the blob painting over the ball's underside.
        blobShadow.position = pose.GroundPosition + offset + Vector3.forward * 0.02f;
        blobShadow.localScale = new Vector3(scale, scale * shadowFlatten, 1f);

        // Only touch the renderer when the value actually moved; setting a
        // property block re-uploads material data every time it is called.
        if (blobShadowRenderer != null && !Mathf.Approximately(opacity, shadowOpacity))
        {
            shadowOpacity = opacity;
            shadowProperties ??= new MaterialPropertyBlock();
            blobShadowRenderer.GetPropertyBlock(shadowProperties);
            shadowProperties.SetFloat(OpacityId, opacity);
            blobShadowRenderer.SetPropertyBlock(shadowProperties);
        }
    }

    void SetVisible(bool visible)
    {
        if (ballRenderer != null)
        {
            ballRenderer.enabled = visible;
        }

        if (blobShadowRenderer != null)
        {
            blobShadowRenderer.enabled = visible;
        }
    }
}
