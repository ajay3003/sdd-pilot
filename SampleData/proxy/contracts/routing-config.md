# Contract: YARP Routing Configuration Schema

Defines the configuration schema for routes and clusters. This is the mechanism by which
backend services are registered with the proxy.

---

## Configuration Section Layout

```json
{
  "ReverseProxy": {
    "Routes": { ... },
    "Clusters": { ... }
  },
  "AzureAd": { ... },
  "ApplicationInsights": { ... },
  "Resilience": { ... }
}
```

---

## Routes

Each entry under `ReverseProxy.Routes` defines one route.

```json
"ReverseProxy": {
  "Routes": {
    "{routeId}": {
      "ClusterId": "{clusterId}",
      "AuthorizationPolicy": "Default",
      "Match": {
        "Path": "/api/{service}/{**catch-all}"
      },
      "Transforms": [
        { "PathPattern": "/api/{service}/{**catch-all}" }
      ],
      "Metadata": {
        "TimeoutSeconds": "30"
      }
    }
  }
}
```

**Naming convention**: `{routeId}` = `{service-name}-route`, e.g. `person-api-route`.

**Path convention**: All proxied paths MUST begin with `/api/`.

---

## Clusters

Each entry under `ReverseProxy.Clusters` defines one backend cluster.

```json
"ReverseProxy": {
  "Clusters": {
    "{clusterId}": {
      "LoadBalancingPolicy": "RoundRobin",
      "HealthCheck": {
        "Passive": {
          "Enabled": true,
          "ReactivationPeriod": "00:00:30"
        }
      },
      "Destinations": {
        "{destinationId}": {
          "Address": "https://{backend-fqdn}/"
        }
      }
    }
  }
}
```

**Naming convention**: `{clusterId}` = `{service-name}`, e.g. `person-api`.

---

## Resilience Section

```json
"Resilience": {
  "DefaultTimeoutSeconds": 30,
  "RetryCount": 3,
  "RetryBaseDelaySeconds": 1.0,
  "CircuitBreakerFailureRatio": 0.5,
  "CircuitBreakerSamplingSeconds": 30,
  "CircuitBreakerBreakDurationSeconds": 30
}
```

---

## EntraID Section

```json
"AzureAd": {
  "Instance": "https://login.microsoftonline.com/",
  "TenantId": "{tenant-id}",
  "ClientId": "{client-id}",
  "Audience": "api://{client-id}"
}
```

In production, `TenantId`, `ClientId`, and `Audience` are loaded from Azure App Configuration
or Key Vault references — never hardcoded.

---

## Rules

1. Every route MUST reference a cluster that exists under `Clusters`.
2. Every cluster MUST have at least one destination.
3. All destination addresses MUST use `https://`.
4. In production environments, destination addresses MUST be private VNet FQDNs.
5. The `AuthorizationPolicy` field on each route defaults to `"Default"` (requires a valid
   EntraID bearer token). Omitting it is equivalent to `"Default"`.
6. A route MUST NOT set `AuthorizationPolicy` to `"Anonymous"` — there are no unauthenticated
   routes except `/healthz`, which is excluded from the YARP pipeline entirely.
