#!/usr/bin/env zsh
# Generates a random API key and computes its SHA-256 hash for storage in the api_keys table.
#
# Usage:
#   ./new-api-key.sh
#   ./new-api-key.sh 48

KEY_LENGTH=${1:-32}

# Generate a cryptographically random key and base64url-encode it
API_KEY=$(openssl rand -base64 "$KEY_LENGTH" | tr '+/' '-_' | tr -d '=\n')

# Compute SHA-256 hash (lowercase hex) — matches ComputeApiKeyHash in ApiKeyTenantResolver
KEY_HASH=$(printf '%s' "$API_KEY" | openssl dgst -sha256)

echo ""
echo "API Key  : $API_KEY"
echo "Key Hash : $KEY_HASH"
echo ""
echo "Store the API key securely — it cannot be recovered from the hash."
echo "Insert the hash into the database:"
echo ""
echo "  INSERT INTO api_keys (tenant_id, key_hash, name, is_active)"
echo "  VALUES (<tenant_id>, '$KEY_HASH', '<key_name>', TRUE);"
echo ""
