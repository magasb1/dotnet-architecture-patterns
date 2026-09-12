# ONNX Runtime for a .NET 10 multi-stream object-detection service

Researched 2026-09-12. Every claim carries its source. Where a primary source did not
confirm something, it is marked **UNVERIFIED** rather than guessed.

## 1. Packages

| Role | Package id | Latest stable | Date | Download size |
|---|---|---|---|---|
| CPU | `Microsoft.ML.OnnxRuntime` | 1.30.0 | 2026-09-10 | 149.95 MB | 
| GPU (CUDA + TensorRT) | `Microsoft.ML.OnnxRuntime.Gpu` | 1.30.0 | 2026-09-10 | 319 KB (metapackage) |
| GPU natives, Linux | `Microsoft.ML.OnnxRuntime.Gpu.Linux` | 1.30.0 | 2026-09-10 | 225.1 MB |
| GPU natives, Windows | `Microsoft.ML.OnnxRuntime.Gpu.Windows` | 1.30.0 | 2026-09-10 | 146.01 MB |
| Managed bindings | `Microsoft.ML.OnnxRuntime.Managed` | 1.30.0 | 2026-09-10 | — |
| DirectML | `Microsoft.ML.OnnxRuntime.DirectML` | **1.24.4** | 2026-03-17 | 11.88 MB |
| Pre/post-process ops | `Microsoft.ML.OnnxRuntime.Extensions` | **0.14.0** | 2025-03-12 | 26.54 MB |

Sources: <https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime>,
<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu>,
<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu.Linux>,
<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu.Windows>,
<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML>,
<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Extensions>

- **There is no separate TensorRT NuGet package.** The TensorRT EP ships inside the
  `.Gpu.*` native packages alongside the CUDA EP — the package descriptions list
  "TensorRT Execution Provider, CUDA Execution Provider, CPU Execution Provider".
  <https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu>
- **DirectML is on "sustained engineering."** The install docs now recommend
  `Microsoft.AI.MachineLearning` (WinML) for new Windows projects.
  <https://onnxruntime.ai/docs/install/> — note DirectML is pinned at 1.24.4 while the
  main line is at 1.30.0, i.e. it is six releases behind.
- **The GPU package does NOT bundle CUDA or cuDNN.** They must be installed on the host
  (or baked into the container image).
  <https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html>

### CUDA / cuDNN pinning per ORT version

| ORT | CUDA | cuDNN |
|---|---|---|
| 1.27.x – 1.29.x | 13.0 | 9.x |
| 1.21.x – 1.26.x | 12.8 | 9.x |
| 1.18.x | 11.8 | 8.x |

<https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html> — "Starting
with version 1.27, GPU packages published to PyPI and NuGet are built with CUDA 13.0 by
default." The docs table stops at 1.29; **1.30 is UNVERIFIED but is almost certainly also
CUDA 13.0** given the 1.29 release notes list CUDA 12.8 and 13.x arch tables
(<https://github.com/microsoft/onnxruntime/releases/tag/v1.29.0>). "ONNX Runtime built
with cuDNN 8.x is not compatible with cuDNN 9.x, and vice versa."

**TensorRT version:** the TensorRT EP doc's compatibility table is stale — its newest row
is ORT 1.22 → TensorRT 10.9, CUDA 12.0–12.8.
<https://onnxruntime.ai/docs/execution-providers/TensorRT-ExecutionProvider.html>
The TensorRT version for ORT 1.27–1.30 is **UNVERIFIED from primary sources**. Pin it by
inspecting the actual `.Gpu.Linux` package contents before committing to TensorRT.

CUDA 13.x requires NVIDIA Linux driver **>= 580** on the host.
<https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html>

### One project file for both CPU-local and GPU-cluster? Yes.

The `.Gpu.*` native packages contain the CPU EP as well — same `onnxruntime` native
library, with CUDA/TensorRT as *additional* providers
(<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu>). So a single
`<PackageReference Include="Microsoft.ML.OnnxRuntime.Gpu" />` works in both places,
provided the code only appends the CUDA EP when CUDA is actually present. The GPU
provider shared libraries (`onnxruntime_providers_cuda.*`) are loaded lazily, at
`AppendExecutionProvider_CUDA` time, not at process start — the standard failure mode is
an exception *from that call*, not a load failure at startup
(<https://github.com/microsoft/onnxruntime/issues/21256>,
<https://github.com/microsoft/onnxruntime/issues/10336>).

Cost of that choice: a developer restore pulls ~146 MB (Windows) of GPU natives that will
never run. If that matters, a conditional reference keyed on a property is the escape
hatch, but it is not required for correctness.

Emerging alternative worth tracking, not yet recommended: **plugin EPs**, where the EP is
a separately-shipped library registered at runtime via
`OrtEnv.RegisterExecutionProviderLibrary(name, path)` and selected with
`SessionOptions.AppendExecutionProvider_V2` or `SetEpSelectionPolicy`.
<https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html>
A CUDA plugin EP shipped at **v0.1.0** on 2026-08-17
(<https://github.com/microsoft/onnxruntime/releases>) — version 0.1.0 is too green for
production, but this is the direction: base package stays small, EP is a deployment
artefact.

## 2. Execution providers

Selection is via `SessionOptions` (`Microsoft.ML.OnnxRuntime.SessionOptions`):

```csharp
public void AppendExecutionProvider_CPU(int useArena = 1)
public void AppendExecutionProvider_CUDA(int deviceId = 0)
public void AppendExecutionProvider_CUDA(OrtCUDAProviderOptions cudaProviderOptions)
public void AppendExecutionProvider_Tensorrt(int deviceId = 0)
public void AppendExecutionProvider_Tensorrt(OrtTensorRTProviderOptions trtProviderOptions)
public void AppendExecutionProvider_DML(int deviceId = 0)
public void AppendExecutionProvider(string providerName, Dictionary<string,string> opts = null)
```

<https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.SessionOptions.html>
— each carries the note "Use only if you have the onnxruntime package specific to this
Execution Provider."

Two different fallbacks, and conflating them is the usual mistake:

- **EP library missing / wrong CUDA version → throws.** `AppendExecutionProvider_CUDA`
  raises `OnnxRuntimeException` ("LoadLibrary failed with error 126"). There is **no
  automatic fall back to CPU** at this level; the caller must try/catch and build a
  CPU-only session. <https://github.com/microsoft/onnxruntime/issues/10336>,
  <https://github.com/microsoft/onnxruntime/issues/21256>. A known wrinkle: a stale
  `CUDA_PATH` environment variable can defeat even the intended fallback path
  (<https://github.com/microsoft/onnxruntime/issues/21424>).
- **EP present but an op unsupported → automatic, per-node.** ORT partitions the graph
  and assigns unsupported subgraphs to the next EP in the appended list. TensorRT: "If
  target model can't be successfully partitioned … the whole model will fall back to
  other execution providers such as CUDA or CPU."
  <https://onnxruntime.ai/docs/execution-providers/TensorRT-ExecutionProvider.html>
  Note the reported quirk that a failing TensorRT EP falls back to CUDA rather than the
  next *listed* provider (<https://github.com/microsoft/onnxruntime/issues/17394>).

So: probe with try/catch at startup, log which EP you actually got, and never assume.

### TensorRT vs CUDA for this workload

TensorRT is worth it for a fixed detector model running at high volume — that is exactly
the shape it optimises for. The cost is engine build time on first run. Documented
numbers for a complex model:

| Configuration | Init time |
|---|---|
| No cache | ~384 s |
| Timing cache only | 42 s |
| Engine cache | 9 s |
| Embedded engine | 1.9 s |

<https://onnxruntime.ai/docs/execution-providers/TensorRT-ExecutionProvider.html>

Cache options: `trt_engine_cache_enable`, `trt_engine_cache_path`,
`trt_timing_cache_enable`. **The cache cannot be safely warmed in a container image for a
heterogeneous cluster**: "Engine files are not portable and optimized for specific Nvidia
hardware," and a rebuild is required when the model, ORT version, TensorRT version, or
hardware changes (same source). Baking an engine into an image is only valid if every
node has the identical GPU SKU and driver; otherwise mount a per-node cache volume keyed
by GPU model and accept the first-run build. The timing cache is the more portable half
and still cuts 384 s → 42 s.

Dynamic shapes need explicit optimisation profiles: `trt_profile_min_shapes`,
`trt_profile_opt_shapes`, `trt_profile_max_shapes`, e.g. `"input:2x4x64x64"` — all dynamic
inputs need ranges (same source). This directly interacts with batching, below.

## 3. Batching

- **A model exported with a fixed batch dimension cannot be run with a different batch.**
  The dimension is baked into the graph as the literal value and Run fails on shape
  mismatch. The fix is at export: `torch.onnx.export(..., input_names=['input'],
  output_names=['output'], dynamic_axes={'input': {0:'batch'}, 'output': {0:'batch'}},
  opset_version=14)`. Opset < 11 has no dynamic shape support at all.
  <https://theneuralbase.com/onnx/learn/advanced/batch-size-handling/> (secondary source;
  the underlying mechanism is ONNX shape inference, not an ORT feature). Verify by loading
  the exported model and running two different batch sizes — declaring `dynamic_axes` does
  not by itself guarantee it took.
- **In the .NET API batching is just the leading tensor dimension.** Build the shape as
  `new long[]{ batch, 3, h, w }` and pass one contiguous buffer via
  `OrtValue.CreateTensorValueFromMemory<float>(data, shape)`.
  <https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.OrtValue.html>
- **ORT does not batch concurrent `Run` calls for you.** There is no server-side dynamic
  batching in ORT itself; that is a Triton Inference Server feature layered *on top of*
  the ORT backend (`max_batch_size`, `max_queue_delay_microseconds`).
  <https://docs.nvidia.com/deeplearning/triton-inference-server/user-guide/docs/user_guide/ragged_batching.html>
  For a .NET service, the caller assembles batches — a bounded channel per model with a
  size-or-timeout trigger is the whole mechanism.
- **Threads.** `intra_op_num_threads` = threads *within* one operator; default 0 meaning
  one per physical core, with thread affinity enabled. `inter_op_num_threads` = threads
  *across* operators, and **only has any effect when `ExecutionMode` is set to parallel**,
  which is not the default. <https://onnxruntime.ai/docs/performance/tune-performance/threading.html>
  For a server with many sessions the defaults are actively wrong: each session spins up
  its own pool of NCores threads and they fight. The docs call this out and offer two
  remedies — custom thread affinity so pools sit on separate cores, or "create a global
  intra-op thread pool to prevent overheated contentions among session thread pools"
  (same source). On the GPU path, set `intra_op_num_threads = 1`: CPU op threads do
  nothing useful when the graph is on CUDA.

## 4. Throughput and memory

- **`InferenceSession.Run` is thread-safe for concurrent calls on one session**, for most
  EPs. <https://github.com/microsoft/onnxruntime/discussions/10107>. Exceptions worth
  knowing: **DirectML** permits only one `Run` at a time per session (concurrent Run is
  allowed only across *different* sessions)
  (<https://github.com/microsoft/onnxruntime/discussions/9441>), and **CUDA Graphs** mode
  states plainly "Multi-threaded usage is currently not supported … `Run()` should not be
  invoked on the same session from multiple threads"
  (<https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html>).
- **One session per model, shared across worker threads** is the right default. Sessions
  are expensive to create — "Creating and loading sessions are expensive per request. They
  better be cached" (<https://onnxruntime.ai/docs/get-started/with-csharp.html>). One
  session per worker thread multiplies weight memory and thread pools for nothing.
- **Weights are not shared between independent sessions** by default — each session loads
  its own copy, so N sessions of the same model cost N × weights. Sharing across sessions
  requires opting in (prepacked-weights / shared allocator configuration). Note also the
  open report of crashes when instantiating multiple sessions on one GPU from different
  threads concurrently — serialise *session construction*
  (<https://github.com/microsoft/onnxruntime/issues/26610>). GPU arena: `gpu_mem_limit`
  defaults to effectively unlimited, `arena_extend_strategy` defaults to
  `kNextPowerOfTwo`; set `kSameAsRequested` when several sessions share a GPU
  (<https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html>).
- **`OrtValue`** is the modern tensor handle — it can wrap managed, native, stack or
  device memory, and the API that matters here is:
  `OrtValue.CreateTensorValueWithData(OrtMemoryInfo memInfo, TensorElementType elementType,
  long[] shape, nint dataBufferPtr, long bufferLengthInBytes)` — "pre-allocated memory …
  possibly on a device"; the OrtValue does not own or free it.
  <https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.OrtValue.html>
  `OrtMemoryInfo` exposes predefined allocator names including `allocatorCUDA` and
  `allocatorCUDA_PINNED`, each constructor taking a `deviceId`.
  <https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.OrtMemoryInfo.html>
- **`IOBinding`** pre-arranges inputs on the device and pre-allocates outputs there, so
  `Run` does no host↔device copy. <https://onnxruntime.ai/docs/performance/tune-performance/iobinding.html>
  **Yes, this keeps data on the GPU across calls**: "CreateTensor can wrap existing
  cuda_resource pointers allocated via cudaMalloc," and device tensors enable "fully
  asynchronous execution on custom compute streams" plus CUDA graph capture, "eliminating
  runtime copy operations." <https://onnxruntime.ai/docs/performance/device-tensor.html>
  C# reference implementation:
  `csharp/test/Microsoft.ML.OnnxRuntime.Tests.Common/OrtIoBindingAllocationTest.cs`.

## 5. Decode → inference, honestly

**Short answer: a true zero-copy FFmpeg-CUDA-frame → ORT path is not practical from .NET
today. Budget for either a host round trip per frame, or a small native helper.**

The pieces that *do* line up:

- An FFmpeg `AVFrame` with `AV_PIX_FMT_CUDA` holds `CUdeviceptr` values in `data[i]` with
  `linesize[i]` as the pitch — FFmpeg's own filters cast them exactly that way:
  `(CUdeviceptr)out_frame->data[0]`.
  <https://ffmpeg.org/doxygen/trunk/vf__scale__cuda_8c_source.html>
- ORT can wrap an existing `cudaMalloc` pointer with no copy via
  `OrtValue.CreateTensorValueWithData` + an `OrtMemoryInfo` built with `allocatorCUDA`,
  then `IOBinding.BindInput`.
  <https://onnxruntime.ai/docs/performance/device-tensor.html>

The pieces that do not:

1. **Format.** The decoder produces pitched NV12 (or P010) `uint8`. The model wants
   contiguous, unpitched, normalised `float32` NCHW RGB. Nothing in ORT converts that.
   Bridging it is a CUDA kernel — colour conversion, de-pitching, resize, scale/offset.
2. **You cannot write a CUDA kernel from .NET** without adding a native component or a
   GPU-compute dependency. That is the actual blocker, and it is not an ORT limitation.
3. **CUDA context.** FFmpeg's `AVCUDADeviceContext` and ORT's CUDA EP must be on the same
   context and, ideally, the same stream, or you need explicit synchronisation. ORT
   exposes `user_compute_stream` through provider options V2
   (<https://onnxruntime.ai/docs/performance/device-tensor.html>), and FFmpeg's CUDA hwctx
   exposes its stream — but whether handing FFmpeg's stream to ORT works cleanly through
   the .NET bindings is **UNVERIFIED**. Assume it needs a spike.

**The realistic path, best first:**

- **(a) Stay on GPU by moving preprocessing into FFmpeg and into the model.** Chain
  `scale_cuda` in the decode filtergraph to resize and convert on-device — it supports
  format conversion (added 2021,
  <https://ffmpeg.org/pipermail/ffmpeg-cvslog/2021-June/127969.html>) across YUV420P,
  NV12, YUV444P, P010, P016
  (<https://ffmpeg.org/doxygen/trunk/vf__scale__cuda_8c_source.html>). Then **export the
  ONNX model with a `uint8` input and the Cast/Sub/Div normalisation as graph nodes**, so
  ORT consumes the device buffer directly. This removes the custom kernel entirely. The
  remaining risk is pitch: `linesize` must equal `width × bytes-per-pixel` for the buffer
  to be a valid dense tensor — verify per frame and fall back if not.
- **(b) Accept one host copy per frame.** `av_hwframe_transfer_data` to host, preprocess
  on CPU, upload. Honest cost: a 1920×1080 NV12 frame is ~3.1 MB; at 30 fps that is
  ~93 MB/s per stream each way — fine for a handful of streams, a real ceiling at scale.
  Use `allocatorCUDA_PINNED` staging buffers to keep the upload asynchronous.
- **(c) A small C shim** doing NV12→normalised-NCHW in one kernel, P/Invoked, returning a
  device pointer wrapped by `CreateTensorValueWithData`. Genuine zero-copy, at the price
  of a native build in the pipeline. Only justified once (a) is proven insufficient.

Do not plan the architecture around (c) before measuring (a).

## 6. Containers

- **The ORT GPU package does not run on a plain `mcr.microsoft.com/dotnet/aspnet` base.**
  It needs the CUDA runtime and cuDNN present in the image. ORT's own GPU Dockerfile uses
  `nvcr.io/nvidia/cuda:${CUDA_VERSION}-runtime-${OS}` (default CUDA 12.8.1, Ubuntu 24.04)
  and installs cuDNN explicitly.
  <https://github.com/microsoft/onnxruntime/blob/main/dockerfiles/Dockerfile.cuda>
  The practical shape is: start from `nvidia/cuda:<ver>-runtime-ubuntu24.04`, install the
  ASP.NET runtime into it (or publish self-contained). Going the other way — adding CUDA
  to the aspnet image — is more work for the same result.
- **Host side:** the NVIDIA GPU driver plus the NVIDIA Container Toolkit (current
  1.20.0-1). The toolkit is what gives containers access to GPUs; the driver stays on the
  host.
  <https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html>
  For CUDA 13.x builds the host driver must be **>= 580**
  (<https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html>); the
  `cuda-compat-13-x` forward-compatibility package extends that, but only on Data Center
  GPUs and select NGC-Server-Ready SKUs
  (<https://docs.nvidia.com/deploy/cuda-compatibility/forward-compatibility.html>).
- **Kubernetes:** the NVIDIA device plugin DaemonSet must run on every GPU node, then the
  pod requests GPUs as an extended resource:

  ```yaml
  resources:
    limits:
      nvidia.com/gpu: 1
  ```

  <https://github.com/NVIDIA/k8s-device-plugin/blob/main/README.md>. `nvidia.com/gpu` goes
  under `limits` (requests are set equal automatically); GPUs cannot be fractionally
  requested or oversubscribed without MPS/MIG/time-slicing configured in the plugin. The
  GPU Operator packages the driver, toolkit and plugin together and is the lower-effort
  route on a managed cluster
  (<https://docs.nvidia.com/datacenter/cloud-native/gpu-operator/latest/getting-started.html>).

## 7. Preprocessing

- **ORT core does not resize or normalise for you** — it runs the graph it is given.
- **ORT Extensions** (`Microsoft.ML.OnnxRuntime.Extensions`, 0.14.0) supplies custom ops
  including `DecodeImage`, `EncodeImage` and `DrawBoundingBoxes`, aimed at pre/post
  processing for object detection, and is registered into a session so the work happens
  inside the graph.
  <https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Extensions>
  Caveats for this project: it is a **0.x package last published 2025-03-12** (18 months
  stale relative to ORT 1.30), and `DecodeImage` solves the wrong problem — frames arrive
  already decoded, not as JPEG bytes.
- **Best option here: bake preprocessing into the ONNX graph at export.** Add the
  resize/cast/normalise as ordinary ONNX nodes so the model takes `uint8` pixels. No extra
  package, the work lands on whichever EP the session uses, and it is what makes the
  on-GPU path in §5(a) possible.
- **If the caller must produce the tensor:** the project already has FFmpeg. Use
  `sws_scale` to go straight to the target size and to a planar RGB format (`GBRP`, giving
  separated channel planes) — that is one well-optimised SIMD pass covering resize and
  colour conversion with no new dependency. What remains is `uint8 → float` scaling, which
  is a `Vector<float>`/`TensorPrimitives` loop over `Span<byte>`, or is eliminated
  entirely by the graph-baking above. Do **not** add SixLabors.ImageSharp or
  System.Drawing for this; swscale is faster and already linked.

---

### Open items to verify before committing

1. TensorRT version bundled with ORT 1.27–1.30 (docs table stale at 1.22). Inspect the
   `.Gpu.Linux` nupkg contents.
2. CUDA version for 1.30 specifically (table documents through 1.29).
3. Whether FFmpeg's CUDA stream can be handed to ORT via `user_compute_stream` through the
   .NET bindings.
4. Whether `scale_cuda` output `linesize` is dense enough to wrap as a tensor without a
   de-pitch pass.
