using System;
using System.Runtime.InteropServices;

namespace RamCleaner.Core
{
    public static class MemoryManager
    {
        public static bool EnablePrivilege(string privilegeName)
        {
            IntPtr processHandle = NativeMethods.GetCurrentProcess();
            if (!NativeMethods.OpenProcessToken(processHandle,
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out IntPtr tokenHandle))
            {
                return false;
            }

            try
            {
                if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out NativeMethods.LUID luid))
                    return false;

                var privileges = new NativeMethods.TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                };

                return NativeMethods.AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(tokenHandle);
                }
            }
        }

        public static bool FlushCommand(NativeMethods.SYSTEM_MEMORY_LIST_COMMAND command)
        {
            try
            {
                EnablePrivilege(NativeMethods.SE_PROFILE_SINGLE_PROCESS_NAME);
                EnablePrivilege(NativeMethods.SE_INCREASE_QUOTA_NAME);

                GCHandle handle = GCHandle.Alloc((int)command, GCHandleType.Pinned);
                try
                {
                    int result = NativeMethods.NtSetSystemInformation(
                        NativeMethods.SYSTEM_INFORMATION_CLASS.SystemMemoryListInformation,
                        handle.AddrOfPinnedObject(),
                        Marshal.SizeOf<int>());

                    return result == 0; // STATUS_SUCCESS
                }
                finally
                {
                    handle.Free();
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool FlushWorkingSets() => FlushCommand(NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryEmptyWorkingSets);
        public static bool FlushModifiedPageList() => FlushCommand(NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryFlushModifiedList);
        public static bool FlushPriority0StandbyList() => FlushCommand(NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeLowPriorityStandbyList);
        public static bool FlushStandbyList() => FlushCommand(NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeStandbyList);

        public static bool FlushAll()
        {
            bool r1 = FlushWorkingSets();
            bool r2 = FlushModifiedPageList();
            bool r3 = FlushPriority0StandbyList();
            bool r4 = FlushStandbyList();
            return r1 && r2 && r3 && r4;
        }
    }
}