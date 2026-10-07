# Guía de implementación — BH-Mapas

Indicaciones paso a paso para construir desde cero la solución **BH-Mapas**: rastreo vehicular en tiempo real con una app híbrida (.NET MAUI Blazor Hybrid), una Web API con SignalR y un cliente Blazor WebAssembly. El objetivo final: la app móvil transmite la posición GPS del vehículo y el navegador la ve moverse en vivo sobre un mapa Leaflet.

Para la vista general de la arquitectura ya construida, ver el [diagrama de arquitectura](https://claude.ai/artifact/8VmumKGkaGCwAGVwmb1x1u) y el `README.md` del repo (instrucciones de ejecución). Este documento se enfoca en **cómo se construye**, paso a paso.

## Requisitos previos

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Workload de MAUI: `dotnet workload install maui`
- Para compilar/ejecutar en Android: Android SDK + un emulador o dispositivo físico
- Para compilar/ejecutar en Windows: Windows 10 build 17763 o superior
- Conexión a internet (Leaflet y los tiles de OpenStreetMap se cargan desde CDN, no hay assets locales que mantener)

## Paso 1 — Crear la solución y los cuatro proyectos

```powershell
dotnet new sln -n BH-Mapas

# Librería compartida: DTOs que viajan entre las tres apps
dotnet new classlib -n BH-Mapas-Shared -f net10.0

# Web API con SignalR
dotnet new webapi -n BH-Mapas.API -f net10.0

# Blazor WebAssembly (el "monitor" que se ve en el navegador)
dotnet new blazorwasm -n BH-Mapas.Web -f net10.0

# .NET MAUI Blazor Hybrid (la app que lleva el vehículo)
dotnet new maui-blazor -n BH-Mapas.Client

dotnet sln add BH-Mapas-Shared BH-Mapas.API BH-Mapas.Web BH-Mapas.Client
```

En `BH-Mapas.Client.csproj`, fijar los target frameworks a `net10.0` (el template puede generar una versión distinta según el SDK instalado) y mantener solo las plataformas que se van a usar — en este proyecto: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` y, en Windows, `net10.0-windows10.0.19041.0`.

Las tres apps necesitan el DTO compartido:

```powershell
dotnet add BH-Mapas.API reference BH-Mapas-Shared
dotnet add BH-Mapas.Web reference BH-Mapas-Shared
dotnet add BH-Mapas.Client reference BH-Mapas-Shared
```

## Paso 2 — Contrato compartido (`BH-Mapas-Shared`)

Una única fuente de verdad para la posición de un vehículo, usada tal cual por el emisor, el hub y el receptor:

```csharp
// BH-Mapas-Shared/DTOs/UbicacionVehiculoDto.cs
namespace BH_Mapas_Shared.DTOs;

public record UbicacionVehiculoDto(
    string VehiculoId,
    double Latitud,
    double Longitud,
    double? VelocidadKmh,
    DateTime FechaHoraUtc
);
```

Al ser un `record` en una librería `net10.0` sin dependencias, lo puede referenciar tanto la API (servidor) como el WASM y el MAUI (cliente) sin ningún ajuste adicional.

## Paso 3 — API con el hub de SignalR (`BH-Mapas.API`)

1. Agregar los paquetes necesarios:

   ```powershell
   dotnet add BH-Mapas.API package Microsoft.AspNetCore.OpenApi
   ```

   (`SignalR` ya viene incluido en el SDK `Microsoft.NET.Sdk.Web`, no requiere paquete aparte).

2. Crear el hub. Su única responsabilidad es recibir una posición y reenviarla a todos los demás clientes conectados:

   ```csharp
   // BH-Mapas.API/Hubs/UbicacionHub.cs
   using BH_Mapas_Shared.DTOs;
   using Microsoft.AspNetCore.SignalR;

   namespace BH_Mapas.API.Hubs;

   public class UbicacionHub : Hub
   {
       // Método invocado por el emisor (Móvil / Hybrid)
       public async Task EnviarUbicacionVehiculo(UbicacionVehiculoDto ubicacion)
       {
           // Retransmite a todos los clientes conectados (WASM y otros navegadores)
           await Clients.Others.SendAsync("RecibirUbicacionVehiculo", ubicacion);
       }
   }
   ```

3. Registrar SignalR, CORS y mapear el hub en `Program.cs`:

   ```csharp
   using BH_Mapas.API.Hubs;

   var builder = WebApplication.CreateBuilder(args);

   builder.Services.AddControllers();
   builder.Services.AddOpenApi();
   builder.Services.AddSignalR();

   builder.Services.AddCors(options =>
   {
       options.AddDefaultPolicy(policy =>
       {
           // SignalR negocia con credenciales, incompatible con AllowAnyOrigin
           policy.SetIsOriginAllowed(_ => true)
               .AllowAnyHeader()
               .AllowAnyMethod()
               .AllowCredentials();
       });
   });

   var app = builder.Build();

   if (app.Environment.IsDevelopment())
   {
       app.MapOpenApi();
   }

   app.UseHttpsRedirection();
   app.UseCors();
   app.UseAuthorization();

   app.MapHub<UbicacionHub>("/hubs/ubicacion");
   app.MapControllers();

   app.Run();
   ```

   > **Por qué `SetIsOriginAllowed(_ => true)` y no `AllowAnyOrigin()`**: el cliente de SignalR negocia la conexión enviando credenciales, y la especificación CORS prohíbe combinar `AllowCredentials()` con `AllowAnyOrigin()`. `SetIsOriginAllowed` permite reflejar cualquier origen sin chocar con esa restricción. Es válido para desarrollo; en producción conviene restringirlo con `WithOrigins(...)`.

4. En `Properties/launchSettings.json`, confirmar que el perfil `http` expone `http://localhost:5101` — es el que se usa siempre en desarrollo, porque el perfil `https` redirige (`UseHttpsRedirection`) y eso rompe la negociación inicial de SignalR desde los clientes que apuntan a `http://`.

## Paso 4 — Interoperabilidad JS con Leaflet (común a Web y Client)

Tanto `BH-Mapas.Web` como `BH-Mapas.Client` necesitan dibujar un mapa y mover un marcador. En vez de envolver Leaflet en un paquete .NET, se expone un puente JS mínimo de dos funciones, cargado directamente en el `index.html` de cada proyecto (sin build step, sin dependencias npm):

```html
<!-- CSS y JS de Leaflet desde CDN, antes del script propio -->
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" crossorigin="" />
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js" crossorigin=""></script>

<script>
    let mapaVehiculo = null;
    let marcadorVehiculo = null;

    window.inicializarMapa = function (elementId, latInicial, lngInicial, zoom) {
        if (mapaVehiculo) mapaVehiculo.remove();

        mapaVehiculo = L.map(elementId).setView([latInicial, lngInicial], zoom);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '© OpenStreetMap contributors'
        }).addTo(mapaVehiculo);

        marcadorVehiculo = L.marker([latInicial, lngInicial]).addTo(mapaVehiculo)
            .bindPopup("<b>Vehículo en línea</b>")
            .openPopup();
    };

    window.actualizarPosicionVehiculo = function (lat, lng, infoTexto) {
        if (!mapaVehiculo || !marcadorVehiculo) return;

        const nuevaPos = [lat, lng];
        marcadorVehiculo.setLatLng(nuevaPos);
        marcadorVehiculo.setPopupContent(infoTexto);
        mapaVehiculo.panTo(nuevaPos, { animate: true, duration: 1.0 });
    };
</script>
```

Agregar también, en el `<head>`, un contenedor con tamaño fijo para que Leaflet tenga dónde dibujar:

```html
<style>
    .mapa-contenedor { height: 480px; width: 100%; border-radius: 8px; }
</style>
```

Este bloque se repite igual en `BH-Mapas.Web/wwwroot/index.html` y `BH-Mapas.Client/wwwroot/index.html`; es la misma pieza de JS, copiada en los dos `index.html` porque cada app Blazor carga su propio host HTML.

## Paso 5 — Cliente web (`BH-Mapas.Web`)

1. Agregar el cliente de SignalR:

   ```powershell
   dotnet add BH-Mapas.Web package Microsoft.AspNetCore.SignalR.Client
   ```

2. Crear la página que se suscribe al hub y pinta cada posición recibida:

   ```razor
   @* BH-Mapas.Web/Pages/MonitoreoVehiculo.razor *@
   @page "/monitoreo"
   @inject IJSRuntime JS
   @implements IAsyncDisposable
   @using BH_Mapas_Shared.DTOs
   @using Microsoft.AspNetCore.SignalR.Client

   <h3>Rastreo Vehicular en Vivo (Blazor WASM)</h3>
   <div id="mapaContenedorWasm" class="mapa-contenedor"></div>

   @code {
       private HubConnection? hubConnection;

       protected override async Task OnAfterRenderAsync(bool firstRender)
       {
           if (!firstRender) return;

           await JS.InvokeVoidAsync("inicializarMapa", "mapaContenedorWasm", -36.6167, -64.2833, 14);

           hubConnection = new HubConnectionBuilder()
               .WithUrl("http://localhost:5101/hubs/ubicacion") // URL base de la Web API
               .WithAutomaticReconnect()
               .Build();

           hubConnection.On<UbicacionVehiculoDto>("RecibirUbicacionVehiculo", async (datos) =>
           {
               var popupHtml = $"<b>Vehículo:</b> {datos.VehiculoId}<br/><b>Velocidad:</b> {datos.VelocidadKmh:F1} km/h";
               await JS.InvokeVoidAsync("actualizarPosicionVehiculo", datos.Latitud, datos.Longitud, popupHtml);
               StateHasChanged();
           });

           await hubConnection.StartAsync();
       }

       public async ValueTask DisposeAsync()
       {
           if (hubConnection != null) await hubConnection.DisposeAsync();
       }
   }
   ```

   El punto clave: `hubConnection.On<UbicacionVehiculoDto>("RecibirUbicacionVehiculo", ...)` debe registrarse **antes** de `StartAsync()`, o se pueden perder los primeros mensajes.

## Paso 6 — App híbrida (`BH-Mapas.Client`)

1. Paquetes necesarios:

   ```powershell
   dotnet add BH-Mapas.Client package Microsoft.AspNetCore.SignalR.Client
   ```

   (`Microsoft.Maui.Controls` y `Microsoft.AspNetCore.Components.WebView.Maui` ya vienen del template `maui-blazor`; `Geolocation` es parte de `Microsoft.Maui.Essentials`, incluido en MAUI).

2. Habilitar el sensor GPS en Android, agregando los permisos en `Platforms/Android/AndroidManifest.xml`:

   ```xml
   <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
   <uses-permission android:name="android.permission.INTERNET" />
   <uses-permission android:name="android.permission.ACCESS_COARSE_LOCATION" />
   <uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
   <uses-feature android:name="android.hardware.location.gps" android:required="true" />
   ```

   Sin `INTERNET` el `HubConnection` no puede conectarse; sin los permisos de ubicación, `Geolocation.Default.GetLocationAsync` falla en tiempo de ejecución aunque el manifiesto declare la *feature* de GPS.

3. Crear la página transmisora. A diferencia del receptor Web, esta además lee el sensor nativo y actualiza su propio mapa local:

   ```razor
   @* BH-Mapas.Client/Components/Pages/Gps.razor *@
   @page "/gps"
   @inject IJSRuntime JS
   @implements IAsyncDisposable
   @using Microsoft.AspNetCore.SignalR.Client
   @using System.Threading
   @using BH_Mapas_Shared.DTOs

   <h3>Transmisor GPS y Visualización Móvil (Blazor Hybrid)</h3>
   <button @onclick="ToggleTransmision">@(transmitiendo ? "Detener GPS" : "Iniciar Transmisión GPS")</button>
   <div id="mapaContenedorHybrid" class="mapa-contenedor"></div>

   @code {
       private HubConnection? hubConnection;
       private CancellationTokenSource? cts;
       private bool transmitiendo = false;
       private const string VehiculoId = "Kardian-01";

       protected override async Task OnAfterRenderAsync(bool firstRender)
       {
           if (!firstRender) return;

           await JS.InvokeVoidAsync("inicializarMapa", "mapaContenedorHybrid", -36.6167, -64.2833, 14);

   #if ANDROID
           // 10.0.2.2 es localhost de la PC host en el emulador Android
           const string hubUrl = "http://10.0.2.2:5101/hubs/ubicacion";
   #else
           const string hubUrl = "http://localhost:5101/hubs/ubicacion";
   #endif

           hubConnection = new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build();
           await hubConnection.StartAsync();
       }

       private async Task ToggleTransmision()
       {
           if (transmitiendo)
           {
               cts?.Cancel();
               transmitiendo = false;
           }
           else
           {
               cts = new CancellationTokenSource();
               transmitiendo = true;
               _ = IniciarCicloGpsAsync(cts.Token);
           }
       }

       private async Task IniciarCicloGpsAsync(CancellationToken token)
       {
           while (!token.IsCancellationRequested)
           {
               var request = new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(5));
               var location = await Geolocation.Default.GetLocationAsync(request, token);

               if (location != null)
               {
                   var dto = new UbicacionVehiculoDto(
                       VehiculoId, location.Latitude, location.Longitude,
                       (location.Speed ?? 0) * 3.6, DateTime.UtcNow);

                   if (hubConnection?.State == HubConnectionState.Connected)
                       await hubConnection.SendAsync("EnviarUbicacionVehiculo", dto, cancellationToken: token);

                   var popupHtml = $"<b>Mi Ubicación:</b> {location.Latitude:F5}, {location.Longitude:F5}";
                   await JS.InvokeVoidAsync("actualizarPosicionVehiculo", location.Latitude, location.Longitude, popupHtml);
               }

               await Task.Delay(3000, token);
           }
       }

       public async ValueTask DisposeAsync()
       {
           cts?.Cancel();
           if (hubConnection != null) await hubConnection.DisposeAsync();
       }
   }
   ```

   Puntos a no pasar por alto:
   - El `#if ANDROID` es imprescindible: en el emulador Android, `localhost` apunta al propio emulador, no a la PC host; hay que usar `10.0.2.2`.
   - `(location.Speed ?? 0) * 3.6` convierte de m/s (lo que entrega `Geolocation`) a km/h.
   - El ciclo corre en un `Task.Delay(3000, token)`: cada 3 segundos se pide una nueva posición y se emite; el `CancellationTokenSource` es lo que permite detener la transmisión desde el botón sin bloquear la UI.

## Paso 7 — Ejecutar y probar de punta a punta

Siempre levantar la API primero:

```powershell
# 1. API  (http://localhost:5101)
dotnet run --project BH-Mapas.API --launch-profile http

# 2. Cliente web (http://localhost:5224) -> abrir /monitoreo
dotnet run --project BH-Mapas.Web --launch-profile http

# 3. App MAUI en Windows -> abrir la pestaña GPS (/gps)
dotnet build BH-Mapas.Client -f net10.0-windows10.0.19041.0 -t:Run
```

En la app MAUI, presionar **Iniciar Transmisión GPS**. Si todo está bien conectado, la posición debería aparecer moviéndose en `/monitoreo` dentro de los 3 segundos siguientes, y también en el mapa local de la propia app MAUI.

Si no se ve nada, revisar en este orden:

1. ¿La API está corriendo en el puerto `5101` con el perfil `http` (no `https`)?
2. ¿El navegador en `/monitoreo` muestra el badge "Conectado al Servidor SignalR"? Si no, revisar la consola del navegador por errores de CORS.
3. En Android, ¿se usó `10.0.2.2` y no `localhost`? ¿Se otorgaron los permisos de ubicación al abrir la app por primera vez?
4. ¿El emisor realmente invoca el método (`EnviarUbicacionVehiculo`) con el mismo nombre que espera el hub? Los nombres de métodos de SignalR son *strings*: un error de tipeo no da error de compilación, simplemente nunca llega nada.

## Siguientes pasos (fuera del alcance de este prototipo)

- Restringir CORS a orígenes concretos con `WithOrigins(...)` antes de desplegar a producción.
- Reemplazar las URLs del hub fijas en el código por configuración (`appsettings.json` / variables de entorno), para no tener que recompilar al cambiar de servidor.
- Agregar autenticación al hub (`[Authorize]`) si va a haber más de un vehículo o un operador por tenant.
- Persistir el historial de posiciones (hoy el hub solo retransmite; no guarda nada) si se necesita reconstruir recorridos.
