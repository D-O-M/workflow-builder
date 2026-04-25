using Backend.Plugins.Harness;

namespace DeliveryScenarioNormalizer;

public sealed class DeliveryScenarioNormalizerPlugin : IPlugin
{
    public required DeliveryScenarioInput Input { get; init; }
    public DeliveryScenarioResult Scenario { get; set; } = default!;

    public Task Execute()
    {
        var shipmentLabel = Input.ShipmentLabel.Trim();
        var destinationTown = Input.DestinationTown.Trim();
        var destinationCountry = Input.DestinationCountryCode.Trim().ToUpperInvariant();
        var supplierCountry = Input.SupplierCountryCode.Trim().ToUpperInvariant();
        var receiverCountry = Input.ReceiverCountryCode.Trim().ToUpperInvariant();
        var baseCurrency = Input.BaseCurrencyCode.Trim().ToUpperInvariant();
        var quoteCurrency = Input.QuoteCurrencyCode.Trim().ToUpperInvariant();
        var start = Input.WindowStartIsoDate;
        var end = Input.WindowEndIsoDate;

        if (string.IsNullOrWhiteSpace(shipmentLabel) || string.IsNullOrWhiteSpace(destinationTown))
        {
            throw new InvalidDataException("ShipmentLabel and DestinationTown are required.");
        }

        if (start == default || end == default)
        {
            throw new InvalidDataException("WindowStartIsoDate and WindowEndIsoDate are required.");
        }

        if (end < start)
        {
            throw new InvalidDataException("WindowEndIsoDate must be on or after WindowStartIsoDate.");
        }

        Scenario = new DeliveryScenarioResult
        {
            ShipmentLabel = shipmentLabel,
            DestinationTown = destinationTown,
            DestinationCountryCode = destinationCountry,
            SupplierCountryCode = supplierCountry,
            ReceiverCountryCode = receiverCountry,
            BaseCurrencyCode = baseCurrency,
            QuoteCurrencyCode = quoteCurrency,
            WindowStartIsoDate = start,
            WindowEndIsoDate = end,
            DestinationLabel = $"{shipmentLabel} / {destinationTown}"
        };

        return Task.CompletedTask;
    }
}
