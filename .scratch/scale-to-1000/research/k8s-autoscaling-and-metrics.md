# Scaling a .NET 10 SRT/UDP service in Kubernetes on "streams owned per pod"

Researched 2026-09-11 from primary sources only. Package versions checked on NuGet the same day.

## 1. .NET metrics API (`System.Diagnostics.Metrics`)

- Namespace `System.Diagnostics.Metrics`; assembly `System.Diagnostics.DiagnosticSource.dll`; NuGet `System.Diagnostics.DiagnosticSource`. `ObservableGauge<T>` "Applies to" lists net-6.0 through net-11.0 plus netstandard2.0/netfx via the package. https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.metrics.observablegauge-1
- In the shared framework for .NET 8+: "Applications that target .NET 8+ include this reference by default." Older targets add the NuGet package (v8+). https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation
- Instruments: `Counter<T>`, `UpDownCounter<T>`, `ObservableCounter<T>`, `ObservableUpDownCounter<T>`, `Gauge<T>`, `ObservableGauge<T>`, `Histogram<T>`. For "current size of a set" the doc recommends `UpDownCounter`/`ObservableUpDownCounter`; `ObservableGauge` reports the callback value as-is. Same URL.
- DI: use `IMeterFactory` (auto-registered by the host in .NET 8+), `meterFactory.Create("Name")`. Same URL.
- Callback caveat: observable callbacks run on a non-synchronized thread; read a cached/volatile value, never block. Same URL.
- `dotnet-counters` reads a custom Meter with no extra package: `--counters <COUNTERS>` is "A comma-separated list of counters ... `provider_name[:counter_name]` ... for Meters, `provider_name` is the name of the Meter." Example from the tutorial: `dotnet-counters monitor -n metric-demo.exe --counters HatCo.Store`. https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters and https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation
- Naming: .NET enforces nothing, recommends OpenTelemetry lowercase dotted names, e.g. `contoso.reserved_tickets`. Same tutorial URL.

## 2. Exposing the Meter to Prometheus (ASP.NET Core)

Versions on NuGet, 2026-09-11 (flat-container index, last entries):

| Package | Latest | Stable? | Source |
|---|---|---|---|
| `OpenTelemetry` | 1.18.0 | yes | https://api.nuget.org/v3-flatcontainer/opentelemetry/index.json |
| `OpenTelemetry.Extensions.Hosting` | 1.18.0 (rc.1 also listed, same day) | yes | https://api.nuget.org/v3-flatcontainer/opentelemetry.extensions.hosting/index.json |
| `OpenTelemetry.Exporter.Prometheus.AspNetCore` | 1.18.0-beta.1 | **no, never had a stable release** (every version in the index has alpha/beta/rc suffix) | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.prometheus.aspnetcore/index.json |

- Why still beta: "This component is still under development due to a dependency on the experimental Prometheus and OpenMetrics Compatibility specification and can undergo breaking changes before stable release." Install: `dotnet add package --prerelease OpenTelemetry.Exporter.Prometheus.AspNetCore`. https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.Prometheus.AspNetCore/README.md
- Minimal `Program.cs` (README "Steps to enable" plus `AddMeter` from the Microsoft tutorial):

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Srt.Ingest").AddPrometheusExporter());
var app = builder.Build();
app.MapPrometheusScrapingEndpoint();   // default path "/metrics"
```
  README URL above; `AddMeter("...")` "configures OpenTelemetry to transmit all the metrics collected by the Meter" per https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-collection
- Defaults: `ScrapeEndpointPath` = `/metrics`; `ScrapeResponseCacheDurationMilliseconds` = 300 (0 disables). README URL above.
- Name rendering: default `TranslationStrategy` = `UnderscoreEscapingWithSuffixes`: dots become underscores, unit suffix appended, counters get `_total` (README example: counter `foo.bar` unit `By` -> `foo_bar_bytes_total`). So an `ObservableGauge`/`UpDownCounter` named `live.streams.owned` with no unit renders as `live_streams_owned` (gauges get no `_total`; a unit such as `{stream}` is a UCUM annotation and adds no suffix in the README examples - verify against your own `/metrics` output). README URL above.
- Microsoft's own tutorial also uses `--prerelease` for the Prometheus exporter: "This tutorial uses a pre-release build of OpenTelemetry's Prometheus support." https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-collection

## 3. HPA on a custom per-pod metric (prometheus-adapter)

- HPA reads custom metrics from the aggregated `custom.metrics.k8s.io` API: "The common use for HorizontalPodAutoscaler is to configure it to fetch metrics from aggregated APIs (`metrics.k8s.io`, `custom.metrics.k8s.io`, or `external.metrics.k8s.io`)." "For per-pod custom metrics, the controller functions similarly to per-pod resource metrics, except that it works with raw values, not utilization values." https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/
- `averageValue` = "the target value of the average of the metric across all relevant pods"; `value` = "the target value of the metric". https://kubernetes.io/docs/reference/kubernetes-api/workload-resources/horizontal-pod-autoscaler-v2/
- HPA manifest (shape taken verbatim from the walkthrough's `packets-per-second` example, metric name substituted). https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale-walkthrough/

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata: { name: srt-ingest }
spec:
  scaleTargetRef: { apiVersion: apps/v1, kind: Deployment, name: srt-ingest }
  minReplicas: 2
  maxReplicas: 50
  metrics:
  - type: Pods
    pods:
      metric: { name: live_streams_owned }
      target: { type: AverageValue, averageValue: "800" }   # target streams per pod
```

- prometheus-adapter rule: `seriesQuery` discovers series, `resources.overrides` maps Prometheus labels to Kubernetes resources, `name` rewrites, `metricsQuery` templates with `<<.Series>>`, `<<.LabelMatchers>>`, `<<.GroupBy>>`. Result is served at `custom.metrics.k8s.io/v1beta1/namespaces/{ns}/pods/*/{name}`. https://github.com/kubernetes-sigs/prometheus-adapter/blob/master/docs/config-walkthrough.md and https://github.com/kubernetes-sigs/prometheus-adapter/blob/master/docs/config.md
- Gauge rule (adapter docs only show `rate()` counter examples; this is the same structure without `rate`). Labels `namespace`/`pod` are what kube-prometheus-style relabeling produces; adjust to your scrape config.

```yaml
rules:
- seriesQuery: 'live_streams_owned{namespace!="",pod!=""}'
  resources:
    overrides:
      namespace: { resource: "namespace" }
      pod:       { resource: "pod" }
  name: { matches: "^live_streams_owned$", as: "live_streams_owned" }
  metricsQuery: 'max(<<.Series>>{<<.LabelMatchers>>}) by (<<.GroupBy>>)'
```
- Walkthrough HPA consuming an adapter metric uses `type: Pods` + `averageValue` (`http_requests`, `500m`). https://github.com/kubernetes-sigs/prometheus-adapter/blob/master/docs/walkthrough.md

## 4. KEDA alternative

- `prometheus` trigger fields: `serverAddress`, `query` ("query must return a vector/scalar single element response"), `threshold` (float), optional `activationThreshold` (default 0), `ignoreNullValues` (default true). https://keda.sh/docs/latest/scalers/prometheus/
- Per-pod average vs total: trigger `metricType` accepts `AverageValue` (default), `Value`, `Utilization`. https://keda.sh/docs/latest/reference/scaledobject-spec/ KEDA hands the value to an HPA it creates ("KEDA passes the target value to the Horizontal Pod Autoscaler (HPA) and the built-in HPA controller will handle all the autoscaling"). https://keda.sh/docs/latest/concepts/scaling-deployments/ With HPA semantics, `AverageValue` = query result divided across pods, `Value` = compared directly (HPA v2 API URL above). So: `query: sum(live_streams_owned)` with default `AverageValue` and `threshold: "800"` scales on streams-per-pod; `query: avg(live_streams_owned)` needs `metricType: Value`.
- `metrics-api` trigger can hit the app directly, no Prometheus: `format` is one of `json`, `xml`, `yaml`, `prometheus` (default json); fields `url`, `valueLocation`, `targetValue`. But it polls ONE url, so it sees one pod behind the Service, not a per-pod average - unsuitable for this metric unless the app exposes a fleet-wide value. https://keda.sh/docs/latest/scalers/metrics-api/
- `pollingInterval` default 30s; `cooldownPeriod` (300s) only applies to scale-to-zero; 1..N uses HPA behavior via `advanced.horizontalPodAutoscalerConfig.behavior`. https://keda.sh/docs/latest/reference/scaledobject-spec/

```yaml
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata: { name: srt-ingest }
spec:
  scaleTargetRef: { name: srt-ingest }
  minReplicaCount: 2
  maxReplicaCount: 50
  advanced:
    horizontalPodAutoscalerConfig:
      behavior: { scaleDown: { stabilizationWindowSeconds: 600 } }
  triggers:
  - type: prometheus
    metricType: AverageValue
    metadata:
      serverAddress: http://prometheus.monitoring:9090
      query: sum(live_streams_owned{namespace="media"})
      threshold: "800"
```
- Recommendation: for a single gauge with Prometheus already present, prometheus-adapter is fewer moving parts (one Deployment + one APIService, no CRDs); pick KEDA only if you also want scale-to-zero or non-Prometheus triggers.

## 5. Scale-down safety

- Termination order: kubelet runs `preStop` (only if `terminationGracePeriodSeconds` != 0; default 30s), then sends TERM to PID 1; preStop time counts inside the grace period ("If the preStop hook is still running after the grace period expires, the kubelet requests a small, one-off grace period extension of 2 seconds"); SIGKILL after. Pod is removed from EndpointSlices at the same time termination begins ("terminating endpoints always have their `ready` status as `false`"). https://kubernetes.io/docs/concepts/workloads/pods/pod-lifecycle/
- `behavior.scaleDown.stabilizationWindowSeconds`: "the number of seconds for which past recommendations should be considered while scaling down ... selects the highest recommended replica count ... defaults to 300". Default scaleDown policy: 100 Percent per 15s. It delays the decision to lower `replicas`; it does not extend per-pod grace. https://kubernetes.io/docs/reference/kubernetes-api/workload-resources/horizontal-pod-autoscaler-v2/ and https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/
- HPA cannot pick the pod: it only sets replicas through the `scale` subresource ("a subresource named `scale` ... allows you to dynamically set the number of replicas"). Which pod goes is the ReplicaSet controller's choice, in this order: pending/unschedulable first; then `controller.kubernetes.io/pod-deletion-cost`; then pods on nodes with more replicas; then newer creation time; then random. https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/ and https://kubernetes.io/docs/concepts/workloads/controllers/replicaset/
- `controller.kubernetes.io/pod-deletion-cost`: Pod annotation, int32, default 0, "Pods with lower deletion cost are preferred to be deleted before pods with higher deletion cost"; beta, enabled by default since v1.21 (`PodDeletionCost` gate); best-effort; docs warn against updating it frequently. Practical: a pod can PATCH its own annotation to its current stream count (lower = drained first). https://kubernetes.io/docs/reference/labels-annotations-taints/#pod-deletion-cost and https://kubernetes.io/docs/concepts/workloads/controllers/replicaset/#pod-deletion-cost
- Net effect: set `terminationGracePeriodSeconds` to the longest drain you accept, let the app on SIGTERM stop accepting handshakes and finish/relocate streams, and use pod-deletion-cost to steer which pod is picked. Nothing in HPA/ReplicaSet waits for "zero streams".

## 6. UDP Services, conntrack, session affinity

- kube-proxy iptables/nftables DNAT is decided per connection: iptables `nat` table "is consulted when a packet that creates a new connection is encountered"; later packets of the same conntrack flow (5-tuple) follow the recorded mapping. https://man7.org/linux/man-pages/man8/iptables.8.html
- kube-proxy exposes conntrack timeouts, confirming UDP flows are conntrack entries: `conntrack.udpTimeout` = "how long an idle UDP conntrack entry in UNREPLIED state will remain in the conntrack table (e.g. '30s')", `conntrack.udpStreamTimeout` = "... in ASSURED state ... (e.g. '300s')". Neither has a default in `defaults.go` (only TCP established 24h / close-wait 1h), so kernel defaults apply unless set. https://kubernetes.io/docs/reference/config-api/kube-proxy-config.v1alpha1/ and https://raw.githubusercontent.com/kubernetes/kubernetes/master/pkg/proxy/apis/config/v1alpha1/defaults.go (exact sysctl write path in kube-proxy source not located; the field docs describe the same semantics as the kernel sysctls below)
- Kernel defaults: `nf_conntrack_udp_timeout` = 30s; `nf_conntrack_udp_timeout_stream` = 120s, "used in case there is an UDP stream detected" (bidirectional/assured). An SRT session idle longer than that loses its conntrack entry and the next packet is load-balanced as a new flow. SRT keepalives (1s) keep it alive; only a stalled client is at risk. https://www.kernel.org/doc/html/latest/networking/nf_conntrack-sysctl.html
- Cilium kube-proxy replacement: has its own BPF CT tables (`cilium_ct_{4,6}_global`, `cilium_ct_{4,6}_any`; default sized "512k TCP/256k UDP") https://docs.cilium.io/en/stable/network/ebpf/maps/ ; non-TCP CT timeouts: `--bpf-ct-timeout-regular-any` default 1m0s, `--bpf-ct-timeout-service-any` default 1m0s (TCP: 2h13m20s). https://docs.cilium.io/en/stable/cmdref/cilium-agent/
- `sessionAffinity: ClientIP`: "connections from a particular client are passed to the same Pod each time"; `timeoutSeconds` default 10800; "the mechanism relies on an in-memory store in kube-proxy". The docs do not restrict it by protocol; kube-proxy's iptables implementation uses `-m recent --rcheck --seconds <sticky> --reap` / `--set` on the endpoint chain, which is not protocol-specific, so it applies to UDP. https://kubernetes.io/docs/reference/networking/virtual-ips/ and https://raw.githubusercontent.com/kubernetes/kubernetes/master/pkg/proxy/iptables/proxier.go Cilium: "Each connection from the same pod or host to a service configured with `sessionAffinity: ClientIP` will always select the same service endpoint"; per service IP+port; no UDP limitation stated. https://docs.cilium.io/en/stable/network/kubernetes/kubeproxy-free/
- Caveat for this design: ClientIP affinity would pin a retrying client to the full pod; leave it `None` so the retry can land elsewhere.

## 7. Retry from a new source port: which backend?

- iptables mode: "installs iptables rules which, by default, select a backend Pod at random" - implemented with `-m statistic --mode random --probability`. A new 5-tuple is an independent random draw, so a retry lands on the same pod with probability 1/N. https://kubernetes.io/docs/reference/networking/virtual-ips/ and https://raw.githubusercontent.com/kubernetes/kubernetes/master/pkg/proxy/iptables/proxier.go
- nftables mode: same wording, "select a backend Pod at random". Note the docs say the default mode will move from iptables to nftables in a future release. https://kubernetes.io/docs/reference/networking/virtual-ips/
- IPVS mode: scheduler from `ipvs.scheduler` (rr, wrr, lc, wlc, lblc, lblcr, sh, dh, sed, nq, mh); kube-proxy default is `rr` (`defaultScheduler = "rr"`). With `rr` a new flow goes to the next backend, so a retry almost never hits the same pod; with `sh` (source hashing, source IP only) it would. IPVS is deprecated from v1.35. https://kubernetes.io/docs/reference/networking/virtual-ips/ and https://raw.githubusercontent.com/kubernetes/kubernetes/master/pkg/proxy/ipvs/proxier.go
- Cilium: `--bpf-lb-algorithm` default `random`; optional `maglev` hashes the 5-tuple ("consistent backend selection throughout the cluster for a given 5-tuple"), so a new source port changes the hash input and generally selects a different backend; with ClientIP affinity Maglev "will not take the source port as input". https://docs.cilium.io/en/stable/cmdref/cilium-agent/ and https://docs.cilium.io/en/stable/network/kubernetes/kubeproxy-free/
- Conclusion: under every default (iptables random, nftables random, IPVS rr, Cilium random/maglev) a handshake refused by a full pod and retried from a fresh source port is re-balanced; only `sessionAffinity: ClientIP` or IPVS `sh` would defeat it. Expected retries to escape a full pod under random selection: geometric with p = (N-full)/N; the app should allow several handshake attempts.

## Not confirmed

- Exact rendering of a unit annotation like `{stream}` by the Prometheus exporter (README only documents `By` -> `_bytes`); check `/metrics`.
- The source line where kube-proxy writes `nf_conntrack_udp_timeout*` (config field semantics confirmed; file not located via raw fetch).
- A verbatim Cilium sentence that established flows survive backend changes; inferred from CT maps + Maglev reassignment wording ("at most 1% difference in the reassignments").
