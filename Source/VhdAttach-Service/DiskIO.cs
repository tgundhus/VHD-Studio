using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VhdAttachService {
    internal static class DiskIO {

        public static void InitializeDisk(string path) {
            using (SafeFileHandle handle = NativeMethods.CreateFile(path, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero)) {
                if (handle.IsInvalid) { throw new Win32Exception(); }
                EnsureBlank(handle);

                var signature = new byte[4];
                RandomNumberGenerator.Create().GetBytes(signature);

                Int32 bytesOut = 0;

                var cd = new NativeMethods.CREATE_DISK();
                cd.PartitionStyle = NativeMethods.PARTITION_STYLE.PARTITION_STYLE_MBR;
                cd.Mbr.Signature = BitConverter.ToInt32(signature, 0);
                if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_CREATE_DISK, ref cd, Marshal.SizeOf(cd), IntPtr.Zero, 0, ref bytesOut, IntPtr.Zero) == false) { throw new Win32Exception(); }

                if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, ref bytesOut, IntPtr.Zero) == false) { throw new Win32Exception(); } //just update cache

                var pi = new NativeMethods.PARTITION_INFORMATION();
                if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_GET_PARTITION_INFO, IntPtr.Zero, 0, ref pi, Marshal.SizeOf(pi), ref bytesOut, IntPtr.Zero) == false) { throw new Win32Exception(); }

                var dli = new NativeMethods.DRIVE_LAYOUT_INFORMATION_EX();
                dli.PartitionStyle = NativeMethods.PARTITION_STYLE.PARTITION_STYLE_MBR;
                dli.PartitionCount = 1;
                dli.Partition1.PartitionStyle = NativeMethods.PARTITION_STYLE.PARTITION_STYLE_MBR;
                dli.Partition1.StartingOffset = 65536;
                dli.Partition1.PartitionLength = pi.PartitionLength - dli.Partition1.StartingOffset;
                dli.Partition1.PartitionNumber = 1;
                dli.Partition1.RewritePartition = true;
                dli.Partition1.Mbr.PartitionType = NativeMethods.PARTITION_IFS;
                dli.Partition1.Mbr.BootIndicator = true;
                dli.Partition1.Mbr.RecognizedPartition = true;
                dli.Partition1.Mbr.HiddenSectors = 0;
                dli.Mbr.Signature = BitConverter.ToInt32(signature, 0);
                if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_SET_DRIVE_LAYOUT_EX, ref dli, Marshal.SizeOf(dli), IntPtr.Zero, 0, ref bytesOut, IntPtr.Zero) == false) { throw new Win32Exception(); }

                if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, ref bytesOut, IntPtr.Zero) == false) { throw new Win32Exception(); } //just update cache
            }
        }



        /// <summary>
        /// Refuses to initialize anything but a brand-new, all-zero disk, so an existing partition table can never be overwritten.
        /// </summary>
        private static void EnsureBlank(SafeFileHandle handle) {
            const int probeSize = 1024 * 1024;
            var buffer = new byte[probeSize];
            int total = 0;
            using (var stream = new System.IO.FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), System.IO.FileAccess.Read, 0)) {
                int read;
                while ((total < probeSize) && ((read = stream.Read(buffer, total, probeSize - total)) > 0)) { total += read; }
            }
            if (total < 1024) { throw new InvalidOperationException("Disk could not be read; it was not initialized."); }
            var hasMbrSignature = (buffer[510] == 0x55) && (buffer[511] == 0xAA);
            var hasGptHeader = System.Text.Encoding.ASCII.GetString(buffer, 512, 8) == "EFI PART";
            if (hasMbrSignature || hasGptHeader || Array.Exists(buffer, b => b != 0)) {
                throw new InvalidOperationException("Disk already contains data or a partition table; it was not initialized.");
            }
        }


        private static class NativeMethods {

            public const int GENERIC_READ = -2147483648;
            public const int GENERIC_WRITE = 1073741824;
            public const int OPEN_EXISTING = 3;

            public const int IOCTL_DISK_CREATE_DISK = 0x7C058;
            public const int IOCTL_DISK_GET_PARTITION_INFO = 0x74004;
            public const int IOCTL_DISK_UPDATE_PROPERTIES = 0x70140;
            public const int IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x70050;
            public const int IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x7C054;
            public const byte PARTITION_IFS = 0x07;


            public enum PARTITION_STYLE : int {
                PARTITION_STYLE_MBR = 0,
                PARTITION_STYLE_GPT = 1,
                PARTITION_STYLE_RAW = 2,
            }


            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct CREATE_DISK {
                public PARTITION_STYLE PartitionStyle;
                public CREATE_DISK_MBR Mbr;
            }

            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct CREATE_DISK_MBR {
                public Int32 Signature;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
                public Byte[] Reserved; //because of CREATE_DISK_GPT
            }

            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct PARTITION_INFORMATION {
                public Int64 StartingOffset;
                public Int64 PartitionLength;
                public UInt32 HiddenSectors;
                public UInt32 PartitionNumber;
                public Byte PartitionType;
                public Byte BootIndicator;
                public Byte RecognizedPartition;
                public Byte RewritePartition;
            }

            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct DRIVE_LAYOUT_INFORMATION_EX {
                public PARTITION_STYLE PartitionStyle;
                public Int32 PartitionCount;
                public DRIVE_LAYOUT_INFORMATION_MBR Mbr;
                public PARTITION_INFORMATION_EX Partition1;
            }

            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct DRIVE_LAYOUT_INFORMATION_MBR {
                public Int32 Signature;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                public Byte[] Reserved; //because of DRIVE_LAYOUT_INFORMATION_GPT
            }

            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct PARTITION_INFORMATION_EX {
                public PARTITION_STYLE PartitionStyle;
                public Int64 StartingOffset;
                public Int64 PartitionLength;
                public Int32 PartitionNumber;
                [MarshalAsAttribute(UnmanagedType.U1)] //native BOOLEAN is one byte
                public Boolean RewritePartition;
                public PARTITION_INFORMATION_MBR Mbr;
            }

            [StructLayoutAttribute(LayoutKind.Sequential)]
            public struct PARTITION_INFORMATION_MBR {
                public Byte PartitionType;
                [MarshalAsAttribute(UnmanagedType.U1)] //native BOOLEAN is one byte; 4-byte marshalling shifted HiddenSectors
                public Boolean BootIndicator;
                [MarshalAsAttribute(UnmanagedType.U1)]
                public Boolean RecognizedPartition;
                public Int32 HiddenSectors;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)]
                public Byte[] Reserved; //because of PARTITION_INFORMATION_GPT
            }



            [DllImportAttribute("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
            public static extern SafeFileHandle CreateFile([MarshalAsAttribute(UnmanagedType.LPWStr)] string lpFileName, Int32 dwDesiredAccess, Int32 dwShareMode, IntPtr lpSecurityAttributes, Int32 dwCreationDisposition, Int32 dwFlagsAndAttributes, IntPtr hTemplateFile);

            [DllImportAttribute("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
            [return: MarshalAsAttribute(UnmanagedType.Bool)]
            public static extern Boolean DeviceIoControl(SafeFileHandle hDevice, Int32 dwIoControlCode, ref CREATE_DISK lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, Int32 nOutBufferSize, ref Int32 lpBytesReturned, IntPtr lpOverlapped);

            [DllImportAttribute("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
            [return: MarshalAsAttribute(UnmanagedType.Bool)]
            public static extern Boolean DeviceIoControl(SafeFileHandle hDevice, Int32 dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, ref PARTITION_INFORMATION lpOutBuffer, Int32 nOutBufferSize, ref Int32 lpBytesReturned, IntPtr lpOverlapped);

            [DllImportAttribute("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
            [return: MarshalAsAttribute(UnmanagedType.Bool)]
            public static extern Boolean DeviceIoControl(SafeFileHandle hDevice, Int32 dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, Int32 nOutBufferSize, ref Int32 lpBytesReturned, IntPtr lpOverlapped);

            [DllImportAttribute("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
            [return: MarshalAsAttribute(UnmanagedType.Bool)]
            public static extern Boolean DeviceIoControl(SafeFileHandle hDevice, Int32 dwIoControlCode, ref DRIVE_LAYOUT_INFORMATION_EX lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, Int32 nOutBufferSize, ref Int32 lpBytesReturned, IntPtr lpOverlapped);

        }

    }
}
