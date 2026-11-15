using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Layout;

/// <summary>Sidebar state in the All Modules workspace, for tests that check the full catalog layout. Manual Testing is the default workspace, so
/// these tests select All Modules explicitly instead of relying on a default.</summary>
internal static class AllModulesWorkspace
{
    public static NavigationSectionState State()
    {
        var state = new NavigationSectionState();
        state.SetView(WorkspaceView.AllModules);
        return state;
    }
}
