using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Windows.UI.Xaml.Controls;

namespace Eden.UwpPorting.Gpu;

public interface IGraphicsBackend
{
    void Initialize();
    void Present();
}

public sealed class D3D12GraphicsBackend : IGraphicsBackend
{
    private const int FrameCount = 2;

    private readonly SwapChainPanel _panel;
    private IDXGIFactory4? _factory;
    private ID3D12Device? _device;
    private ID3D12CommandQueue? _queue;
    private IDXGISwapChain3? _swapChain;
    private ID3D12DescriptorHeap? _rtvHeap;
    private readonly ID3D12Resource[] _renderTargets = new ID3D12Resource[FrameCount];
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[FrameCount];
    private ID3D12GraphicsCommandList? _commandList;
    private ID3D12Fence? _fence;
    private ulong _fenceValue;
    private nint _fenceEvent;
    private int _rtvDescriptorSize;
    private int _frameIndex;

    public D3D12GraphicsBackend(SwapChainPanel panel)
    {
        _panel = panel;
    }

    public void Initialize()
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        _device = D3D12.D3D12CreateDevice<ID3D12Device>(null, FeatureLevel.Level_11_0);
        _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));

        var swapDesc = new SwapChainDescription1
        {
            Width = Math.Max(1, (int)_panel.ActualWidth),
            Height = Math.Max(1, (int)_panel.ActualHeight),
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = FrameCount,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore
        };

        using IDXGISwapChain1 tempSwap = _factory.CreateSwapChainForComposition(_queue, swapDesc);
        _swapChain = tempSwap.QueryInterface<IDXGISwapChain3>();
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        nint panelPtr = Marshal.GetIUnknownForObject(_panel);
        try
        {
            var native = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(panelPtr);
            native.SwapChain = _swapChain.NativePointer;
        }
        finally
        {
            Marshal.Release(panelPtr);
        }

        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        CpuDescriptorHandle rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle + i * _rtvDescriptorSize);
            _allocators[i] = _device.CreateCommandAllocator(CommandListType.Direct);
        }

        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocators[0], null);
        _commandList.Close();

        _fence = _device.CreateFence(0);
        _fenceValue = 1;
        _fenceEvent = Kernel32.CreateEventEx(IntPtr.Zero, null, 0, Kernel32.EVENT_ALL_ACCESS);
    }

    public void Present()
    {
        if (_queue is null || _swapChain is null || _commandList is null || _fence is null || _rtvHeap is null)
        {
            return;
        }

        ID3D12CommandAllocator allocator = _allocators[_frameIndex];
        allocator.Reset();
        _commandList.Reset(allocator);

        _commandList.ResourceBarrier(ResourceBarrier.Transition(_renderTargets[_frameIndex], ResourceStates.Present, ResourceStates.RenderTarget));

        CpuDescriptorHandle rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart() + _frameIndex * _rtvDescriptorSize;
        float[] cornflowerBlue = { 0.392f, 0.584f, 0.929f, 1.0f };
        _commandList.ClearRenderTargetView(rtv, cornflowerBlue);

        _commandList.ResourceBarrier(ResourceBarrier.Transition(_renderTargets[_frameIndex], ResourceStates.RenderTarget, ResourceStates.Present));

        _commandList.Close();
        _queue.ExecuteCommandList(_commandList);
        _swapChain.Present(1, PresentFlags.None);

        ulong signalValue = _fenceValue;
        _queue.Signal(_fence, signalValue);
        _fenceValue++;

        if (_fence.CompletedValue < signalValue)
        {
            _fence.SetEventOnCompletion(signalValue, _fenceEvent);
            Kernel32.WaitForSingleObject(_fenceEvent, uint.MaxValue);
        }

        _frameIndex = _swapChain.CurrentBackBufferIndex;
    }
}

internal static class Kernel32
{
    public const uint EVENT_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern nint CreateEventEx(nint lpEventAttributes, string? lpName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
}

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    nint SwapChain { set; }
}
