using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess;

/// <summary>
/// Supplies the caller for the current request. Registered by the host, because only the host knows
/// where its request state lives (the gateway reads BlocksContext and its request context).
/// </summary>
public interface IAccessPrincipalAccessor
{
    AccessPrincipal Current { get; }
}
