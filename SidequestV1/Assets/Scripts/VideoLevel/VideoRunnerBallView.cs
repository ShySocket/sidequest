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

        /// <summary>Light sampled from the footage here; white in ordinary daylight.</summary>
        public Color Ambient;
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

    [Tooltip("Landing squash multiplier by surface: hedges give, rails do not.")]
    [SerializeField] float hedgeLandingSoftness = 1.4f;
    [SerializeField] float grassLandingSoftness = 1.2f;
    [SerializeField] float railLandingSoftness = 0.7f;

    float rollAngle;
    float squash;
    bool wasAirborne;
    float shadowOpacity = -1f;

    // Reused rather than allocated per frame: a new MaterialPropertyBlock every
    // frame is pure garbage for the collector to chase.
    MaterialPropertyBlock shadowProperties;
    static readonly int OpacityId = Shader.PropertyToID("_Opacity");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int LightColorId = Shader.PropertyToID("_LightColor");
    static readonly int SkyColorId = Shader.PropertyToID("_SkyColor");
    static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
    static readonly int SpecStrengthId = Shader.PropertyToID("_SpecStrength");

    float flashUntil = -1f;
    float flashDuration = 1f;

    Color appliedAmbient = Color.clear;
    float appliedLuma = -1f;
    Color baseBase;
    Color baseLight;
    Color baseSky;
    Color baseGround;
    float baseSpecStrength;
    float shadowLightScale = 1f;

    /// <summary>Flash the ball red: the visible verdict for a missed cue.</summary>
    public void Flash(float duration)
    {
        flashDuration = Mathf.Max(duration, 0.05f);
        flashUntil = Time.time + flashDuration;
    }

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
            // A hedge absorbs the landing, a steel rail does not; the squash is
            // most of how that difference reads.
            float softness = pose.Surface switch
            {
                "hedge" => hedgeLandingSoftness,
                "grass" => grassLandingSoftness,
                "rail" => railLandingSoftness,
                _ => 1f,
            };
            squash = landingSquash * softness;
        }

        wasAirborne = airborne;
        squash = Mathf.MoveTowards(squash, 0f, landingRecovery * deltaTime * Mathf.Max(squash, 0.1f));

        UpdateBall(pose, radius, deltaTime);
        UpdateShadow(pose);
        UpdateLighting(pose);
    }

    /// <summary>
    /// Tint the ball's lighting from the footage around it.
    /// </summary>
    /// <remarks>
    /// One constant sun looks pasted on the moment the clip drives through tree
    /// shade or past a sunlit wall. The level carries light sampled from the
    /// pixels where the ball stands, normalized so ordinary daylight is white;
    /// the tuned material colours are multiplied by it, and the contact shadow
    /// fades in shade, where real shadows lose their edge.
    /// </remarks>
    void UpdateLighting(in Pose pose)
    {
        if (ballRenderer == null)
        {
            return;
        }

        Color ambient = pose.Ambient.a > 0f ? pose.Ambient : Color.white;

        // The miss flash pulls the light toward red, and - because a black
        // albedo reflects almost nothing whatever colour the light is - the
        // albedo itself as well. Specular and rim stay alive, so it reads as
        // the ball burning red rather than being swapped for a red one.
        float flash = Mathf.Clamp01((flashUntil - Time.time) / flashDuration);
        if (flash > 0f)
        {
            ambient = Color.Lerp(ambient, new Color(2.2f, 0.25f, 0.2f), flash * 0.85f);
        }

        float luma = ambient.r * 0.299f + ambient.g * 0.587f + ambient.b * 0.114f;
        if (flash <= 0f
            && Mathf.Abs(luma - appliedLuma) < 0.02f
            && Mathf.Abs(ambient.r - appliedAmbient.r) < 0.02f
            && Mathf.Abs(ambient.b - appliedAmbient.b) < 0.02f)
        {
            return;
        }

        appliedAmbient = ambient;
        appliedLuma = luma;

        // .material: EnsureBuilt already makes a per-instance material, and a
        // scene-supplied shared one must not be edited in place.
        Material material = ballRenderer.material;
        if (baseLight.a == 0f)
        {
            baseBase = material.GetColor(BaseColorId);
            baseLight = material.GetColor(LightColorId);
            baseSky = material.GetColor(SkyColorId);
            baseGround = material.GetColor(GroundColorId);
            baseSpecStrength = material.GetFloat(SpecStrengthId);
        }

        material.SetColor(
            BaseColorId, Color.Lerp(baseBase, new Color(0.8f, 0.04f, 0.03f), flash));
        material.SetColor(LightColorId, baseLight * ambient);
        material.SetColor(SkyColorId, baseSky * ambient);
        material.SetColor(GroundColorId, baseGround * ambient);
        // Glints dull in shade along with everything else.
        material.SetFloat(SpecStrengthId, baseSpecStrength * Mathf.Clamp(luma, 0.5f, 1.3f));

        // Direct sun casts a hard dark blob; shade barely casts one at all.
        shadowLightScale = Mathf.Clamp(Mathf.Pow(luma, 0.8f), 0.45f, 1.25f);
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
        float opacity =
            Mathf.Lerp(shadowContactOpacity, shadowApexOpacity, altitude) * shadowLightScale;

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
