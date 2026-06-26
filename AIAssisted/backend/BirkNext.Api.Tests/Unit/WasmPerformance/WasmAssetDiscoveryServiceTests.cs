using BirkNext.Api.Services.WasmPerformance;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmPerformance;

public class WasmAssetDiscoveryServiceTests
{
    // ── ParseBootManifest ─────────────────────────────────────────────────────

    [Fact]
    public void ParseBootManifest_ValidJson_ReturnsManifest()
    {
        const string json = """
            {
              "mainAssemblyName": "MyApp",
              "resources": {
                "assembly": { "MyApp.wasm": "sha256-abc" },
                "wasmNative": { "dotnet.native.wasm": "sha256-def" }
              },
              "cacheBootResources": true,
              "debugLevel": -1,
              "globalizationMode": "sharded"
            }
            """;

        var result = WasmAssetDiscoveryService.ParseBootManifest(json);

        result.Should().NotBeNull();
        result!.MainAssemblyName.Should().Be("MyApp");
        result.CacheBootResources.Should().BeTrue();
        result.DebugLevel.Should().Be(-1);
        result.GlobalizationMode.Should().Be("sharded");
        result.Resources.Should().NotBeNull();
        result.Resources!.Assembly.Should().ContainKey("MyApp.wasm");
        result.Resources.WasmNative.Should().ContainKey("dotnet.native.wasm");
    }

    [Fact]
    public void ParseBootManifest_EmptyString_ReturnsNull()
    {
        WasmAssetDiscoveryService.ParseBootManifest("").Should().BeNull();
    }

    [Fact]
    public void ParseBootManifest_WhitespaceOnly_ReturnsNull()
    {
        WasmAssetDiscoveryService.ParseBootManifest("   ").Should().BeNull();
    }

    [Fact]
    public void ParseBootManifest_MalformedJson_ReturnsNull()
    {
        WasmAssetDiscoveryService.ParseBootManifest("not json at all { ]").Should().BeNull();
    }

    [Fact]
    public void ParseBootManifest_MissingResources_ReturnsManifestWithNullResources()
    {
        const string json = """{"mainAssemblyName":"MyApp","cacheBootResources":false}""";

        var result = WasmAssetDiscoveryService.ParseBootManifest(json);

        result.Should().NotBeNull();
        result!.MainAssemblyName.Should().Be("MyApp");
        result.Resources.Should().BeNull();
    }

    [Fact]
    public void ParseBootManifest_FullBoot_ParsesAllSections()
    {
        const string json = """
            {
              "mainAssemblyName": "BirkNext.Web",
              "resources": {
                "jsModuleNative":  { "dotnet.native.js":  "sha256-a" },
                "jsModuleRuntime": { "dotnet.runtime.js": "sha256-b" },
                "wasmNative":      { "dotnet.native.wasm": "sha256-c" },
                "icu":             { "icudt_EFIGS.dat": "sha256-d" },
                "assembly": {
                  "Microsoft.AspNetCore.Components.wasm": "sha256-e",
                  "BirkNext.Web.wasm": "sha256-f"
                },
                "pdb": { "BirkNext.Web.pdb": "sha256-g" }
              },
              "debugLevel": -1
            }
            """;

        var result = WasmAssetDiscoveryService.ParseBootManifest(json);

        result.Should().NotBeNull();
        result!.Resources!.JsModuleNative.Should().ContainKey("dotnet.native.js");
        result.Resources.JsModuleRuntime.Should().ContainKey("dotnet.runtime.js");
        result.Resources.WasmNative.Should().ContainKey("dotnet.native.wasm");
        result.Resources.Icu.Should().ContainKey("icudt_EFIGS.dat");
        result.Resources.Assembly.Should().HaveCount(2);
        result.Resources.Pdb.Should().ContainKey("BirkNext.Web.pdb");
    }

    // ── ExpandManifestAssets ──────────────────────────────────────────────────

    [Fact]
    public void ExpandManifestAssets_NullResources_ReturnsEmpty()
    {
        var manifest = new BlazorBootManifest();
        WasmAssetDiscoveryService.ExpandManifestAssets(manifest).Should().BeEmpty();
    }

    [Fact]
    public void ExpandManifestAssets_EmptyResources_ReturnsEmpty()
    {
        var manifest = new BlazorBootManifest { Resources = new BlazorBootResources() };
        WasmAssetDiscoveryService.ExpandManifestAssets(manifest).Should().BeEmpty();
    }

    [Fact]
    public void ExpandManifestAssets_WasmNative_ClassifiedAsWasmRuntime()
    {
        var manifest = new BlazorBootManifest
        {
            Resources = new BlazorBootResources
            {
                WasmNative = new() { ["dotnet.native.wasm"] = "sha256-x" }
            }
        };

        var results = WasmAssetDiscoveryService.ExpandManifestAssets(manifest);

        results.Should().ContainSingle(r =>
            r.Filename == "dotnet.native.wasm" && r.Type == AssetType.WasmRuntime);
    }

    [Fact]
    public void ExpandManifestAssets_JsModules_ClassifiedAsFrameworkJs()
    {
        var manifest = new BlazorBootManifest
        {
            Resources = new BlazorBootResources
            {
                JsModuleNative  = new() { ["dotnet.native.js"]  = "sha256-a" },
                JsModuleRuntime = new() { ["dotnet.runtime.js"] = "sha256-b" }
            }
        };

        var results = WasmAssetDiscoveryService.ExpandManifestAssets(manifest);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.Type.Should().Be(AssetType.FrameworkJs));
    }

    [Fact]
    public void ExpandManifestAssets_SatelliteResources_ClassifiedCorrectly()
    {
        var manifest = new BlazorBootManifest
        {
            MainAssemblyName = "MyApp",
            Resources = new BlazorBootResources
            {
                SatelliteResources = new()
                {
                    ["en-US"] = new() { ["MyApp.resources.wasm"] = "sha256-x" },
                    ["nb"]    = new() { ["MyApp.resources.wasm"] = "sha256-y" }
                }
            }
        };

        var results = WasmAssetDiscoveryService.ExpandManifestAssets(manifest);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.Type.Should().Be(AssetType.SatelliteAssembly));
        results.Should().Contain(r => r.Filename == "en-US/MyApp.resources.wasm");
        results.Should().Contain(r => r.Filename == "nb/MyApp.resources.wasm");
    }

    [Fact]
    public void ExpandManifestAssets_TypicalManifest_IncludesAllSections()
    {
        var manifest = new BlazorBootManifest
        {
            MainAssemblyName = "MyApp",
            Resources = new BlazorBootResources
            {
                JsModuleNative  = new() { ["dotnet.native.js"]  = "sha256-a" },
                WasmNative      = new() { ["dotnet.native.wasm"] = "sha256-b" },
                Icu             = new() { ["icudt_EFIGS.dat"] = "sha256-c" },
                Assembly        = new()
                {
                    ["Microsoft.AspNetCore.Components.wasm"] = "sha256-d",
                    ["MyApp.wasm"] = "sha256-e"
                }
            }
        };

        var results = WasmAssetDiscoveryService.ExpandManifestAssets(manifest);

        results.Should().Contain(r => r.Type == AssetType.FrameworkJs);
        results.Should().Contain(r => r.Type == AssetType.WasmRuntime);
        results.Should().Contain(r => r.Type == AssetType.Other);           // icu
        results.Should().Contain(r => r.Type == AssetType.FrameworkDll);
        results.Should().Contain(r => r.Type == AssetType.ApplicationDll);
    }

    // ── ClassifyAssembly ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Microsoft.AspNetCore.Components.wasm",  null,         AssetType.FrameworkDll)]
    [InlineData("System.Text.Json.wasm",                 null,         AssetType.FrameworkDll)]
    [InlineData("System.Collections.Immutable.wasm",     null,         AssetType.FrameworkDll)]
    [InlineData("HotChocolate.Language.SyntaxTree.wasm", null,         AssetType.FrameworkDll)]
    [InlineData("StrawberryShake.Core.wasm",             null,         AssetType.FrameworkDll)]
    [InlineData("Newtonsoft.Json.wasm",                  null,         AssetType.FrameworkDll)]
    [InlineData("Serilog.wasm",                          null,         AssetType.FrameworkDll)]
    [InlineData("BirkNext.Web.wasm",                     "BirkNext.Web", AssetType.ApplicationDll)]
    [InlineData("MyApp.wasm",                            "MyApp",      AssetType.ApplicationDll)]
    [InlineData("BirkNext.Web.resources.wasm",           "BirkNext.Web", AssetType.SatelliteAssembly)]
    [InlineData("MyApp.resources.wasm",                  null,         AssetType.SatelliteAssembly)]
    [InlineData("dotnet.native.js",                      null,         AssetType.FrameworkDll)]
    public void ClassifyAssembly_ReturnsCorrectType(
        string filename, string? mainAssemblyName, AssetType expected)
    {
        WasmAssetDiscoveryService.ClassifyAssembly(filename, mainAssemblyName)
            .Should().Be(expected);
    }

    [Fact]
    public void ClassifyAssembly_UnknownPrefix_ReturnsApplicationDll()
    {
        WasmAssetDiscoveryService.ClassifyAssembly("Acme.Payments.wasm", null)
            .Should().Be(AssetType.ApplicationDll);
    }

    [Fact]
    public void ClassifyAssembly_NullMainAssembly_DoesNotThrow()
    {
        var act = () => WasmAssetDiscoveryService.ClassifyAssembly("MyApp.wasm", null);
        act.Should().NotThrow();
    }

    // ── ParseIndexHtmlAssets ──────────────────────────────────────────────────

    [Fact]
    public void ParseIndexHtmlAssets_EmptyHtml_ReturnsEmpty()
    {
        WasmAssetDiscoveryService.ParseIndexHtmlAssets("").Should().BeEmpty();
    }

    [Fact]
    public void ParseIndexHtmlAssets_WithCssLinks_ReturnsCssAssets()
    {
        const string html = """
            <link rel="stylesheet" href="css/app.css" />
            <link href="css/bootstrap.min.css" rel="stylesheet" />
            """;

        var results = WasmAssetDiscoveryService.ParseIndexHtmlAssets(html);

        results.Should().Contain(r => r.RelativePath == "css/app.css"          && r.Type == AssetType.Css);
        results.Should().Contain(r => r.RelativePath == "css/bootstrap.min.css" && r.Type == AssetType.Css);
    }

    [Fact]
    public void ParseIndexHtmlAssets_WithScriptTags_ReturnsJsAssets()
    {
        const string html = """<script src="js/app.js"></script>""";

        var results = WasmAssetDiscoveryService.ParseIndexHtmlAssets(html);

        results.Should().ContainSingle(r =>
            r.RelativePath == "js/app.js" && r.Type == AssetType.JavaScript);
    }

    [Fact]
    public void ParseIndexHtmlAssets_FrameworkScripts_Excluded()
    {
        const string html = """<script src="_framework/blazor.webassembly.js"></script>""";

        WasmAssetDiscoveryService.ParseIndexHtmlAssets(html).Should().BeEmpty();
    }

    [Fact]
    public void ParseIndexHtmlAssets_WithFavicon_ReturnsImage()
    {
        const string html = """<link rel="icon" type="image/png" href="favicon.png" />""";

        var results = WasmAssetDiscoveryService.ParseIndexHtmlAssets(html);

        results.Should().ContainSingle(r =>
            r.RelativePath == "favicon.png" && r.Type == AssetType.Image);
    }

    [Fact]
    public void ParseIndexHtmlAssets_ExternalUrls_Excluded()
    {
        const string html = """
            <link rel="stylesheet" href="https://cdn.example.com/app.css" />
            <script src="https://cdn.example.com/app.js"></script>
            """;

        WasmAssetDiscoveryService.ParseIndexHtmlAssets(html).Should().BeEmpty();
    }

    [Fact]
    public void ParseIndexHtmlAssets_ProtocolRelativeUrls_Excluded()
    {
        const string html = """<link rel="stylesheet" href="//cdn.example.com/app.css" />""";

        WasmAssetDiscoveryService.ParseIndexHtmlAssets(html).Should().BeEmpty();
    }

    [Fact]
    public void ParseIndexHtmlAssets_DuplicatePaths_DeduplicatedInOutput()
    {
        const string html = """
            <link rel="stylesheet" href="css/app.css" />
            <link rel="stylesheet" href="css/app.css" />
            """;

        WasmAssetDiscoveryService.ParseIndexHtmlAssets(html).Should().HaveCount(1);
    }

    [Fact]
    public void ParseIndexHtmlAssets_TypicalBlazorIndex_ReturnsExpectedAssets()
    {
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
                <link rel="stylesheet" href="css/bootstrap/bootstrap.min.css" />
                <link rel="stylesheet" href="css/app.css" />
                <link rel="icon" type="image/png" href="favicon.png" />
                <link href="MyApp.styles.css" rel="stylesheet" />
            </head>
            <body>
                <script src="_framework/blazor.webassembly.js"></script>
            </body>
            </html>
            """;

        var results = WasmAssetDiscoveryService.ParseIndexHtmlAssets(html);

        results.Should().Contain(r => r.Type == AssetType.Css);
        results.Should().Contain(r => r.Type == AssetType.Image);
        results.Should().NotContain(r => r.RelativePath.Contains("_framework"));
    }

    // ── ExtractBaseHref ───────────────────────────────────────────────────────

    [Fact]
    public void ExtractBaseHref_RootHref_Returns_Slash()
    {
        const string html = """<base href="/" />""";
        WasmAssetDiscoveryService.ExtractBaseHref(html).Should().Be("/");
    }

    [Fact]
    public void ExtractBaseHref_SubPathHref_ReturnsPath()
    {
        const string html = """<base href="/myapp/" />""";
        WasmAssetDiscoveryService.ExtractBaseHref(html).Should().Be("/myapp/");
    }

    [Fact]
    public void ExtractBaseHref_NoBaseTag_ReturnsNull()
    {
        WasmAssetDiscoveryService.ExtractBaseHref("<html><body></body></html>")
            .Should().BeNull();
    }

    [Fact]
    public void ExtractBaseHref_EmptyHtml_ReturnsNull()
    {
        WasmAssetDiscoveryService.ExtractBaseHref("").Should().BeNull();
    }

    // ── Missing boot manifest scenario ────────────────────────────────────────

    [Fact]
    public void ParseBootManifest_MissingAssemblySection_ManifestHasNoAssemblies()
    {
        const string json = """
            {
              "mainAssemblyName": "MyApp",
              "resources": {
                "wasmNative": { "dotnet.native.wasm": "sha256-x" }
              }
            }
            """;

        var manifest = WasmAssetDiscoveryService.ParseBootManifest(json);
        manifest.Should().NotBeNull();

        var assets = WasmAssetDiscoveryService.ExpandManifestAssets(manifest!);
        assets.Should().NotContain(a => a.Type == AssetType.ApplicationDll);
        assets.Should().NotContain(a => a.Type == AssetType.FrameworkDll);
        assets.Should().Contain(a => a.Type == AssetType.WasmRuntime);
    }

    [Fact]
    public void ExpandManifestAssets_ManifestWithOnlyIcu_ReturnsOtherType()
    {
        var manifest = new BlazorBootManifest
        {
            Resources = new BlazorBootResources
            {
                Icu = new()
                {
                    ["icudt_CJK.dat"]   = "sha256-a",
                    ["icudt_EFIGS.dat"] = "sha256-b",
                    ["icudt_no_CJK.dat"]= "sha256-c"
                }
            }
        };

        var results = WasmAssetDiscoveryService.ExpandManifestAssets(manifest);

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.Type.Should().Be(AssetType.Other));
    }
}
