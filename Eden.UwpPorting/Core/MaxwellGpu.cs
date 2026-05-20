using System.Diagnostics;

namespace Eden.UwpPorting.Core;

public sealed class MaxwellGpu
{
    private readonly Func<ulong, uint> _read32;

    public MaxwellGpu(Func<ulong, uint> read32)
    {
        _read32 = read32;
    }

    public void PushCommandBuffer(ulong address, int size)
    {
        ulong end = address + (ulong)size;
        ulong cursor = address;

        while (cursor + 4 <= end)
        {
            uint header = _read32(cursor);
            cursor += 4;

            int method = (int)((header >> 0) & 0x1FFF);
            int subChannel = (int)((header >> 13) & 0x7);
            int argCount = (int)((header >> 16) & 0x1FFF);
            int mode = (int)((header >> 29) & 0x7);

            Debug.WriteLine($"[Maxwell] hdr=0x{header:X8} method=0x{method:X} sub={subChannel} count={argCount} mode={mode}");

            if (mode == 1)
            {
                Debug.WriteLine($"[Maxwell] MethodBind: sub={subChannel} method=0x{method:X}");
            }
            else if (mode == 4)
            {
                Debug.WriteLine($"[Maxwell] Macro call: method=0x{method:X} args={argCount}");
            }

            for (int i = 0; i < argCount && cursor + 4 <= end; i++)
            {
                uint arg = _read32(cursor);
                cursor += 4;
                Debug.WriteLine($"[Maxwell]   arg[{i}]=0x{arg:X8}");
            }
        }
    }
}
