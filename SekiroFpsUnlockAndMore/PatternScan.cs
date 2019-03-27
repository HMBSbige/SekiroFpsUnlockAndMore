﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SekiroFpsUnlockAndMore
{
    class PatternScan
    {
        /// <summary>
        /// Finds a pattern or signature inside another process's memory.
        /// </summary>
        /// <param name="hProcess">Handle to the process in whose memory pattern will be searched for.</param>
        /// <param name="pModule">Module which will be searched for the pattern.</param>
        /// <param name="szPattern">A character-delimited string representing the pattern to be found.</param>
        /// <param name="szMask">A string of 'x' (match), '!' (not-match), or '?' (wildcard).</param>
        /// <param name="cDelimiter">Determines how the string will be split. If null, defaults to ' '.</param>
        /// <returns>The address of the beginning of the pattern if found, 0 if not found</returns>
        internal static Int64 FindPattern(IntPtr hProcess, ProcessModule pModule, string szPattern, string szMask, char cDelimiter = ' ')
        {
            var saPattern = szPattern.Split(cDelimiter);
            var bPattern = new byte[saPattern.Length];
            for (var i = 0; i < saPattern.Length; i++)
                bPattern[i] = Convert.ToByte(saPattern[i], 0x10);

            if (bPattern == null || bPattern.Length == 0)
                throw new ArgumentNullException(nameof(szPattern), @"Pattern's length is zero!");
            if (bPattern.Length != szMask.Length)
                throw new ArgumentException("Pattern's bytes and szMask must be of the same size!");

            long dwStart = 0;
            if (IntPtr.Size == 4)
                dwStart = (uint)pModule.BaseAddress;
            else if (IntPtr.Size == 8)
                dwStart = (long)pModule.BaseAddress;
            var nSize = pModule.ModuleMemorySize;

            var bData = new byte[nSize];

            if (!ReadProcessMemory(hProcess, dwStart, bData, nSize, out var lpNumberOfBytesRead))
                throw new Exception("ReadProcessMemory error!");
            if (lpNumberOfBytesRead.ToInt64() != nSize)
                throw new Exception("ReadProcessMemory error!");
            if (bData == null || bData.Length == 0)
                throw new Exception("Could not read memory in FindPattern.");

            long ix;
            var patternLength = bPattern.Length;
            var dataLength = bData.Length - patternLength;

            for (ix = 0; ix < dataLength; ix++)
            {
                var bFound = true;
                int iy;
                for (iy = 0; iy < patternLength; iy++)
                {
                    if (szMask[iy] == 'x' && bPattern[iy] != bData[ix + iy] ||
                        szMask[iy] == '!' && bPattern[iy] == bData[ix + iy])
                    {
                        bFound = false;
                        break;
                    }
                }
                if (bFound)
                    return Convert.ToInt64(dwStart + ix);
            }
            return 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
           IntPtr hProcess,
           Int64 lpBaseAddress,
           [Out] Byte[] lpBuffer,
           Int64 dwSize,
           out IntPtr lpNumberOfBytesRead);
    }
}
