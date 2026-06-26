namespace BirkNext.Web.Models;

public enum ReviewSavePhase
{
    Idle,
    Saving,
    PartialSuccess,
    Complete,
    Failed,
}
