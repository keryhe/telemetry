# telemetry-client

The Angular 20 source of the Keryhe Telemetry UI. It is not part of the .NET solution: `Keryhe.Telemetry.Ui` builds it and packages
the output, which the API host serves (see [the UI package](../Keryhe.Telemetry.Ui/README.md)).

## Development

```bash
npm install
npm start      # http://localhost:4201, proxies /api to https://localhost:7105
```

Run the API host beside it with the API at `/api`:

```bash
dotnet run --project src/Keryhe.Telemetry.Api.Server -- --Telemetry:Api:BasePath=/api
```

## Build

`npm run build` writes `dist/telemetry-client/browser`; publishing `Keryhe.Telemetry.Api.Server` runs it for you. `npm test` runs
Karma/Jasmine (no meaningful tests yet).
