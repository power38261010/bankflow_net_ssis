# BankflowWeb

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 21.2.26.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

# BankFlow Web

Angular 21 standalone con TypeScript, signals, Angular Material/CDK, formularios reactivos y Playwright. Requiere Node 22.12+.

El operador puede crear clientes, crear cuentas para ellos y asignar las cuentas antiguas. El cliente ve Mis cuentas, historial y transferencias desde sus cuentas, sin operaciones de caja. Los permisos se validan siempre en el backend. Las transferencias tienen revision, comprobante JSON y recuperacion de peticiones inciertas con la misma clave tras recarga o nuevo login.

```bash
npm ci
npm start -- --host 127.0.0.1 --port 4200
npm run build
```

La API debe escuchar en `http://127.0.0.1:5080`; el proxy convierte `/api/*` en las rutas del backend. No se usa CORS ni almacenamiento de tokens en el navegador.

`npm test` ejecuta Playwright con el servidor ya arrancado. Configura E2E_EMAIL/E2E_PASSWORD para el flujo bancario y una base de pruebas: crea cuentas reales de prueba.

Consulta [la documentacion principal](../README.md) para Identity, bootstrap de usuarios, Docker, pruebas y limites de produccion.

Seguridad de acceso permite activar TOTP de forma voluntaria, con QR generado localmente. El login pide el segundo factor si esta activo. Guarda los codigos de recuperacion; no se persisten en el almacenamiento web. Los E2E MFA utilizan usuarios de pruebas y no activan MFA en el operador de bootstrap.

Pagos gestiona beneficiarios privados y mandatos unicos, semanales o mensuales. Fecha de entrada local, calendario recurrente UTC. No reserva saldo; el cobro depende del worker y se refleja tras actualizar la agenda. Una programacion con respuesta incierta se conserva por usuario en sessionStorage para recuperar el mismo mandato, no crear otro.

Las transferencias emiten avisos en vivo por SignalR. El contador, el tipo e importe se actualizan desde una bandeja persistente; al reconectar o recargar, la bandeja HTTP recupera eventos aunque se haya perdido WebSocket. Se muestra solo el sufijo de cuatro digitos de la cuenta contraparte. El proxy local reenvia `/hubs/**` con WebSocket habilitado y Nginx configura `Upgrade`/`Connection`.
