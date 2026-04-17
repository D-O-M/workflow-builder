namespace ShowcaseTownRecommendation;

public static class ShowcaseTownRecommendationScoring
{
    public static int CalculateFinalRecommendationScore(
        int weatherSuitabilityScore,
        double holidayDemandAdjustmentPct)
    {
        var adjustedScore = (int)Math.Round(weatherSuitabilityScore * (1 + holidayDemandAdjustmentPct));
        return Math.Clamp(adjustedScore, 0, 100);
    }

    public static string GetRecommendationBand(int finalRecommendationScore) =>
        finalRecommendationScore switch
        {
            >= 75 => "Go",
            >= 50 => "Maybe",
            _ => "Skip"
        };
}
