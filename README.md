# BH-Mapas

Rastreo vehicular en tiempo real. Una app móvil/escritorio (.NET MAUI Blazor Hybrid) transmite la posición GPS de un vehículo a una API con SignalR, y un cliente web (Blazor WebAssembly) la muestra en vivo sobre un mapa Leaflet / OpenStreetMap.

```
 MAUI Hybrid (emisor GPS) ──SignalR──▶ API (hub) ──SignalR──▶ Blazor WASM (monitor)
```

## Estructura de la solución

| Proyecto | Descripción |
|---|---|
| `BH-Mapas.API` | ASP.NET Core Web API. Expone el hub SignalR `/hubs/ubicacion`, CORS y OpenAPI. |
| `BH-Mapas-Shared` | Librería compartida con los DTOs (`UbicacionVehiculoDto`). |
| `BH-Mapas.Web` | Blazor WebAssembly. Página `/monitoreo`: recibe y dibuja la ubicación en el mapa. |
| `BH-Mapas.Client` | .NET MAUI Blazor Hybrid (Android, iOS, MacCatalyst, Windows). Página `/tgps`: lee el GPS y lo transmite. |

## Requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Workload de MAUI: `dotnet workload install maui`
- Para Android: Android SDK y un emulador o dispositivo
- Para Windows: Windows 10 1809 (17763) o superior
- Conexión a internet (Leaflet y los tiles de OpenStreetMap se cargan desde CDN)

## Ejecución

Iniciar siempre la API primero, con el perfil **http**:

```powershell
# 1. API  (http://localhost:5101)
dotnet run --project BH-Mapas.API --launch-profile http

# 2. Cliente web  (http://localhost:5224)  -> abrir /monitoreo
dotnet run --project BH-Mapas.Web --launch-profile http

# 3. App MAUI (Windows)  -> abrir la pestaña GPS (/tgps)
dotnet build BH-Mapas.Client -f net10.0-windows10.0.19041.0 -t:Run
```

Con la app MAUI se pulsa **Iniciar Transmisión GPS**; la posición aparece en tiempo real en `/monitoreo`.

> El perfil `https` de la API redirige las peticiones HTTP y rompe la negociación de SignalR de los clientes, por eso se usa `http` en desarrollo.

## Direcciones del hub SignalR

| Cliente | URL |
|---|---|
| Blazor WASM | `http://localhost:5101/hubs/ubicacion` |
| MAUI en Windows | `http://localhost:5101/hubs/ubicacion` |
| MAUI en emulador Android | `http://10.0.2.2:5101/hubs/ubicacion` (`10.0.2.2` es el localhost de la PC) |

Las URLs están fijas en `MonitoreoVehiculo.razor` (Web) y `TransmisorGps.razor` (Client). Para un dispositivo físico o producción hay que cambiarlas por la dirección real del servidor.

## Notas

- **CORS**: la API usa `SetIsOriginAllowed(_ => true)` + `AllowCredentials()` para desarrollo. En producción restringirlo con `WithOrigins(...)`.
- **Android**: el manifiesto habilita `usesCleartextTraffic` para poder usar HTTP en desarrollo; quitarlo al pasar a HTTPS.
- **Estilos con scope (`*.razor.css`)**: el identificador depende del nombre del proyecto. Si se renombra un proyecto, borrar sus carpetas `bin` y `obj` y recompilar.
- **Contrato SignalR**: el emisor invoca `EnviarUbicacionVehiculo(UbicacionVehiculoDto)` y el hub reenvía a los demás clientes el evento `RecibirUbicacionVehiculo`.
