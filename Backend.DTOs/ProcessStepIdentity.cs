using System.Security.Cryptography;
using System.Text;

namespace Backend.Models;

public static class ProcessStepIdentity
{
    public static Guid Create(Guid processId, Guid blueprintNodeId, string? iterationKey = null)
    {
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(iterationKey)
            ? $"{processId:N}:{blueprintNodeId:N}"
            : $"{processId:N}:{blueprintNodeId:N}:{iterationKey}");
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
