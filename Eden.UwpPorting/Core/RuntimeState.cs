namespace Eden.UwpPorting.Core;

public sealed class RuntimeInputState
{
    public ulong Buttons;
    public float Lx;
    public float Ly;
    public float Rx;
    public float Ry;
}

public static class RuntimeSharedState
{
    public static RuntimeInputState Input { get; } = new();
}
