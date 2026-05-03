using Keryhe.Telemetry.Client.Services;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class TenantPicker : ComponentBase, IDisposable
{
    private const string StorageKey = "state.selectedTenantId";

    [Inject] private ITenantCatalogService TenantCatalogService { get; set; } = null!;
    [Inject] private TenantContext TenantContext { get; set; } = null!;
    [Inject] private LocalStorageService LocalStorage { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;

    private List<TenantOption> _tenantOptions = new();
    private long _selectedTenantId;
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        TenantContext.OnChange += OnTenantContextChanged;

        _tenantOptions = (await TenantCatalogService.GetTenantsAsync()).ToList();
        _selectedTenantId = TenantContext.CurrentTenantId;

        if (_tenantOptions.Count > 0 && _tenantOptions.All(option => option.Id != _selectedTenantId))
        {
            var fallbackTenantId = _tenantOptions[0].Id;
            TenantContext.SetTenantId(fallbackTenantId);
            _selectedTenantId = fallbackTenantId;
            await LocalStorage.SetItemAsync(StorageKey, fallbackTenantId);
        }

        _loading = false;
    }

    private async Task OnTenantChangedAsync(long tenantId)
    {
        if (tenantId == TenantContext.CurrentTenantId)
        {
            _selectedTenantId = tenantId;
            return;
        }

        TenantContext.SetTenantId(tenantId);
        _selectedTenantId = tenantId;
        await LocalStorage.SetItemAsync(StorageKey, tenantId);
        NavigationManager.Refresh();
    }

    private void OnTenantContextChanged()
    {
        _selectedTenantId = TenantContext.CurrentTenantId;
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        TenantContext.OnChange -= OnTenantContextChanged;
    }
}