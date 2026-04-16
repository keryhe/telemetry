using System.Text.Json;
using Microsoft.JSInterop;

namespace Keryhe.Telemetry.Client.Services;

public class LocalStorageService(IJSRuntime js)
{
    public async Task<T?> GetItemAsync<T>(string key)
    {
        var json = await js.InvokeAsync<string?>("localStorageGetItem", key);
        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public async Task SetItemAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await js.InvokeVoidAsync("localStorageSetItem", key, json);
    }
}
