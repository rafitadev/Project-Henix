using Eden.UwpPorting.Core;
using System.Diagnostics;

namespace Eden.UwpPorting.Cpu;

public enum CpuExecutionMode
{
    Jit,
    InterpreterOnly
}

public interface ICpuEngine
{
    void Configure(CpuExecutionMode mode);
    void StepFrame();
}

public sealed class InterpreterCpuEngine : ICpuEngine
{
    private CpuExecutionMode _mode = CpuExecutionMode.InterpreterOnly;
    private readonly ulong[] _x = new ulong[32];
    private ulong _sp = 0x0000007401FF0000;
    private ulong _pc;
    private bool _initialized;
    private HorizonOS? _horizon;
    private MaxwellGpu? _maxwell;
    private VirtualMemoryManager? _mmu;

    public void Configure(CpuExecutionMode mode)
    {
        _mode = CpuExecutionMode.InterpreterOnly;
        RuntimeImage image = RuntimeState.LoadedImage ?? throw new InvalidOperationException("Imagem não carregada no VFS.");

        byte[] imageData = new byte[image.Text.Length + image.Rodata.Length + image.Data.Length];
        Buffer.BlockCopy(image.Text, 0, imageData, 0, image.Text.Length);
        Buffer.BlockCopy(image.Rodata, 0, imageData, image.Text.Length, image.Rodata.Length);
        Buffer.BlockCopy(image.Data, 0, imageData, image.Text.Length + image.Rodata.Length, image.Data.Length);

        _mmu = new VirtualMemoryManager(imageData);
        _horizon = new HorizonOS(_mmu);
        _maxwell = new MaxwellGpu(ReadUInt32);

        _pc = image.EntryPoint;
        _initialized = true;
    }

    public void StepFrame()
    {
        if (_mode != CpuExecutionMode.InterpreterOnly) throw new InvalidOperationException("Modo inválido para UWP.");
        if (!_initialized || _mmu is null) throw new InvalidOperationException("CPU não configurada.");

        const int opsPerFrame = 120000;
        for (int i = 0; i < opsPerFrame; i++)
        {
            uint op = ReadUInt32(_pc);
            Execute(op);
            _x[31] = 0;
        }
    }

    private uint ReadUInt32(ulong va) => _mmu!.ReadUInt32(va);
    private ulong ReadUInt64(ulong va) => _mmu!.ReadUInt64(va);
    private void WriteUInt64(ulong va, ulong value) => _mmu!.WriteUInt64(va, value);

    private void Execute(uint op)
    {
        if ((op & 0xFFE0001F) == 0xD4000001)
        {
            uint svcId = (op >> 5) & 0xFFFF;
            if (_horizon is null || !_horizon.HandleSvc(svcId, _x, ref _sp, ref _pc))
            {
                throw new NotSupportedException($"SVC não suportado: 0x{svcId:X}");
            }

            _pc += 4;
            return;
        }

        if (op == 0xD65F03C0)
        {
            _pc = _x[30];
            return;
        }

        uint top6 = op >> 26;

        if (top6 == 0b000101)
        {
            int imm26 = SignExtend((int)(op & 0x03FF_FFFF), 26);
            _pc = (ulong)((long)_pc + ((long)imm26 << 2));
            return;
        }

        if ((op & 0xFF000010) == 0x54000000)
        {
            int imm19 = SignExtend((int)((op >> 5) & 0x7FFFF), 19);
            int cond = (int)(op & 0xF);
            bool take = EvaluateCond(cond);
            _pc = take ? (ulong)((long)_pc + ((long)imm19 << 2)) : _pc + 4;
            return;
        }

        if ((op & 0x7F000000) == 0x34000000 || (op & 0x7F000000) == 0x35000000)
        {
            bool nonZero = (op & 0x01000000) != 0;
            int rt = (int)(op & 0x1F);
            int imm19 = SignExtend((int)((op >> 5) & 0x7FFFF), 19);
            ulong value = ReadReg(rt);
            bool take = nonZero ? value != 0 : value == 0;
            _pc = take ? (ulong)((long)_pc + ((long)imm19 << 2)) : _pc + 4;
            return;
        }

        if ((op & 0x7F000000) == 0x11000000)
        {
            int rd = (int)(op & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            uint imm12 = (op >> 10) & 0xFFF;
            WriteReg(rd, ReadReg(rn) + imm12);
            _pc += 4;
            return;
        }

        if ((op & 0x7F000000) == 0x51000000)
        {
            int rd = (int)(op & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            uint imm12 = (op >> 10) & 0xFFF;
            WriteReg(rd, ReadReg(rn) - imm12);
            _pc += 4;
            return;
        }

        if ((op & 0xFF000000) == 0xD2800000)
        {
            int rd = (int)(op & 0x1F);
            uint imm16 = (op >> 5) & 0xFFFF;
            int shift = (int)((op >> 21) & 0x3) * 16;
            WriteReg(rd, (ulong)imm16 << shift);
            _pc += 4;
            return;
        }

        if ((op & 0x1F200000) == 0x0A000000)
        {
            int rm = (int)((op >> 16) & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            int rd = (int)(op & 0x1F);
            int opc = (int)((op >> 29) & 0x3);

            ulong a = ReadReg(rn);
            ulong b = ReadReg(rm);
            ulong r = opc switch
            {
                0 => a & b,
                1 => a | b,
                2 => a ^ b,
                _ => a
            };

            WriteReg(rd, r);
            _pc += 4;
            return;
        }

        if ((op & 0xFFC00000) == 0xF9400000)
        {
            int rt = (int)(op & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            uint imm12 = (op >> 10) & 0xFFF;
            ulong addr = ReadReg(rn) + (imm12 << 3);
            WriteReg(rt, ReadUInt64(addr));
            _pc += 4;
            return;
        }

        if ((op & 0xFFC00000) == 0xF9000000)
        {
            int rt = (int)(op & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            uint imm12 = (op >> 10) & 0xFFF;
            ulong addr = ReadReg(rn) + (imm12 << 3);
            WriteUInt64(addr, ReadReg(rt));
            _pc += 4;

            if (addr >= 0x57000000 && addr < 0x58000000 && _maxwell is not null)
            {
                int commandBytes = (int)Math.Min((ulong)0x4000, ReadReg(1));
                if (commandBytes > 0)
                {
                    _maxwell.PushCommandBuffer(ReadReg(0), commandBytes);
                }
            }

            return;
        }

        if ((op & 0xFFC00000) == 0xA9000000)
        {
            int rt = (int)(op & 0x1F);
            int rt2 = (int)((op >> 10) & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            int imm7 = SignExtend((int)((op >> 15) & 0x7F), 7);
            ulong baseAddr = ReadRegOrSp(rn);
            ulong addr = (ulong)((long)baseAddr + ((long)imm7 << 3));

            WriteUInt64(addr, ReadReg(rt));
            WriteUInt64(addr + 8, ReadReg(rt2));
            _pc += 4;
            return;
        }

        if ((op & 0xFFC00000) == 0xA9400000)
        {
            int rt = (int)(op & 0x1F);
            int rt2 = (int)((op >> 10) & 0x1F);
            int rn = (int)((op >> 5) & 0x1F);
            int imm7 = SignExtend((int)((op >> 15) & 0x7F), 7);
            ulong baseAddr = ReadRegOrSp(rn);
            ulong addr = (ulong)((long)baseAddr + ((long)imm7 << 3));

            WriteReg(rt, ReadUInt64(addr));
            WriteReg(rt2, ReadUInt64(addr + 8));
            _pc += 4;
            return;
        }

        _pc += 4;
    }

    private bool EvaluateCond(int cond)
    {
        // condição mínima para fluxo de boot inicial sem flags reais
        return cond switch
        {
            0x0 => false,
            0x1 => true,
            0xE => true,
            _ => false
        };
    }

    private ulong ReadReg(int idx) => idx == 31 ? 0UL : _x[idx];

    private ulong ReadRegOrSp(int idx) => idx == 31 ? _sp : _x[idx];

    private void WriteReg(int idx, ulong value)
    {
        if (idx == 31) return;
        _x[idx] = value;
    }

    private static int SignExtend(int value, int bits)
    {
        int shift = 32 - bits;
        return (value << shift) >> shift;
    }
}
