# telemetry-client

The Angular 20 source of the Keryhe Telemetry UI (Angular Material, ApexCharts, ngx-graph). It is not part of the .NET
solution: `Keryhe.Telemetry.Ui` builds it (`npm ci` / `npm run build`) and packages `dist/telemetry-client/browser` as
static web assets, which the API host serves. See [the UI package README](../Keryhe.Telemetry.Ui/README.md) for hosting
and configuration.

## Development server

```bash
npm install
npm start
```

The dev server runs on `http://localhost:4201` and proxies `/api` to the API host at `https://localhost:7105`
(`proxy.conf.json`), so run `Keryhe.Telemetry.Api.Server` beside it with the API at `/api` (its shipped
`appsettings.json` mounts it at `/telemetry/api`):

```bash
dotnet run --project src/Keryhe.Telemetry.Api.Server -- --Telemetry:Api:BasePath=/api
```

The client reads its API location and branding
from `GET config.json` before it bootstraps (`src/app/core/config/load-config.ts`), so one build works wherever a host
mounts the UI or the API.

## Building

```bash
npm run build
```

The output is in `dist/telemetry-client/browser`. Publishing `Keryhe.Telemetry.Api.Server` runs this build for you.

## Tests

`npm test` runs Karma/Jasmine; no meaningful tests are set up yet.
