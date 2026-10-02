namespace BirkNext.Api.Tests.Services.TestEvidence;

/// <summary>
/// Neutral fixtures modeled on real artifact shapes: a multi-project .NET/xUnit v3 solution (unit, integration, contract, bUnit component, NUnit
/// and a non-test helper project under tests/), a pipeline that generates and publishes TRX, and two TRX files whose structure was produced by the
/// real writers (Microsoft.Testing.Platform TrxReport 1.7 and the VSTest TRX logger) for the OrderPricingTests class below. Machine names, users
/// and paths are placeholders so the tests can prove none of them persist. No business code from any real project.
/// </summary>
internal static class TestEvidenceFixtures
{
    public const string UnitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net9.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="coverlet.collector" Version="6.0.0" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="1.7.3" />
            <PackageReference Include="Shouldly" Version="4.3.0" />
            <PackageReference Include="xunit.v3" Version="2.0.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
          </ItemGroup>
        </Project>
        """;

    public const string OrderTests = """
        namespace Contoso.Orders.Unit.Tests;

        /// <summary>Order pricing rules (FR-090).</summary>
        [Trait("Category", "Unit")]
        public class OrderPricingTests
        {
            // FR-023: a discount never makes the total negative
            [Fact]
            public void Discount_NeverNegative() => Assert.True(Math.Max(0, 10 - 20) >= 0);

            [Trait("Requirement", "FR-026")]
            [Fact]
            public void Rounding_IsBankers() => Assert.Equal(2, Math.Round(2.5));

            [Fact]
            public void Tax_FailsOnPurpose()
            {
                // Assert — the tax rule is covered elsewhere (SC-004)
                Assert.Equal(25, 20);
            }

            [Fact(Skip = "Pending JIRA-123")]
            public void Currency_Skipped() { }

            [Trait("Requirement", "JIRA-123")]
            [Trait("Requirement", "US-A1")]
            [Trait("AcceptanceCriterion", "AC-007")]
            [Theory]
            [InlineData(1, 1)]
            [InlineData(2, 4)]
            [InlineData(3, 10)]
            public void Square(int value, int expected)
            {
                // FR-031: squares are exact
                Assert.Equal(expected, value * value);
            }

            [Fact(DisplayName = "Shipping is free above threshold")]
            public void Shipping_Free() => Assert.True(true, "threshold behaviour (FR-040)");

            [Fact]
            public void FR023_NameLooksLikeARequirement() => Assert.True(true);

            [Theory]
            [MemberData(nameof(Rows))]
            public void Dynamic(int value) => Assert.True(value > 0);

            public static TheoryData<int> Rows => new() { 1, 2 };

            // FR-099 is described by this helper; it is not a test.
            private static int Helper() => 1;

            public class Nested
            {
                [Fact]
                public void Inner_Passes() => Assert.True(true);
            }
        }
        """;

    public const string IntegrationProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="1.7.3" />
            <PackageReference Include="Testcontainers.MsSql" Version="4.0.0" />
            <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="9.0.0" />
            <PackageReference Include="xunit.v3" Version="2.0.3" />
          </ItemGroup>
        </Project>
        """;

    public static string HealthTests(string ns) => $$"""
        namespace {{ns}}
        {
            public class HealthEndpointTests
            {
                [Fact]
                public void Health_ReturnsOk() => Assert.True(true);
            }
        }
        """;

    public const string ContractProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit.v3" Version="2.0.3" />
          </ItemGroup>
        </Project>
        """;

    public const string ComponentProject = """
        <Project Sdk="Microsoft.NET.Sdk.Razor">
          <ItemGroup>
            <PackageReference Include="bunit" Version="1.30.0" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit.v3" Version="2.0.3" />
          </ItemGroup>
        </Project>
        """;

    public const string ComponentTests = """
        namespace Contoso.Web.Tests;
        public class LayoutTests
        {
            [Fact]
            public void SkipLink_Renders() => Assert.True(true);
        }
        """;

    public const string NUnitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="NUnit" Version="4.0.0" />
          </ItemGroup>
        </Project>
        """;

    public const string NUnitTests = """
        namespace Contoso.Legacy.Tests;
        public class LegacyTests
        {
            [Test]
            public void Works() { }
        }
        """;

    public const string HelperProject = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>
        """;

    public const string HelperCode = """
        namespace Contoso.TestHelpers;
        // FR-077 shared fixture data
        public static class Builders
        {
            public static int Fact() => 1;
        }
        """;

    public const string Pipeline = """
        steps:
          - script: dotnet test --project tests/Contoso.Orders.Unit.Tests/Contoso.Orders.Unit.Tests.csproj --report-trx --report-trx-filename Unit-test-results.trx --results-directory $(Agent.TempDirectory)
          - task: PublishTestResults@2
            inputs:
              testResultsFormat: 'VSTest'
              testResultsFiles: '**/*.trx'
              searchFolder: '$(Agent.TempDirectory)'
        """;

    public static (string Path, string Content)[] Solution() =>
    [
        ("Contoso/src/Contoso.Orders/Contoso.Orders.csproj", HelperProject),
        ("Contoso/src/Contoso.Orders/Order.cs", "namespace Contoso.Orders; public class Order { }"),
        ("Contoso/tests/Contoso.Orders.Unit.Tests/Contoso.Orders.Unit.Tests.csproj", UnitProject),
        ("Contoso/tests/Contoso.Orders.Unit.Tests/OrderTests.cs", OrderTests),
        ("Contoso/tests/Contoso.Orders.Integration.Tests/Contoso.Orders.Integration.Tests.csproj", IntegrationProject),
        ("Contoso/tests/Contoso.Orders.Integration.Tests/HealthEndpointTests.cs", HealthTests("Contoso.Orders.Integration.Tests")),
        ("Contoso/tests/Contoso.Billing.Integration.Tests/Contoso.Billing.Integration.Tests.csproj", IntegrationProject),
        ("Contoso/tests/Contoso.Billing.Integration.Tests/HealthEndpointTests.cs", HealthTests("Contoso.Billing.Integration.Tests")),
        ("Contoso/tests/Contoso.Orders.ContractTests/Contoso.Orders.ContractTests.csproj", ContractProject),
        ("Contoso/tests/Contoso.Orders.ContractTests/HealthEndpointTests.cs", HealthTests("Shared.Contract")),
        ("Contoso/tests/Contoso.Billing.ContractTests/Contoso.Billing.ContractTests.csproj", ContractProject),
        ("Contoso/tests/Contoso.Billing.ContractTests/HealthEndpointTests.cs", HealthTests("Shared.Contract")),
        ("Contoso/tests/Contoso.Web.Tests/Contoso.Web.Tests.csproj", ComponentProject),
        ("Contoso/tests/Contoso.Web.Tests/LayoutTests.cs", ComponentTests),
        ("Contoso/tests/Contoso.Legacy.Tests/Contoso.Legacy.Tests.csproj", NUnitProject),
        ("Contoso/tests/Contoso.Legacy.Tests/LegacyTests.cs", NUnitTests),
        ("Contoso/tests/TestHelpers/TestHelpers.csproj", HelperProject),
        ("Contoso/tests/TestHelpers/Builders.cs", HelperCode),
        ("Contoso/.pipeline/runtests.yml", Pipeline),
    ];

    private const string Ns = "Contoso.Orders.Unit.Tests.OrderPricingTests";
    private const string Agent = @"C:\agent\_work\1\s\Contoso\tests\Contoso.Orders.Unit.Tests\bin\Release\net9.0\Contoso.Orders.Unit.Tests.dll";

    private static string MtpResult(string executionId, string testId, string testName, string outcome, string? error = null) =>
        $"""    <UnitTestResult executionId="{executionId}" testId="{testId}" testName="{testName}" computerName="BUILD-AGENT-01" duration="00:00:00.0012000" startTime="2026-09-30T08:00:01.0000000+00:00" endTime="2026-09-30T08:00:01.0012000+00:00" testType="13CDC9D9-DDB5-4fa4-A97D-D965CCFC6D4B" outcome="{outcome}" testListId="8C84FA94-04C1-424b-9868-57A2D4851A1D" relativeResultsDirectory="{executionId}">{error}</UnitTestResult>""";

    private static string MtpDefinition(string testId, string executionId, string className, string name) =>
        $"""    <UnitTest name="{name}" storage="{Agent.ToLowerInvariant()}" id="{testId}"><Execution id="{executionId}" /><TestMethod codeBase="{Agent}" adapterTypeName="executor://30ea7c6e-dd24-4152-a360-1387158cd41d/2.0.3" className="{className}" name="{name}" /></UnitTest>""";

    private static readonly (string Exec, string Test, string Class, string Name, string Outcome, string? Error)[] MtpRows =
    [
        ("b0c3b566-0000-0000-0000-000000000001", "13610eef-0000-0000-0000-000000000001", Ns + "+Nested", Ns + "+Nested.Inner_Passes", "Passed", null),
        ("b0c3b566-0000-0000-0000-000000000002", "13610eef-0000-0000-0000-000000000002", Ns, Ns + ".Tax_FailsOnPurpose", "Failed",
            $"""<Output><ErrorInfo><Message>Assert.Equal() Failure: Values differ&#xA;Expected: 25&#xA;Actual:   20 Authorization: Bearer abc.def.ghi</Message><StackTrace>   at {Ns}.Tax_FailsOnPurpose() in C:\Users\builduser\source\Contoso\tests\Contoso.Orders.Unit.Tests\OrderTests.cs:line 19&#xA;   at System.RuntimeMethodHandle.InvokeMethod(Object target)</StackTrace></ErrorInfo></Output>"""),
        ("b0c3b566-0000-0000-0000-000000000003", "13610eef-0000-0000-0000-000000000003", Ns, Ns + ".Discount_NeverNegative", "Passed", null),
        ("b0c3b566-0000-0000-0000-000000000004", "13610eef-0000-0000-0000-000000000004", Ns, Ns + ".Currency_Skipped", "NotExecuted", null),
        ("b0c3b566-0000-0000-0000-000000000005", "13610eef-0000-0000-0000-000000000005", Ns, "Shipping is free above threshold", "Passed", null),
        ("b0c3b566-0000-0000-0000-000000000006", "13610eef-0000-0000-0000-000000000006", Ns, Ns + ".Square(value: 1, expected: 1)", "Passed", null),
        ("b0c3b566-0000-0000-0000-000000000007", "13610eef-0000-0000-0000-000000000007", Ns, Ns + ".Square(value: 2, expected: 4)", "Passed", null),
        ("b0c3b566-0000-0000-0000-000000000008", "13610eef-0000-0000-0000-000000000008", Ns, Ns + ".Square(value: 3, expected: 10)", "Failed",
            """<Output><ErrorInfo><Message>Assert.Equal() Failure: Values differ</Message></ErrorInfo></Output>"""),
        ("b0c3b566-0000-0000-0000-000000000009", "13610eef-0000-0000-0000-000000000009", Ns, Ns + ".Rounding_IsBankers", "Passed", null),
    ];

    /// <summary>Microsoft.Testing.Platform TrxReport shape (dotnet test --report-trx): TestMethod name is the fully-qualified name (+ theory args) or a display name.</summary>
    public static string MtpTrx(string runId = "7abfec54-72a5-4096-99bc-60bcde8bed85") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="{runId}" name="builduser@BUILD-AGENT-01 2026-09-30 08:00:00.000" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Times creation="2026-09-30T08:00:00.0000000Z" queuing="2026-09-30T08:00:00.0000000Z" start="2026-09-30T08:00:00.0000000Z" finish="2026-09-30T08:00:02.0000000Z" />
          <TestSettings name="default" id="5bcfa7a5-60db-4586-ad78-0eb137f5dc6d"><Deployment runDeploymentRoot="builduser_BUILD-AGENT-01_2026-09-30_08_00_00.000" /></TestSettings>
          <Results>
        {string.Join("\n", MtpRows.Select(r => MtpResult(r.Exec, r.Test, r.Name, r.Outcome, r.Error)))}
          </Results>
          <TestDefinitions>
        {string.Join("\n", MtpRows.Select(r => MtpDefinition(r.Test, r.Exec, r.Class, r.Name)))}
          </TestDefinitions>
          <ResultSummary outcome="Failed">
            <Counters total="9" executed="8" passed="6" failed="2" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
            <RunInfos><RunInfo computerName="BUILD-AGENT-01" outcome="Error" timestamp="2026-09-30T08:00:02.0000000"><Text>Exit code indicates failure: '2'. See C:\Users\builduser\logs\run.log</Text></RunInfo></RunInfos>
          </ResultSummary>
        </TestRun>
        """;

    /// <summary>VSTest TRX logger shape (dotnet test --logger trx): TestMethod name is the bare method name; declared counters omit the skipped test.</summary>
    public static string VsTestTrx() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="8a9274ba-98ae-417a-bfcc-b23e4992b813" name="builduser@BUILD-AGENT-01 2026-09-30 08:10:00" runUser="CORP\builduser" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Times creation="2026-09-30T08:10:00.0000000+02:00" queuing="2026-09-30T08:10:00.0000000+02:00" start="2026-09-30T08:10:00.0000000+02:00" finish="2026-09-30T08:10:01.0000000+02:00" />
          <Results>
            <UnitTestResult executionId="e1" testId="t1" testName="{Ns}.Square(value: 1, expected: 1)" computerName="BUILD-AGENT-01" duration="00:00:00.0010000" startTime="2026-09-30T08:10:00.1000000+02:00" endTime="2026-09-30T08:10:00.1010000+02:00" outcome="Passed" />
            <UnitTestResult executionId="e2" testId="t2" testName="Shipping is free above threshold" computerName="BUILD-AGENT-01" duration="00:00:00.0010000" outcome="Passed" />
            <UnitTestResult executionId="e3" testId="t3" testName="{Ns}.Currency_Skipped" computerName="BUILD-AGENT-01" duration="00:00:00.0010000" outcome="NotExecuted"><Output><ErrorInfo><Message>Pending JIRA-123</Message></ErrorInfo></Output></UnitTestResult>
            <UnitTestResult executionId="e4" testId="t4" testName="Contoso.Legacy.Tests.LegacyTests.Works" computerName="BUILD-AGENT-01" duration="00:00:00.0010000" outcome="Passed" />
          </Results>
          <TestDefinitions>
            <UnitTest name="{Ns}.Square(value: 1, expected: 1)" storage="{Agent.ToLowerInvariant()}" id="t1"><Execution id="e1" /><TestMethod codeBase="{Agent}" adapterTypeName="executor://xunit/VsTestRunner3/netcore/" className="{Ns}" name="Square" /></UnitTest>
            <UnitTest name="Shipping is free above threshold" storage="{Agent.ToLowerInvariant()}" id="t2"><Execution id="e2" /><TestMethod codeBase="{Agent}" adapterTypeName="executor://xunit/VsTestRunner3/netcore/" className="{Ns}" name="Shipping_Free" /></UnitTest>
            <UnitTest name="{Ns}.Currency_Skipped" storage="{Agent.ToLowerInvariant()}" id="t3"><Execution id="e3" /><TestMethod codeBase="{Agent}" adapterTypeName="executor://xunit/VsTestRunner3/netcore/" className="{Ns}" name="Currency_Skipped" /></UnitTest>
            <UnitTest name="Contoso.Legacy.Tests.LegacyTests.Works" storage="/home/vsts/work/1/s/Contoso.Legacy.Tests.dll" id="t4"><Execution id="e4" /><TestMethod codeBase="/home/vsts/work/1/s/Contoso.Legacy.Tests.dll" adapterTypeName="executor://NUnit3TestExecutor" className="Contoso.Legacy.Tests.LegacyTests" name="Works" /></UnitTest>
          </TestDefinitions>
          <ResultSummary outcome="Completed">
            <Counters total="4" executed="3" passed="3" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
          </ResultSummary>
        </TestRun>
        """;

    /// <summary>A run aborted after two results: the remaining tests have no row and must not become Failed.</summary>
    public const string AbortedTrx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="11111111-2222-3333-4444-555555555555" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult executionId="a1" testId="d1" testName="Contoso.Orders.Unit.Tests.OrderPricingTests.Discount_NeverNegative" outcome="Passed" />
            <UnitTestResult executionId="a2" testId="d2" testName="Contoso.Orders.Unit.Tests.OrderPricingTests.Rounding_IsBankers" outcome="Timeout" />
            <UnitTestResult executionId="a3" testId="d3" testName="Contoso.Orders.Unit.Tests.OrderPricingTests.Tax_FailsOnPurpose" outcome="Error" />
          </Results>
          <ResultSummary outcome="Aborted"><Counters total="3" executed="2" passed="1" failed="0" error="1" timeout="1" aborted="0" /><RunInfos><RunInfo outcome="Error"><Text>Test host process crashed</Text></RunInfo></RunInfos></ResultSummary>
        </TestRun>
        """;
}
