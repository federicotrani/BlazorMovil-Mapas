namespace BH_Mapas_Shared.DTOs;

public record UbicacionVehiculoDto(
    string VehiculoId,
    double Latitud,
    double Longitud,
    double? VelocidadKmh,
    DateTime FechaHoraUtc
);