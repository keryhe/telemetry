# Generates a random API key and computes its SHA-256 hash for storage in the api_keys table.
#
# Usage:
#   .\New-ApiKey.ps1
#   .\New-ApiKey.ps1 -KeyLength 48

param(
    [int]$KeyLength = 32
)

# Generate a cryptographically random key and base64url-encode it
$bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($KeyLength)
$apiKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

# Compute SHA-256 hash (lowercase hex) — matches ComputeApiKeyHash in ApiKeyTenantResolver
$sha256 = [System.Security.Cryptography.SHA256]::Create()
$hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($apiKey))
$sha256.Dispose()
$keyHash = [BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()

Write-Host ""
Write-Host "API Key  : $apiKey"
Write-Host "Key Hash : $keyHash"
Write-Host ""
Write-Host "Store the API key securely — it cannot be recovered from the hash."
Write-Host "Insert the hash into the database:"
Write-Host ""
Write-Host "  INSERT INTO api_keys (tenant_id, key_hash, name, is_active)"
Write-Host "  VALUES (<tenant_id>, '$keyHash', '<key_name>', TRUE);"
Write-Host ""
