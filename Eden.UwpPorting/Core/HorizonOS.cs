using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Eden.UwpPorting.Core;

public sealed class HorizonOS
{
    private readonly VirtualMemoryManager _mmu;
    private readonly ConcurrentDictionary<ulong, KernelHandle> _handles = new();
    private readonly ConcurrentDictionary<ulong, MutexState> _mutexes = new();
    private long _nextHandle = 0x100;

    public HorizonOS(VirtualMemoryManager mmu)
    {
        _mmu = mmu;
    }

    public bool HandleSvc(uint svcId, ulong[] x, ref ulong sp, ref ulong pc)
    {
        switch (svcId)
        {
            case 0x01: // SetHeapSize
            {
                ulong size = x[1];
                ulong heapBase = _mmu.ResizeHeap(size);
                x[1] = heapBase;
                x[0] = 0;
                return true;
            }
            case 0x03: // SetMemoryAttribute
            {
                _mmu.SetMemoryAttribute(x[0], x[1], x[2], x[3]);
                x[0] = 0;
                return true;
            }
            case 0x13: // MapProcessMemory
            {
                _mmu.MapProcessMemory(x[0], x[1], x[2]);
                x[0] = 0;
                return true;
            }
            case 0x16: // CloseHandle
            {
                _handles.TryRemove(x[0], out _);
                x[0] = 0;
                return true;
            }
            case 0x1A: // ArbitrateLock
            {
                ulong ownerThread = x[0];
                ulong mutexAddress = x[1];
                ulong requesterThread = x[2];
                ArbitrateLock(ownerThread, mutexAddress, requesterThread);
                x[0] = 0;
                return true;
            }
            case 0x1B: // ArbitrateUnlock
            {
                ulong mutexAddress = x[0];
                ArbitrateUnlock(mutexAddress);
                x[0] = 0;
                return true;
            }
            case 0x1C: // ControlMemory
            {
                ulong addr0 = x[0];
                ulong addr1 = x[1];
                ulong size = x[2];
                uint op = (uint)x[3];
                ulong perm = x[4];
                ulong outAddr = _mmu.ControlMemory(addr0, addr1, size, op, perm);
                x[1] = outAddr;
                x[0] = 0;
                return true;
            }
            case 0x1F: // ConnectToNamedPort
            {
                ulong namePtr = x[1];
                string portName = _mmu.ReadNullTerminatedAscii(namePtr, 12);
                ulong handle = CreatePortHandle(portName);
                x[1] = handle;
                x[0] = 0;
                return true;
            }
            case 0x21: // SendSyncRequest
            {
                ulong handle = x[0];
                SendSyncRequest(handle, sp);
                x[0] = 0;
                return true;
            }
            default:
                Debug.WriteLine($"[HOS] Unhandled SVC 0x{svcId:X}");
                return false;
        }
    }

    private ulong CreatePortHandle(string portName)
    {
        ulong handle = (ulong)Interlocked.Increment(ref _nextHandle);
        _handles[handle] = new KernelHandle(handle, portName, HandleType.Port);
        return handle;
    }

    private void SendSyncRequest(ulong handle, ulong threadStackPointer)
    {
        if (!_handles.TryGetValue(handle, out KernelHandle? kernelHandle)) return;

        ulong tls = threadStackPointer & ~0xFFFUL;
        uint cmd0 = _mmu.ReadUInt32(tls + 0x80);
        uint commandId = _mmu.ReadUInt32(tls + 0x90);

        string service = kernelHandle.Name;
        if (service == "sm:")
        {
            if (commandId == 0)
            {
                _mmu.WriteUInt32(tls + 0x80, 0x2);
                _mmu.WriteUInt32(tls + 0x84, 0);
            }
            else if (commandId == 1)
            {
                string requestedService = _mmu.ReadAscii(tls + 0xA0, 8).TrimEnd('\0');
                ulong serviceHandle = CreatePortHandle(requestedService);
                _mmu.WriteUInt32(tls + 0x80, 0x2);
                _mmu.WriteUInt32(tls + 0x84, 0);
                _mmu.WriteUInt32(tls + 0x88, (uint)serviceHandle);
            }
        }
        else
        {
            _mmu.WriteUInt32(tls + 0x80, 0x2);
            _mmu.WriteUInt32(tls + 0x84, 0);
        }

        _ = cmd0;
    }

    private void ArbitrateLock(ulong ownerThread, ulong mutexAddress, ulong requesterThread)
    {
        var state = _mutexes.GetOrAdd(mutexAddress, _ => new MutexState());
        lock (state.Sync)
        {
            if (state.OwnerThreadId == 0 || state.OwnerThreadId == ownerThread)
            {
                state.OwnerThreadId = requesterThread;
                return;
            }

            state.WaitQueue.Enqueue(requesterThread);
        }
    }

    private void ArbitrateUnlock(ulong mutexAddress)
    {
        if (!_mutexes.TryGetValue(mutexAddress, out MutexState? state)) return;

        lock (state.Sync)
        {
            if (state.WaitQueue.Count > 0)
            {
                state.OwnerThreadId = state.WaitQueue.Dequeue();
            }
            else
            {
                state.OwnerThreadId = 0;
            }
        }
    }

    private sealed record KernelHandle(ulong Value, string Name, HandleType Type);

    private enum HandleType
    {
        Port,
        Service,
        Event,
        Session
    }

    private sealed class MutexState
    {
        public readonly object Sync = new();
        public ulong OwnerThreadId;
        public Queue<ulong> WaitQueue { get; } = new();
    }
}

public sealed class VirtualMemoryManager
{
    private const ulong PageSize = 0x1000;
    private readonly List<VmRegion> _regions = new();

    private readonly ulong _aslrBase = 0x0000007100000000;
    private readonly ulong _aslrSize = 0x0000000100000000;
    private readonly ulong _aliasBase = 0x0000007200000000;
    private readonly ulong _aliasSize = 0x0000000080000000;
    private readonly ulong _heapBase = 0x0000007300000000;
    private readonly ulong _stackBase = 0x0000007400000000;

    private ulong _heapSize;

    public VirtualMemoryManager(byte[] image)
    {
        MapFixed(_aslrBase, (ulong)image.LongLength, MemoryPermission.Read | MemoryPermission.Execute, image);
        MapFixed(_aliasBase, _aliasSize, MemoryPermission.Read | MemoryPermission.Write);
        MapFixed(_heapBase, PageSize, MemoryPermission.Read | MemoryPermission.Write);
        MapFixed(_stackBase, 0x02000000, MemoryPermission.Read | MemoryPermission.Write);
        _heapSize = PageSize;
    }

    public ulong ResizeHeap(ulong newSize)
    {
        ulong aligned = AlignUp(newSize, PageSize);
        if (aligned == _heapSize) return _heapBase;

        if (aligned > _heapSize)
        {
            ulong delta = aligned - _heapSize;
            MapFixed(_heapBase + _heapSize, delta, MemoryPermission.Read | MemoryPermission.Write);
        }
        else
        {
            Unmap(_heapBase + aligned, _heapSize - aligned);
        }

        _heapSize = aligned;
        return _heapBase;
    }

    public void SetMemoryAttribute(ulong address, ulong size, ulong mask, ulong attr)
    {
        _ = mask;
        MemoryPermission perm = (attr & 1) != 0 ? MemoryPermission.Read : MemoryPermission.None;
        if ((attr & 2) != 0) perm |= MemoryPermission.Write;
        if ((attr & 4) != 0) perm |= MemoryPermission.Execute;

        foreach (VmRegion r in FindRegions(address, size)) r.Permission = perm;
    }

    public void MapProcessMemory(ulong dst, ulong src, ulong size)
    {
        byte[] block = ReadBytes(src, (int)size);
        MapFixed(dst, size, MemoryPermission.Read | MemoryPermission.Write, block);
    }

    public ulong ControlMemory(ulong addr0, ulong addr1, ulong size, uint op, ulong perm)
    {
        ulong alignedSize = AlignUp(size, PageSize);
        MemoryPermission p = (MemoryPermission)(perm & 0x7);

        return op switch
        {
            0 => MapAnonymous(addr0 != 0 ? addr0 : addr1, alignedSize, p),
            1 => Unmap(addr0, alignedSize),
            3 => Protect(addr0, alignedSize, p),
            _ => addr0
        };
    }

    public uint ReadUInt32(ulong va)
    {
        VmSpan s = Resolve(va, 4, MemoryPermission.Read);
        return (uint)(s.Region.Data[s.Offset] | (s.Region.Data[s.Offset + 1] << 8) | (s.Region.Data[s.Offset + 2] << 16) | (s.Region.Data[s.Offset + 3] << 24));
    }

    public ulong ReadUInt64(ulong va)
    {
        VmSpan s = Resolve(va, 8, MemoryPermission.Read);
        ulong v = 0;
        for (int i = 0; i < 8; i++) v |= (ulong)s.Region.Data[s.Offset + i] << (i * 8);
        return v;
    }

    public void WriteUInt32(ulong va, uint value)
    {
        VmSpan s = Resolve(va, 4, MemoryPermission.Write);
        s.Region.Data[s.Offset] = (byte)(value & 0xFF);
        s.Region.Data[s.Offset + 1] = (byte)((value >> 8) & 0xFF);
        s.Region.Data[s.Offset + 2] = (byte)((value >> 16) & 0xFF);
        s.Region.Data[s.Offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public void WriteUInt64(ulong va, ulong value)
    {
        VmSpan s = Resolve(va, 8, MemoryPermission.Write);
        for (int i = 0; i < 8; i++) s.Region.Data[s.Offset + i] = (byte)((value >> (8 * i)) & 0xFF);
    }

    public byte[] ReadBytes(ulong va, int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = ReadByte(va + (ulong)i);
        return data;
    }

    public string ReadAscii(ulong va, int len)
    {
        byte[] bytes = ReadBytes(va, len);
        return Encoding.ASCII.GetString(bytes);
    }

    public string ReadNullTerminatedAscii(ulong va, int maxLen)
    {
        Span<byte> bytes = stackalloc byte[maxLen];
        for (int i = 0; i < maxLen; i++)
        {
            byte b = ReadByte(va + (ulong)i);
            bytes[i] = b;
            if (b == 0) return Encoding.ASCII.GetString(bytes[..i]);
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private byte ReadByte(ulong va)
    {
        VmSpan s = Resolve(va, 1, MemoryPermission.Read);
        return s.Region.Data[s.Offset];
    }

    private ulong MapAnonymous(ulong address, ulong size, MemoryPermission permission)
    {
        ulong baseAddress = address != 0 ? address : FindFreeRegion(_aliasBase, _aliasSize, size);
        MapFixed(baseAddress, size, permission);
        return baseAddress;
    }

    private ulong Unmap(ulong address, ulong size)
    {
        _regions.RemoveAll(r => address <= r.Base && (r.Base + r.Size) <= (address + size));
        return address;
    }

    private ulong Protect(ulong address, ulong size, MemoryPermission permission)
    {
        foreach (VmRegion r in FindRegions(address, size)) r.Permission = permission;
        return address;
    }

    private IEnumerable<VmRegion> FindRegions(ulong address, ulong size)
    {
        ulong end = address + size;
        return _regions.Where(r => r.Base < end && (r.Base + r.Size) > address);
    }

    private void MapFixed(ulong baseAddress, ulong size, MemoryPermission permission, byte[]? source = null)
    {
        int allocSize = checked((int)size);
        byte[] data = new byte[allocSize];
        if (source is not null) Buffer.BlockCopy(source, 0, data, 0, Math.Min(source.Length, data.Length));
        _regions.Add(new VmRegion(baseAddress, size, permission, data));
    }

    private VmSpan Resolve(ulong va, int size, MemoryPermission required)
    {
        foreach (VmRegion region in _regions)
        {
            if (va >= region.Base && (va + (ulong)size) <= (region.Base + region.Size))
            {
                if ((region.Permission & required) != required)
                    throw new AccessViolationException($"Permissão inválida em 0x{va:X}");

                return new VmSpan(region, (int)(va - region.Base));
            }
        }

        throw new AccessViolationException($"Page fault em 0x{va:X}");
    }

    private ulong FindFreeRegion(ulong baseAddress, ulong rangeSize, ulong size)
    {
        ulong candidate = baseAddress;
        ulong end = baseAddress + rangeSize;

        while (candidate + size < end)
        {
            bool overlap = _regions.Any(r => candidate < r.Base + r.Size && (candidate + size) > r.Base);
            if (!overlap) return candidate;
            candidate += size;
        }

        throw new OutOfMemoryException("Sem VA livre para mapear.");
    }

    private static ulong AlignUp(ulong v, ulong a) => (v + a - 1) & ~(a - 1);

    private sealed class VmRegion
    {
        public VmRegion(ulong @base, ulong size, MemoryPermission permission, byte[] data)
        {
            Base = @base;
            Size = size;
            Permission = permission;
            Data = data;
        }

        public ulong Base { get; }
        public ulong Size { get; }
        public MemoryPermission Permission { get; set; }
        public byte[] Data { get; }
    }

    private readonly struct VmSpan(VmRegion region, int offset)
    {
        public VmRegion Region { get; } = region;
        public int Offset { get; } = offset;
    }
}

[Flags]
public enum MemoryPermission : ulong
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4
}
