namespace BirkNext.Web.Models;

public readonly record struct ReviewStatusCounts(
    int New,
    int Accepted,
    int Rejected,
    int NeedsReview)
{
    public int Total => New + Accepted + Rejected + NeedsReview;
}
