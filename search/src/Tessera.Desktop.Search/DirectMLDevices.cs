using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tessera.Desktop.Search;

/// <summary>
/// Finds a hardware DXGI adapter so DirectML is only chosen when a real GPU
/// exists; without this check DirectML silently runs on WARP, the software
/// rasteriser, which is slower than the CPU provider.
/// </summary>
public static class DirectMLDevices
{
    public sealed record Adapter(int Index, string Name, long DedicatedVideoMemory);

    /// <summary>
    /// The hardware adapter with the most dedicated video memory, or null.
    /// DXGI lists the integrated GPU first on many machines; on this one the
    /// Intel iGPU came before the RTX 5060 Ti and ran ten times slower.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Adapter? BestHardwareAdapter()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return Enumerate().OrderByDescending(a => a.DedicatedVideoMemory).ThenBy(a => a.Index).FirstOrDefault(); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (COMException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    public static unsafe List<Adapter> Enumerate()
    {
        var result = new List<Adapter>();
        Guid iidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        void* factory;
        int hr = CreateDXGIFactory1(&iidFactory1, &factory);
        if (hr < 0 || factory == null) return result;
        try
        {
            // IDXGIFactory1 vtable: IUnknown[0..2], IDXGIObject[3..6], IDXGIFactory[7..11], EnumAdapters1 = 12
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)(*(void***)factory)[12];
            for (uint i = 0; ; i++)
            {
                void* adapter;
                hr = enumAdapters1(factory, i, &adapter);
                if (hr != 0 || adapter == null) break; // DXGI_ERROR_NOT_FOUND ends the list
                try
                {
                    // IDXGIAdapter1 vtable: IUnknown[0..2], IDXGIObject[3..6], IDXGIAdapter[7..9], GetDesc1 = 10
                    var getDesc1 = (delegate* unmanaged[Stdcall]<void*, DxgiAdapterDesc1*, int>)(*(void***)adapter)[10];
                    DxgiAdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) == 0)
                    {
                        const uint software = 2; // DXGI_ADAPTER_FLAG_SOFTWARE
                        if ((desc.Flags & software) == 0)
                        {
                            string name = new string(desc.Description, 0, DescriptionLength(desc.Description));
                            result.Add(new Adapter((int)i, name, (long)desc.DedicatedVideoMemory));
                        }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return result;
    }

    private static unsafe int DescriptionLength(char* description)
    {
        int n = 0;
        while (n < 128 && description[n] != '\0') n++;
        return n;
    }

    private static unsafe void Release(void* unknown)
    {
        var release = (delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)unknown)[2];
        release(unknown);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern unsafe int CreateDXGIFactory1(Guid* riid, void** factory);
}
