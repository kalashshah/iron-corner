using System;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;

// Hold-to-talk: 16 kHz mono, max 10 s, returned as a PCM16 WAV.
public static class Mic
{
    const int Rate = 16000, MaxSeconds = 10;
    static AudioClip clip;

    public static bool Available => Microphone.devices.Length > 0;

    public static bool Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        { UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone); return false; }
#endif
        if (!Available) return false;
        clip = Microphone.Start(null, false, MaxSeconds, Rate);
        return clip != null;
    }

    public static byte[] Stop()
    {
        if (clip == null) return null;
        int pos = Microphone.GetPosition(null);
        Microphone.End(null);
        if (pos <= 0) pos = clip.samples; // hit the 10 s cap
        var samples = new float[pos * clip.channels];
        clip.GetData(samples, 0);
        int channels = clip.channels;
        clip = null;
        return samples.Length < Rate / 4 ? null : Wav(samples, Rate, channels);
    }

    public static byte[] Wav(float[] samples, int rate, int channels)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataLen); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write((short)Mathf.RoundToInt(Mathf.Clamp(s, -1f, 1f) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}

public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Action onDown, onUp;
    bool held;
    public void OnPointerDown(PointerEventData e) { held = true; onDown?.Invoke(); }
    public void OnPointerUp(PointerEventData e) { if (!held) return; held = false; onUp?.Invoke(); }
}
