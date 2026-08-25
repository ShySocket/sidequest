using UnityEngine;

/// <summary>
/// Generates an animated fake street (sky, verges, sidewalk strips, asphalt
/// with scrolling lane dashes). It doubles as the editor stand-in for the
/// rear camera and as a deterministic input for the segmentation model,
/// which genuinely classifies its regions as road/vegetation/sky.
/// </summary>
public sealed class ProceduralStreetTexture : MonoBehaviour
{
    private const int Width = 480;
    private const int Height = 288;

    private Texture2D texture;
    private Color32[] pixels;
    private float scroll;

    public Texture2D Texture
    {
        get
        {
            if (texture == null)
            {
                texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
                {
                    name = "Procedural Street",
                    wrapMode = TextureWrapMode.Clamp
                };
                pixels = new Color32[Width * Height];
                Redraw();
            }

            return texture;
        }
    }

    /// <summary>Speed source so the dashes scroll with the vehicle.</summary>
    public float ScrollSpeed { get; set; } = 6f;

    private float redrawTimer;

    private void Update()
    {
        scroll += ScrollSpeed * Time.deltaTime;
        redrawTimer += Time.deltaTime;
        if (redrawTimer >= 0.08f)
        {
            redrawTimer = 0f;
            Redraw();
        }
    }

    private void Redraw()
    {
        if (texture == null)
        {
            _ = Texture;
            return;
        }

        Color32 sky = new Color32(126, 178, 224, 255);
        Color32 asphalt = new Color32(58, 58, 62, 255);
        Color32 sidewalk = new Color32(168, 162, 152, 255);
        Color32 grass = new Color32(52, 118, 48, 255);
        Color32 dash = new Color32(226, 220, 160, 255);

        int horizon = (int)(Height * 0.52f);
        for (int y = 0; y < Height; y++)
        {
            bool above = y >= horizon; // texture row 0 = bottom
            // Perspective: the road narrows toward the horizon.
            float depth = above ? 0f : 1f - (float)y / horizon;
            int roadHalf = (int)(Width * (0.14f + 0.34f * depth));
            int walkWidth = (int)(Width * (0.03f + 0.08f * depth));
            int center = Width / 2;
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                Color32 c;
                if (above)
                {
                    c = sky;
                }
                else
                {
                    int dx = Mathf.Abs(x - center);
                    if (dx < roadHalf)
                    {
                        c = asphalt;
                        // Scrolling center dashes, spaced in world units.
                        if (dx < Mathf.Max(2, roadHalf / 24))
                        {
                            float world = (1f / Mathf.Max(0.05f, depth)) * 4f + scroll;
                            if (Mathf.Repeat(world, 3f) < 1.4f)
                            {
                                c = dash;
                            }
                        }
                    }
                    else if (dx < roadHalf + walkWidth)
                    {
                        c = sidewalk;
                    }
                    else
                    {
                        c = grass;
                    }
                }

                pixels[row + x] = c;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false);
    }
}
