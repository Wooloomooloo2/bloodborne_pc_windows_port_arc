using System.Runtime.InteropServices;

namespace BloodborneLauncher.Services;

/// <summary>The graphics adapter name through DXGI: the high-performance adapter when the
/// system has several (IDXGIFactory6), else the hardware adapter with the most dedicated
/// memory. COM is called through the vtables, so no interop assembly is needed.</summary>
internal static unsafe partial class GpuInfo
{
    public static string Describe()
    {
        try
        {
            return Query() ?? "Unknown";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return "Unknown";
        }
    }

    private static string? Query()
    {
        var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        var iidFactory6 = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
        var iidAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");
        void* factory;
        if (CreateDXGIFactory1(&iidFactory1, &factory) < 0 || factory is null)
        {
            return null;
        }
        try
        {
            // IDXGIFactory6::EnumAdapterByGpuPreference (vtable slot 29), HIGH_PERFORMANCE = 2.
            void* factory6;
            if (QueryInterface(factory, &iidFactory6, &factory6) >= 0 && factory6 is not null)
            {
                try
                {
                    var enumByPref = (delegate* unmanaged[Stdcall]<void*, uint, int, Guid*, void**, int>)VTable(factory6)[29];
                    for (uint i = 0; ; i++)
                    {
                        void* adapter;
                        if (enumByPref(factory6, i, 2, &iidAdapter1, &adapter) < 0 || adapter is null)
                        {
                            break;
                        }
                        var desc = Describe(adapter, out var software, out _);
                        Release(adapter);
                        if (!software && desc is not null)
                        {
                            return desc;
                        }
                    }
                }
                finally
                {
                    Release(factory6);
                }
            }
            // IDXGIFactory1::EnumAdapters1 (slot 12): the one with the most video memory.
            var enum1 = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)VTable(factory)[12];
            string? best = null;
            nuint bestMem = 0;
            for (uint i = 0; ; i++)
            {
                void* adapter;
                if (enum1(factory, i, &adapter) < 0 || adapter is null)
                {
                    break;
                }
                var desc = Describe(adapter, out var software, out var mem);
                Release(adapter);
                if (!software && desc is not null && (best is null || mem > bestMem))
                {
                    best = desc;
                    bestMem = mem;
                }
            }
            return best;
        }
        finally
        {
            Release(factory);
        }
    }

    private static string? Describe(void* adapter, out bool software, out nuint dedicated)
    {
        // IDXGIAdapter1::GetDesc1 (slot 10).
        DXGI_ADAPTER_DESC1 desc;
        var getDesc1 = (delegate* unmanaged[Stdcall]<void*, DXGI_ADAPTER_DESC1*, int>)VTable(adapter)[10];
        if (getDesc1(adapter, &desc) < 0)
        {
            software = true;
            dedicated = 0;
            return null;
        }
        software = (desc.Flags & 2) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
        dedicated = desc.DedicatedVideoMemory;
        return new string(desc.Description).Trim();
    }

    private static void** VTable(void* obj) => *(void***)obj;

    private static int QueryInterface(void* obj, Guid* iid, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTable(obj)[0])(obj, iid, result);

    private static void Release(void* obj) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)VTable(obj)[2])(obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, void** factory);
}
