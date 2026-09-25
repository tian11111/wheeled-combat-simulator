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

    internal static IntPtr CreateModel(string xml)
    {
        EnsureAvailable();
        var error = new byte[4096];
        var spec = ParseXmlString(xml, IntPtr.Zero, error, error.Length);
        if (spec == IntPtr.Zero)
        {
            throw new ArgumentException($"MuJoCo MJCF parse failed: {ReadError(error)}", nameof(xml));
        }
        try
        {
            var model = Compile(spec, IntPtr.Zero);
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
