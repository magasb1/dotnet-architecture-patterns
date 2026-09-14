# Web UI modernization plan

## Outcome

Turn the single `/streaming` operator page into a small, modern administration console while
preserving its current stream-configuration behavior. The first delivery remains focused on
sources and forwards, but establishes a shell, component vocabulary, application boundary, and
configuration model that can later host backend settings and other operator features without
growing another monolithic page.

The recommended shape is still a server-rendered Blazor Web App using Interactive Server. It fits
the existing deployment, avoids adding a separate frontend build and authentication surface, and
can use the services already in the API process. The UI should depend on purpose-built application
services rather than directly coordinating infrastructure services from Razor components.

## What exists today

The current page already has good operational semantics that must not be lost:

- It distinguishes configured sources from streams that happen to be on air.
- It refreshes every two seconds without overlapping reads and retains the last good view on a
  refresh failure.
- It reports total, streaming, alert, and inactive counts.
- It creates and edits pull or push sources, enables or parks them, and keeps the stream name
  immutable after creation.
- It shows the local SRT output, throughput, viewers, forward health, transport statistics, and the
  last forward error.
- It adds and removes forward targets while preserving the source as the aggregate.
- It explains that deleting configuration does not stop an already-running feed.
- It handles disabled live streaming and protects the page with the configured live token.

The main constraints are structural rather than functional:

- `Streaming.razor` is 708 lines and owns rendering, authentication, polling, validation,
  formatting, commands, and transient UI state.
- `app.css` is one global, page-specific stylesheet with no responsive breakpoint, reusable design
  primitives, or visible keyboard-focus treatment.
- `App.razor` has no layout or navigation because it assumes there will only ever be one page.
- Source commands call `ILiveSourceStore` directly. This is efficient, but puts orchestration and
  error behavior in the component and makes another administration feature likely to repeat it.
- Validation is duplicated between the component and the API rules.
- Only view-model arithmetic is tested. Component interactions, accessibility, responsive layout,
  authorization, and destructive workflows have no automated coverage.
- The page renders every source every two seconds. That is acceptable for today's small manual
  list but is not the intended route to hundreds or thousands of sources.

## Product and interaction design

### Information architecture

Build a durable application shell now, but only expose destinations that work:

```text
Storage Demo
├── Streaming
│   └── Sources
├── Settings                 (introduced when the first settings page exists)
│   ├── Live streaming
│   ├── Storage
│   ├── Database
│   ├── Messaging
│   ├── Media processing
│   └── Retention
└── System                    (future)
    ├── Health
    └── About / diagnostics
```

Use a collapsible left navigation on desktop and a compact top bar plus drawer on narrow screens.
The shell owns the product name, environment badge, connection/health indicator, navigation, and
user/session action. Each page owns only its title, description, actions, and content.

Keep `/streaming` as a redirect or route alias so existing bookmarks continue to work. Make the
canonical route `/streaming/sources`; reserve `/settings/*` and `/system/*` for later modules.

### Sources page

The page should be an operator workspace rather than a wall of controls:

```text
Sources                                      [Add source]
Configure ingest and forwarding

[Search by name or URL...] [All states v] [All types v]       Updated 4s ago [Refresh]

[ 24 total ] [ 18 streaming ] [ 2 alerts ] [ 4 inactive ]

  NAME / OUTPUT          TYPE   STATUS       RATE       VIEWERS   FORWARDS   ACTIONS
  camera/north           Pull   Streaming    4.2 Mb/s       3       2/2       ...
  camera/loading-bay     Push   Interrupted   0 kb/s          0       1/2       ...
```

- On desktop, use a compact semantic table with a sticky header and expandable detail row. It is
  easier to scan and compare than independently sized cards.
- On small screens, switch each row to a card with the same label/value order; do not force a wide
  table into horizontal scrolling.
- Add client-side name/URL search, state and pull/push filters, and deterministic sorting. Show the
  filtered count without changing the overall status summary.
- Use status text and icons as well as color. Mark stale data explicitly when refreshes fail.
- Keep transport details on demand. Group them into `Input / local output`, `Forwards`, and
  `Diagnostics`, with unavailable metrics explained rather than represented by unexplained dashes.
- Move add/edit into a right-side drawer on desktop and a full-screen sheet on mobile. Use one
  `EditForm` with field-level validation, a summary for server errors, dirty-state protection, and
  clear saving/saved feedback.
- Edit forwards in that same source drawer. Existing forward status remains visible beside each
  target, while changes are staged and submitted as one source aggregate.
- Put copy, edit, and delete in a labelled overflow menu. Keep copy as a convenient inline action
  when space permits. Use real SVG icons, tooltips, and accessible names rather than glyph text.
- Use a modal confirmation for delete. State the exact source name and preserve the current
  configuration-only deletion semantics. Do not add a combined “delete and stop” action implicitly.
- Provide useful empty states for no sources, no search results, disabled streaming, first load,
  and loss of the Blazor connection.

### Visual system

Retain the dark, video-operations character, but make it calmer and more systematic:

- Define semantic tokens for surface levels, borders, text, focus, accent, success, warning,
  danger, spacing, radii, elevation, and typography. Support light mode later without changing
  component CSS.
- Use a neutral surface hierarchy with color reserved for state and primary actions. Avoid a
  colored edge, badge, dot, and text all communicating the same state at once.
- Establish 8 px spacing, 40 px minimum control height, consistent field/help/error placement, and
  a readable content width while allowing the source table to use the viewport.
- Use tabular numbers for changing metrics and monospace only for URLs and identifiers.
- Include `:focus-visible`, skip navigation, keyboard-operable expansion and menus, labelled form
  controls, `aria-live` refresh/save feedback, reduced-motion support, sufficient contrast, and a
  logical mobile reading order. Target WCAG 2.2 AA.
- Prefer component-scoped `.razor.css` files. Keep `app.css` for reset, tokens, typography, and the
  few true shell-wide rules.

Do not add live thumbnails in the first delivery. They introduce decoding, bandwidth, caching,
visibility, and privacy concerns unrelated to modernizing configuration. A later preview component
can be designed as an optional capability with explicit loading and refresh budgets.

## Target code structure

Organize by feature, with a small shared UI layer:

```text
Components/
├── App.razor
├── Layout/
│   ├── MainLayout.razor
│   ├── AppSidebar.razor
│   └── ConnectionStatus.razor
├── Shared/
│   ├── PageHeader.razor
│   ├── StatusBadge.razor
│   ├── MetricCard.razor
│   ├── EmptyState.razor
│   ├── ConfirmDialog.razor
│   ├── FormField.razor
│   └── ToastHost.razor
├── Streaming/
│   ├── Pages/Sources.razor
│   ├── Components/SourceTable.razor
│   ├── Components/SourceRowDetails.razor
│   ├── Components/SourceEditor.razor
│   ├── Components/ForwardEditor.razor
│   └── Models/...
└── Settings/                 (added with the first settings feature)
    ├── Pages/...
    └── Components/...

Administration/
├── Streaming/IStreamConfigurationService.cs
├── Streaming/StreamConfigurationService.cs
├── Streaming/Models/...
├── Settings/ISettingsCatalog.cs
└── Settings/...
```

`IStreamConfigurationService` should expose task-oriented operations such as listing the combined
source status, validating/saving a source, and deleting a configuration. It can still use
`ILiveSourceStore` and `ILiveStreamService` in-process, but owns cancellation, validation,
exception mapping, and view-model construction. Razor components then manage presentation state,
not storage behavior.

Keep the existing REST and gRPC surfaces. They are useful public boundaries and should share the
same domain validation/application operations where practical, but the server-side UI need not call
its own HTTP endpoint merely to create architectural ceremony.

Split live updates from edits:

- A page-scoped state container owns the current query, filters, expanded row, selection, last
  successful refresh, and refresh error.
- A polling service uses `PeriodicTimer`, cancellation, and one in-flight refresh, preserving the
  current behavior.
- Re-render only data-bearing components. Use stable `@key` values and preserve drawer/form state
  across refreshes.
- Start with filtered rendering. Add `Virtualize` after measuring it with representative source
  counts; do not combine expansion and virtualization without testing variable row heights.
- Keep writes pessimistic: disable the affected action, wait for durable storage, refresh, then
  report success. Never display an unsaved source as committed.

Before supporting multiple simultaneous administrators, add optimistic concurrency to the source
aggregate. A version or ETag must be checked by both file and Redis stores so one operator cannot
silently overwrite another operator's forward changes. `UpdatedAt` may be displayed, but should
not be treated as a concurrency token unless stores compare it atomically.

## Extending into backend settings

Backend configuration is not one homogeneous editable object. Model these categories explicitly:

| Category | Examples | UI behavior |
|---|---|---|
| Runtime editable | retention schedule, selected operational thresholds where consumers support reload | Validate, save durably, apply through `IOptionsMonitor`, show effective time |
| Restart required | ports, provider selection, FFmpeg library path, database wiring | Stage and validate; label `Restart required`; never imply the running process changed |
| Deployment owned | Kubernetes service ports, pod identity, peer/public addresses supplied by the platform | Show effective value and source; provide copyable deployment guidance rather than rewriting the pod |
| Secret | live token, database/Redis credentials, object-store credentials | Never return the current value; support replace/clear only with an authorization policy and audit trail |
| Read only / derived | active providers, node name, health, bound endpoints | Display as system information, not form fields |

Do not reflect every `IOptions<T>` property into a generic form. Each settings section should be a
strongly typed feature with an explicit descriptor: route, title, authorization policy, read model,
editor model, validator, restart behavior, secret fields, and apply handler. A small
`ISettingsCatalog` can register those sections for navigation without making their persistence or
validation generic.

Do not write `appsettings.json` from the web process. In containers it may be immutable, changes
would be replica-local, environment variables would still override it, and a rollout would discard
the edit. When editable backend settings are introduced:

1. Show an effective, redacted configuration view with the winning source (`default`, JSON,
   environment, secret provider, or operational override).
2. Add a durable, shared `IConfigurationOverrideStore` only for explicitly supported fields.
3. Add an `IConfigurationProvider` for those overrides at a documented precedence and use reload
   tokens only for services proven to handle live change.
4. Store pending restart-required changes separately from active values and expose an export or
   deployment integration rather than pretending this process can safely restart a cluster.
5. Record actor, timestamp, old/new redacted values, validation result, and apply/restart outcome.
6. Add role-based policies such as `Streams.View`, `Streams.Manage`, `Settings.View`, and
   `Settings.ManageSecrets`.

The current live-token gate is acceptable only as a compatibility bridge for the sources page.
Before exposing backend or secret settings, use normal ASP.NET Core authentication and authorization
(for example, OIDC plus a secure cookie), protect the whole admin route group, and keep secrets out
of browser storage and rendered markup. The current fixed-time token comparison can remain on the
external live APIs until their authentication is deliberately migrated.

## Delivery phases

### Phase 0 — Baseline and decisions

- Record the current behaviors above as acceptance tests.
- Add a browser-level smoke path for unlock, list, filter, create/edit, forward add/remove, delete,
  refresh failure, disabled live streaming, and narrow viewport.
- Add accessibility automation and a manual keyboard checklist.
- Capture representative fixtures for empty, healthy, interrupted, failed-forward, long-name,
  long-URL, and 500+ source states.
- Write short decisions confirming Interactive Server, no UI framework initially, authentication
  migration timing, and which configuration categories may eventually be editable.

Exit: behavior is protected, scope is agreed, and the team can compare the old and new surfaces.

### Phase 1 — Shell and design foundation

- Introduce `MainLayout`, responsive navigation, page header, connection status, error boundary,
  toast host, tokens, typography, buttons, inputs, badges, drawers, and dialogs.
- Keep `/streaming` functional while adding `/streaming/sources`.
- Add the authorization policy boundary even if it temporarily delegates to the existing token
  compatibility mechanism.
- Verify desktop, tablet, and phone layouts plus keyboard and screen-reader basics.

Exit: the application looks and behaves like a multi-page console, with no stream behavior moved
yet.

### Phase 2 — Sources workspace

- Extract the stream configuration application service and presentation models.
- Replace the page with the searchable/filterable responsive table/card view.
- Implement source and forward editing in the drawer, destructive confirmation in a modal, and
  clear loading/saving/stale/error states.
- Preserve every semantic behavior listed in “What exists today.”
- Add component and end-to-end coverage; measure refresh/render performance with the large fixture.

Exit: feature parity is achieved in the modern UI, the old monolithic component can be deleted,
and `/streaming` redirects without breaking bookmarks.

### Phase 3 — Operational hardening

- Add aggregate concurrency protection, authorization policies, audit events for source mutations,
  and structured user-safe errors with correlation IDs.
- Tune rendering from measurements; introduce virtualization only if needed.
- Add telemetry for active circuits, refresh duration/failures, command outcomes, and disconnected
  clients without putting sensitive URLs into labels or logs.
- Update the README and operator documentation.

Exit: the console is safe for concurrent operators and representative production scale.

### Phase 4 — First backend settings slice

- Start with a low-risk section: an effective, read-only settings/system view.
- Show active providers, redacted endpoints, restart classification, and configuration origin.
- Then choose one truly reloadable, non-secret section and implement the override-store/audit path
  end to end. This validates the extension model before adding storage, database, or credential
  changes.
- Add restart-required staging and secret replacement only after deployment ownership and identity
  are defined.

Exit: adding a settings section is a bounded feature addition, not a modification of the shell or
streaming page.

## Verification and acceptance criteria

The modernization is complete when:

- All existing source and forward operations have the same durable and runtime semantics.
- A refresh failure retains the last good data and clearly marks it stale.
- Editing is not disrupted by the two-second refresh loop, and repeated clicks cannot issue
  duplicate writes.
- Search/filter interactions remain responsive with at least 500 representative rows; the agreed
  target source count is tested before release.
- The layout works at 360 px, 768 px, 1280 px, and a wide operator display without clipped actions
  or mandatory horizontal page scrolling.
- Primary workflows are keyboard-complete, status does not rely on color, automated accessibility
  checks have no serious violations, and manual screen-reader checks cover forms and dialogs.
- Unauthorized users cannot render or invoke management operations, and secret values never enter
  HTML, logs, telemetry, or browser storage.
- Component tests cover state and validation; integration tests cover application services and
  persistence failures; end-to-end tests cover the critical operator path.
- Adding a placeholder second feature requires only a page/module registration and navigation
  entry—no changes to the streaming feature.

## Suggested first implementation increment

Deliver Phases 0–2 as the first user-visible milestone. It gives the requested modern stream
configuration experience and creates the extension seams. Keep actual backend-setting mutation out
of that milestone; build only the shell and contracts needed for it. Backend settings have different
persistence, deployment, restart, authorization, and secret-handling requirements, and treating
them as ordinary source forms would create a polished but unsafe control plane.
