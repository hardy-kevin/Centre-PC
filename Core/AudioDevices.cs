using System.Runtime.InteropServices;

namespace CentrePC.Core;

/// <summary>Une sortie (casque, enceintes, écran HDMI…) ou une entrée (micro) audio active.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool Render, int WaveIndex);

/// <summary>
/// Tous les périphériques audio branchés : liste, son de test joué sur une sortie précise, capture d'un micro précis,
/// et choix du périphérique par défaut de Windows (comme dans les paramètres du son).
/// </summary>
public static class AudioDevices
{
    public static List<AudioDevice> List(bool render)
    {
        try { return CoreAudio.Endpoints(render); }
        catch (Exception ex) { Log.Error("Périphériques audio", ex); return new(); }
    }

    /// <summary>Périphérique utilisé par défaut par Windows (multimédia et communications).</summary>
    public static bool SetDefault(AudioDevice d)
    {
        try { return CoreAudio.SetDefault(d.Id); }
        catch (Exception ex) { Log.Error("Périphérique audio par défaut", ex); return false; }
    }

    /// <summary>Joue un son WAV (16 bits) sur cette sortie précisément, sans changer la sortie par défaut.</summary>
    public static Task PlayOnAsync(AudioDevice d, byte[] wav) => Task.Run(() =>
    {
        if (d.WaveIndex < 0) return;
        // En-tête WAV de 44 octets : canaux et fréquence
        short channels = BitConverter.ToInt16(wav, 22);
        int rate = BitConverter.ToInt32(wav, 24);
        int len = wav.Length - 44;
        var fmt = new WaveFormat { Tag = 1, Channels = (ushort)channels, Rate = rate, AvgBytes = rate * channels * 2, BlockAlign = (ushort)(channels * 2), Bits = 16 };
        if (waveOutOpen(out var h, d.WaveIndex, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;
        var data = Marshal.AllocHGlobal(len);
        var hdr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        try
        {
            Marshal.Copy(wav, 44, data, len);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)len }, hdr, false);
            int size = Marshal.SizeOf<WaveHeader>();
            waveOutPrepareHeader(h, hdr, size);
            waveOutWrite(h, hdr, size);
            var until = DateTime.UtcNow.AddSeconds(len / (double)fmt.AvgBytes + 2);
            while ((Marshal.PtrToStructure<WaveHeader>(hdr).Flags & 1) == 0 && DateTime.UtcNow < until) Thread.Sleep(20);   // WHDR_DONE
            waveOutReset(h);
            waveOutUnprepareHeader(h, hdr, size);
        }
        finally
        {
            waveOutClose(h);
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(data);
        }
    });

    // ------------------------------------------------------------------ correspondance avec les numéros de l'API wave
    /// <summary>Numéro « wave » (waveOut / waveIn) d'un périphérique à partir de son identifiant Windows.</summary>
    internal static int WaveIndexOf(string endpointId, bool render)
    {
        int n = render ? waveOutGetNumDevs() : waveInGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            int size = 0;
            int r = render ? waveOutMessage((IntPtr)i, 0x812, ref size, IntPtr.Zero) : waveInMessage((IntPtr)i, 0x812, ref size, IntPtr.Zero);   // DRV_QUERYFUNCTIONINSTANCEIDSIZE
            if (r != 0 || size <= 0) continue;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                r = render ? waveOutMessage((IntPtr)i, 0x811, buf, (IntPtr)size) : waveInMessage((IntPtr)i, 0x811, buf, (IntPtr)size);   // DRV_QUERYFUNCTIONINSTANCEID
                if (r == 0 && string.Equals(Marshal.PtrToStringUni(buf), endpointId, StringComparison.OrdinalIgnoreCase)) return i;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public ushort Tag, Channels; public int Rate, AvgBytes; public ushort BlockAlign, Bits, Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

    [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveOutMessage(IntPtr device, uint msg, ref int size, IntPtr unused);
    [DllImport("winmm.dll")] private static extern int waveOutMessage(IntPtr device, uint msg, IntPtr buffer, IntPtr size);
    [DllImport("winmm.dll")] private static extern int waveInMessage(IntPtr device, uint msg, ref int size, IntPtr unused);
    [DllImport("winmm.dll")] private static extern int waveInMessage(IntPtr device, uint msg, IntPtr buffer, IntPtr size);
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr wave, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr wave);
}

internal static partial class CoreAudio
{
    /// <summary>Périphériques actifs (sortie ou entrée), le périphérique par défaut signalé.</summary>
    public static List<AudioDevice> Endpoints(bool render)
    {
        var res = new List<AudioDevice>();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            string defaultId = null;
            if (en.GetDefaultAudioEndpoint(render ? 0 : 1, render ? 1 : 2, out var def) == 0 && def != null)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }
            if (en.EnumAudioEndpoints(render ? 0 : 1, 1, out var list) != 0 || list == null) return res;   // DEVICE_STATE_ACTIVE
            list.GetCount(out var count);
            for (int i = 0; i < count; i++)
            {
                if (list.Item(i, out var dev) != 0 || dev == null) continue;
                dev.GetId(out var id);
                var name = FriendlyName(dev) ?? (render ? "Sortie audio" : "Micro");
                res.Add(new AudioDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), render, AudioDevices.WaveIndexOf(id, render)));
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(list);
        }
        finally { Marshal.ReleaseComObject(en); }
        return res.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Périphérique par défaut pour les trois usages de Windows (console, multimédia, communications).</summary>
    public static bool SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    // Interface de Windows utilisée par les paramètres du son pour changer le périphérique par défaut
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr id);
        [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr id, int defaultPeriod, IntPtr period, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr id, int visible);
    }
}
