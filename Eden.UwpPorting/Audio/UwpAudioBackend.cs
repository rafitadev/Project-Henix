using Windows.Media.Audio;
using Windows.Media.Render;

namespace Eden.UwpPorting.Audio;

public interface IAudioBackend
{
    void Initialize();
    void PushFrame();
}

public sealed class UwpAudioBackend : IAudioBackend
{
    private AudioGraph? _graph;
    private AudioFrameInputNode? _inputNode;

    public void Initialize()
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.GameMedia);
        var graphResult = AudioGraph.CreateAsync(settings).AsTask().GetAwaiter().GetResult();
        if (graphResult.Status != AudioGraphCreationStatus.Success)
        {
            throw new InvalidOperationException("Falha ao iniciar AudioGraph no UWP.");
        }

        _graph = graphResult.Graph;

        var output = _graph.CreateDeviceOutputNodeAsync().AsTask().GetAwaiter().GetResult();
        if (output.Status != AudioDeviceNodeCreationStatus.Success)
        {
            throw new InvalidOperationException("Falha ao criar saída de áudio do dispositivo.");
        }

        _inputNode = _graph.CreateFrameInputNode();
        _inputNode.AddOutgoingConnection(output.DeviceOutputNode);
        _graph.Start();
    }

    public void PushFrame()
    {
        // TODO: converter buffer PCM do mixer do Eden para AudioFrame e enviar:
        // _inputNode?.AddFrame(audioFrame);
    }
}
