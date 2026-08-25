using System;
using Unity.Sentis;
using UnityEngine;

/// <summary>
/// Runs Fast-SCNN (Cityscapes) over the live feed at a low cadence and
/// publishes a per-lane surface report. Without a model it reports an
/// all-road surface so the game never depends on segmentation to function.
/// </summary>
public sealed class SegmentationRunner : MonoBehaviour
{
    private const int InputWidth = 480;
    private const int InputHeight = 288;

    [SerializeField] private ModelAsset modelAsset;
    [SerializeField] private LiveCameraFeed feed;
    [SerializeField, Range(0.1f, 3f)] private float intervalSeconds = 0.5f;

    private Worker worker;
    private Tensor<float> inputTensor;
    private Tensor<int> pendingOutput;
    private RenderTexture inputTarget;
    private SurfaceAnalyzer analyzer;
    private byte[] classMapBuffer;
    private float nextScheduleTime;
    private bool inferencePending;

    public bool IsAvailable { get; private set; }
    public SurfaceReport LastReport { get; private set; }
    public double LastReportTime { get; private set; } = -1d;
    public event Action<SurfaceReport> SurfaceReady;

    private void Start()
    {
        analyzer = new SurfaceAnalyzer();
        classMapBuffer = new byte[InputWidth * InputHeight];
        LastReport = SurfaceReport.AllRoad(3);
        if (modelAsset == null)
        {
            Debug.LogWarning("SegmentationRunner has no model; assuming an all-road surface.", this);
            return;
        }

        try
        {
            Model model = ModelLoader.Load(modelAsset);
            // TextureConverter yields 0..1 RGB; Fast-SCNN expects ImageNet-style
            // normalization of 0..255 values, so bake it into the graph.
            FunctionalGraph graph = new FunctionalGraph();
            FunctionalTensor input = graph.AddInput(model, 0);
            FunctionalTensor mean = Functional.Constant(
                new TensorShape(1, 3, 1, 1),
                new[] { 123.675f, 116.28f, 103.53f });
            FunctionalTensor std = Functional.Constant(
                new TensorShape(1, 3, 1, 1),
                new[] { 58.395f, 57.12f, 57.375f });
            FunctionalTensor normalized = (input * 255f - mean) / std;
            FunctionalTensor[] outputs = Functional.Forward(model, normalized);
            Model normalizedModel = graph.Compile(outputs[0]);
            worker = new Worker(normalizedModel, BackendType.GPUCompute);
            inputTensor = new Tensor<float>(new TensorShape(1, 3, InputHeight, InputWidth));
            inputTarget = new RenderTexture(InputWidth, InputHeight, 0, RenderTextureFormat.ARGB32);
            IsAvailable = true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"SegmentationRunner failed to initialize: {exception.Message}", this);
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
                int[] classes = pendingOutput.DownloadToArray();
                pendingOutput = null; // owned by the worker; do not dispose
                inferencePending = false;
                Publish(classes);
            }

            return;
        }

        if (Time.unscaledTime < nextScheduleTime)
        {
            return;
        }

        nextScheduleTime = Time.unscaledTime + intervalSeconds;
        Graphics.Blit(feed.SourceTexture, inputTarget);
        TextureConverter.ToTensor(inputTarget, inputTensor, new TextureTransform());
        worker.Schedule(inputTensor);
        pendingOutput = worker.PeekOutput() as Tensor<int>;
        if (pendingOutput == null)
        {
            return;
        }

        pendingOutput.ReadbackRequest();
        inferencePending = true;
    }

    private void Publish(int[] classes)
    {
        int count = Math.Min(classes.Length, classMapBuffer.Length);
        for (int i = 0; i < count; i++)
        {
            classMapBuffer[i] = (byte)classes[i];
        }

        // The tensor is laid out top-to-bottom while the analyzer indexes
        // rows top-to-bottom as well, so no flip is needed: row 0 of the
        // tensor is the top image row for TextureConverter's default layout.
        SurfaceReport report = analyzer.Analyze(classMapBuffer, InputWidth, InputHeight);
        LastReport = report;
        LastReportTime = Time.unscaledTimeAsDouble;
        SurfaceReady?.Invoke(report);
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
        if (inputTarget != null)
        {
            inputTarget.Release();
            Destroy(inputTarget);
            inputTarget = null;
        }
    }
}
