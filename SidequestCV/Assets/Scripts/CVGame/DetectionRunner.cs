using System;
using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;

/// <summary>
/// Runs YOLOX-tiny over the live feed on a fixed cadence with non-blocking
/// GPU readback, then publishes lane-level detection events. If the model is
/// missing or fails to load, the runner reports itself disabled and the game
/// falls back to procedural spawning.
/// </summary>
public sealed class DetectionRunner : MonoBehaviour
{
    private const int InputSize = 416;

    [SerializeField] private ModelAsset modelAsset;
    [SerializeField] private LiveCameraFeed feed;
    [SerializeField, Range(0.05f, 1f)] private float intervalSeconds = 0.25f;
    [SerializeField, Range(0.05f, 0.95f)] private float scoreThreshold = 0.35f;
    [SerializeField, Range(0.05f, 0.95f)] private float iouThreshold = 0.45f;

    private Worker worker;
    private Tensor<float> inputTensor;
    private Tensor<float> pendingOutput;
    private RenderTexture letterboxTarget;
    private RenderTexture scaledSource;
    private YoloxPostProcessor postProcessor;
    private DetectionEventMapper eventMapper;
    private float nextScheduleTime;
    private bool inferencePending;
    private float letterboxScale;
    private int sourceWidth;
    private int sourceHeight;

    public bool IsAvailable { get; private set; }
    public IReadOnlyList<Detection> LastDetections { get; private set; } = Array.Empty<Detection>();
    public double LastDetectionTime { get; private set; } = -1d;
    public event Action<IReadOnlyList<DetectionEvent>> DetectionsReady;

    private void Start()
    {
        postProcessor = new YoloxPostProcessor(InputSize, scoreThreshold, iouThreshold);
        eventMapper = new DetectionEventMapper();
        if (modelAsset == null)
        {
            Debug.LogWarning("DetectionRunner has no model; CV obstacle detection is off.", this);
            return;
        }

        try
        {
            Model model = ModelLoader.Load(modelAsset);
            // The texture converter delivers 0..1 values; YOLOX expects raw
            // 0..255, so bake the multiplication into the compiled graph.
            FunctionalGraph graph = new FunctionalGraph();
            FunctionalTensor input = graph.AddInput(model, 0);
            FunctionalTensor[] outputs = Functional.Forward(model, input * 255f);
            Model scaledModel = graph.Compile(outputs[0]);
            worker = new Worker(scaledModel, BackendType.GPUCompute);
            inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
            letterboxTarget = new RenderTexture(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
            IsAvailable = true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"DetectionRunner failed to initialize: {exception.Message}", this);
            DisposeResources();
            IsAvailable = false;
        }
    }

    private void Update()
    {
        if (!IsAvailable || feed == null || feed.SourceTexture == null)
        {
            return;
        }

        if (inferencePending)
        {
            if (pendingOutput != null && pendingOutput.IsReadbackRequestDone())
            {
                float[] raw = pendingOutput.DownloadToArray();
                pendingOutput = null; // owned by the worker; do not dispose
                inferencePending = false;
                Publish(raw);
            }

            return;
        }

        if (Time.unscaledTime < nextScheduleTime)
        {
            return;
        }

        nextScheduleTime = Time.unscaledTime + intervalSeconds;
        Blit(feed.SourceTexture);
        TextureTransform transform = new TextureTransform()
            .SetChannelSwizzle(ChannelSwizzle.BGRA);
        TextureConverter.ToTensor(letterboxTarget, inputTensor, transform);
        worker.Schedule(inputTensor);
        Tensor<float> output = worker.PeekOutput() as Tensor<float>;
        if (output == null)
        {
            return;
        }

        // The worker owns this tensor; it stays valid because the next
        // Schedule only happens after this readback completes.
        pendingOutput = output;
        pendingOutput.ReadbackRequest();
        inferencePending = true;
    }

    private void Blit(Texture source)
    {
        sourceWidth = source.width;
        sourceHeight = source.height;
        letterboxScale = LetterboxMath.ComputeScale(sourceWidth, sourceHeight, InputSize);
        LetterboxMath.ScaledSize(sourceWidth, sourceHeight, InputSize, out int scaledW, out int scaledH);

        if (scaledSource == null || scaledSource.width != scaledW || scaledSource.height != scaledH)
        {
            if (scaledSource != null)
            {
                scaledSource.Release();
                Destroy(scaledSource);
            }

            scaledSource = new RenderTexture(scaledW, scaledH, 0, RenderTextureFormat.ARGB32);
        }

        Graphics.Blit(source, scaledSource);

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = letterboxTarget;
        GL.Clear(true, true, new Color(114f / 255f, 114f / 255f, 114f / 255f, 1f));
        RenderTexture.active = previous;
        Graphics.CopyTexture(
            scaledSource, 0, 0, 0, 0, scaledW, scaledH,
            letterboxTarget, 0, 0, 0, InputSize - scaledH);
        // CopyTexture destination Y places the image at the TOP of the model
        // input (texture row 0 is the bottom), matching the Python reference
        // letterbox which anchors at the top-left.
    }

    private void Publish(float[] raw)
    {
        List<Detection> modelSpace = postProcessor.Decode(raw);
        List<Detection> sourceSpace = new List<Detection>(modelSpace.Count);
        for (int i = 0; i < modelSpace.Count; i++)
        {
            sourceSpace.Add(LetterboxMath.ToSource(modelSpace[i], letterboxScale));
        }

        LastDetections = sourceSpace;
        LastDetectionTime = Time.unscaledTimeAsDouble;
        List<DetectionEvent> events = eventMapper.Map(sourceSpace, sourceWidth, sourceHeight);
        DetectionsReady?.Invoke(events);
    }

    private void OnDestroy()
    {
        DisposeResources();
    }

    private void DisposeResources()
    {
        pendingOutput = null; // owned by the worker
        inputTensor?.Dispose();
        inputTensor = null;
        worker?.Dispose();
        worker = null;
        if (letterboxTarget != null)
        {
            letterboxTarget.Release();
            Destroy(letterboxTarget);
            letterboxTarget = null;
        }

        if (scaledSource != null)
        {
            scaledSource.Release();
            Destroy(scaledSource);
            scaledSource = null;
        }
    }
}
