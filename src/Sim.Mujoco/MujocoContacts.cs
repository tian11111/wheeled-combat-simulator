using System.Runtime.InteropServices;

namespace Sim.Mujoco;

/// <summary>
/// Narrow 3.14.0 mjData/mjContact ABI view. It is intentionally version gated by
/// MujocoNative. mjData's first 97 pointers end immediately before contact.
/// </summary>
internal static unsafe class MujocoContacts
{
    private const int SolverStatBytes = 40;
    private const int SolverSlots = 20 * 200;
    private const int WarningStatBytes = 8;
    private const int WarningSlots = 7;
    private const int TimerStatBytes = 16;
    private const int TimerSlots = 15;
    private const int ContactBytes = 584;
    private const int ContactGeom0Offset = 540;
    private const int ContactGeom1Offset = 544;
    private const int ContactPointerOrdinal = 98;

    static MujocoContacts()
    {
        // Compiled from the official 3.14.0 Windows x64 headers (abi-probe.c).
        if (sizeof(DataHeader) != 160672
            || Marshal.OffsetOf<DataHeader>(nameof(DataHeader.Ncon)).ToInt64() != 160560
            || sizeof(DataHeader) + (ContactPointerOrdinal - 1) * IntPtr.Size != 161448)
        {
            throw new InvalidOperationException("MuJoCo 3.14.0 contact ABI layout is not compatible with this process.");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct DataHeader
    {
        public long Narena;
        public long Nbuffer;
        public int Nplugin;
        public nuint Pstack;
        public nuint Pbase;
        public nuint Parena;
        public nuint Threadpool;
        public byte Threadlock;
        public long MaxuseStack;
        public long MaxuseArena;
        public int MaxuseCon;
        public int MaxuseEfc;
        public fixed byte Solver[SolverStatBytes * SolverSlots];
        public fixed int SolverNiter[20];
        public fixed int SolverNnz[20];
        public fixed double SolverFwdinv[2];
        public fixed byte Warning[WarningStatBytes * WarningSlots];
        public fixed byte Timer[TimerStatBytes * TimerSlots];
        public int Ncon;
        public int Ne;
        public int Nf;
        public int Nl;
        public int Nefc;
        public int Nj;
        public int EfmActive;
        public int NefmK;
        public int Nefmcon;
        public int NefmT;
        public int NefmA;
        public int Nefmdof;
        public int NefmL;
        public int Ny;
        public int Na;
        public int Nisland;
        public int Nidof;
        public int NtreeAwake;
        public int NbodyAwake;
        public int NparentAwake;
        public int NvAwake;
        public byte FlgEnergypos;
        public byte FlgEnergyvel;
        public byte FlgSubtreevel;
        public byte FlgRnepost;
        public double Time;
        public fixed double Energy[2];
    }

    internal static void Visit(IntPtr data, Action<int, int> callback)
    {
        var header = (DataHeader*)data;
        var ncon = header->Ncon;
        if (ncon < 0 || ncon > 500)
        {
            throw new InvalidOperationException($"Invalid MuJoCo contact count {ncon}; native ABI may be incompatible.");
        }
        if (ncon == 0) return;
        var contactAddress = (byte*)data + sizeof(DataHeader) + (ContactPointerOrdinal - 1) * IntPtr.Size;
        var contacts = *(byte**)contactAddress;
        if (contacts == null)
        {
            throw new InvalidOperationException("MuJoCo reported contacts but its contact array is null.");
        }
        for (var i = 0; i < ncon; i++)
        {
            var c = contacts + i * ContactBytes;
            callback(*(int*)(c + ContactGeom0Offset), *(int*)(c + ContactGeom1Offset));
        }
    }
}
