using System.Security.Cryptography;
using System.Text;

namespace Backend.Models;

public static class ProcessStepIdentity
{
    public static Guid Create(Guid processId, Guid blueprintNodeId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{processId:N}:{blueprintNodeId:N}");
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
