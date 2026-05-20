namespace Eden.UwpPorting.Core;

public sealed class RuntimeImage
{
    public required byte[] Text { get; init; }
    public required byte[] Rodata { get; init; }
    public required byte[] Data { get; init; }
    public ulong EntryPoint { get; init; }
}

public static class RuntimeState
{
    public static RuntimeImage? LoadedImage { get; set; }
}
