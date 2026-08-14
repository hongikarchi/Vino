using Vino.Contracts;

namespace Vino.Core;

public static class OperationSemantics
{
    public static bool IsWrite(OperationKind kind) => kind is not (
        OperationKind.Read or OperationKind.ReadRuntimeMessages);
}
