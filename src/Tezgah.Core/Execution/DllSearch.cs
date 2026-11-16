using Tezgah.Core.Interop;

namespace Tezgah.Core.Execution;

/// <summary>Process-wide DLL search hardening, applied once at startup before any UI or native code loads.</summary>
public static class DllSearch
{
    /// <summary>
    /// Restricts the default search for every later LoadLibrary in the process (ours, the runtime's, WPF's and those of
    /// in-process shell components) to the application folder, System32 and explicitly added folders. The current
    /// directory and PATH are no longer searched, so a planted DLL in a scanned project folder or a writable PATH entry
    /// cannot be picked up. Returns false if Windows refused (the process keeps the legacy search order).
    /// </summary>
    public static bool Restrict() => NativeMethods.SetDefaultDllDirectories(NativeMethods.LoadLibrarySearchDefaultDirs);
}
