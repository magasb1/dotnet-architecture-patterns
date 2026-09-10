# Storage & Database Demo Application

## 1. Purpose

Build a small but production-oriented ASP.NET Core application demonstrating a pluggable architecture for:

1. **File/object storage**

   * Local filesystem on Windows/Linux.
   * S3-compatible object storage when deployed as a container/pod.

2. **Database persistence**

   * LiteDB for standalone/local execution.
   * PostgreSQL for container/Kubernetes execution.

The application must not couple application/domain code to any specific storage or database technology.

The intended configurations are:

| Runtime           | File storage  | Database   |
| ----------------- | ------------- | ---------- |
| Developer Windows | Filesystem    | LiteDB     |
| Developer Linux   | Filesystem    | LiteDB     |
| Docker            | S3-compatible | PostgreSQL |
| Kubernetes        | S3            | PostgreSQL |

The architecture must also technically support any combination of the two providers.

---

# 2. Core architectural principle

Treat file storage and database persistence as **two completely independent infrastructure concerns**.

Do not create:

```text
IStorageProvider
    ├── SaveFile()
    ├── SaveEntity()
    └── DeleteEntity()
```

Instead create:

```text
IFileStorage
IDocumentRepository
```

The application service coordinates them.

```text
                       API
                        |
                        v
                DocumentService
                  /           \
                 /             \
                v               v
        IFileStorage       IDocumentRepository
             |                    |
       +-----+-----+        +-----+------+
       |           |        |            |
       v           v        v            v
 Filesystem       S3      LiteDB     PostgreSQL
```

This separation is mandatory.

---

# 3. Technology baseline

Use:

* .NET 8 or later LTS
* ASP.NET Core Web API
* C#
* Nullable reference types enabled
* Dependency injection through the built-in Microsoft DI container
* `IOptions<T>` for configuration
* Entity Framework Core for PostgreSQL
* LiteDB for local database storage
* AWS SDK for S3
* Serilog for application logging
* Docker
* Docker Compose
* Kubernetes manifests
* xUnit for tests

Use asynchronous APIs throughout the application.

---

# 4. Solution structure

Use a layered/clean architecture without over-engineering the demo.

Recommended solution:

```text
StorageDemo.sln

src/
├── StorageDemo.Api/
│   ├── Controllers/
│   ├── Middleware/
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── StorageDemo.Api.csproj
│
├── StorageDemo.Application/
│   ├── Documents/
│   │   ├── DocumentService.cs
│   │   ├── DocumentDtos.cs
│   │   └── IDocumentService.cs
│   │
│   └── Common/
│       └── ...
│
├── StorageDemo.Domain/
│   ├── Documents/
│   │   └── Document.cs
│   └── Common/
│       └── ...
│
├── StorageDemo.Infrastructure/
│   ├── FileStorage/
│   │   ├── IFileStorage.cs
│   │   ├── FileSystem/
│   │   │   ├── FileSystemStorage.cs
│   │   │   └── FileSystemStorageOptions.cs
│   │   │
│   │   └── S3/
│   │       ├── S3Storage.cs
│   │       └── S3StorageOptions.cs
│   │
│   ├── Database/
│   │   ├── IDocumentRepository.cs
│   │   ├── LiteDb/
│   │   │   ├── LiteDbDocumentRepository.cs
│   │   │   ├── LiteDbOptions.cs
│   │   │   └── LiteDbInitializer.cs
│   │   │
│   │   └── PostgreSql/
│   │       ├── AppDbContext.cs
│   │       ├── DocumentConfiguration.cs
│   │       ├── PostgresDocumentRepository.cs
│   │       ├── PostgresOptions.cs
│   │       └── PostgresInitializer.cs
│   │
│   ├── Seeding/
│   │   ├── ISeedData.cs
│   │   ├── ReferenceDataSeeder.cs
│   │   └── DevelopmentDataSeeder.cs
│   │
│   └── DependencyInjection.cs
│
└── StorageDemo.Tests/
    ├── Application/
    ├── Infrastructure/
    └── Integration/

docker/
├── Dockerfile
└── docker-compose.yml

k8s/
├── namespace.yaml
├── configmap.yaml
├── deployment.yaml
├── service.yaml
└── postgres.yaml

README.md
```

The exact number of projects can be reduced if desired, but the dependency direction must remain.

---

# 5. Dependency direction

Dependencies must point inward:

```text
Api
 |
 v
Application
 |
 v
Domain

Infrastructure
 |
 +----> Application
 |
 +----> Domain
```

Domain must not reference Infrastructure.

Application must not reference:

* LiteDB
* PostgreSQL
* Entity Framework
* AWS SDK
* filesystem APIs

Infrastructure contains those dependencies.

---

# 6. Domain model

Create a `Document` entity.

```csharp
public sealed class Document
{
    public Guid Id { get; init; }

    public required string FileName { get; init; }

    public required string StorageKey { get; init; }

    public string? ContentType { get; init; }

    public long Size { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
```

The database stores metadata only.

The actual file bytes are stored through `IFileStorage`.

The `StorageKey` identifies the object in the selected file store.

Example:

```text
documents/
    6d6e3c4a.../
        report.pdf
```

Never use the original filename as the sole storage key.

---

# 7. File storage abstraction

Define:

```csharp
public interface IFileStorage
{
    Task SaveAsync(
        string key,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string key,
        CancellationToken cancellationToken = default);
}
```

The abstraction represents object/blob storage, not a physical filesystem.

Do not expose:

* `FileInfo`
* `IFormFile`
* `AmazonS3Client`
* S3 bucket names
* filesystem paths

outside Infrastructure.

---

# 8. Filesystem implementation

Implement:

```text
FileSystemStorage : IFileStorage
```

Configuration:

```json
{
  "Storage": {
    "Provider": "FileSystem",
    "FileSystem": {
      "RootPath": "./data/files"
    }
  }
}
```

The implementation must:

* create directories when required;
* stream uploads rather than loading entire files into memory;
* support Windows;
* support Linux;
* normalize paths;
* prevent `../` path traversal;
* ensure the resulting path remains underneath the configured root;
* create the root directory if necessary.

Example:

```text
Storage root:
    /var/lib/storagedemo/files

Key:
    documents/123/report.pdf

Physical file:
    /var/lib/storagedemo/files/documents/123/report.pdf
```

The key must never be allowed to escape the configured root.

---

# 9. S3 implementation

Implement:

```text
S3Storage : IFileStorage
```

Configuration:

```json
{
  "Storage": {
    "Provider": "S3",
    "S3": {
      "Bucket": "storage-demo",
      "Region": "eu-north-1",
      "ServiceUrl": null,
      "ForcePathStyle": false
    }
  }
}
```

`ServiceUrl` and `ForcePathStyle` exist to make the implementation work with local S3-compatible systems such as MinIO.

Production AWS configuration should normally leave `ServiceUrl` unset.

Use the AWS SDK credential provider chain.

Do not put AWS access keys in source code.

For Kubernetes, the design should support workload identity/IRSA or the equivalent AWS identity mechanism.

---

# 10. Database abstraction

Do not create a generic repository such as:

```csharp
IRepository<T>
```

for this demo.

Define a domain/application-specific repository:

```csharp
public interface IDocumentRepository
{
    Task<Document?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Document>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Document document,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
```

The interface must not expose EF Core or LiteDB types.

---

# 11. LiteDB implementation

Implement:

```text
LiteDbDocumentRepository : IDocumentRepository
```

Configuration:

```json
{
  "Database": {
    "Provider": "LiteDb",
    "LiteDb": {
      "Path": "./data/database/app.db"
    }
  }
}
```

LiteDB should be registered as a singleton because it owns the database connection/file lifecycle.

Create:

```text
documents
```

collection.

Ensure a unique index on:

```text
Id
```

The repository maps between LiteDB persistence and the domain entity.

Do not leak LiteDB-specific types through the repository interface.

---

# 12. PostgreSQL implementation

Use EF Core.

Create:

```text
AppDbContext
```

with:

```csharp
public DbSet<Document> Documents => Set<Document>();
```

Configuration:

```json
{
  "Database": {
    "Provider": "Postgres",
    "Postgres": {
      "ConnectionString": "..."
    }
  }
}
```

Use PostgreSQL-specific EF configuration in Infrastructure.

The domain entity should not contain EF Core annotations unless there is a compelling reason.

Prefer:

```text
DocumentConfiguration : IEntityTypeConfiguration<Document>
```

over decorating the domain model with database attributes.

---

# 13. Provider selection

Storage and database providers must be independently selectable.

Example:

```text
Storage__Provider=FileSystem
Database__Provider=LiteDb
```

or:

```text
Storage__Provider=S3
Database__Provider=Postgres
```

Do not combine them into:

```text
Provider=PostgresS3
```

The application must support:

```text
Filesystem + LiteDB
Filesystem + PostgreSQL
S3 + LiteDB
S3 + PostgreSQL
```

even though only two combinations are the primary documented scenarios.

---

# 14. Configuration classes

Prefer strongly typed options.

Example:

```csharp
public sealed class FileSystemStorageOptions
{
    public required string RootPath { get; init; }
}
```

```csharp
public sealed class S3StorageOptions
{
    public required string Bucket { get; init; }

    public required string Region { get; init; }

    public string? ServiceUrl { get; init; }

    public bool ForcePathStyle { get; init; }
}
```

Validate options at startup.

A misconfigured application should fail during startup rather than during the first upload.

---

# 15. Application service

Create:

```csharp
public interface IDocumentService
{
    Task<Document> UploadAsync(...);

    Task<Stream?> DownloadAsync(...);

    Task<Document?> GetAsync(...);

    Task DeleteAsync(...);
}
```

Implementation:

```text
DocumentService
    |
    +---- IFileStorage
    |
    +---- IDocumentRepository
```

The service is responsible for coordinating the two systems.

---

# 16. Upload flow

The intended upload flow is:

```text
HTTP POST
   |
   v
DocumentsController
   |
   v
DocumentService
   |
   +--------------------+
   |                    |
   v                    |
Generate Document ID    |
   |                    |
   v                    |
Generate StorageKey     |
   |                    |
   v                    |
IFileStorage.SaveAsync()
   |
   v
File successfully stored
   |
   v
IDocumentRepository.AddAsync()
   |
   v
HTTP 201
```

The original uploaded filename must not determine the physical object identity.

Example:

```text
ID:
    8c5e...

Storage key:
    documents/8c5e.../invoice.pdf
```

---

# 17. Failure handling

File storage and database storage cannot share a normal ACID transaction.

Therefore explicitly handle partial failure.

Recommended flow:

```text
1. Generate document ID.
2. Generate storage key.
3. Store file.
4. Store metadata.
5. Return success.
```

If step 4 fails:

```text
file exists
database record does not
```

The service should attempt to delete the newly created file.

Conceptually:

```csharp
await fileStorage.SaveAsync(...);

try
{
    await repository.AddAsync(document);
}
catch
{
    await fileStorage.DeleteAsync(document.StorageKey);
    throw;
}
```

If cleanup itself fails, log the failure.

Do not hide the original exception.

For the demo, this compensation approach is sufficient.

Document in the README that a production system with very high reliability requirements should consider an outbox/state-machine approach.

---

# 18. Delete flow

Delete should work in the opposite direction:

```text
1. Read document metadata.
2. Delete file.
3. Delete database metadata.
```

If file deletion succeeds but database deletion fails, the database still references a missing object.

If database deletion succeeds but file deletion fails, an orphaned object remains.

The implementation should therefore:

* log failures;
* make deletes idempotent;
* treat missing files as successfully deleted;
* provide enough information for cleanup/reconciliation.

Do not make the API falsely claim distributed transactional guarantees.

---

# 19. Download flow

```text
GET /api/documents/{id}
```

Process:

```text
Controller
    |
    v
DocumentService
    |
    +----> Repository.GetAsync()
    |
    +----> IFileStorage.OpenReadAsync()
    |
    v
HTTP streaming response
```

Do not load the entire file into a byte array.

Use a streaming response.

Return:

* `404` if the document metadata doesn't exist;
* `404` if the metadata exists but the object is missing;
* appropriate content type;
* original filename in the response.

---

# 20. API endpoints

Implement:

```text
POST   /api/documents
GET    /api/documents
GET    /api/documents/{id}
GET    /api/documents/{id}/content
DELETE /api/documents/{id}
```

Swagger/OpenAPI should be enabled in development.

The upload endpoint should use `multipart/form-data`.

---

# 21. Database seeding

Seeding must be idempotent.

Do not use:

```csharp
if (!db.Documents.Any())
```

as the only seed mechanism.

Use stable IDs.

Example:

```text
11111111-1111-1111-1111-111111111111
```

and insert only if the record doesn't already exist.

For LiteDB use `Upsert` where appropriate.

For PostgreSQL use an existence check or an appropriate upsert strategy.

---

# 22. Separate reference data and development data

Production seed:

```text
ReferenceDataSeeder
```

Development seed:

```text
DevelopmentDataSeeder
```

Development-only seed data must never be automatically inserted into production.

Example:

```text
Development:
    welcome.txt
    demo document
    test records

Production:
    only required reference/configuration data
```

---

# 23. Seed files

If development seed data contains an actual file, do not fake the database record.

Use the real abstractions:

```text
DevelopmentDataSeeder
        |
        +---- IFileStorage.SaveAsync()
        |
        +---- IDocumentRepository.AddAsync()
```

This verifies that:

```text
Filesystem + LiteDB
```

and:

```text
S3 + PostgreSQL
```

both perform the same logical operation.

The seed file should live under something like:

```text
SeedData/
    welcome.txt
```

---

# 24. Database initialization

Introduce:

```csharp
public interface IDatabaseInitializer
{
    Task InitializeAsync(
        CancellationToken cancellationToken = default);
}
```

LiteDB:

```text
Create database
Create collections
Create indexes
Seed reference data
```

PostgreSQL:

```text
Apply EF migrations
Seed reference data
```

For production Kubernetes deployments, prefer migrations as a dedicated deployment/init job rather than having multiple replicas simultaneously perform migrations.

---

# 25. Logging

Use Serilog.

Application logging must not depend on the file-storage abstraction.

Logging is a separate concern.

Development:

```text
Console
+
local rolling log file
```

Container:

```text
Console / stdout
```

The preferred Kubernetes approach is to write structured logs to stdout/stderr and let the container platform collect them.

Do not implement "one S3 object per log event" as the production logging architecture.

If demonstrating archival to S3, make it a separate optional sink/example.

---

# 26. Logging requirements

Use structured properties:

```text
DocumentId
StorageKey
StorageProvider
DatabaseProvider
RequestId
TraceId
```

Example:

```csharp
logger.LogInformation(
    "Document uploaded {DocumentId} using {StorageProvider}",
    document.Id,
    storageProvider);
```

Do not log:

* file contents;
* credentials;
* AWS secrets;
* database passwords;
* authorization headers.

---

# 27. Health checks

Implement:

```text
GET /health/live
GET /health/ready
```

Liveness should answer:

```text
Is the application process alive?
```

Readiness should verify the dependencies required to serve requests.

For example:

```text
PostgreSQL → database connectivity
S3 → bucket/service availability where appropriate
Filesystem → configured root is accessible
LiteDB → database can be opened
```

Avoid making liveness dependent on external infrastructure.

---

# 28. Docker

Create a multi-stage Dockerfile.

Requirements:

* build using SDK image;
* run using ASP.NET runtime image;
* non-root container user where practical;
* expose HTTP port;
* no credentials baked into image;
* configuration supplied through environment variables.

Example runtime configuration:

```text
Storage__Provider=S3
Database__Provider=Postgres
```

---

# 29. Docker Compose

Provide a Compose environment containing:

```text
storage-demo-api
postgres
minio
```

The API should be configured as:

```text
File storage → MinIO
Database → PostgreSQL
```

This gives the developer a complete containerized environment without requiring an AWS account.

MinIO is only a local development substitute for S3.

---

# 30. Kubernetes

Provide manifests demonstrating:

```text
Deployment
Service
ConfigMap
Secret
```

The deployment should demonstrate:

```text
Storage__Provider=S3
Database__Provider=Postgres
```

Do not store AWS access keys in Kubernetes manifests.

The documentation should explain that AWS workload identity should be used in a real cluster.

PostgreSQL can be included as a demo deployment, but explicitly document that production PostgreSQL should normally be an externally managed database or a properly operated database service rather than a simple demo pod.

---

# 31. Persistent volumes

The architecture should make persistence requirements obvious.

Standalone filesystem mode:

```text
Application
    |
    +-- ./data/files
    +-- ./data/database
```

Container filesystem mode should use a volume if data must survive container replacement.

Kubernetes S3 mode does not require a persistent volume for uploaded files.

LiteDB does require persistent storage.

Therefore:

```text
Filesystem + LiteDB
        |
        +---- persistent disk required
```

while:

```text
S3 + PostgreSQL
        |
        +---- application pod can remain stateless
```

This distinction should be explicitly documented.

---

# 32. Security

Minimum requirements:

### File uploads

* enforce configurable maximum upload size;
* never trust client-provided paths;
* sanitize/display filenames;
* generate storage keys;
* validate content type where appropriate;
* never execute uploaded files;
* do not serve uploads from an executable web root.

### S3

* use IAM roles/workload identity;
* least-privilege bucket permissions;
* do not use public buckets;
* do not expose AWS credentials through configuration files committed to Git.

### Database

* credentials via environment/secret management;
* no passwords in source control;
* TLS in production;
* least-privilege database user.

---

# 33. Testing strategy

The abstractions exist partly to make testing easy.

## Unit tests

Test `DocumentService` with mocks/fakes:

```text
FakeFileStorage
FakeDocumentRepository
```

Test:

```text
upload succeeds
database failure causes file cleanup
download missing document
download missing file
delete
```

No actual filesystem, S3, LiteDB or PostgreSQL is required for these tests.

---

# 34. File storage integration tests

Run tests against:

```text
FileSystemStorage
```

using a temporary directory.

Test:

```text
save
read
exists
delete
nested keys
path traversal rejection
```

Also run S3 tests against MinIO where practical.

---

# 35. Database integration tests

LiteDB tests can use temporary database files.

PostgreSQL integration tests should run against a disposable PostgreSQL instance.

The tests should verify that both implementations satisfy the same behavioral contract.

For example:

```text
IDocumentRepository contract tests
        |
        +---- LiteDbDocumentRepository
        |
        +---- PostgresDocumentRepository
```

This is preferable to writing completely different test logic for each implementation.

---

# 36. Contract testing

Create shared test specifications.

Conceptually:

```csharp
public abstract class DocumentRepositoryContract
{
    protected abstract IDocumentRepository CreateRepository();

    [Fact]
    public async Task Can_insert_and_read_document()
    {
        ...
    }
}
```

Then execute the contract against LiteDB and PostgreSQL.

Do the same for `IFileStorage`.

This ensures that provider implementations behave consistently.

---

# 37. Do not leak infrastructure

The following must not appear in Application:

```text
LiteDatabase
ILiteCollection
DbContext
DbSet
IAmazonS3
AmazonS3Exception
FileStream
DirectoryInfo
```

Infrastructure is allowed to know about all of these.

Application should only know:

```text
IFileStorage
IDocumentRepository
```

and domain types.

---

# 38. Provider-specific error handling

Infrastructure should translate provider-specific errors into meaningful application/infrastructure exceptions where necessary.

For example:

```text
AmazonS3Exception
        ↓
StorageException
```

Do not force the application layer to know what an `AmazonS3Exception` is.

Likewise:

```text
PostgresException
LiteException
        ↓
appropriate infrastructure exception
```

Do not blindly catch every exception and return `null`.

---

# 39. Cancellation

Every external operation must accept and propagate:

```csharp
CancellationToken
```

This includes:

* file uploads;
* S3 operations;
* database queries;
* database writes;
* downloads where supported.

HTTP request cancellation should flow into the application and infrastructure layers.

---

# 40. Configuration example

Development:

```json
{
  "Storage": {
    "Provider": "FileSystem",
    "FileSystem": {
      "RootPath": "./data/files"
    }
  },
  "Database": {
    "Provider": "LiteDb",
    "LiteDb": {
      "Path": "./data/database/app.db"
    }
  }
}
```

Container:

```yaml
environment:
  Storage__Provider: S3
  Storage__S3__Bucket: storage-demo
  Storage__S3__Region: eu-north-1
  Storage__S3__ServiceUrl: http://minio:9000
  Storage__S3__ForcePathStyle: true

  Database__Provider: Postgres
  Database__Postgres__ConnectionString: ...
```

---

# 41. Example application flow

A user uploads:

```text
invoice.pdf
```

The application creates:

```text
Document ID:
    42e7c...

Storage key:
    documents/42e7c.../invoice.pdf
```

Filesystem mode:

```text
./data/files/documents/42e7c.../invoice.pdf
```

S3 mode:

```text
s3://storage-demo/documents/42e7c.../invoice.pdf
```

The database contains:

```text
Id
42e7c...

FileName
invoice.pdf

StorageKey
documents/42e7c.../invoice.pdf

ContentType
application/pdf

Size
123456

CreatedAt
...
```

No database implementation knows where the bytes are physically stored.

---

# 42. What the demo must demonstrate

The demo is successful if the following can be performed without changing application code.

## Scenario A

```text
Windows
Filesystem
LiteDB
```

Upload/download/delete works.

## Scenario B

```text
Linux
Filesystem
LiteDB
```

Upload/download/delete works.

## Scenario C

```text
Docker Compose
MinIO
PostgreSQL
```

Upload/download/delete works.

## Scenario D

```text
Kubernetes
AWS S3
PostgreSQL
```

Upload/download/delete works.

The only changes between scenarios should be configuration/deployment.

---

# 43. Acceptance criteria

The senior developer should consider the implementation complete when:

* [ ] Application/domain code has no S3 dependency.
* [ ] Application/domain code has no LiteDB dependency.
* [ ] Application/domain code has no EF Core dependency.
* [ ] File storage is represented by `IFileStorage`.
* [ ] Database persistence is represented by `IDocumentRepository`.
* [ ] Filesystem implementation works on Windows.
* [ ] Filesystem implementation works on Linux.
* [ ] S3 implementation works against AWS S3.
* [ ] S3 implementation works against MinIO.
* [ ] LiteDB implementation works.
* [ ] PostgreSQL implementation works.
* [ ] File storage provider and database provider are independently configurable.
* [ ] Uploads are streamed.
* [ ] Downloads are streamed.
* [ ] Filesystem path traversal is prevented.
* [ ] Provider-specific exceptions do not leak into Application.
* [ ] Database seed is idempotent.
* [ ] Development seed can create both file and database data through the abstractions.
* [ ] PostgreSQL uses EF migrations.
* [ ] Health endpoints exist.
* [ ] Docker Compose starts the complete demo.
* [ ] Kubernetes manifests are provided.
* [ ] No credentials are committed.
* [ ] Unit tests exist for application orchestration.
* [ ] Contract tests exist for repository/storage implementations.
* [ ] README documents all four primary runtime combinations.

---

# 44. Definition of done

The final repository should allow a developer to perform:

```bash
dotnet run
```

and get:

```text
Filesystem + LiteDB
```

without installing PostgreSQL or AWS infrastructure.

It should also allow:

```bash
docker compose up
```

and get:

```text
Container + MinIO + PostgreSQL
```

with no application source changes.

The architecture should make it obvious how to replace:

```text
LiteDB
```

with:

```text
PostgreSQL
```

and:

```text
Filesystem
```

with:

```text
S3
```

without modifying controllers, application services, or domain objects.

---

# 45. Architectural summary

The final design should look like this:

```
                     ┌─────────────────────┐
                     │    ASP.NET Core API  │
                     └──────────┬──────────┘
                                │
                                ▼
                     ┌─────────────────────┐
                     │   Application       │
                     │                     │
                     │   DocumentService   │
                     └───────┬───────┬─────┘
                             │       │
                ┌────────────┘       └────────────┐
                ▼                                 ▼
         ┌──────────────┐                 ┌──────────────┐
         │ IFileStorage  │                 │ IDocument    │
         │              │                 │ Repository   │
         └──────┬───────┘                 └──────┬───────┘
                │                                │
         ┌──────┴──────┐                  ┌──────┴──────┐
         ▼             ▼                  ▼             ▼
    Filesystem        S3               LiteDB       PostgreSQL
         │             │                  │             │
         └─────────────┴──────────────────┴─────────────┘
                          Infrastructure
```

Configuration determines which implementation is injected.

The application does not know which implementation is active.
