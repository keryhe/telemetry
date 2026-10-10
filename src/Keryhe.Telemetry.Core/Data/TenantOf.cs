using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>The tenant a queued record belongs to: its resolved resource's tenant (the services always set it), else the default.</summary>
public static class TenantOf
{
    public static long Of(object record) => record switch
    {
        SpanModel s => s.Resource?.TenantId ?? ResourceModel.DefaultTenantId,
        LogRecordModel l => l.Resource?.TenantId ?? ResourceModel.DefaultTenantId,
        MetricModel m => m.Resource?.TenantId ?? ResourceModel.DefaultTenantId,
        _ => ResourceModel.DefaultTenantId
    };
}
