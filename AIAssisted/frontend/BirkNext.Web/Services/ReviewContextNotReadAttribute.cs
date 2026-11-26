namespace BirkNext.Web.Services;

/// <summary>
/// The page does not read the review context synchronously: what it needs it reads through a reader that awaits
/// <see cref="IReviewContextProvider.WhenCurrentAsync"/> (e.g. <see cref="ManualTestingWorkspace"/>). The documents gate shows it while the
/// context is still being built; other pages wait for the build, because they read <see cref="IReviewContextProvider.GetCurrent"/> directly.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReviewContextNotReadAttribute : Attribute;
