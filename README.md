# Storage Demo

A test of one idea: **can the same code, unmodified, run on a bare laptop with no infrastructure
and scale out across a multi-node Kubernetes cluster** — with nothing but configuration telling it
which world it's in?

Nothing in the application or domain code knows whether a file lands on disk or in a bucket,
whether metadata lives in LiteDB or PostgreSQL, whether a live video stream is served by the one
process handling it or relayed from a pod on the other side of the cluster. Every one of those
decisions is behind an interface, chosen by configuration, and proven identical on both sides by
running the same tests against every implementation.

Two subsystems carry that test:

- **Documents** — upload, store, list, download, delete, with pluggable file storage and database
  providers.
- **Live video** — SRT ingest and low-latency playback, a gateway for pulling and forwarding
  streams, and object detection, sized to run as one process on a Windows desktop with no GPU and
  to scale to hundreds of concurrent streams across many Kubernetes replicas with GPU workers.

```
                              ASP.NET Core API
                  gRPC :5080 (primary)   REST :8080 (secondary)
                                     |
                                     v
                             DocumentService
                             /             \
                            v               v
                    IFileStorage       IDocumentRepository
                         |                    |
                   +-----+-----+        +-----+------+
                   v           v        v            v
              Filesystem      S3     LiteDB     PostgreSQL
```

## The four runtime combinations

| Scenario | Runtime | File storage | Database | Persistence needed |
| --- | --- | --- | --- | --- |
| A | Developer Windows | Filesystem | LiteDB | Local disk |
| B | Developer Linux | Filesystem | LiteDB | Local disk |
| C | Docker Compose | MinIO (S3-compatible) | PostgreSQL | Container volumes |
| D | Kubernetes | AWS S3 | PostgreSQL | None in the pod |

The two providers are chosen independently, so `Filesystem + PostgreSQL` and `S3 + LiteDB` work
too. Only the configuration changes between scenarios, never the source.

## Run it

```bash
# A and B: no infrastructure required
dotnet run --project src/StorageDemo.Api
# gRPC on :5080, REST and Swagger on :8080

# C: containers, MinIO and PostgreSQL
docker compose -f docker/docker-compose.yml up --build
# gRPC on :5081, REST on :8081, MinIO console on :9001 (minioadmin / minioadmin)

# D: Kubernetes
kubectl apply -f k8s/           # documents
kubectl apply -f k8s/live/      # and live streaming, layered over the same Deployment
kubectl -n storage-demo port-forward svc/storage-demo 5082:5080
```

Read `k8s/deployment.yaml` before applying it: the image name, the IRSA role annotation and the
`CHANGE_ME` secrets are placeholders, and its demo Redis and PostgreSQL exist so the manifests
apply on a laptop cluster — both should be managed services in a real one.

A desktop client (`dotnet run --project src/StorageDemo.Client`) is included as a thin demo of the
gRPC surface: file browsing and playback, live view with a MISB telemetry overlay, and a **Server**
box that switches between the four scenarios above without restarting.

## Live streaming

An encoder connects over SRT, names itself, and is on air from that moment — nothing is requested
first, and nothing is written to disk unless something asks for it. Full detail, including the
SRT internals, the gateway page for pulling and forwarding streams, and the detection pipeline, is
in [`CONTEXT.md`](CONTEXT.md) and the design notes under [`.scratch/scale-to-1000/`](.scratch/scale-to-1000);
the four pictures in [`docs/video-server.drawio`](docs/video-server.drawio) show the shape of it.
It is off by default (`Live__Enabled`) — switching it on opens a port anyone who can reach it may
push a stream into, so a deployment opts in rather than inheriting it.

## Security

- Storage keys are generated, never taken from the client; the filesystem provider refuses any key
  that escapes its configured root.
- Uploaded bytes are served with `nosniff` and a sandboxing `Content-Security-Policy`.
- No credentials in the repository: S3 uses the AWS provider chain (IRSA in a cluster), database
  credentials come from environment or secret references, and a live source URL is held to an
  explicit scheme allowlist, `http`/`https` excluded by default.
- Provider exceptions are translated at the infrastructure boundary, so `AmazonS3Exception` and
  `NpgsqlException` never reach the application layer.

## Tests

```bash
dotnet test
```

One specification per abstraction, run against every implementation, so LiteDB and PostgreSQL — and
the filesystem and S3 providers — are held to identical behaviour. Live streaming is tested the
same way it runs: a real SRT transport, a real demultiplexer, a real viewer. PostgreSQL's contract
tests skip unless `POSTGRES_TEST_CONNECTION` points at a real database.

## Migrations

```bash
dotnet ef migrations add <Name> --project src/StorageDemo.Infrastructure
```

Applied at startup in Compose; run as the Kubernetes Job in `k8s/deployment.yaml` instead, so
replicas never migrate concurrently.
