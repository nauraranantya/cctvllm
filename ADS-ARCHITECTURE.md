# ADS project structure

## Abstraction

- `Interfaces`: contracts used by services and controllers. `IQueueRepository` isolates persistence.
- `Models`: request and configuration models.
- `Entities`: persisted domain records such as `VideoJob`.

## Infrastructure

- `Services`: video inspection, people detection and background description processing.
- `Helpers`: prompt construction and application exceptions.

## EntityFramework

- `Repositories`: persistence implementations. The current `QueueStore` is a JSON-backed development repository behind `IQueueRepository`; replace it with an EF Core implementation for production without changing the UI or processing services.
- `Queries`: reusable queue projections and filters.

## Web

- `Controllers`: MVC entry points. Existing `/api` routes remain compatible while they are migrated into API controllers.
- `Views`: Razor views and shared partials.
- `wwwroot`: JavaScript, CSS and static assets.

## UI and integration

The MVC view loads Bootstrap 5.2.3 and keeps application styling in `wwwroot/style.css`. Shared Razor partials are the home for reusable login/authentication status, quick find, grid and form components. Telerik UI for ASP.NET Core or Kendo UI can replace the queue and form markup after the licensed package/feed is provided; do not commit license keys.

Authentication should use an ASP.NET Core authentication handler (typically OpenID Connect or cookies). Authorization belongs on controllers/actions through policies and `[Authorize]`, while secrets remain in environment variables or the project secret store. The current loopback demo intentionally remains locally accessible without sign-in.
