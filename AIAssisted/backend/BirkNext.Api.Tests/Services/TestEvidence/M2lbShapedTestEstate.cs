namespace BirkNext.Api.Tests.Services.TestEvidence;

/// <summary>
/// A synthetic test estate with the structure of the M2LB solution the real-archive acceptance test was written against: ten xunit.v3 test
/// projects (bUnit component, two contract, integration with Testcontainers, two unit, four whose names state no kind), a non-test support
/// project under tests/, TRX reporting inherited from Directory.Build.props, a pipeline that generates and publishes TRX, a primary-constructor
/// test class, a C# 12 collection expression and requirement references with each confidence the provider assigns. Project names mirror the
/// real solution; every line of code is invented. The repository owns it, so the mandatory regression never depends on a downloaded archive.
/// </summary>
internal static class M2lbShapedTestEstate
{
    public const int ProjectCount = 10;
    public const int DefinitionCount = 18;
    public const int TheoryCount = 4;
    public const int TrxPublicationCount = 2;

    public const string ContractProjectPath = "M2LB/tests/M2LB.Person.Contract.Tests/M2LB.Person.Contract.Tests.csproj";

    private static string Project(params string[] extraPackages) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net9.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit.v3" Version="2.0.3" />
        {string.Concat(extraPackages.Select(p => $"    <PackageReference Include=\"{p}\" Version=\"1.0.0\" />\n"))}  </ItemGroup>
        </Project>
        """;

    private static string OneFact(string ns, string type, string method) => $$"""
        namespace {{ns}};

        public class {{type}}
        {
            [Fact]
            public void {{method}}() => Assert.True(true);
        }
        """;

    private const string BuildProps = """
        <Project>
          <ItemGroup>
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="1.7.3" />
            <PackageReference Include="coverlet.collector" Version="6.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string FrontendTests = """
        namespace M2LB.Frontend.Tests;

        public class CalendarComponentTests
        {
            [Fact]
            public void RendersTheWeek() => Assert.True(true);

            [Fact]
            public void HighlightsToday() => Assert.True(true);
        }
        """;

    private const string PersonContractTests = """
        namespace M2LB.Person.Contract.Tests;

        [Trait("Category", "Contract")]
        public class PersonContractTests
        {
            /// <summary>FR-026: the published person contract keeps every required field.</summary>
            [Fact]
            public void PublishedContract_KeepsRequiredFields() => Assert.True(true);

            [Theory]
            [InlineData("v1")]
            [InlineData("v2")]
            public void EveryVersion_Deserializes(string version) => Assert.NotNull(version);
        }
        """;

    private const string AuthorizationContractTests = """
        namespace M2LB.Autorisasjon.ContractTests;

        public class RoleContractTests
        {
            [Fact]
            [Trait("Category", "Security")]
            public void AnonymousCaller_GetsNoRoles() => Assert.True(true);
        }
        """;

    private const string PersonIntegrationTests = """
        namespace M2LB.Person.Integration.Tests;

        public class PersonStoreTests(DatabaseFixture database)
        {
            [Fact]
            public void StoredPerson_CanBeReadBack() => Assert.NotNull(database);

            [Theory]
            [InlineData(1)]
            [InlineData(2)]
            public void Paging_ReturnsRequestedSize(int size) => Assert.True(size > 0);
        }

        public sealed class DatabaseFixture { }
        """;

    private const string IntakeTests = """
        namespace M2LB.Hendelse.Unit;

        /// <summary>Intake of synthetic events; the reporting window comes from FR-012.</summary>
        [Trait("Category", "Unit")]
        public class InnmatingServiceTests
        {
            // FR-023: a duplicate message is rejected
            [Fact]
            public void DuplicateMessage_IsRejected() => Assert.True(true);

            [Fact]
            public void ValidPeriod_IsAccepted()
            {
                // AC-003: a period inside the window is accepted
                // the window rule from FR-012 applies here as well
                Assert.True(true);
            }

            [Theory]
            [InlineData(3)]
            public void CountsEveryRow(int expected)
            {
                int[] rows = [1, 2, 3];
                Assert.Equal(expected, rows.Length);
            }

            [Fact]
            public void RejectionMessage_NamesTheRule() => Assert.True(true, "FR-010 names the rule in the message");

            // covers FR-025 and FR-027
            [Fact]
            public void ChangedEvent_IsForwardedOnce() => Assert.True(true);

            [Fact]
            public void OrderingIsKept()
            {
                // see FR-029 for the background
                Assert.True(true);
            }
        }
        """;

    private const string SupportCode = """
        namespace M2LB.TestSupport;

        // FR-077 shared builders; not tests
        public static class Builders
        {
            public static int Fact() => 1;
        }
        """;

    private const string Pipeline = """
        steps:
          - script: dotnet test --solution M2LB.sln --report-trx --results-directory $(Agent.TempDirectory)
          - task: PublishTestResults@2
            inputs:
              testResultsFormat: 'VSTest'
              testResultsFiles: '**/*.trx'
              searchFolder: '$(Agent.TempDirectory)'
          - task: PublishTestResults@2
            inputs:
              testResultsFiles: '**/integration/*.trx'
        """;

    public static (string Path, string Content)[] Files() =>
    [
        ("M2LB/src/M2LB.Person.Api/M2LB.Person.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"""),
        ("M2LB/src/M2LB.Person.Api/Program.cs", "namespace M2LB.Person.Api; public static class Program { public static void Main() { } }"),
        ("M2LB/tests/Directory.Build.props", BuildProps),
        ("M2LB/tests/M2LB.Frontend.Tests/M2LB.Frontend.Tests.csproj", Project("bunit")),
        ("M2LB/tests/M2LB.Frontend.Tests/CalendarComponentTests.cs", FrontendTests),
        (ContractProjectPath, Project()),
        ("M2LB/tests/M2LB.Person.Contract.Tests/PersonContractTests.cs", PersonContractTests),
        ("M2LB/tests/M2LB.Autorisasjon.ContractTests/M2LB.Autorisasjon.ContractTests.csproj", Project()),
        ("M2LB/tests/M2LB.Autorisasjon.ContractTests/RoleContractTests.cs", AuthorizationContractTests),
        ("M2LB/tests/M2LB.Person.Integration.Tests/M2LB.Person.Integration.Tests.csproj", Project("Testcontainers.PostgreSql")),
        ("M2LB/tests/M2LB.Person.Integration.Tests/PersonStoreTests.cs", PersonIntegrationTests),
        ("M2LB/tests/M2LB.Hendelse.Unit/M2LB.Hendelse.Unit.csproj", Project()),
        ("M2LB/tests/M2LB.Hendelse.Unit/InnmatingServiceTests.cs", IntakeTests),
        ("M2LB/tests/M2LB.Tjeneste.Unit/M2LB.Tjeneste.Unit.csproj", Project()),
        ("M2LB/tests/M2LB.Tjeneste.Unit/ServiceTests.cs", OneFact("M2LB.Tjeneste.Unit", "ServiceTests", "Starts")),
        ("M2LB/tests/M2LB.CdcReplay.Tests/M2LB.CdcReplay.Tests.csproj", Project()),
        ("M2LB/tests/M2LB.CdcReplay.Tests/ReplayTests.cs", OneFact("M2LB.CdcReplay.Tests", "ReplayTests", "ReplaysInOrder")),
        ("M2LB/tests/M2LB.Person.Api.Tests/M2LB.Person.Api.Tests.csproj", Project("Microsoft.AspNetCore.Mvc.Testing")),
        ("M2LB/tests/M2LB.Person.Api.Tests/EndpointTests.cs", OneFact("M2LB.Person.Api.Tests", "EndpointTests", "Health_IsOk")),
        ("M2LB/tests/M2LB.Person.Application.Tests/M2LB.Person.Application.Tests.csproj", Project()),
        ("M2LB/tests/M2LB.Person.Application.Tests/HandlerTests.cs", """
            namespace M2LB.Person.Application.Tests;

            public class HandlerTests
            {
                [Theory]
                [InlineData(true)]
                public void Handles(bool value) => Assert.True(value);
            }
            """),
        ("M2LB/tests/M2LB.Person.Domain.Tests/M2LB.Person.Domain.Tests.csproj", Project()),
        ("M2LB/tests/M2LB.Person.Domain.Tests/PersonTests.cs", OneFact("M2LB.Person.Domain.Tests", "PersonTests", "NameIsRequired")),
        ("M2LB/tests/M2LB.TestSupport/M2LB.TestSupport.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"""),
        ("M2LB/tests/M2LB.TestSupport/Builders.cs", SupportCode),
        ("M2LB/.pipeline/runtests.yml", Pipeline),
    ];

    /// <summary>A second test framework next to xUnit: discovered as projects, never as tests.</summary>
    public static (string Path, string Content)[] UnsupportedFrameworkProjects() =>
    [
        ("M2LB/tests/M2LB.Legacy.NUnit/M2LB.Legacy.NUnit.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="NUnit" Version="4.0.0" /></ItemGroup></Project>
            """),
        ("M2LB/tests/M2LB.Legacy.NUnit/LegacyTests.cs", "namespace M2LB.Legacy.NUnit; public class LegacyTests { [Test] public void Works() { } [Fact] public void LooksLikeXunit() { } }"),
        ("M2LB/tests/M2LB.Legacy.MSTest/M2LB.Legacy.MSTest.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="MSTest" Version="3.6.0" /></ItemGroup></Project>
            """),
        ("M2LB/tests/M2LB.Legacy.MSTest/LegacyTests.cs", "namespace M2LB.Legacy.MSTest; [TestClass] public class LegacyTests { [TestMethod] public void Works() { } }"),
    ];
}
