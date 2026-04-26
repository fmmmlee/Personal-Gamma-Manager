using System;
using System.Runtime.InteropServices;

namespace Gamma_Manager
{
    internal class ExternalMonitor
    {
        #region DllImport
        [DllImport("dxva2.dll", EntryPoint = "GetMonitorContrast")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorContrast(IntPtr handle, ref uint minimumContrast, ref uint currentContrast, ref uint maxContrast);

        [DllImport("dxva2.dll", EntryPoint = "SetMonitorContrast")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetMonitorContrast(IntPtr handle, uint newContrast);
        #endregion

        #region Get & Set
        public static void SetContrast(IntPtr hPhysicalMonitor, uint contrast)
        {
            uint realNewValue = 100 * contrast / 100;
            SetMonitorContrast(hPhysicalMonitor, realNewValue);
        }

        public static int GetContrast(IntPtr hPhysicalMonitor)
        {
            uint min = 0;
            uint cur = 0;
            uint max = 0;

            GetMonitorContrast(hPhysicalMonitor, ref min, ref cur, ref max);

            return (int)cur;
        }
        #endregion
    }
}
