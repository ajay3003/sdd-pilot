using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Provider adapters onto the provider-neutral infrastructure categories. The core model knows only categories; Azure, AWS, Google Cloud and
/// Kubernetes enrich it here by resource-type pattern. An unknown provider is recorded generically (category Other) and reported, never guessed.
/// </summary>
internal static class InfrastructureCatalog
{
    private sealed record Rule(string Prefix, InfrastructureCategory Category, string Detail);

    // Longest matching prefix wins, so "azurerm_servicebus_topic" beats "azurerm_servicebus".
    private static readonly Rule[] TerraformRules =
    [
        // Azure
        new("azurerm_resource_group", InfrastructureCategory.ResourceContainer, "Resource group"),
        new("azurerm_linux_web_app", InfrastructureCategory.Compute, "App Service (Linux)"), new("azurerm_windows_web_app", InfrastructureCategory.Compute, "App Service (Windows)"),
        new("azurerm_app_service", InfrastructureCategory.Compute, "App Service"), new("azurerm_service_plan", InfrastructureCategory.Compute, "App Service plan"),
        new("azurerm_linux_function_app", InfrastructureCategory.Compute, "Function App"), new("azurerm_windows_function_app", InfrastructureCategory.Compute, "Function App"),
        new("azurerm_function_app", InfrastructureCategory.Compute, "Function App"), new("azurerm_container_app_environment", InfrastructureCategory.Compute, "Container Apps environment"),
        new("azurerm_container_app", InfrastructureCategory.Compute, "Container App"), new("azurerm_container_app_job", InfrastructureCategory.Compute, "Container Apps job"),
        new("azurerm_kubernetes_cluster", InfrastructureCategory.Compute, "AKS cluster"), new("azurerm_container_group", InfrastructureCategory.Compute, "Container instance"),
        new("azurerm_linux_virtual_machine", InfrastructureCategory.Compute, "Virtual machine"), new("azurerm_windows_virtual_machine", InfrastructureCategory.Compute, "Virtual machine"),
        new("azurerm_static_web_app", InfrastructureCategory.Compute, "Static Web App"),
        new("azurerm_postgresql_flexible_server_database", InfrastructureCategory.Database, "PostgreSQL database"), new("azurerm_postgresql_flexible_server", InfrastructureCategory.Database, "PostgreSQL flexible server"),
        new("azurerm_postgresql", InfrastructureCategory.Database, "PostgreSQL"), new("azurerm_mssql_database", InfrastructureCategory.Database, "SQL database"),
        new("azurerm_mssql_server", InfrastructureCategory.Database, "SQL server"), new("azurerm_sql_", InfrastructureCategory.Database, "SQL"),
        new("azurerm_mysql", InfrastructureCategory.Database, "MySQL"), new("azurerm_cosmosdb", InfrastructureCategory.Database, "Cosmos DB"),
        new("azurerm_eventhub_namespace", InfrastructureCategory.Messaging, "Event Hubs namespace"), new("azurerm_eventhub_consumer_group", InfrastructureCategory.Messaging, "Event Hub consumer group"),
        new("azurerm_eventhub", InfrastructureCategory.Messaging, "Event Hub"),
        new("azurerm_servicebus_namespace", InfrastructureCategory.Messaging, "Service Bus namespace"), new("azurerm_servicebus_topic", InfrastructureCategory.Messaging, "Service Bus topic"),
        new("azurerm_servicebus_queue", InfrastructureCategory.Messaging, "Service Bus queue"), new("azurerm_servicebus_subscription", InfrastructureCategory.Messaging, "Service Bus subscription"),
        new("azurerm_eventgrid", InfrastructureCategory.Messaging, "Event Grid"),
        new("azurerm_storage_account", InfrastructureCategory.Storage, "Storage account"), new("azurerm_storage_container", InfrastructureCategory.Storage, "Blob container"),
        new("azurerm_storage_queue", InfrastructureCategory.Storage, "Storage queue"), new("azurerm_storage_share", InfrastructureCategory.Storage, "File share"), new("azurerm_storage_", InfrastructureCategory.Storage, "Storage"),
        new("azurerm_redis_cache", InfrastructureCategory.Cache, "Azure Cache for Redis"), new("azurerm_redis", InfrastructureCategory.Cache, "Redis"),
        new("azurerm_key_vault_access_policy", InfrastructureCategory.AccessControl, "Key Vault access policy"), new("azurerm_key_vault", InfrastructureCategory.SecretStore, "Key Vault"),
        new("azurerm_user_assigned_identity", InfrastructureCategory.Identity, "User-assigned managed identity"), new("azurerm_federated_identity_credential", InfrastructureCategory.Identity, "Federated identity credential"),
        new("azurerm_role_assignment", InfrastructureCategory.AccessControl, "Role assignment"), new("azurerm_role_definition", InfrastructureCategory.AccessControl, "Role definition"),
        new("azurerm_application_insights", InfrastructureCategory.Observability, "Application Insights"), new("azurerm_log_analytics", InfrastructureCategory.Observability, "Log Analytics"),
        new("azurerm_monitor_diagnostic_setting", InfrastructureCategory.Observability, "Diagnostic setting"), new("azurerm_monitor_", InfrastructureCategory.Observability, "Azure Monitor"),
        new("azurerm_portal_dashboard", InfrastructureCategory.Observability, "Dashboard"),
        new("azurerm_private_endpoint", InfrastructureCategory.Networking, "Private endpoint"), new("azurerm_virtual_network", InfrastructureCategory.Networking, "Virtual network"),
        new("azurerm_subnet", InfrastructureCategory.Networking, "Subnet"), new("azurerm_network_security", InfrastructureCategory.Networking, "Network security group"),
        new("azurerm_public_ip", InfrastructureCategory.Networking, "Public IP"), new("azurerm_firewall", InfrastructureCategory.Networking, "Firewall"), new("azurerm_nat_gateway", InfrastructureCategory.Networking, "NAT gateway"),
        new("azurerm_private_dns", InfrastructureCategory.Dns, "Private DNS"), new("azurerm_dns", InfrastructureCategory.Dns, "DNS"),
        new("azurerm_api_management", InfrastructureCategory.ApiGateway, "API Management"), new("azurerm_application_gateway", InfrastructureCategory.ApiGateway, "Application Gateway"),
        new("azurerm_cdn_frontdoor", InfrastructureCategory.ApiGateway, "Front Door"), new("azurerm_frontdoor", InfrastructureCategory.ApiGateway, "Front Door"),
        new("azurerm_container_registry", InfrastructureCategory.ContainerRegistry, "Container registry"), new("azurerm_app_configuration", InfrastructureCategory.Configuration, "App Configuration"),
        new("azuread_application", InfrastructureCategory.Identity, "App registration"), new("azuread_service_principal", InfrastructureCategory.Identity, "Service principal"),
        new("azuread_application_federated_identity_credential", InfrastructureCategory.Identity, "Federated identity credential"), new("azuread_group", InfrastructureCategory.Identity, "Entra group"),
        // AWS
        new("aws_instance", InfrastructureCategory.Compute, "EC2 instance"), new("aws_ecs_", InfrastructureCategory.Compute, "ECS"), new("aws_lambda_function", InfrastructureCategory.Compute, "Lambda function"),
        new("aws_eks_cluster", InfrastructureCategory.Compute, "EKS cluster"), new("aws_db_instance", InfrastructureCategory.Database, "RDS instance"), new("aws_rds_cluster", InfrastructureCategory.Database, "RDS cluster"),
        new("aws_dynamodb_table", InfrastructureCategory.Database, "DynamoDB table"), new("aws_sqs_queue", InfrastructureCategory.Messaging, "SQS queue"), new("aws_sns_topic", InfrastructureCategory.Messaging, "SNS topic"),
        new("aws_kinesis_stream", InfrastructureCategory.Messaging, "Kinesis stream"), new("aws_msk_cluster", InfrastructureCategory.Messaging, "MSK cluster"),
        new("aws_s3_bucket", InfrastructureCategory.Storage, "S3 bucket"), new("aws_elasticache", InfrastructureCategory.Cache, "ElastiCache"),
        new("aws_secretsmanager_secret", InfrastructureCategory.SecretStore, "Secrets Manager secret"), new("aws_kms_key", InfrastructureCategory.SecretStore, "KMS key"),
        new("aws_iam_role_policy_attachment", InfrastructureCategory.AccessControl, "IAM policy attachment"), new("aws_iam_role_policy", InfrastructureCategory.AccessControl, "IAM role policy"),
        new("aws_iam_policy", InfrastructureCategory.AccessControl, "IAM policy"), new("aws_iam_role", InfrastructureCategory.Identity, "IAM role"), new("aws_iam_user", InfrastructureCategory.Identity, "IAM user"),
        new("aws_cloudwatch_", InfrastructureCategory.Observability, "CloudWatch"), new("aws_vpc_endpoint", InfrastructureCategory.Networking, "VPC endpoint"), new("aws_vpc", InfrastructureCategory.Networking, "VPC"),
        new("aws_subnet", InfrastructureCategory.Networking, "Subnet"), new("aws_security_group", InfrastructureCategory.Networking, "Security group"), new("aws_lb", InfrastructureCategory.Networking, "Load balancer"),
        new("aws_route53_", InfrastructureCategory.Dns, "Route 53"), new("aws_api_gateway", InfrastructureCategory.ApiGateway, "API Gateway"), new("aws_apigatewayv2", InfrastructureCategory.ApiGateway, "API Gateway"),
        new("aws_ecr_repository", InfrastructureCategory.ContainerRegistry, "ECR repository"),
        // Google Cloud
        new("google_compute_instance", InfrastructureCategory.Compute, "Compute instance"), new("google_cloud_run", InfrastructureCategory.Compute, "Cloud Run"),
        new("google_container_cluster", InfrastructureCategory.Compute, "GKE cluster"), new("google_cloudfunctions", InfrastructureCategory.Compute, "Cloud Function"),
        new("google_sql_", InfrastructureCategory.Database, "Cloud SQL"), new("google_spanner", InfrastructureCategory.Database, "Spanner"), new("google_firestore", InfrastructureCategory.Database, "Firestore"),
        new("google_pubsub", InfrastructureCategory.Messaging, "Pub/Sub"), new("google_storage_bucket", InfrastructureCategory.Storage, "Cloud Storage bucket"),
        new("google_redis_instance", InfrastructureCategory.Cache, "Memorystore Redis"), new("google_secret_manager", InfrastructureCategory.SecretStore, "Secret Manager"),
        new("google_service_account", InfrastructureCategory.Identity, "Service account"), new("google_logging_", InfrastructureCategory.Observability, "Cloud Logging"),
        new("google_monitoring_", InfrastructureCategory.Observability, "Cloud Monitoring"), new("google_compute_network", InfrastructureCategory.Networking, "VPC network"),
        new("google_compute_subnetwork", InfrastructureCategory.Networking, "Subnetwork"), new("google_compute_firewall", InfrastructureCategory.Networking, "Firewall rule"),
        new("google_dns_", InfrastructureCategory.Dns, "Cloud DNS"), new("google_artifact_registry", InfrastructureCategory.ContainerRegistry, "Artifact Registry"),
        // Kubernetes / Helm Terraform providers
        new("kubernetes_deployment", InfrastructureCategory.Compute, "Deployment"), new("kubernetes_stateful_set", InfrastructureCategory.Compute, "StatefulSet"),
        new("kubernetes_cron_job", InfrastructureCategory.Compute, "CronJob"), new("kubernetes_job", InfrastructureCategory.Compute, "Job"),
        new("kubernetes_service_account", InfrastructureCategory.Identity, "Service account"), new("kubernetes_service", InfrastructureCategory.Networking, "Service"),
        new("kubernetes_ingress", InfrastructureCategory.Networking, "Ingress"), new("kubernetes_network_policy", InfrastructureCategory.Networking, "NetworkPolicy"),
        new("kubernetes_config_map", InfrastructureCategory.Configuration, "ConfigMap"), new("kubernetes_secret", InfrastructureCategory.SecretStore, "Secret"),
        new("kubernetes_role_binding", InfrastructureCategory.AccessControl, "RoleBinding"), new("kubernetes_cluster_role_binding", InfrastructureCategory.AccessControl, "ClusterRoleBinding"),
        new("kubernetes_namespace", InfrastructureCategory.ResourceContainer, "Namespace"), new("kubernetes_persistent_volume", InfrastructureCategory.Storage, "Persistent volume"),
        new("helm_release", InfrastructureCategory.Compute, "Helm release"),
    ];

    private static readonly HashSet<string> KnownProviders = new(StringComparer.Ordinal) { "azurerm", "azuread", "azapi", "aws", "google", "kubernetes", "helm", "random", "null", "local", "tls", "time", "external", "archive", "http", "template" };
    private static readonly HashSet<string> UtilityProviders = new(StringComparer.Ordinal) { "random", "null", "local", "tls", "time", "external", "archive", "http", "template" };

    public static string Provider(string resourceType) => resourceType.Contains('_') ? resourceType[..resourceType.IndexOf('_')] : resourceType;
    public static bool KnownProvider(string provider) => KnownProviders.Contains(provider);
    public static bool UtilityProvider(string provider) => UtilityProviders.Contains(provider);

    public static (InfrastructureCategory Category, string Detail) Terraform(string resourceType)
    {
        var rule = TerraformRules.Where(r => resourceType.StartsWith(r.Prefix, StringComparison.Ordinal)).MaxBy(r => r.Prefix.Length);
        return rule is null ? (InfrastructureCategory.Other, resourceType) : (rule.Category, rule.Detail);
    }

    private static readonly (string Prefix, InfrastructureCategory Category, string Detail)[] Arm =
    [
        ("Microsoft.Resources/resourceGroups", InfrastructureCategory.ResourceContainer, "Resource group"),
        ("Microsoft.Web/sites", InfrastructureCategory.Compute, "App Service / Function App"), ("Microsoft.Web/serverfarms", InfrastructureCategory.Compute, "App Service plan"),
        ("Microsoft.App/containerApps", InfrastructureCategory.Compute, "Container App"), ("Microsoft.App/managedEnvironments", InfrastructureCategory.Compute, "Container Apps environment"),
        ("Microsoft.ContainerService/managedClusters", InfrastructureCategory.Compute, "AKS cluster"),
        ("Microsoft.DBforPostgreSQL", InfrastructureCategory.Database, "PostgreSQL"), ("Microsoft.Sql", InfrastructureCategory.Database, "SQL"), ("Microsoft.DocumentDB", InfrastructureCategory.Database, "Cosmos DB"),
        ("Microsoft.EventHub/namespaces/eventhubs/consumergroups", InfrastructureCategory.Messaging, "Event Hub consumer group"), ("Microsoft.EventHub/namespaces/eventhubs", InfrastructureCategory.Messaging, "Event Hub"),
        ("Microsoft.EventHub", InfrastructureCategory.Messaging, "Event Hubs namespace"), ("Microsoft.ServiceBus/namespaces/topics/subscriptions", InfrastructureCategory.Messaging, "Service Bus subscription"),
        ("Microsoft.ServiceBus/namespaces/topics", InfrastructureCategory.Messaging, "Service Bus topic"), ("Microsoft.ServiceBus/namespaces/queues", InfrastructureCategory.Messaging, "Service Bus queue"),
        ("Microsoft.ServiceBus", InfrastructureCategory.Messaging, "Service Bus namespace"), ("Microsoft.Storage", InfrastructureCategory.Storage, "Storage"),
        ("Microsoft.Cache", InfrastructureCategory.Cache, "Redis"), ("Microsoft.KeyVault", InfrastructureCategory.SecretStore, "Key Vault"),
        ("Microsoft.ManagedIdentity", InfrastructureCategory.Identity, "Managed identity"), ("Microsoft.Authorization/roleAssignments", InfrastructureCategory.AccessControl, "Role assignment"),
        ("Microsoft.Insights/diagnosticSettings", InfrastructureCategory.Observability, "Diagnostic setting"), ("Microsoft.Insights", InfrastructureCategory.Observability, "Application Insights / Monitor"),
        ("Microsoft.OperationalInsights", InfrastructureCategory.Observability, "Log Analytics"), ("Microsoft.Network/privateEndpoints", InfrastructureCategory.Networking, "Private endpoint"),
        ("Microsoft.Network/privateDnsZones", InfrastructureCategory.Dns, "Private DNS"), ("Microsoft.Network/dnsZones", InfrastructureCategory.Dns, "DNS"),
        ("Microsoft.Network/applicationGateways", InfrastructureCategory.ApiGateway, "Application Gateway"), ("Microsoft.Cdn", InfrastructureCategory.ApiGateway, "Front Door / CDN"),
        ("Microsoft.Network", InfrastructureCategory.Networking, "Network"), ("Microsoft.ApiManagement", InfrastructureCategory.ApiGateway, "API Management"),
        ("Microsoft.ContainerRegistry", InfrastructureCategory.ContainerRegistry, "Container registry"), ("Microsoft.AppConfiguration", InfrastructureCategory.Configuration, "App Configuration"),
    ];

    public static (InfrastructureCategory Category, string Detail) AzureResourceType(string type)
    {
        var rule = Arm.Where(r => type.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Prefix.Length).FirstOrDefault();
        return rule.Prefix is null ? (InfrastructureCategory.Other, type) : (rule.Category, rule.Detail);
    }

    public static (InfrastructureCategory Category, string Detail) KubernetesKind(string kind) => kind switch
    {
        "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "Pod" => (InfrastructureCategory.Compute, kind),
        "Job" or "CronJob" or "HorizontalPodAutoscaler" => (InfrastructureCategory.Compute, kind),
        "Service" or "Ingress" or "NetworkPolicy" or "Gateway" or "HTTPRoute" => (InfrastructureCategory.Networking, kind),
        "ConfigMap" => (InfrastructureCategory.Configuration, kind),
        "Secret" or "SecretProviderClass" or "ExternalSecret" => (InfrastructureCategory.SecretStore, kind),
        "ServiceAccount" => (InfrastructureCategory.Identity, kind),
        "Role" or "ClusterRole" or "RoleBinding" or "ClusterRoleBinding" => (InfrastructureCategory.AccessControl, kind),
        "PersistentVolumeClaim" or "PersistentVolume" or "StorageClass" => (InfrastructureCategory.Storage, kind),
        "Namespace" => (InfrastructureCategory.ResourceContainer, kind),
        "ServiceMonitor" or "PodMonitor" or "PrometheusRule" => (InfrastructureCategory.Observability, kind),
        _ => (InfrastructureCategory.Other, kind),
    };

    // ── Settings worth recording (provider-neutral attribute names) ─────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> SettingAreas = new(StringComparer.Ordinal)
    {
        // Security
        ["minimum_tls_version"] = "Security", ["min_tls_version"] = "Security", ["ssl_minimal_tls_version_enforced"] = "Security", ["tls_version"] = "Security",
        ["https_only"] = "Security", ["public_network_access_enabled"] = "Security", ["public_network_access"] = "Security", ["allow_blob_public_access"] = "Security",
        ["allow_nested_items_to_be_public"] = "Security", ["shared_access_key_enabled"] = "Security", ["local_auth_enabled"] = "Security", ["infrastructure_encryption_enabled"] = "Security",
        ["purge_protection_enabled"] = "Security", ["soft_delete_retention_days"] = "Security", ["enable_rbac_authorization"] = "Security", ["rbac_authorization_enabled"] = "Security",
        ["admin_enabled"] = "Security", ["ftps_state"] = "Security", ["client_certificate_enabled"] = "Security", ["client_certificate_mode"] = "Security",
        ["publicly_accessible"] = "Security", ["storage_encrypted"] = "Security", ["deletion_protection"] = "Security", ["block_public_acls"] = "Security", ["block_public_policy"] = "Security",
        ["ssl_enforcement_enabled"] = "Security", ["kms_key_id"] = "Security", ["sse_algorithm"] = "Security", ["anonymous_pull_enabled"] = "Security", ["auth_settings.enabled"] = "Security",
        ["auth_settings_v2.auth_enabled"] = "Security", ["active_directory_auth_enabled"] = "Security", ["password_auth_enabled"] = "Security", ["authentication.active_directory_auth_enabled"] = "Security",
        ["authentication.password_auth_enabled"] = "Security", ["site_config.minimum_tls_version"] = "Security", ["site_config.ftps_state"] = "Security", ["site_config.http2_enabled"] = "Security",
        ["ingress.external_enabled"] = "Networking", ["ingress.allow_insecure_connections"] = "Security", ["ingress.target_port"] = "Networking", ["min_tls_version_enforced"] = "Security",
        // Networking
        ["subnet_id"] = "Networking", ["virtual_network_subnet_id"] = "Networking", ["ip_rules"] = "Networking", ["ip_range_filter"] = "Networking", ["address_space"] = "Networking",
        ["address_prefixes"] = "Networking", ["network_rules.default_action"] = "Networking", ["network_acls.default_action"] = "Networking", ["network_rule_set.default_action"] = "Networking",
        ["private_dns_zone_id"] = "Networking", ["delegated_subnet_id"] = "Networking", ["private_service_connection.is_manual_connection"] = "Networking",
        ["private_service_connection.subresource_names"] = "Networking", ["private_service_connection.private_connection_resource_id"] = "Networking", ["vnet_route_all_enabled"] = "Networking",
        ["infrastructure_subnet_id"] = "Networking", ["internal_load_balancer_enabled"] = "Networking", ["cidr_blocks"] = "Networking", ["ingress.cidr_blocks"] = "Networking",
        ["egress.cidr_blocks"] = "Networking", ["map_public_ip_on_launch"] = "Networking", ["virtual_network_id"] = "Networking", ["site_config.ip_restriction.action"] = "Networking",
        // Identity
        ["identity.type"] = "Identity", ["identity.identity_ids"] = "Identity", ["principal_id"] = "Identity", ["role_definition_name"] = "Identity", ["role_definition_id"] = "Identity",
        ["scope"] = "Identity", ["issuer"] = "Identity", ["subject"] = "Identity", ["audience"] = "Identity", ["object_id"] = "Identity", ["tenant_id"] = "Identity",
        ["service_account_name"] = "Identity", ["iam_instance_profile"] = "Identity", ["assume_role_policy"] = "Identity",
        // Observability
        ["retention_in_days"] = "Observability", ["sampling_percentage"] = "Observability", ["daily_data_cap_in_gb"] = "Observability", ["workspace_id"] = "Observability",
        ["log_analytics_workspace_id"] = "Observability", ["application_type"] = "Observability", ["target_resource_id"] = "Observability", ["enabled_log.category"] = "Observability",
        ["enabled_log.category_group"] = "Observability", ["log.category"] = "Observability", ["metric.category"] = "Observability", ["application_insights_connection_string"] = "Observability",
        ["site_config.application_insights_connection_string"] = "Observability", ["app_settings.APPLICATIONINSIGHTS_CONNECTION_STRING"] = "Observability",
        // Messaging
        ["partition_count"] = "Messaging", ["message_retention"] = "Messaging", ["max_delivery_count"] = "Messaging", ["dead_lettering_on_message_expiration"] = "Messaging",
        ["requires_session"] = "Messaging", ["enable_partitioning"] = "Messaging", ["partitioning_enabled"] = "Messaging", ["auto_inflate_enabled"] = "Messaging",
        ["max_size_in_megabytes"] = "Messaging", ["default_message_ttl"] = "Messaging", ["lock_duration"] = "Messaging", ["eventhub_name"] = "Messaging", ["topic_id"] = "Messaging",
        ["namespace_id"] = "Messaging", ["namespace_name"] = "Messaging", ["eventhub_id"] = "Messaging", ["storage_account_id"] = "Datastore", ["storage_account_name"] = "Datastore", ["server_id"] = "Datastore", ["visibility_timeout_seconds"] = "Messaging", ["message_retention_seconds"] = "Messaging",
        // Datastore / capacity
        ["location"] = "Configuration", ["sku"] = "Capacity", ["sku_name"] = "Capacity", ["capacity"] = "Capacity", ["version"] = "Datastore", ["storage_mb"] = "Datastore", ["backup_retention_days"] = "Datastore",
        ["geo_redundant_backup_enabled"] = "Datastore", ["high_availability.mode"] = "Datastore", ["zone"] = "Datastore", ["account_tier"] = "Capacity",
        ["account_replication_type"] = "Datastore", ["container_access_type"] = "Security", ["engine"] = "Datastore", ["engine_version"] = "Datastore", ["max_size_gb"] = "Capacity",
        ["backup_retention_period"] = "Datastore", ["multi_az"] = "Datastore",
    };

    public static string? SettingArea(string key) => SettingAreas.TryGetValue(key, out var area) ? area
        : key.StartsWith("cors.", StringComparison.Ordinal) || key.StartsWith("site_config.cors.", StringComparison.Ordinal) ? "Security"
        : key.StartsWith("app_settings.", StringComparison.Ordinal) ? "Configuration"
        : SourceEvidenceRedaction.SensitiveKey(key) ? "Security" : null;
}
