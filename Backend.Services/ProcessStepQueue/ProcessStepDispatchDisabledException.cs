namespace Backend.Services.ProcessStepQueue;

public sealed class ProcessStepDispatchDisabledException : InvalidOperationException
{
    public ProcessStepDispatchDisabledException()
        : base("Durable process-step dispatch is disabled. Process execution cannot safely create pending steps unless a named test dispatcher is used explicitly.")
    {
    }
}
