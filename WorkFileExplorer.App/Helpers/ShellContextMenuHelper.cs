using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkFileExplorer.App.Helpers;

internal static class ShellContextMenuHelper
{
    private const uint CMF_NORMAL = 0x00000000;
    private const int CMD_FIRST = 1;
    private const int CMD_LAST = 0x7FFF;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const int SW_SHOWNORMAL = 1;

    private static readonly Guid IidIContextMenu = new("000214e4-0000-0000-c000-000000000046");
    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");

    public static bool ShowForPaths(Window owner, IReadOnlyList<string> selectedPaths, string? currentDirectoryPath)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle == IntPtr.Zero)
        {
            return false;
        }

        var targetPaths = selectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetPaths.Count == 0 && !string.IsNullOrWhiteSpace(currentDirectoryPath) && Directory.Exists(currentDirectoryPath))
        {
            targetPaths.Add(currentDirectoryPath);
        }

        if (targetPaths.Count == 0)
        {
            return false;
        }

        // The native shell API expects selected items to share one parent folder.
        var primaryParent = GetParentKey(targetPaths[0]);
        targetPaths = targetPaths
            .Where(path => string.Equals(primaryParent, GetParentKey(path), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targetPaths.Count == 0)
        {
            return false;
        }

        var pidls = new List<IntPtr>(targetPaths.Count);
        try
        {
            foreach (var path in targetPaths)
            {
                if (!TryParseDisplayName(path, out var pidl))
                {
                    continue;
                }

                pidls.Add(pidl);
            }

            if (pidls.Count == 0)
            {
                return false;
            }

            var iidShellFolder = IidIShellFolder;
            if (SHBindToParent(pidls[0], ref iidShellFolder, out var shellFolderObj, out var firstChild) != 0 || shellFolderObj is null)
            {
                return false;
            }

            var shellFolder = (IShellFolder)shellFolderObj;
            try
            {
                var childPidls = BuildChildPidls(pidls, firstChild);
                if (childPidls.Length == 0)
                {
                    return false;
                }

                var iidContextMenu = IidIContextMenu;
                if (shellFolder.GetUIObjectOf(
                        IntPtr.Zero,
                        (uint)childPidls.Length,
                        childPidls,
                        ref iidContextMenu,
                        IntPtr.Zero,
                        out var contextMenuPtr) != 0 || contextMenuPtr == IntPtr.Zero)
                {
                    return false;
                }

                var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
                try
                {
                    var hMenu = CreatePopupMenu();
                    if (hMenu == IntPtr.Zero)
                    {
                        return false;
                    }

                    try
                    {
                        if (contextMenu.QueryContextMenu(hMenu, 0, CMD_FIRST, CMD_LAST, CMF_NORMAL) < 0)
                        {
                            return false;
                        }

                        if (!GetCursorPos(out var point))
                        {
                            return false;
                        }

                        _ = SetForegroundWindow(ownerHandle);
                        var selectedCommand = TrackPopupMenuEx(
                            hMenu,
                            TPM_RETURNCMD | TPM_RIGHTBUTTON,
                            point.X,
                            point.Y,
                            ownerHandle,
                            IntPtr.Zero);

                        if (selectedCommand == 0)
                        {
                            return false;
                        }

                        var invoke = new CMINVOKECOMMANDINFOEX
                        {
                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                            fMask = CMIC_MASK_UNICODE,
                            hwnd = ownerHandle,
                            lpVerb = (IntPtr)(selectedCommand - CMD_FIRST),
                            lpVerbW = (IntPtr)(selectedCommand - CMD_FIRST),
                            nShow = SW_SHOWNORMAL
                        };

                        contextMenu.InvokeCommand(ref invoke);
                        return true;
                    }
                    finally
                    {
                        _ = DestroyMenu(hMenu);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(contextMenu);
                    Marshal.Release(contextMenuPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shellFolder);
            }
        }
        finally
        {
            foreach (var pidl in pidls.Where(p => p != IntPtr.Zero))
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static string GetParentKey(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            return parent;
        }

        var root = Path.GetPathRoot(trimmed);
        return string.IsNullOrWhiteSpace(root) ? trimmed : root;
    }

    private static IntPtr[] BuildChildPidls(IReadOnlyList<IntPtr> absolutePidls, IntPtr firstChild)
    {
        var children = new List<IntPtr>(absolutePidls.Count);
        if (firstChild != IntPtr.Zero)
        {
            children.Add(firstChild);
        }

        for (var index = 1; index < absolutePidls.Count; index++)
        {
            var iidShellFolder = IidIShellFolder;
            if (SHBindToParent(absolutePidls[index], ref iidShellFolder, out var parent, out var child) == 0)
            {
                if (parent is not null)
                {
                    Marshal.ReleaseComObject(parent);
                }

                if (child != IntPtr.Zero)
                {
                    children.Add(child);
                }
            }
        }

        return children.ToArray();
    }

    private static bool TryParseDisplayName(string path, out IntPtr pidl)
    {
        pidl = IntPtr.Zero;
        return SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr pidl,
        uint sfgaoIn,
        out uint attributes);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv,
        out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr ownerWindow,
        IntPtr reserved);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WinPoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpTitleW;
        public WinPoint ptInvoke;
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string displayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr enumIdList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint flags, out IntPtr name);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(int idCmd, uint uFlags, int reserved, [MarshalAs(UnmanagedType.LPStr)] string name, int cchMax);
    }
}
