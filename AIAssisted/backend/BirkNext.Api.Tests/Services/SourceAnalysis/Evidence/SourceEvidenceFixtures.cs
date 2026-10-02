using System.IO.Compression;
using System.Text;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Services.SourceAnalysis.Observability;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>
/// Generic, non-M2LB fixtures for the source-evidence domains. "Acme.Ordering" is an Azure-hosted API + worker with Terraform (App Service,
/// PostgreSQL, Service Bus, managed identity, private endpoint, App Insights), environment appsettings, an Azure pipeline and an OpenAPI
/// contract. Secrets are planted as SECRET_SENTINEL_* values so tests can prove they never appear in the persisted evidence.
/// </summary>
internal static class SourceEvidenceFixtures
{
    public const string Sentinel = "SECRET_SENTINEL";

    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    public static SourceEvidenceDomainsSnapshot Analyze(params (string Path, string Content)[] files) => Analyze(out _, files);

    public static SourceEvidenceDomainsSnapshot Analyze(out SourceConfigurationModel configuration, params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("fixture.zip", Zip(files));
        error.Should().BeNull();
        var id = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
        var architecture = SourceArchitectureAnalyzer.Analyze(id, workspace!, at, null, default, out var input, out _);
        var observability = ObservabilitySourceAnalyzer.Analyze(id, workspace!, input, architecture, at, default);
        return SourceEvidenceAnalyzer.Analyze(id, workspace!, input, at, architecture, null, observability, null, default, out configuration);
    }

    private const string WebProject = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Azure.Messaging.ServiceBus" Version="7.0.0" /><PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.0" /><PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" Version="1.0.0" /></ItemGroup></Project>""";
    private const string WorkerProject = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Azure.Messaging.ServiceBus" Version="7.0.0" /></ItemGroup></Project>""";
    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="xunit" Version="2.0.0" /></ItemGroup></Project>""";

    private const string WebProgram = """
        using Azure.Messaging.ServiceBus;
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOpenTelemetry().UseAzureMonitor();
        builder.Services.AddSingleton(new ServiceBusClient(builder.Configuration["ServiceBus:Namespace"]));
        builder.Services.AddDbContext<OrderingDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));
        var app = builder.Build();
        app.MapGet("/orders/{id}", (string id) => Results.Ok(id));
        app.Run();
        public sealed class OrderPublisher(ServiceBusClient client)
        {
            private readonly ServiceBusSender _sender = client.CreateSender("orders-created");
        }
        public sealed class OrderingDbContext : DbContext { }
        """;
    private const string WorkerProgram = """
        using Azure.Messaging.ServiceBus;
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddHostedService<OrderWorker>();
        builder.Build().Run();
        public sealed class OrderWorker(ServiceBusClient client) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken ct) { var p = client.CreateProcessor("orders-created", "fulfilment"); return Task.CompletedTask; }
        }
        """;

    public const string MainTf = """
        terraform {
          required_providers {
            azurerm = { source = "hashicorp/azurerm", version = "~> 3.100" }
          }
          backend "azurerm" {
            resource_group_name  = "rg-tfstate"
            storage_account_name = "sttfstate"
            access_key           = "SECRET_SENTINEL_BACKEND_KEY"
          }
        }

        provider "azurerm" {
          features {}
          client_secret = var.client_secret
        }

        variable "prefix" {
          type    = string
          default = "acme"
        }

        variable "client_secret" {
          type      = string
          sensitive = true
          default   = "SECRET_SENTINEL_CLIENT_SECRET"
        }

        variable "db_password" {
          type    = string
          default = "SECRET_SENTINEL_DB_PASSWORD"
        }

        locals {
          location = "norwayeast"
        }

        resource "azurerm_resource_group" "main" {
          name     = "rg-${var.prefix}-ordering"
          location = local.location
          tags = { owner = "team-ordering", cost_center = "1234" }
        }

        resource "azurerm_service_plan" "plan" {
          name                = "asp-${var.prefix}-ordering"
          resource_group_name = azurerm_resource_group.main.name
          location            = azurerm_resource_group.main.location
          os_type             = "Linux"
          sku_name            = "P1v3"
        }

        resource "azurerm_user_assigned_identity" "api" {
          name                = "id-${var.prefix}-ordering-api"
          resource_group_name = azurerm_resource_group.main.name
          location            = azurerm_resource_group.main.location
        }

        resource "azurerm_linux_web_app" "api" {
          name                          = "app-${var.prefix}-ordering-api"
          resource_group_name           = azurerm_resource_group.main.name
          location                      = azurerm_resource_group.main.location
          service_plan_id               = azurerm_service_plan.plan.id
          https_only                    = true
          public_network_access_enabled = false
          identity {
            type         = "UserAssigned"
            identity_ids = [azurerm_user_assigned_identity.api.id]
          }
          site_config {
            minimum_tls_version = "1.2"
            ftps_state          = "Disabled"
          }
          app_settings = {
            "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.main.connection_string
            "ServiceBus__Namespace"                 = "${azurerm_servicebus_namespace.bus.name}.servicebus.windows.net"
          }
        }

        resource "azurerm_postgresql_flexible_server" "db" {
          name                   = "psql-${var.prefix}-ordering"
          resource_group_name    = azurerm_resource_group.main.name
          location               = azurerm_resource_group.main.location
          administrator_login    = "orderadmin"
          administrator_password = var.db_password
          backup_retention_days  = 14
          public_network_access_enabled = false
        }

        resource "azurerm_private_endpoint" "db" {
          name                = "pe-${var.prefix}-ordering-db"
          resource_group_name = azurerm_resource_group.main.name
          location            = azurerm_resource_group.main.location
          subnet_id           = module.network.private_subnet_id
          private_service_connection {
            name                           = "psc-db"
            private_connection_resource_id = azurerm_postgresql_flexible_server.db.id
            is_manual_connection           = false
            subresource_names              = ["postgresqlServer"]
          }
        }

        resource "azurerm_role_assignment" "api_bus_sender" {
          scope                = azurerm_servicebus_namespace.bus.id
          role_definition_name = "Azure Service Bus Data Sender"
          principal_id         = azurerm_user_assigned_identity.api.principal_id
        }

        data "azurerm_client_config" "current" {}

        module "network" {
          source        = "./modules/network"
          prefix        = var.prefix
          resource_group = azurerm_resource_group.main.name
        }

        module "naming" {
          source  = "Azure/naming/azurerm"
          version = "0.4.0"
        }

        output "api_url" {
          value = azurerm_linux_web_app.api.default_hostname
        }

        output "db_admin_password" {
          value     = var.db_password
          sensitive = true
        }
        """;

    public const string MessagingTf = """
        resource "azurerm_servicebus_namespace" "bus" {
          name                = "sb-acme-ordering"
          resource_group_name = azurerm_resource_group.main.name
          location            = azurerm_resource_group.main.location
          sku                 = "Standard"
          local_auth_enabled  = false
        }

        resource "azurerm_servicebus_topic" "orders_created" {
          name         = "orders-created"
          namespace_id = azurerm_servicebus_namespace.bus.id
        }

        resource "azurerm_servicebus_subscription" "fulfilment" {
          name               = "fulfilment"
          topic_id           = azurerm_servicebus_topic.orders_created.id
          max_delivery_count = 10
          dead_lettering_on_message_expiration = true
          depends_on = [azurerm_servicebus_topic.orders_created]
        }
        """;

    public const string MonitoringTf = """
        resource "azurerm_log_analytics_workspace" "main" {
          name                = "log-acme-ordering"
          resource_group_name = azurerm_resource_group.main.name
          location            = azurerm_resource_group.main.location
          retention_in_days   = 30
        }

        resource "azurerm_application_insights" "main" {
          name                = "appi-acme-ordering"
          resource_group_name = azurerm_resource_group.main.name
          location            = azurerm_resource_group.main.location
          workspace_id        = azurerm_log_analytics_workspace.main.id
          application_type    = "web"
        }

        resource "azurerm_monitor_diagnostic_setting" "bus" {
          name                       = "diag-bus"
          target_resource_id         = azurerm_servicebus_namespace.bus.id
          log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
          enabled_log {
            category = "OperationalLogs"
          }
        }
        """;

    public const string NetworkModuleTf = """
        variable "prefix" { type = string }
        variable "resource_group" { type = string }

        resource "azurerm_virtual_network" "vnet" {
          name                = "vnet-${var.prefix}"
          resource_group_name = var.resource_group
          address_space       = ["10.20.0.0/16"]
        }

        resource "azurerm_subnet" "private" {
          name                 = "snet-private"
          virtual_network_name = azurerm_virtual_network.vnet.name
          resource_group_name  = var.resource_group
          address_prefixes     = ["10.20.1.0/24"]
        }

        output "private_subnet_id" { value = azurerm_subnet.private.id }
        """;

    public const string QaTfvars = """
        prefix      = "acme-qa"
        db_password = "SECRET_SENTINEL_QA_DB_PASSWORD"
        """;

    public const string ProdTfvars = """
        prefix = "acme-prod"
        """;

    public const string AppSettings = """
        {
          "AzureAd": { "Instance": "https://login.microsoftonline.com/", "TenantId": "11111111-2222-3333-4444-555555555555", "ClientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" },
          "ConnectionStrings": { "Orders": "Host=psql-acme-ordering.postgres.database.azure.com;Database=orders;Username=orderadmin;Password=SECRET_SENTINEL_CONN_PASSWORD" },
          "ServiceBus": { "Namespace": "sb-acme-ordering.servicebus.windows.net", "Topic": "orders-created" },
          "Logging": { "LogLevel": { "Default": "Information" } },
          "FeatureManagement": { "NewCheckout": false },
          "Payments": { "ApiKey": "SECRET_SENTINEL_API_KEY", "BaseUrl": "https://payments.example.com/v2" },
          "KeyVaultSecret": "@Microsoft.KeyVault(SecretUri=https://kv-acme.vault.azure.net/secrets/orders-db)"
        }
        """;

    public const string AppSettingsQa = """{ "ServiceBus": { "Topic": "orders-created" }, "Logging": { "LogLevel": { "Default": "Debug" } } }""";
    public const string AppSettingsProduction = """{ "ServiceBus": { "Topic": "orders-created" }, "Logging": { "LogLevel": { "Default": "Warning" } }, "Inventory": { "Topic": "inventory-changed" } }""";

    public const string Pipeline = """
        trigger:
          branches:
            include:
              - main
          paths:
            include:
              - src/*
              - Infrastructure/*

        pr:
          branches:
            include:
              - main

        variables:
          - group: ordering-secrets
          - name: buildConfiguration
            value: Release
          - name: sqlHost
            value: psql-acme-ordering.postgres.database.azure.com

        stages:
          - stage: Build
            jobs:
              - job: Build
                steps:
                  - task: UseDotNet@2
                    inputs:
                      version: '8.x'
                  - script: |
                      dotnet restore
                      dotnet build --configuration $(buildConfiguration)
                      dotnet test tests/Acme.Ordering.UnitTests/Acme.Ordering.UnitTests.csproj --collect "XPlat Code Coverage"
                      echo "$(DbAdminPassword)" > /dev/null
                    displayName: 'Build and test'
                  - script: dotnet list package --vulnerable
                    displayName: 'Dependency scan'
                  - task: AzureKeyVault@2
                    inputs:
                      azureSubscription: sc-acme
                      KeyVaultName: kv-acme
          - stage: DeployQa
            dependsOn: Build
            jobs:
              - deployment: Infra
                environment: qa
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - script: |
                            terraform -chdir=Infrastructure init
                            terraform -chdir=Infrastructure apply -auto-approve -var-file=Infrastructure/qa.tfvars
                          displayName: 'Terraform apply'
                        - task: AzureWebApp@1
                          inputs:
                            azureSubscription: sc-acme
                            appName: app-acme-ordering-api
        """;

    public const string OpenApiYaml = """
        openapi: 3.0.1
        info:
          title: Acme Ordering API
          version: 1.2.0
        paths:
          /orders/{id}:
            get:
              operationId: getOrder
              parameters:
                - name: id
                  in: path
                  required: true
          /orders:
            post:
              operationId: createOrder
        components:
          schemas:
            Order:
              type: object
              required:
                - id
              properties:
                id:
                  type: string
                total:
                  type: number
        """;

    public static (string Path, string Content)[] Acme() =>
    [
        ("Acme.Ordering.sln", ""),
        ("src/Acme.Ordering.Web/Acme.Ordering.Web.csproj", WebProject),
        ("src/Acme.Ordering.Web/Program.cs", WebProgram),
        ("src/Acme.Ordering.Web/appsettings.json", AppSettings),
        ("src/Acme.Ordering.Web/appsettings.QA.json", AppSettingsQa),
        ("src/Acme.Ordering.Web/appsettings.Production.json", AppSettingsProduction),
        ("src/Acme.Ordering.Web/openapi.yaml", OpenApiYaml),
        ("src/Acme.Ordering.Web/Properties/launchSettings.json", """{ "profiles": { "Local": { "applicationUrl": "https://localhost:5001", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "AzureAd__ClientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" } } } }"""),
        ("src/Acme.Ordering.Worker/Acme.Ordering.Worker.csproj", WorkerProject),
        ("src/Acme.Ordering.Worker/Program.cs", WorkerProgram),
        ("src/Acme.Ordering.Worker/appsettings.json", """{ "ServiceBus": { "Namespace": "sb-acme-ordering.servicebus.windows.net", "Topic": "orders-created", "Subscription": "fulfilment" } }"""),
        ("tests/Acme.Ordering.UnitTests/Acme.Ordering.UnitTests.csproj", TestProject),
        ("Infrastructure/main.tf", MainTf),
        ("Infrastructure/messaging.tf", MessagingTf),
        ("Infrastructure/monitoring.tf", MonitoringTf),
        ("Infrastructure/modules/network/main.tf", NetworkModuleTf),
        ("Infrastructure/qa.tfvars", QaTfvars),
        ("Infrastructure/prod.tfvars", ProdTfvars),
        (".azure-pipelines.yml", Pipeline),
        ("renovate.json", """{ "extends": ["config:recommended"] }"""),
    ];

    /// <summary>A provider-neutral fixture: AWS Terraform + Kubernetes manifests + GitHub Actions, no Azure anywhere.</summary>
    public static (string Path, string Content)[] AwsKubernetes() =>
    [
        ("infra/main.tf", """
            provider "aws" { region = "eu-north-1" }
            resource "aws_sqs_queue" "orders" {
              name                      = "orders"
              message_retention_seconds = 86400
            }
            resource "aws_s3_bucket" "invoices" { bucket = "acme-invoices" }
            resource "aws_iam_role" "worker" { name = "orders-worker" }
            resource "aws_iam_role_policy_attachment" "worker_sqs" {
              role       = aws_iam_role.worker.name
              policy_arn = "arn:aws:iam::aws:policy/AmazonSQSFullAccess"
            }
            resource "exoticcloud_widget" "w" { name = "w1" }
            """),
        ("deploy/k8s/overlays/prod/deployment.yaml", """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: orders-api
              namespace: shop
            spec:
              template:
                spec:
                  serviceAccountName: orders-api
                  containers:
                    - name: api
                      image: registry.example.com/orders-api:1.0
                      envFrom:
                        - configMapRef:
                            name: orders-config
            ---
            apiVersion: v1
            kind: ServiceAccount
            metadata:
              name: orders-api
              namespace: shop
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: orders-config
              namespace: shop
            data:
              QUEUE_NAME: orders
              DB_PASSWORD: SECRET_SENTINEL_K8S_PASSWORD
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: orders-secret
              namespace: shop
            data:
              token: U0VDUkVUX1NFTlRJTkVMX0s4U19UT0tFTg==
            """),
        (".github/workflows/ci.yml", """
            name: ci
            on:
              push:
                branches: [main]
                paths: ['infra/**']
              workflow_dispatch:
            permissions:
              id-token: write
              contents: read
            jobs:
              deploy:
                runs-on: ubuntu-latest
                environment: production
                steps:
                  - uses: actions/checkout@v4
                  - uses: hashicorp/setup-terraform@v3
                  - run: terraform -chdir=infra apply -auto-approve
                    env:
                      TOKEN: ${{ secrets.DEPLOY_TOKEN }}
                  - uses: aquasecurity/trivy-action@0.20.0
                  - run: npx playwright test
            """),
    ];
}
