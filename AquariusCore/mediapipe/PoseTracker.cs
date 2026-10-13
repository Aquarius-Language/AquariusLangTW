using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using AquariusLang.Application;
using AquariusLang.External;
using AquariusLang.Onnx;
using AquariusLang.Wasm;

namespace AquariusLang.MediaPipe;

public sealed record PoseResult(bool HasPose, float Confidence, float[] Landmarks,
    double TotalMilliseconds, double InferenceMilliseconds, bool DetectionRan);

/// <summary>Bulk transport and ONNX orchestration. Portable pose math lives exclusively in pose.wasm.</summary>
public sealed class PoseTracker : IDisposable {
    private static readonly Lazy<byte[]> Code = new(() => ExternalLibraries.ReadAsset(ExternalLibraries.MediaPipe, "pose.wasm"));
    private readonly IWasmInstance wasm;
    private readonly IOnnxSession detector, landmark;
    private readonly string boxes, scores, points, confidence, heatmap;
    private readonly object gate = new();
    private bool disposed;
    public PoseTracker(IWasmEngine engine, IOnnxBackend backend, byte[] detectorModel, byte[] landmarkModel, OnnxProvider provider) {
        wasm = engine.Instantiate(Code.Value, new Dictionary<string, Delegate>());
        IOnnxSession? d = null, l = null;
        try {
            if (wasm.Invoke("pose_abi") != 1) throw new NotSupportedException("姿態處理介面版本不相容。");
            d = backend.CreateSession(detectorModel, new(provider)).AsTask().GetAwaiter().GetResult();
            l = backend.CreateSession(landmarkModel, new(provider)).AsTask().GetAwaiter().GetResult();
            if (d.Inputs.Count != 1 || l.Inputs.Count != 1 || d.Inputs[0].Type != "float32" || l.Inputs[0].Type != "float32" ||
                !d.Inputs[0].Shape.SequenceEqual(new long[] {1,3,224,224}) || !l.Inputs[0].Shape.SequenceEqual(new long[] {1,256,256,3}))
                throw new ArgumentException("必須使用相容的 MediaPipe 人物偵測與姿態模型。");
            boxes = Output(d, 2254*12); scores = Output(d, 2254);
            points = Output(l, 195); confidence = Output(l, 1); heatmap = Output(l, 64*64*39);
            detector = d; landmark = l; wasm.Invoke("pose_reset");
        } catch { l?.Dispose(); d?.Dispose(); wasm.Dispose(); throw; }
    }
    private static string Output(IOnnxSession session, long count) {
        var matches=session.Outputs.Where(m=>m.Type=="float32"&&m.Shape.Aggregate(1L,(a,b)=>a*b)==count).ToArray();
        return matches.Length==1?matches[0].Name:throw new ArgumentException("姿態模型的輸出介面不相容。");
    }
    private Span<byte> Memory(int slot, int bytes) => wasm.Memory(wasm.Invoke("pose_ptr", slot), bytes);
    private float[] Read(int slot, int count) => MemoryMarshal.Cast<byte,float>(Memory(slot, count*4)).ToArray();
    private void Write(int slot, OnnxTensor tensor) {
        var data = (float[])tensor.CopyData(); MemoryMarshal.AsBytes(data.AsSpan()).CopyTo(Memory(slot, data.Length*4));
    }
    private IReadOnlyDictionary<string,OnnxTensor> Run(IOnnxSession session, int slot, string[] outputs) {
        var info = session.Inputs[0]; int count = checked((int)info.Shape.Aggregate(1L,(a,b)=>a*b));
        var tensor = new OnnxTensor("float32", info.Shape.ToArray(), Read(slot,count));
        return session.Run(new Dictionary<string,OnnxTensor> { [info.Name]=tensor }, outputs).AsTask().GetAwaiter().GetResult();
    }
    public PoseResult Analyze(PixelImage image, double timestampMilliseconds) {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!double.IsFinite(timestampMilliseconds) || timestampMilliseconds < 0 || timestampMilliseconds > float.MaxValue || image.Width > 1920 || image.Height > 1080)
                throw new ArgumentException("影像上限為 1920 × 1080，時間必須是非負有限毫秒數。");
            var total = Stopwatch.StartNew(); double inference = 0;
            int detect = wasm.Invoke("pose_begin", image.Width, image.Height, BitConverter.SingleToInt32Bits((float)timestampMilliseconds));
            if (detect == -2) throw new ArgumentException("姿態影像或時間無效。");
            if (detect >= 0) {
                if (image.Alpha == AlphaRepresentation.Premultiplied) image.StraightPixels().AsSpan().CopyTo(Memory(0,image.Pixels.Length));
                else image.Pixels.Span.CopyTo(Memory(0,image.Pixels.Length));
                if (detect == 1) {
                    wasm.Invoke("pose_detector_input"); var timer=Stopwatch.StartNew();
                    var found=Run(detector,1,new[] {boxes,scores}); inference+=timer.Elapsed.TotalMilliseconds;
                    Write(2,found[boxes]);Write(3,found[scores]);
                    if (wasm.Invoke("pose_detect") == 0) return new(false,0,Array.Empty<float>(),total.Elapsed.TotalMilliseconds,inference,true);
                }
                wasm.Invoke("pose_landmark_input"); var clock=Stopwatch.StartNew();
                var pose=Run(landmark,4,new[] {points,confidence,heatmap});inference+=clock.Elapsed.TotalMilliseconds;
                Write(5,pose[points]);Write(7,pose[confidence]);Write(6,pose[heatmap]);wasm.Invoke("pose_finish");
            }
            var result=Read(8,166);bool present=result[0]>=.5f;
            return new(present,result[0],present?result[1..]:Array.Empty<float>(),total.Elapsed.TotalMilliseconds,inference,detect==1);
        }
    }
    public void Reset() { lock(gate) { ObjectDisposedException.ThrowIf(disposed,this);wasm.Invoke("pose_reset"); } }
    public void Dispose() { lock(gate) { if(disposed)return;disposed=true;try {landmark.Dispose();} finally {try {detector.Dispose();} finally {wasm.Dispose();}} } }
}
