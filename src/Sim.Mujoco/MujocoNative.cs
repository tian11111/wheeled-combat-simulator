using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sim.Mujoco;

/// <summary>MuJoCo 3.14.0 C ABI. No native library is loaded on the legacy path.</summary>
internal static unsafe class MujocoNative
{
    internal const string ExpectedVersion = "3.14.0";
    internal const string ExpectedDllSha256 = "da487aed0d534fc52b1612a09571f9f2836b8abb9f46567ab5868620ca0e7418";
    private const string LibraryName = "mujoco";
    private const int ExpectedNumericVersion = 3014000;
    private const int StateQpos = 1 << 1;
    private const int StateQvel = 1 << 2;
    private const int StateCtrl = 1 << 6;
    internal const int ObjectGeom = 5; // mjOBJ_GEOM in mjtObj
    private static readonly object LoadGate = new();
    private static bool _registered;
    private static IntPtr _library;

    internal static void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("MuJoCo 3.14.0 physics is packaged for Windows x64 only.");
        }
        lock (LoadGate)
        {
            if (!_registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(MujocoNative).Assembly, Resolve);
                _registered = true;
            }
            if (_library == IntPtr.Zero)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "mujoco.dll");
                if (!File.Exists(path))
                {
                    throw new DllNotFoundException($"MuJoCo 3.14.0 native library is missing: {path}");
                }
                using (var stream = File.OpenRead(path))
                {
                    var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    if (actualHash != ExpectedDllSha256)
                    {
                        throw new InvalidOperationException($"MuJoCo native library SHA-256 mismatch at '{path}': expected {ExpectedDllSha256}, found {actualHash}.");
                    }
                }
                try
                {
                    _library = NativeLibrary.Load(path);
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
                {
                    throw new InvalidOperationException($"Cannot load MuJoCo 3.14.0 native library at '{path}': {ex.Message}", ex);
                }
            }
            var numeric = Version();
            var reported = Marshal.PtrToStringUTF8(VersionString()) ?? "unknown";
            if (numeric != ExpectedNumericVersion || reported != ExpectedVersion)
            {
                throw new InvalidOperationException($"MuJoCo native version mismatch: expected {ExpectedVersion} ({ExpectedNumericVersion}), found {reported} ({numeric}).");
            }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        => name == LibraryName ? _library : IntPtr.Zero;

    [DllImport(LibraryName, EntryPoint = "mj_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Version();
    [DllImport(LibraryName, EntryPoint = "mj_versionString", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VersionString();
    [DllImport(LibraryName, EntryPoint = "mj_parseXMLString", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ParseXmlString([MarshalAs(UnmanagedType.LPUTF8Str)] string xml, IntPtr vfs, byte[] error, int errorSize);
    [DllImport(LibraryName, EntryPoint = "mj_compile", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Compile(IntPtr spec, IntPtr vfs);
    [DllImport(LibraryName, EntryPoint = "mj_deleteSpec", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DeleteSpec(IntPtr spec);
    [DllImport(LibraryName, EntryPoint = "mjs_getError", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetSpecError(IntPtr spec);
    // 内存 VFS: mesh 资产以字节挂进去, XML 里只出现逻辑文件名(无机器路径)。
    // 3.14.0 的导出名是 mj_defaultVFS / mj_addBufferVFS / mj_deleteVFS(不是 mju_addBufferToVFS),
    // 见 native 库导出表; mj_addBufferVFS **不拷贝** 缓冲区, 编译期间必须保持 pin 住。
    [DllImport(LibraryName, EntryPoint = "mj_defaultVFS", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DefaultVfs(IntPtr vfs);
    [DllImport(LibraryName, EntryPoint = "mj_addBufferVFS", CallingConvention = CallingConvention.Cdecl)]
    private static extern void AddBufferVfs(IntPtr vfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr buffer, int size);
    [DllImport(LibraryName, EntryPoint = "mj_deleteVFS", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DeleteVfs(IntPtr vfs);
    [DllImport(LibraryName, EntryPoint = "mj_deleteModel", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DeleteModel(IntPtr model);
    [DllImport(LibraryName, EntryPoint = "mj_makeData", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MakeData(IntPtr model);
    [DllImport(LibraryName, EntryPoint = "mj_deleteData", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DeleteData(IntPtr data);
    [DllImport(LibraryName, EntryPoint = "mj_step", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Step(IntPtr model, IntPtr data);
    [DllImport(LibraryName, EntryPoint = "mj_forward", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Forward(IntPtr model, IntPtr data);
    [DllImport(LibraryName, EntryPoint = "mj_stateSize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int StateSize(IntPtr model, int signature);
    [DllImport(LibraryName, EntryPoint = "mj_getState", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GetState(IntPtr model, IntPtr data, double[] state, int signature);
    [DllImport(LibraryName, EntryPoint = "mj_setState", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetState(IntPtr model, IntPtr data, double[] state, int signature);
    [DllImport(LibraryName, EntryPoint = "mj_name2id", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NameToId(IntPtr model, int objectType, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(LibraryName, EntryPoint = "mj_geomDistance", CallingConvention = CallingConvention.Cdecl)]
    private static extern double GeomDistanceNative(IntPtr model, IntPtr data, int geom1, int geom2, double distmax, double[] fromto);

    // mj_ray 3.14.0 C 原型 (include/mujoco/mujoco.h):
    //   mjtNum mj_ray(const mjModel*, const mjData*, const mjtNum pnt[3],
    //     const mjtNum vec[3], const mjtByte* geomgroup, mjtBool flg_static,
    //     int bodyexclude, int geomid[1], mjtNum normal[3]);
    // 四个致命细节 (tmp/rayprobe 探针实证, 违反即延迟 CLR 崩溃或读数错位):
    //  1. 末参数是 mjtNum normal[3] 输出(命中面法线) —— 3.14.0 已把旧版 pdist[1]
    //     改成 normal[3], 传 double[1] 会被 native zero3/copy3 越界写 16 字节踩坏
    //     GC 堆, 延迟 fatal (0x80131506) 且崩溃点漂移。
    //  2. flg_static 是 mjtBool = 1 字节 _Bool, 必须用 byte 传; C# bool 默认
    //     marshal 成 4 字节 Win32 BOOL 会错位。
    //  3. flgStatic=1 才能命中 worldbody 静态 geom; bodyexclude 传 -1 不排除。
    //  4. 返回值是沿 vec 的命中参数 t(单位 vec 时 = 几何距离), 命中点 = pnt + t*vec;
    //     未命中返回 -1。调用前必须 mj_forward 过(d->geom_xpos 已填)。
    [DllImport(LibraryName, EntryPoint = "mj_ray", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double Ray(IntPtr model, IntPtr data, double[] pnt, double[] vec,
        byte[]? geomgroup, byte flgStatic, int bodyExclude, int[] geomId, double[] normal);

    /// <summary>mjVFS 未公开 sizeof; 2000×1000 文件名表 ≈ 2.02 MB, 留一倍余量。</summary>
    private const int VfsBytes = 4 << 20;

    internal static IntPtr CreateModel(string xml, IReadOnlyList<MujocoMeshAsset>? assets = null)
    {
        EnsureAvailable();
        if (assets is null || assets.Count == 0)
        {
            // 无资产模型(v1): 不建 VFS, 与历史调用逐字节一致。
            return CompileXml(xml, IntPtr.Zero);
        }
        var vfs = (IntPtr)NativeMemory.AllocZeroed(VfsBytes);
        var pinned = new List<GCHandle>(assets.Count);
        try
        {
            DefaultVfs(vfs);
            foreach (var asset in assets)
            {
                var handle = GCHandle.Alloc(asset.Bytes, GCHandleType.Pinned);
                pinned.Add(handle);
                AddBufferVfs(vfs, asset.Name, handle.AddrOfPinnedObject(), asset.Bytes.Length);
            }
            return CompileXml(xml, vfs);
        }
        finally
        {
            // 编译后 mjModel 自带网格数据, VFS 与 pin 的缓冲区都可以释放。
            foreach (var handle in pinned) handle.Free();
            DeleteVfs(vfs);
            NativeMemory.Free((void*)vfs);
        }
    }

    private static IntPtr CompileXml(string xml, IntPtr vfs)
    {
        var error = new byte[4096];
        var spec = ParseXmlString(xml, vfs, error, error.Length);
        if (spec == IntPtr.Zero)
        {
            throw new ArgumentException($"MuJoCo MJCF parse failed: {ReadError(error)}", nameof(xml));
        }
        try
        {
            var model = Compile(spec, vfs);
            if (model == IntPtr.Zero)
            {
                var nativeError = Marshal.PtrToStringUTF8(GetSpecError(spec)) ?? "unknown compiler error";
                throw new ArgumentException($"MuJoCo MJCF compile failed: {nativeError}", nameof(xml));
            }
            return model;
        }
        finally
        {
            DeleteSpec(spec);
        }
    }

    // ---- mjModel 内省(3.14.0 win-x64 ABI) ----
    // 三个数组字段的字节偏移由 tmp/wf_probe/{find,confirm}_model_offsets.py 扫描钉死:
    // 编译"几何值独一无二"的参考模型, 在 mjModel 前 8 KB 里找指向已知 double 三元组
    // (ground box size 1.9/1.9/0.025、pos 1.9/1.9/-0.025)与已知 int 序列(geom_type
    // 6,7,5,5 = box,mesh,cylinder,cylinder)的指针槽。DLL 由 ExpectedDllSha256 门控,
    // 因此偏移随版本固定; 调用方(测试)还要用 ground geom 的已知 size/pos 自校验一次,
    // 读错时下面的有限性检查会立刻抛错而不是静默给假值。
    private const int ModelGeomTypeOffset = 2416;
    private const int ModelGeomSizeOffset = 2528;
    private const int ModelGeomPosOffset = 2552;

    /// <summary>mjtGeom 类型(5=cylinder, 6=box, 7=mesh)。</summary>
    internal static int ReadGeomType(IntPtr model, int geomId) => (int)ReadGeomInts(model, ModelGeomTypeOffset, geomId, 1)[0];

    internal static int[] ReadGeomTypes(IntPtr model, int count) => ReadGeomInts(model, ModelGeomTypeOffset, 0, count);

    internal static double[] ReadGeomSize(IntPtr model, int geomId)
        => ReadGeomDoubles(model, ModelGeomSizeOffset, geomId, "size");

    internal static double[] ReadGeomPos(IntPtr model, int geomId)
        => ReadGeomDoubles(model, ModelGeomPosOffset, geomId, "pos");

    /// <summary>两个 geom 的距离与最近点(fromto[0:3] 在 geom1 上, fromto[3:6] 在 geom2 上)。</summary>
    internal static (double Distance, double[] FromTo) GeomDistance(IntPtr model, IntPtr data,
        int geom1, int geom2, double distMax = 10.0)
    {
        var fromto = new double[6];
        var distance = GeomDistanceNative(model, data, geom1, geom2, distMax, fromto);
        return (distance, fromto);
    }

    private static int[] ReadGeomInts(IntPtr model, int offset, int index, int count)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var array = *(int**)((byte*)model + offset);
        if (array == null)
        {
            throw new InvalidOperationException("MuJoCo mjModel array is null; native ABI may be incompatible.");
        }
        var result = new int[count];
        for (var i = 0; i < count; i++) result[i] = array[index + i];
        return result;
    }

    private static double[] ReadGeomDoubles(IntPtr model, int offset, int geomId, string field)
    {
        if (geomId < 0) throw new ArgumentOutOfRangeException(nameof(geomId));
        var array = *(double**)((byte*)model + offset);
        if (array == null)
        {
            throw new InvalidOperationException($"MuJoCo mjModel.geom_{field} is null; native ABI may be incompatible.");
        }
        var result = new double[3];
        for (var i = 0; i < 3; i++) result[i] = array[geomId * 3 + i];
        foreach (var value in result)
        {
            if (!double.IsFinite(value))
            {
                throw new InvalidOperationException(
                    $"MuJoCo mjModel.geom_{field}[{geomId}] is not finite ({value}); native ABI may be incompatible.");
            }
        }
        return result;
    }

    internal static double[] ReadQpos(IntPtr model, IntPtr data) => ReadState(model, data, StateQpos);
    internal static double[] ReadQvel(IntPtr model, IntPtr data) => ReadState(model, data, StateQvel);
    internal static double[] ReadCtrl(IntPtr model, IntPtr data) => ReadState(model, data, StateCtrl);
    internal static void WriteQpos(IntPtr model, IntPtr data, double[] state) => WriteState(model, data, state, StateQpos);
    internal static void WriteQvel(IntPtr model, IntPtr data, double[] state) => WriteState(model, data, state, StateQvel);
    internal static void WriteCtrl(IntPtr model, IntPtr data, double[] state) => WriteState(model, data, state, StateCtrl);

    private static double[] ReadState(IntPtr model, IntPtr data, int signature)
    {
        var state = new double[StateSize(model, signature)];
        GetState(model, data, state, signature);
        return state;
    }

    private static void WriteState(IntPtr model, IntPtr data, double[] state, int signature)
    {
        var expected = StateSize(model, signature);
        if (state.Length != expected)
        {
            throw new InvalidOperationException($"MuJoCo state signature {signature}: expected {expected} values, got {state.Length}.");
        }
        SetState(model, data, state, signature);
    }

    private static string ReadError(byte[] error)
    {
        var end = Array.IndexOf(error, (byte)0);
        return Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end);
    }
}
