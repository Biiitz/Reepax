using System;
using System.IO;
using System.Runtime.InteropServices;
using Reepax.Services.Storage;

namespace Reepax.Services.AdBlock.Native;

/// <summary>
/// Direct P/Invoke bindings to the native Rust-based Brave adblock engine (reepax_adblock.dll).
/// </summary>
public static class AdBlockRustNative
{
    private const string DllName = "reepax_adblock";

    public static bool IsAvailable { get; private set; }

    static AdBlockRustNative()
    {
        try
        {
            // Test if native library can be loaded
            var testPtr = NativeMethods.adblock_create();
            if (testPtr != IntPtr.Zero)
            {
                NativeMethods.adblock_free(testPtr);
                IsAvailable = true;
                AppLogger.Info("[AdBlockRust] Native Rust engine library loaded successfully.");
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AppLogger.Warn($"[AdBlockRust] Native engine unavailable, falling back to built-in C# engine: {ex.Message}");
        }
    }

    public static IntPtr Create() => IsAvailable ? NativeMethods.adblock_create() : IntPtr.Zero;

    public static IntPtr CreateFromRules(string rules)
    {
        if (!IsAvailable || string.IsNullOrEmpty(rules)) return IntPtr.Zero;
        try
        {
            return NativeMethods.adblock_create_from_rules(rules);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AdBlockRust] Failed to create engine from rules: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    public static IntPtr CreateFromBuffer(byte[] buffer)
    {
        if (!IsAvailable || buffer == null || buffer.Length == 0) return IntPtr.Zero;
        try
        {
            return NativeMethods.adblock_create_from_buffer(buffer, (nuint)buffer.Length);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AdBlockRust] Failed to deserialize engine: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    public static byte[]? Serialize(IntPtr engine)
    {
        if (!IsAvailable || engine == IntPtr.Zero) return null;
        try
        {
            var ptr = NativeMethods.adblock_serialize(engine, out var outLen);
            if (ptr == IntPtr.Zero || outLen == 0) return null;

            var bytes = new byte[(int)outLen];
            Marshal.Copy(ptr, bytes, 0, (int)outLen);
            NativeMethods.adblock_free_buffer(ptr, outLen);
            return bytes;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AdBlockRust] Serialization error: {ex.Message}");
            return null;
        }
    }

    public static int CheckNetwork(IntPtr engine, string url, string sourceUrl, string requestType)
    {
        if (!IsAvailable || engine == IntPtr.Zero) return 0;
        try
        {
            return NativeMethods.adblock_check_network(engine, url, sourceUrl ?? string.Empty, requestType ?? "other");
        }
        catch
        {
            return 0;
        }
    }

    public static (string Css, string Script) GetCosmeticResources(IntPtr engine, string url)
    {
        if (!IsAvailable || engine == IntPtr.Zero || string.IsNullOrEmpty(url)) return (string.Empty, string.Empty);
        try
        {
            var ptr = NativeMethods.adblock_url_cosmetic_resources(engine, url);
            if (ptr == IntPtr.Zero) return (string.Empty, string.Empty);

            var jsonStr = Marshal.PtrToStringUTF8(ptr);
            NativeMethods.adblock_free_string(ptr);

            if (string.IsNullOrEmpty(jsonStr)) return (string.Empty, string.Empty);

            // Simple JSON extraction to avoid heavy parser dependencies
            var css = ExtractJsonField(jsonStr, "css");
            var script = ExtractJsonField(jsonStr, "script");
            return (css, script);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string ExtractJsonField(string json, string field)
    {
        try
        {
            var key = $"\"{field}\":\"";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;

            var start = idx + key.Length;
            var sb = new System.Text.StringBuilder();
            bool escaping = false;

            for (int i = start; i < json.Length; i++)
            {
                var c = json[i];
                if (escaping)
                {
                    switch (c)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(c); break;
                    }
                    escaping = false;
                }
                else if (c == '\\')
                {
                    escaping = true;
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void Free(IntPtr engine)
    {
        if (!IsAvailable || engine == IntPtr.Zero) return;
        try
        {
            NativeMethods.adblock_free(engine);
        }
        catch { }
    }

    private static class NativeMethods
    {
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr adblock_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr adblock_create_from_rules([MarshalAs(UnmanagedType.LPUTF8Str)] string rules);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr adblock_create_from_buffer(byte[] buf, nuint len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr adblock_serialize(IntPtr engine, out nuint outLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void adblock_free_buffer(IntPtr buf, nuint len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int adblock_check_network(
            IntPtr engine,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceUrl,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestType);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr adblock_url_cosmetic_resources(
            IntPtr engine,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void adblock_free_string(IntPtr ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void adblock_free(IntPtr engine);
    }
}
