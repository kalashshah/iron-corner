using System;
using UnityEngine;

// Background music, synthesized (no audio files to license or ship). One A-minor progression (Am F C G), three energies:
// Lobby: slow pads and a soft arpeggio. Train: a light beat. Fight: four-on-the-floor kick, claps, hats, a side-chained
// bass and a 16th arpeggio. Each loop is 8 bars, rendered off the main thread (pure maths, its own RNG).
public static class Music
{
    public enum Kind { Lobby, Train, Fight }
    public const int SR = 44100;

    static float LpA(float fc) => 1f - Mathf.Exp(-2f * Mathf.PI * fc / SR);
    static float Tri(double ph) => (float)(4 * Math.Abs(ph % 1 - 0.5) - 1);
    static float Saw(double ph) => (float)(2 * (ph % 1) - 1);

    // Chord tones per 2 bars, in Hz around A3 (A C E · F A C · C E G · G B D).
    static readonly float[][] Chords =
    {
        new[] { 220f, 261.63f, 329.63f }, new[] { 174.61f, 220f, 261.63f },
        new[] { 261.63f, 329.63f, 392f }, new[] { 196f, 246.94f, 293.66f },
    };

    public static float[] Render(Kind k)
    {
        var rng = new System.Random(11 + (int)k);
        float Noise() => (float)(rng.NextDouble() * 2 - 1);
        bool fight = k == Kind.Fight, lobby = k == Kind.Lobby;
        float bpm = fight ? 124 : k == Kind.Train ? 108 : 84;
        float beat = 60f / bpm, bar = beat * 4, step = beat / 4; // step = 16th note
        const int bars = 8;
        int n = (int)(bar * bars * SR);
        var o = new float[n];

        // ---- tonal layers, sample by sample
        var padPh = new double[9]; double bassPh = 0, arpPh = 0, subPh = 0;
        float p1 = 0, p2 = 0, b1 = 0, a1 = 0;
        float padA = LpA(lobby ? 1100 : 1500);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SR;
            int b = (int)(t / bar);
            var ch = Chords[(b / 2) % 4];
            float inBeat = t % beat;
            // side-chain: everything but drums dips right after each kick
            float duck = lobby ? 1f : 0.3f + 0.7f * Mathf.SmoothStep(0, 1, inBeat / (beat * 0.55f));

            // pad: three chord tones, three slightly detuned triangles each, warm low-pass, slow swell per chord
            float pad = 0;
            for (int v = 0; v < 9; v++)
            {
                float f = ch[v / 3] * (1 + ((v % 3) - 1) * 0.0035f);
                padPh[v] += f / SR;
                pad += Tri(padPh[v]) * 0.8f + Saw(padPh[v] * 1.0) * 0.12f;
            }
            p1 += padA * (pad / 9 - p1); p2 += padA * (p1 - p2);
            float chordT = t % (bar * 2);
            float swell = Mathf.Clamp01(chordT / 0.6f) * (0.85f + 0.15f * Mathf.Sin(t * 0.9f));
            float padOut = p2 * swell * duck * (lobby ? 0.55f : 0.32f);

            // bass: fight/train play the root on every off-beat 8th; lobby holds a soft sub
            float bassOut;
            if (lobby)
            {
                subPh += ch[0] / 2 / SR;
                bassOut = Mathf.Sin((float)(2 * Math.PI * subPh)) * 0.22f * swell;
            }
            else
            {
                float e8 = t % (beat / 2); bool off = ((int)(t / (beat / 2)) & 1) == 1;
                float env = off ? Mathf.Exp(-e8 * 9f) * Mathf.Clamp01(e8 / 0.004f) : 0;
                bassPh += ch[0] / 2 / SR;
                float raw = Saw(bassPh) * 0.6f + Mathf.Sin((float)(2 * Math.PI * bassPh)) * 0.6f;
                b1 += LpA(180 + 900 * env) * (raw - b1);
                bassOut = b1 * env * (fight ? 0.55f : 0.35f);
            }

            // arpeggio: chord tones up an octave, a plucked triangle. 16ths in the fight (second half), 8ths elsewhere.
            float arpOut = 0;
            bool arpOn = fight ? b >= 4 : true;
            float arpStep = fight ? step : step * 2;
            if (arpOn)
            {
                int ai = (int)(t / arpStep);
                int[] pattern = { 0, 1, 2, 1, 2, 0, 1, 2 };
                float f = ch[pattern[ai % pattern.Length]] * 2;
                float at = t % arpStep;
                arpPh += f / SR;
                float env = Mathf.Exp(-at * (fight ? 22f : 9f)) * Mathf.Clamp01(at / 0.003f);
                a1 += LpA(2600) * (Tri(arpPh) - a1);
                arpOut = a1 * env * (lobby ? 0.16f : 0.13f) * duck;
            }
            o[i] = padOut + bassOut + arpOut;
        }

        // ---- drums, mixed in as one-shots
        if (!lobby)
        {
            var kick = Kick(Noise); var clap = Clap(Noise); var hat = Hat(Noise, 0.05f); var open = Hat(Noise, 0.22f);
            int beats = bars * 4;
            for (int q = 0; q < beats; q++)
            {
                float at = q * beat;
                Mix(o, kick, at, 0.95f);
                if (fight && (q % 4 == 1 || q % 4 == 3)) Mix(o, clap, at, 0.5f);
                if (!fight && q % 4 == 3) Mix(o, clap, at, 0.3f);
                Mix(o, fight ? open : hat, at + beat / 2, fight ? 0.16f : 0.12f);
                if (fight && q >= 16) { Mix(o, hat, at + beat / 4, 0.07f); Mix(o, hat, at + beat * 3 / 4, 0.07f); }
            }
            if (fight) for (int s = 0; s < 4; s++) Mix(o, clap, (beats - 1) * beat + s * step, 0.2f + s * 0.1f); // fill into the loop
        }

        // ---- master: soft clip, normalise, tiny fades so the loop point never clicks
        float peak = 1e-4f;
        for (int i = 0; i < n; i++) { o[i] = (float)Math.Tanh(o[i] * 1.3f); peak = Mathf.Max(peak, Mathf.Abs(o[i])); }
        int fade = SR / 200;
        for (int i = 0; i < n; i++)
        {
            float g = 0.9f / peak;
            if (i < fade) g *= i / (float)fade; else if (i > n - fade) g *= (n - i) / (float)fade;
            o[i] *= g;
        }
        return o;
    }

    static void Mix(float[] o, float[] s, float at, float vol)
    {
        int start = (int)(at * SR);
        for (int i = 0; i < s.Length; i++) { int j = start + i; if (j >= o.Length) j -= o.Length; o[j] += s[i] * vol; } // tails wrap into the loop start
    }

    static float[] Kick(Func<float> noise)
    {
        var d = new float[(int)(0.42f * SR)]; double ph = 0;
        for (int i = 0; i < d.Length; i++)
        {
            float t = i / (float)SR, f = 46 + 120 * Mathf.Exp(-t * 32);
            ph += f / SR;
            d[i] = Mathf.Sin((float)(2 * Math.PI * ph)) * Mathf.Exp(-t * 6.5f) + (t < 0.004f ? noise() * 0.4f * (1 - t / 0.004f) : 0);
        }
        return d;
    }

    static float[] Clap(Func<float> noise)
    {
        var d = new float[(int)(0.32f * SR)]; float lo = 0, lo2 = 0, a = LpA(3200), a2 = LpA(900);
        for (int i = 0; i < d.Length; i++)
        {
            float t = i / (float)SR;
            float x = noise(); lo += a * (x - lo); lo2 += a2 * (lo - lo2);
            float burst = 0;
            foreach (var s in new[] { 0f, 0.011f, 0.023f }) if (t >= s) burst = Mathf.Max(burst, Mathf.Exp(-(t - s) * 140));
            d[i] = (lo - lo2) * 2.2f * (burst * 0.8f + Mathf.Exp(-t * 16) * 0.5f);
        }
        return d;
    }

    static float[] Hat(Func<float> noise, float len)
    {
        var d = new float[(int)(len * SR)]; float lo = 0, a = LpA(7000);
        for (int i = 0; i < d.Length; i++)
        {
            float t = i / (float)SR, x = noise(); lo += a * (x - lo);
            d[i] = (x - lo) * Mathf.Exp(-t * (len < 0.1f ? 60 : 16));
        }
        return d;
    }
}
