using Eden.UwpPorting.Audio;
using Eden.UwpPorting.Cpu;
using Eden.UwpPorting.Gpu;
using Eden.UwpPorting.Input;
using Eden.UwpPorting.Vfs;
using System.Diagnostics;
using Windows.UI.Xaml.Controls;

namespace Eden.UwpPorting.Core;

public sealed class PortingBootstrap
{
    public EdenRuntime Build(EdenSettings settings, SwapChainPanel renderPanel)
    {
        var vfs = new UwpStorageProvider();
        var cpu = new InterpreterCpuEngine();
        var gpu = new D3D12GraphicsBackend(renderPanel);
        var input = new XboxInputProvider();
        var audio = new UwpAudioBackend();

        return new EdenRuntime(settings, vfs, cpu, gpu, input, audio);
    }
}

public sealed record EdenSettings(string GamePath);

public sealed class EdenRuntime
{
    private readonly IVirtualFileSystem _vfs;
    private readonly ICpuEngine _cpu;
    private readonly IGraphicsBackend _gpu;
    private readonly IInputProvider _input;
    private readonly IAudioBackend _audio;

    public EdenRuntime(
        EdenSettings settings,
        IVirtualFileSystem vfs,
        ICpuEngine cpu,
        IGraphicsBackend gpu,
        IInputProvider input,
        IAudioBackend audio)
    {
        Settings = settings;
        _vfs = vfs;
        _cpu = cpu;
        _gpu = gpu;
        _input = input;
        _audio = audio;
    }

    public EdenSettings Settings { get; }

    public async Task BootAsync(CancellationToken ct = default)
    {
        BootDiagnostics.Info($"Boot start: {Settings.GamePath}");
        await _vfs.InitializeAsync(ct);
        await _vfs.TryLoadSwitchKeysAsync(ct);

        _cpu.Configure(CpuExecutionMode.InterpreterOnly);
        BootDiagnostics.Info("CPU configured (InterpreterOnly)");
        _gpu.Initialize();
        BootDiagnostics.Info("GPU initialized");
        _audio.Initialize();
        BootDiagnostics.Info("Audio initialized");

        bool cpuHalted = false;
        while (!ct.IsCancellationRequested)
        {
            _input.Poll();
            if (!cpuHalted)
            {
                try { _cpu.StepFrame(); }
                catch (Exception ex)
                {
                    cpuHalted = true;
                    BootDiagnostics.Error($"CPU fault: {ex.Message}. Mantendo render loop ativo para diagnóstico visual.");
                }
            }

            _gpu.Present();
            _audio.PushFrame();
            await Task.Delay(1, ct);
        }
    }
}
