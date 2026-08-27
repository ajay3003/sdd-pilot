using Bunit;
using BirkNext.Web.Models;
using BirkNext.Web.Components.Analysis;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// UI component tests for target environment detection panel.
/// Tests rendered component behavior, state transitions, and user interactions.
/// </summary>
public class TargetEnvironmentDetectionPanelTests : TestContext
{
    private readonly TestDetectionService _detectionService = new();

    [Fact]
    public void DetectConfigurationButton_IsVisible()
    {
        // Arrange
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));

        // Act
        var button = cut.Find("button[aria-label='Detect target configuration']");

        // Assert
        Assert.NotNull(button);
        Assert.Contains("Detect", button.TextContent);
    }

    [Fact]
    public void DetectConfiguration_ShowsDetectingState()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TargetEnvironmentDetectionResult>();
        _detectionService.SetResult(tcs.Task);

        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));

        // Act
        cut.Find("button").Click();
        cut.WaitForAssertion(() =>
        {
            var spinner = cut.FindComponent<LoadingSpinner>();
            Assert.NotNull(spinner);
        });

        // Assert - detecting state is displayed
        var detectingText = cut.Markup;
        Assert.Contains("Detecting", detectingText);

        // Cleanup
        tcs.SetResult(new TargetEnvironmentDetectionResult { Success = true });
    }

    [Fact]
    public void SuccessfulDetection_ShowsResultSummary()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedTenantId = "12345678-1234-1234-1234-123456789012",
            DetectedClientId = "client-123",
            Message = "Detection completed successfully"
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Microsoft Entra ID", markup);
            Assert.Contains("12345678-1234-1234-1234-123456789012", markup);
        });
    }

    [Fact]
    public void SuccessfulDetection_ShowsDetectedProvenance()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedTenantId = "org-tenant-guid",
            Confidence = DetectionConfidence.VeryHigh
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert - provenance shown (detected from URL/redirect)
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Detected", markup); // Source indicator
            Assert.Contains("Very High", markup); // Confidence
        });
    }

    [Fact]
    public void Suggestions_ShowSuggestedProvenance()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            SuggestedEnvironmentType = FrontendEnvironmentType.Production,
            SuggestedProfileName = "PROD APP",
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://prod-app.example.test")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Production", markup);
            Assert.Contains("Suggested", markup); // Provenance source
        });
    }

    [Fact]
    public void ExistingValues_ShowUserConfiguredSemantics()
    {
        // Arrange
        var existingSettings = new FrontendAuthenticationSettings
        {
            ExpectedAuthority = "https://login.microsoftonline.com",
            ExpectedTenant = "existing-tenant",
            ExpectedClientId = "existing-client"
        };

        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedTenantId = "detected-tenant"
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, existingSettings)
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("existing-tenant", markup); // User-configured shown
            Assert.Contains("User Configured", markup); // Source indicator
        });
    }

    [Fact]
    public void AuthenticationRequired_ShowsMicrosoftEntraId()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com"
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Microsoft Entra ID", markup);
            Assert.Contains("login.microsoftonline.com", markup);
        });
    }

    [Fact]
    public void AuthenticationRequired_ShowsAuthenticatedReviewNotSupported()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Authenticated review", markup);
            Assert.Contains("not currently supported", markup);
        });
    }

    [Fact]
    public void EmptyEligibleField_IsPopulatedFromReliableDetection()
    {
        // Arrange
        var existingSettings = new FrontendAuthenticationSettings
        {
            ExpectedAuthority = null, // Empty
            ExpectedTenant = null
        };

        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedTenantId = "detected-guid",
            Confidence = DetectionConfidence.VeryHigh
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, existingSettings)
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("detected-guid", markup);
            Assert.Contains("Populated from detection", markup);
        });
    }

    [Fact]
    public void ExistingIdenticalField_RemainsStable()
    {
        // Arrange
        var existingSettings = new FrontendAuthenticationSettings
        {
            ExpectedTenant = "existing-tenant-id"
        };

        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedTenantId = "existing-tenant-id" // Same value
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, existingSettings)
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Value matches detected", markup);
            Assert.Contains("No change required", markup);
        });
    }

    [Fact]
    public void ExistingConflictingField_IsNotOverwritten()
    {
        // Arrange
        var existingSettings = new FrontendAuthenticationSettings
        {
            ExpectedTenant = "existing-tenant-a"
        };

        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedTenantId = "detected-tenant-b" // Different
        };
        _detectionService.SetResult(Task.FromResult(result));

        var onConflict = new Action<string, string>((field, detected) =>
        {
            // Verify conflict was reported
        });

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, existingSettings)
            .Add(p => p.DetectionService, _detectionService)
            .Add(p => p.OnConflict, onConflict));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Conflict detected", markup);
            Assert.Contains("existing-tenant-a", markup); // Existing preserved
            Assert.Contains("detected-tenant-b", markup); // Detected shown separately
        });
    }

    [Fact]
    public void Conflict_IsCommunicatedToUser()
    {
        // Arrange
        var existingSettings = new FrontendAuthenticationSettings
        {
            ExpectedClientId = "client-id-a"
        };

        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedClientId = "client-id-b"
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, existingSettings)
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var alert = cut.FindComponent<AlertBox>();
            Assert.NotNull(alert);
            var alertText = alert.Instance.Message;
            Assert.Contains("conflict", alertText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void DetectConfiguration_DoesNotSaveEnvironment()
    {
        // Arrange
        var savedCalls = 0;
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedTenantId = "detected-tenant"
        };
        _detectionService.SetResult(Task.FromResult(result));

        var onSaved = new Func<FrontendSecuritySettings, Task>(async settings =>
        {
            savedCalls++;
            await Task.CompletedTask;
        });

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService)
            .Add(p => p.OnSaved, onSaved));
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("detected-tenant", cut.Markup));

        // Assert - NO save occurred
        Assert.Equal(0, savedCalls);
    }

    [Fact]
    public void TargetUrlChange_InvalidatesDetection()
    {
        // Arrange
        var result1 = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedTenantId = "tenant-a"
        };
        _detectionService.SetResult(Task.FromResult(result1));

        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example-a.test/app")
            .Add(p => p.DetectionService, _detectionService));

        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("tenant-a", cut.Markup));

        // Act - Change target URL
        var result2 = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedTenantId = "tenant-b"
        };
        _detectionService.SetResult(Task.FromResult(result2));

        cut.SetParametersAndRender(ps => ps.Add(p => p.TargetUrl, "https://example-b.test/app"));

        // Assert - Detection from URL-A is no longer shown
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.DoesNotContain("tenant-a", markup);
        });
    }

    [Fact]
    public void CommonTenant_DoesNotPopulateExpectedTenant()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            TenantMode = "common", // Not a concrete tenant
            DetectedTenantId = null
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("common", markup); // TenantMode shown
            Assert.DoesNotContain("populate", markup); // Not auto-populating
        });
    }

    [Fact]
    public void OrganizationsTenant_DoesNotPopulateExpectedTenant()
    {
        // Arrange - same as CommonTenant but for "organizations"
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            TenantMode = "organizations",
            DetectedTenantId = null
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act & Assert - same pattern
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("organizations", markup);
            Assert.DoesNotContain("auto-populated", markup);
        });
    }

    [Fact]
    public void ConsumersTenant_DoesNotPopulateExpectedTenant()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            TenantMode = "consumers",
            DetectedTenantId = null
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act & Assert
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("consumers", markup);
        });
    }

    [Fact]
    public void SensitiveSentinels_AreNotRendered()
    {
        // Arrange - Detection result should never contain auth tokens
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            Message = "Detection completed",
            // Sensitive data should not be in any field
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert - no auth tokens in rendered output
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.DoesNotContain("FAKE-CODE-SENTINEL", markup);
            Assert.DoesNotContain("FAKE-ACCESS-TOKEN", markup);
            Assert.DoesNotContain("FAKE-STATE-SENTINEL", markup);
        });
    }

    [Fact]
    public void DetectionFailure_ShowsSafeMessage()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = false,
            Message = "Detection failed",
            ErrorCode = "TIMEOUT"
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("Detection failed", markup);
            Assert.DoesNotContain("raw URL", markup);
            Assert.DoesNotContain("query parameter", markup);
        });
    }

    [Fact]
    public void ManualConfiguration_WorksWithoutDetection()
    {
        // Arrange - User configures manually without using detection
        var settings = new FrontendSecuritySettings
        {
            ExpectedAuthority = "https://login.microsoftonline.com",
            ExpectedTenant = "manual-tenant",
            ExpectedClientId = "manual-client"
        };

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, new FrontendAuthenticationSettings())
            .Add(p => p.DetectionService, _detectionService));

        // Assert - component loads without detection
        Assert.NotNull(cut);
        var button = cut.Find("button");
        Assert.NotNull(button);
    }

    [Fact]
    public void ClientId_FollowsResolvedPolicy()
    {
        // Arrange - Policy: SUGGEST ONLY (not auto-populate)
        var existingSettings = new FrontendAuthenticationSettings
        {
            ExpectedClientId = null // Empty
        };

        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedClientId = "detected-client-id"
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.ExistingSettings, existingSettings)
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert - Client ID is DISPLAYED but marked as "suggestion" not auto-populated
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("detected-client-id", markup);
            Assert.Contains("Suggested", markup);
            Assert.DoesNotContain("Auto-populated", markup);
        });
    }

    [Fact]
    public void CanonicalAuthority_HasNoSensitiveQueryOrPath()
    {
        // Arrange
        var result = new TargetEnvironmentDetectionResult
        {
            Success = true,
            DetectedAuthority = "https://login.microsoftonline.com",
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
        };
        _detectionService.SetResult(Task.FromResult(result));

        // Act
        var cut = RenderComponent<TargetEnvironmentDetectionPanel>(ps => ps
            .Add(p => p.TargetUrl, "https://example.test/app")
            .Add(p => p.DetectionService, _detectionService));
        cut.Find("button").Click();

        // Assert - Authority only shows scheme://host, no /oauth2/v2.0/authorize path
        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("https://login.microsoftonline.com", markup);
            Assert.DoesNotContain("/oauth2/v2.0/authorize", markup);
            Assert.DoesNotContain("&code=", markup);
        });
    }

    /// <summary>
    /// Mock detection service for testing.
    /// </summary>
    private class TestDetectionService : ITargetEnvironmentDetectionService
    {
        private Task<TargetEnvironmentDetectionResult> _result =
            Task.FromResult(new TargetEnvironmentDetectionResult { Success = false });

        public void SetResult(Task<TargetEnvironmentDetectionResult> result)
        {
            _result = result;
        }

        public Task<TargetEnvironmentDetectionResult> DetectFromUrlAsync(string targetUrl, CancellationToken cancellationToken = default)
        {
            return _result;
        }
    }
}
