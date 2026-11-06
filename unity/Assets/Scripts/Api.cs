using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Backend client (docs/spec.md). When the server can't be reached every call is answered by OfflineSim instead.
public static class Api
{
    public const string DefaultUrl = "https://iron-corner.fly.dev";
    public static bool Offline = true;
    // Owner token from POST /users (PlayerPrefs). Sent as a bearer header on every call; demo bots ignore it.
    public static string Token;
    static void Auth(UnityWebRequest req) { if (!string.IsNullOrEmpty(Token)) req.SetRequestHeader("Authorization", "Bearer " + Token); }

    // `-server <url>` on the command line wins for this run only (captures against a local backend); never saved.
    static readonly string ArgUrl = ArgAfter("-server");
    static string ArgAfter(string flag)
    {
        var a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public static string BaseUrl
    {
        get => (ArgUrl ?? PlayerPrefs.GetString("server", DefaultUrl)).TrimEnd('/');
        set { PlayerPrefs.SetString("server", value.Trim()); PlayerPrefs.Save(); }
    }

    // GET /bots with a short timeout decides online vs offline.
    public static IEnumerator Probe(Action<bool> done)
    {
        using var req = UnityWebRequest.Get(BaseUrl + "/health");
        req.timeout = 12; // Fly suspends the idle machine; first request wakes it
        yield return req.SendWebRequest();
        Offline = req.result != UnityWebRequest.Result.Success;
        done?.Invoke(!Offline);
    }

    public static IEnumerator Call(string method, string path, JToken body, Action<JToken> ok, Action<string> fail = null)
    {
        if (Offline)
        {
            JToken r = null; string err = null;
            try { r = OfflineSim.Handle(method, path, body); } catch (Exception e) { err = e.Message; }
            yield return null;
            if (err != null) fail?.Invoke(err); else ok?.Invoke(r);
            yield break;
        }
        using var req = new UnityWebRequest(BaseUrl + path, method);
        if (body != null)
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
            req.SetRequestHeader("Content-Type", "application/json");
        }
        req.downloadHandler = new DownloadHandlerBuffer();
        Auth(req);
        req.timeout = path.EndsWith("/save") ? 60 : 15; // a 0G Storage upload with the demo library takes ~15-30 s
        yield return req.SendWebRequest();
        Finish(req, ok, fail);
    }

    // multipart/form-data with field "audio" (wav) plus extra text fields.
    public static IEnumerator Upload(string path, byte[] wav, Dictionary<string, string> fields, Action<JToken> ok, Action<string> fail = null)
    {
        if (Offline)
        {
            yield return new WaitForSecondsRealtime(0.4f);
            JToken r = null; string err = null;
            try { r = OfflineSim.Handle("POST", path, fields != null ? JObject.FromObject(fields) : new JObject()); } catch (Exception e) { err = e.Message; }
            if (err != null) fail?.Invoke(err); else ok?.Invoke(r);
            yield break;
        }
        var form = new List<IMultipartFormSection> { new MultipartFormFileSection("audio", wav, "coach.wav", "audio/wav") };
        if (fields != null) foreach (var kv in fields) form.Add(new MultipartFormDataSection(kv.Key, kv.Value));
        using var req = UnityWebRequest.Post(BaseUrl + path, form);
        Auth(req);
        req.timeout = 30;
        yield return req.SendWebRequest();
        Finish(req, ok, fail);
    }

    public static IEnumerator Texture(string path, Action<Texture2D> ok)
    {
        if (Offline) { ok?.Invoke(OfflineSim.FakeQr(path)); yield break; }
        using var req = UnityWebRequestTexture.GetTexture(BaseUrl + path);
        Auth(req);
        req.timeout = 10;
        yield return req.SendWebRequest();
        if (req.result == UnityWebRequest.Result.Success)
        {
            var tex = DownloadHandlerTexture.GetContent(req);
            tex.filterMode = FilterMode.Point;
            ok?.Invoke(tex);
        }
    }

    // Status and JSON body of the most recent failed call, for screens that explain a refusal (e.g. 409 {reason, have, need}).
    public static long LastStatus; public static JObject LastBody;

    static void Finish(UnityWebRequest req, Action<JToken> ok, Action<string> fail)
    {
        var text = req.downloadHandler != null ? req.downloadHandler.text : null;
        if (req.result != UnityWebRequest.Result.Success)
        {
            string msg = req.error;
            LastStatus = req.responseCode; LastBody = null;
            try { if (!string.IsNullOrEmpty(text)) { var j = JObject.Parse(text); LastBody = j; msg = (string)j["error"] ?? (string)j["reason"] ?? msg; } } catch { }
            Debug.LogWarning($"[api] {req.method} {req.url} -> {msg}");
            fail?.Invoke(msg);
            return;
        }
        JToken r;
        try { r = string.IsNullOrEmpty(text) ? new JObject() : JToken.Parse(text); }
        catch (Exception e) { fail?.Invoke("bad json: " + e.Message); return; }
        ok?.Invoke(r);
    }
}
