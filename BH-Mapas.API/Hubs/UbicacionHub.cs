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