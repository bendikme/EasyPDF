using System.Runtime.InteropServices;

namespace EasyPDF.Pdf;

/// <summary>The process's own memory as Windows counts it - native allocations included, which is where PDFium's are.</summary>
internal static partial class ProcessMemory
{
    /// <summary>Private bytes: what the process has committed for itself. 0 when it cannot be read.</summary>
    public static long Private()
    {
        var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size) ? (long)counters.PrivateUsage : 0;
    }

    /// <summary>PROCESS_MEMORY_COUNTERS_EX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref Counters counters, uint size);
}
