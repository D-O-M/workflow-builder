namespace Backend.API.Infrastructure;

public sealed class ProcessRecoveryAvailability(IConfiguration configuration, IHostEnvironment environment)
{
    public bool Enabled => configuration.GetValue<bool>("ProcessRecovery:Enabled") &&
        (configuration.GetValue<bool>("Authentication:Enabled") || environment.IsDevelopment());
}
