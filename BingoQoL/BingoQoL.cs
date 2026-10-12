// Sekiro Bingo QoL helper. Original implementation, 2026-10-09.
// Tutorial offsets and branch change documented by the Sekiro LiveSplit plugin:
// https://gist.github.com/RefinedHornet/70453f953d536229d093a7f86b4559cd
// No timer, progression, save, item, or executable-file modifications.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BingoQoL
{
    public interface IMemory
    {
        byte[] Read(int offset, int count);
        void Write(int offset, byte value);
    }

    public static class Tutorials
    {
        public static string DetectVersion(int size, IMemory memory)
        {
            if (size == 67727360) return "1.02";
            if (size == 67731456) return "1.03";
            if (size == 70066176)
            {
                bool five = IsLogoSignature(memory.Read(0xE1B1AB, 4));
                bool six = IsLogoSignature(memory.Read(0xE1B51B, 4));
                if (five && !six) return "1.05";
                if (six && !five) return "1.06";
            }
            throw new InvalidOperationException("Unrecognized Sekiro build. No tutorial bytes were changed.");
        }

        private static bool IsLogoSignature(byte[] b)
        {
            return b.Length == 4 && (b[0] == 0x74 || b[0] == 0x75)
                && b[1] == 0x30 && b[2] == 0x48 && b[3] == 0x8D;
        }

        public static int[] Offsets(string version)
        {
            switch (version)
            {
                case "1.02": return new int[] { 0xDC7A8E, 0xD6EB98, 0xD6D51C };
                case "1.03": return new int[] { 0xDC83BE, 0xD6F4C8, 0xD6DE4C };
                case "1.05": return new int[] { 0xDEF53D, 0xD94BC8, 0xD934BC };
                case "1.06": return new int[] { 0xDEF8AD, 0xD94F38, 0xD9382C };
                default: throw new InvalidOperationException("Unsupported Sekiro version.");
            }
        }

        public static string Apply(int moduleSize, IMemory memory)
        {
            string version = DetectVersion(moduleSize, memory);
            int[] offsets = Offsets(version);
            byte[] original = new byte[offsets.Length];
            // Validate every site before writing any site. 0x75 is also accepted
            // so an already-running LiveSplit tutorial patch is left alone.
            for (int i = 0; i < offsets.Length; i++)
            {
                original[i] = memory.Read(offsets[i], 1)[0];
                if (original[i] != 0x74 && original[i] != 0x75)
                    throw new InvalidOperationException("Unexpected tutorial code at 0x" +
                        offsets[i].ToString("X") + ". No tutorial bytes were changed.");
            }
            List<int> attempted = new List<int>();
            try
            {
                for (int i = 0; i < offsets.Length; i++)
                {
                    if (original[i] == 0x75) continue;
                    attempted.Add(i);
                    memory.Write(offsets[i], 0x75);
                    if (memory.Read(offsets[i], 1)[0] != 0x75)
                        throw new InvalidOperationException("Tutorial patch readback failed.");
                }
            }
            catch (Exception failure)
            {
                bool rolledBack = true;
                foreach (int i in attempted)
                {
                    try { memory.Write(offsets[i], original[i]); if (memory.Read(offsets[i], 1)[0] != original[i]) rolledBack = false; }
                    catch { rolledBack = false; }
                }
                throw new InvalidOperationException(rolledBack
                    ? "Tutorial patch failed; attempted changes were rolled back. " + failure.Message
                    : "Tutorial patch failed. Close Sekiro to clear any partial runtime changes. " + failure.Message,
                    failure);
            }
            return version;
        }

        public static string ApplyToProcess(Process process)
        {
            ProcessModule module = process.MainModule ?? throw new InvalidOperationException("Game module unavailable.");
            if (!String.Equals(module.ModuleName, "sekiro.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected process is not sekiro.exe.");
            using (ProcessMemory memory = new ProcessMemory(process.Id, module.BaseAddress))
                return Apply(module.ModuleMemorySize, memory);
        }
    }

    internal sealed class ProcessMemory : IMemory, IDisposable
    {
        private IntPtr handle;
        private readonly long baseAddress;
        public ProcessMemory(int processId, IntPtr moduleBase)
        {
            baseAddress = moduleBase.ToInt64();
            handle = Native.OpenProcess(0x438, false, processId);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public byte[] Read(int offset, int count)
        {
            byte[] result = new byte[count];
            UIntPtr read;
            if (!Native.ReadProcessMemory(handle, new IntPtr(baseAddress + offset), result,
                new UIntPtr((uint)count), out read) || read.ToUInt64() != (ulong)count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read Sekiro memory.");
            return result;
        }
        public void Write(int offset, byte value)
        {
            IntPtr address = new IntPtr(baseAddress + offset);
            uint oldProtection;
            if (!Native.VirtualProtectEx(handle, address, new UIntPtr(1), 0x40, out oldProtection))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable the tutorial patch.");
            try
            {
                UIntPtr written;
                if (!Native.WriteProcessMemory(handle, address, new byte[] { value }, new UIntPtr(1), out written)
                    || written.ToUInt64() != 1)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write the tutorial patch.");
                if (!Native.FlushInstructionCache(handle, address, new UIntPtr(1)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not refresh the instruction cache.");
            }
            finally
            {
                uint ignored;
                if (!Native.VirtualProtectEx(handle, address, new UIntPtr(1), oldProtection, out ignored))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore memory protection.");
            }
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; }
        }
    }

    public sealed class BorderlessWindow
    {
        private IntPtr ownedWindow;
        private long originalStyle;
        private long originalExStyle;
        private Native.Rect originalRect;
        private const long Caption = 0x00C00000L;
        private const long OverlappedWindow = 0x00CF0000L;
        private const long Popup = 0x80000000L;
        private const long EdgeStyles = 0x00020301L;
        // Returns Waiting, WindowedRequired, Minimized, or Active.
        public string Update(IntPtr window)
        {
            if (window == IntPtr.Zero || !Native.IsWindow(window) || !Native.IsWindowVisible(window))
                return "Waiting";
            if (Native.IsIconic(window)) return "Minimized";
            long style = Native.GetWindowLongPtr(window, -16).ToInt64();
            bool hasFrame = (style & Caption) == Caption;
            if (ownedWindow != window)
            {
                // A popup-style exclusive fullscreen window cannot be converted
                // to windowed merely by resizing it. Wait for the in-game setting.
                if (!hasFrame) return "WindowedRequired";
                Restore();
                ownedWindow = window;
                originalStyle = style;
                originalExStyle = Native.GetWindowLongPtr(window, -20).ToInt64();
                if (!Native.GetWindowRect(window, out originalRect))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (Native.GetForegroundWindow() != window) return "Active";
            Native.MonitorInfo info = new Native.MonitorInfo();
            info.Size = Marshal.SizeOf<Native.MonitorInfo>();
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.Rect now;
            if (!Native.GetWindowRect(window, out now)) throw new Win32Exception(Marshal.GetLastWin32Error());
            long desiredStyle = (style & ~OverlappedWindow) | Popup;
            long exStyle = Native.GetWindowLongPtr(window, -20).ToInt64();
            long desiredExStyle = exStyle & ~EdgeStyles;
            bool changed = style != desiredStyle || exStyle != desiredExStyle;
            if (changed)
            {
                Native.SetStyle(window, -16, desiredStyle);
                Native.SetStyle(window, -20, desiredExStyle);
            }
            if (changed || now.Left != info.Monitor.Left || now.Top != info.Monitor.Top ||
                now.Right != info.Monitor.Right || now.Bottom != info.Monitor.Bottom)
            {
                if (!Native.SetWindowPos(window, new IntPtr(-2), info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, 0x70))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return "Active";
        }
        public void Restore()
        {
            if (ownedWindow != IntPtr.Zero && Native.IsWindow(ownedWindow))
            {
                Native.SetStyle(ownedWindow, -16, originalStyle);
                Native.SetStyle(ownedWindow, -20, originalExStyle);
                Native.SetWindowPos(ownedWindow, new IntPtr(-2), originalRect.Left, originalRect.Top,
                    originalRect.Right - originalRect.Left, originalRect.Bottom - originalRect.Top, 0x30);
            }
            ownedWindow = IntPtr.Zero;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo
        { public int Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, UIntPtr s, out UIntPtr n);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, UIntPtr s, out UIntPtr n);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool VirtualProtectEx(IntPtr h, IntPtr a, UIntPtr s, uint p, out uint old);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool FlushInstructionCache(IntPtr h, IntPtr a, UIntPtr s);
        [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
        [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
        internal static IntPtr GetWindowLongPtr(IntPtr h, int index) { return GetWindowLongPtrW(h, index); }
        internal static void SetStyle(IntPtr h, int index, long value)
        {
            SetLastError(0);
            IntPtr previous = SetWindowLongPtrW(h, index, new IntPtr(value));
            int error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
        }
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint="GetMonitorInfoW", SetLastError=true)] internal static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo m);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    }
}
