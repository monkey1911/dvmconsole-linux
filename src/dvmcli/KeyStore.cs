// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* In-memory store for traffic keys delivered by the FNE (KMM). Keys are held only in
* memory for the life of the process and are never logged or written to disk.
*/

using System.Collections.Concurrent;
using fnecore.P25;

namespace dvmcli
{
    /// <summary>
    /// Traffic keys by (algorithm ID, key ID).
    /// </summary>
    public sealed class KeyStore
    {
        private readonly ConcurrentDictionary<(byte alg, ushort kid), byte[]> keys = new();

        /// <summary>Raised when a key is added or replaced.</summary>
        public event Action<byte, ushort> KeyAdded;

        /// <summary>Number of keys held.</summary>
        public int Count => keys.Count;

        /// <summary>
        /// Stores a key; returns false if the algorithm is not one P25 voice uses or the key is empty.
        /// </summary>
        public bool Store(byte algId, ushort keyId, byte[] key)
        {
            if (key == null || key.Length == 0 || !IsSupported(algId))
                return false;

            keys[(algId, keyId)] = (byte[])key.Clone();
            KeyAdded?.Invoke(algId, keyId);
            return true;
        }

        /// <summary>
        /// Gets a copy of a key.
        /// </summary>
        public bool TryGet(byte algId, ushort keyId, out byte[] key)
        {
            if (keys.TryGetValue((algId, keyId), out byte[] stored))
            {
                key = (byte[])stored.Clone();
                return true;
            }
            key = null;
            return false;
        }

        /// <summary>
        /// True for the P25 voice algorithms the decoder supports.
        /// </summary>
        public static bool IsSupported(byte algId) =>
            algId == P25Defines.P25_ALGO_AES || algId == P25Defines.P25_ALGO_DES || algId == P25Defines.P25_ALGO_ARC4;

        /// <summary>
        /// Human-readable algorithm name.
        /// </summary>
        public static string AlgorithmName(byte algId) => algId switch
        {
            P25Defines.P25_ALGO_UNENCRYPT => "clear",
            P25Defines.P25_ALGO_AES => "AES-256",
            P25Defines.P25_ALGO_DES => "DES-OFB",
            P25Defines.P25_ALGO_ARC4 => "ADP/ARC4",
            _ => $"0x{algId:X2}"
        };
    } // public sealed class KeyStore
}
