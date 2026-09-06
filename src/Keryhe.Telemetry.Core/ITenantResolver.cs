using System;

namespace Keryhe.Telemetry.Core;

public interface ITenantResolver
{
    Task<long> ResolveTenantIdAsync(string keyHash, CancellationToken cancellationToken);
}
