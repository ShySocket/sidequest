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

        public float VerticalVelocity;
        public float JumpHeight;
        public int Heading;
        public bool Hidden;
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
    [SerializeField] float stretchPerVelocity = 0.16f;
    [SerializeField] float maxStretch = 0.28f;
    [SerializeField] float landingSquash = 0.30f;
    [SerializeField] float landingRecovery = 6f;

    [Header("Roll")]
    [Tooltip("Extra roll beyond rolling without slipping. 1 = physically exact.")]
    [SerializeField] float rollScale = 1f;

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

    void UpdateBall(in Pose pose, float radius, float deltaTime)
    {
        ball.position = pose.GroundPosition + Vector3.up * (radius + pose.AirHeight);

        // Rolling without slipping: angular rate is speed / radius. Screen speed
        // is in frame widths per second and the radius in world units, so it is
        // converted through the ball's own diameter rather than a magic constant.
        float circumference = Mathf.Max(pose.Diameter * Mathf.PI, 0.0001f);
        float degreesPerSecond = pose.ScreenSpeed / circumference * 360f * rollScale;
        // Heading is negated because rolling leftward is a positive rotation
        // about the axis pointing out of the screen.
        rollAngle += degreesPerSecond * deltaTime * -pose.Heading;

        float stretch = Mathf.Clamp(
            Mathf.Abs(pose.VerticalVelocity) * stretchPerVelocity, 0f, maxStretch);
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

        float scale = Mathf.Lerp(shadowContactScale, shadowApexScale, altitude) * pose.Diameter;
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
