namespace ShowcaseTownForecast;

public static class ShowcaseTownWeatherScoring
{
    public static int CalculateWeatherSuitabilityScore(
        double averageTemperatureC,
        double totalPrecipitationMm,
        double maximumWindSpeedMps)
    {
        var score = 100;

        score -= (int)Math.Clamp(totalPrecipitationMm * 6, 0, 60);

        var temperaturePenalty = averageTemperatureC < 8
            ? (8 - averageTemperatureC) * 2.5
            : averageTemperatureC > 22
                ? (averageTemperatureC - 22) * 2.0
                : 0;

        score -= (int)Math.Clamp(temperaturePenalty, 0, 25);

        var windPenalty = maximumWindSpeedMps <= 8
            ? 0
            : (maximumWindSpeedMps - 8) * 3;

        score -= (int)Math.Clamp(windPenalty, 0, 25);

        return Math.Clamp(score, 0, 100);
    }
}
